using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// Recovers sound that was never recorded on a track of its own: "All PC audio"
/// minus every other track (each app, Other apps, and the microphone in case
/// it's played back through the PC). Whatever is left is the sound only All PC
/// audio has (a game Windows wouldn't let Persocord record on its own, say),
/// which the editor offers as an "Unknown" track.
///
/// How each track is matched, so none of it is left behind as a duplicate:
///  - Lined up in time first. Captures start a few milliseconds apart and can
///    jump by up to a few hundred when Windows hiccups, so each track is
///    followed second by second, with a wide re-search when it stops matching.
///  - Then matched in loudness and tone, per frequency band, second by second
///    (a least-squares fit in the frequency domain). That also absorbs an
///    equaliser or "sound effects" Windows applies to All PC audio, small
///    timing offsets, and a mic heard back through the headset.
/// The seconds are blended with overlapping fades, so nothing clicks.
/// </summary>
public static class Residual
{
    /// <summary>Changes when the method does: cached results from older versions are worked out again.</summary>
    public const int Version = 2;

    private const int Rate = 48000;
    private const int Block = 48000;       // tracks are followed and matched second by second
    private const int Search = 12000;      // up to 250 ms apart
    private const int Refine = 6;
    private const int Nf = 2048;           // frequency-domain frames (43 ms)
    private const int Hop = Nf / 2;
    private const int Bins = Nf / 2 + 1;
    private const int BandWidth = 4;       // bins matched together (about 94 Hz)
    private const int Bands = (Bins + BandWidth - 1) / BandWidth;

    public sealed record Result(bool Significant, double EnergyShare, int ActiveSeconds);

    // Each track's sound is read straight from its decoded file (mapped into
    // memory, so Windows pages it in and out as needed) rather than copied into
    // arrays first. A 10-minute clip with nine tracks held about 2 GB here
    // (and Persocord kept that memory after the editor closed); now the
    // tracks take no memory of Persocord's own.
    private sealed unsafe class Sig : IDisposable
    {
        private readonly System.IO.MemoryMappedFiles.MemoryMappedFile? _map;
        private readonly System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _view;
        private readonly short* _p;
        private bool _held;
        public readonly int Length;

        public Sig(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long frames = fs.Length / 4;
            if (frames <= 0) { fs.Dispose(); return; }
            Length = (int)Math.Min(int.MaxValue, frames);
            _map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(fs, null, 0,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            _view = _map.CreateViewAccessor(0, Length * 4L, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* b = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
            _held = true;
            _p = (short*)(b + _view.PointerOffset);
        }

        public short L(int t) => _p[2 * t];
        public short R(int t) => _p[2 * t + 1];

        /// <summary>The mono mix, worked out exactly as it was when it was kept in memory.</summary>
        public float Mono(int t)
        {
            float l = _p[2 * t] / 32768f, r = _p[2 * t + 1] / 32768f;
            return (l + r) * 0.5f;
        }

        public void Dispose()
        {
            if (_held) { _view!.SafeMemoryMappedViewHandle.ReleasePointer(); _held = false; }
            _view?.Dispose();
            _map?.Dispose();
        }
    }

    /// <param name="pcPcm">All PC audio, decoded (48 kHz, 16-bit, stereo).</param>
    /// <param name="partPcms">Every other track, decoded the same way.</param>
    /// <param name="outWav">Where the Unknown track goes.</param>
    public static Result Compute(string pcPcm, IReadOnlyList<string> partPcms, string outWav, CancellationToken ct)
    {
        var opened = new List<Sig>();
        try
        {
            var pc = new Sig(pcPcm);
            opened.Add(pc);
            var parts = new List<Sig>();
            foreach (var path in partPcms) { var sig = new Sig(path); opened.Add(sig); parts.Add(sig); }
            return Compute(pc, parts, outWav, ct);
        }
        finally { foreach (var sig in opened) sig.Dispose(); }
    }

    private static Result Compute(Sig pc, List<Sig> parts, string outWav, CancellationToken ct)
    {
        int frames = pc.Length;
        int blocks = Math.Max(1, (frames + Block - 1) / Block);
        int k = parts.Count;

        var lags = TrackLags(pc, parts, blocks, ct);

        // ---- frequency-domain matching, block by block --------------------------
        var win = new double[Nf];
        for (int i = 0; i < Nf; i++) win[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Nf));   // sqrt-Hann, periodic

        int frameCount = (frames + Nf / 2) / Hop + 1;
        // The windows' overlap at a sample, worked out when needed (it was a
        // third array as long as the clip): the same sum, in the same order.
        double W(int t)
        {
            int fLo = Math.Max(0, (t - Nf / 2) / Hop - 1), fHi = Math.Min(frameCount - 1, (t + Nf / 2) / Hop);
            double sum = 0;
            for (int f = fLo; f <= fHi; f++)
            {
                int n = t - (f * Hop - Nf / 2);
                if ((uint)n < (uint)Nf) sum += win[n] * win[n];
            }
            return sum;
        }
        // Frames of each block: centre f*Hop falls in the block.
        var blockFrames = new List<int>[blocks];
        for (int b = 0; b < blocks; b++) blockFrames[b] = new List<int>();
        for (int f = 0; f < frameCount; f++) blockFrames[Math.Min(blocks - 1, f * Hop / Block)].Add(f);

        var spectra = new Dictionary<int, Spectra[]>();   // block → its frames' spectra (three kept at once)
        var stats = new Dictionary<int, Stats>();

        // The leftover is summed in a short window that moves along the clip:
        // once no later frame can reach a sample, it's finished, written out and
        // measured. (It used to be two arrays as long as the clip, 460 MB for ten
        // minutes.) The sums, their order and every result are as before.
        int span = 3 * Block + 4 * Nf;
        var accL = new double[span];
        var accR = new double[span];
        int bufStart = 0;         // the clip sample at accL[0]
        int finished = 0;         // samples before this are written
        double resEnergy = 0, pcEnergy = 0, pe = 0, re = 0;
        int active = 0;

        using var wav = new WavWriter(outWav, frames);

        void Finish(int upTo)
        {
            for (int t = finished; t < upTo; t++)
            {
                double ws = W(t);
                double w = ws > 1e-6 ? ws : 1;
                double l = (float)(accL[t - bufStart] / w);
                double r = (float)(accR[t - bufStart] / w);
                wav.Write(l, r);
                // How much is really left: compared with All PC audio, over the
                // whole seconds that have sound.
                int sec = t / Rate;
                if ((sec + 1) * Rate <= frames)
                {
                    float pm = pc.Mono(t);
                    pe += pm * (double)pm;
                    double m = (float)((float)l + (float)r) * 0.5;
                    re += m * m;
                    if (t == (sec + 1) * Rate - 1)
                    {
                        if (pe / Rate >= 1e-6)   // below about -60 dB: nothing playing
                        {
                            pcEnergy += pe;
                            resEnergy += re;
                            if (re / pe > 0.25) active++;   // a second where the leftover really is a sound of its own
                        }
                        pe = re = 0;
                    }
                }
            }
            if (upTo <= finished) return;
            // What's still open moves to the front of the window.
            int keep = Math.Max(0, Math.Min(span, bufStart + span - upTo));
            Array.Copy(accL, upTo - bufStart, accL, 0, keep);
            Array.Copy(accR, upTo - bufStart, accR, 0, keep);
            Array.Clear(accL, keep, span - keep);
            Array.Clear(accR, keep, span - keep);
            bufStart = upTo;
            finished = upTo;
        }

        for (int b = 0; b <= blocks; b++)
        {
            ct.ThrowIfCancellationRequested();
            if (b < blocks)
            {
                var fs = blockFrames[b];
                var sp = new Spectra[fs.Count];
                Parallel.For(0, fs.Count, i => sp[i] = Analyse(pc, parts, lags, b, fs[i], win));
                spectra[b] = sp;
                stats[b] = Accumulate(sp, k);
            }
            int done = b - 1;   // the block now complete with its neighbours
            if (done < 0) continue;
            var h = Solve(stats.GetValueOrDefault(done - 1), stats[done], stats.GetValueOrDefault(done + 1), k);
            var spd = spectra[done];
            var outFrames = new double[spd.Length][];
            Parallel.For(0, spd.Length, i => outFrames[i] = Synthesise(spd[i], h, k));
            var fsd = blockFrames[done];
            for (int i = 0; i < fsd.Count; i++)
            {
                int s0 = fsd[i] * Hop - Nf / 2;
                var o = outFrames[i];
                for (int n = 0; n < Nf; n++)
                {
                    int t = s0 + n;
                    if ((uint)t >= (uint)frames) continue;
                    if (t - bufStart >= span) throw new InvalidOperationException("leftover window overrun");
                    accL[t - bufStart] += o[2 * n] * win[n];
                    accR[t - bufStart] += o[2 * n + 1] * win[n];
                }
            }
            spectra.Remove(done);
            stats.Remove(done - 1);

            // Everything before the next unprocessed frame's reach is final.
            int next = -1;
            for (int nb = done + 1; nb < blocks && next < 0; nb++)
                if (blockFrames[nb].Count > 0) next = blockFrames[nb][0];
            Finish(next < 0 ? frames : Math.Clamp(next * Hop - Nf / 2, finished, frames));
        }
        Finish(frames);
        wav.Complete();

        double share = pcEnergy > 0 ? resEnergy / pcEnergy : 0;
        // Leftovers of the matched tracks come to well under a tenth of All PC's
        // energy; real missing sound (a game) is far more.
        return new Result(share > 0.12 && active >= 3, share, active);
    }

    /* ---- lining up in time --------------------------------------------------- */

    /// <summary>Each track's offset from All PC audio (samples), block by block.</summary>
    private static int[][] TrackLags(Sig pc, List<Sig> parts, int blocks, CancellationToken ct)
    {
        var all = new int[parts.Count][];
        Parallel.For(0, parts.Count, p =>
        {
            var m = parts[p];
            var lag = new int[blocks];
            int cur = CoarseLag(pc, m);
            double typical = 0;
            for (int b = 0; b < blocks; b++)
            {
                if (ct.IsCancellationRequested) break;
                int a = b * Block, e = Math.Min(pc.Length, a + Block);
                if (Energy(m, a - cur, e - cur) < 1e-6 * (e - a)) { lag[b] = cur; continue; }   // silent here
                var (best, score) = Best(pc, m, a, e, cur - Refine, cur + Refine, 1, 1);
                // Stopped matching (Windows dropped or added a moment): look further.
                if (typical > 0.2 && score < 0.5 * typical)
                {
                    var (wide, ws) = Best(pc, m, a, e, cur - Search, cur + Search, 8, 4);
                    (wide, ws) = Best(pc, m, a, e, wide - 8, wide + 8, 1, 1);
                    if (ws > score * 1.25) { best = wide; score = ws; }
                }
                cur = best;
                lag[b] = cur;
                typical = typical == 0 ? score : typical * 0.8 + score * 0.2;
            }
            all[p] = lag;
        });
        return all;
    }

    private static (int Lag, double Score) Best(Sig pc, Sig part, int a, int b, int from, int to, int lagStep, int step)
    {
        int best = from;
        double bestScore = double.MinValue;
        for (int d = from; d <= to; d += lagStep)
        {
            double sc = Correlation(pc, part, a, b, d, step);
            if (sc > bestScore) { bestScore = sc; best = d; }
        }
        return (best, bestScore);
    }

    /// <summary>The offset (samples) at which a track best matches All PC audio, from its loudest seconds.</summary>
    private static int CoarseLag(Sig pc, Sig part)
    {
        int seconds = part.Length / Rate;
        var loud = Enumerable.Range(0, seconds)
            .Select(i => (i, e: Energy(part, i * Rate, (i + 1) * Rate)))
            .Where(x => x.e > 1e-5 * Rate)
            .OrderByDescending(x => x.e).Take(6).Select(x => x.i).ToList();
        if (loud.Count == 0) return 0;
        var found = new List<int>();
        foreach (var i in loud)
        {
            int a = i * Rate, b = Math.Min(pc.Length, (i + 1) * Rate);
            if (b - a < Rate / 4) continue;
            var (coarse, _) = Best(pc, part, a, b, -Search, Search, 8, 4);
            var (best, _) = Best(pc, part, a, b, coarse - 8, coarse + 8, 1, 1);
            found.Add(best);
        }
        if (found.Count == 0) return 0;
        found.Sort();
        return found[found.Count / 2];
    }

    /// <summary>How alike All PC audio and a track are over [a, b) with the track moved by d samples (normalised).</summary>
    private static double Correlation(Sig pc, Sig part, int a, int b, int d, int step = 1)
    {
        double pp = 0, xx = 0, px = 0;
        int lo = Math.Max(a, d), hi = Math.Min(b, part.Length + d);
        for (int t = lo; t < hi; t += step)
        {
            double p = pc.Mono(t), x = part.Mono(t - d);
            pp += p * p; xx += x * x; px += p * x;
        }
        return pp > 0 && xx > 0 ? px / Math.Sqrt(pp * xx) : 0;
    }

    private static double Energy(Sig x, int a, int b)
    {
        double s = 0;
        for (int t = Math.Max(0, a); t < Math.Min(x.Length, b); t++) { float v = x.Mono(t); s += v * (double)v; }
        return s;
    }

    /* ---- matching in loudness and tone ----------------------------------------- */

    /// <summary>One frame's spectra (bins 0..N/2, left and right): All PC audio, then each track.</summary>
    private sealed class Spectra
    {
        public Complex[] PL = null!, PR = null!;
        public Complex[][] XL = null!, XR = null!;
    }

    private static Spectra Analyse(Sig pc, List<Sig> parts, int[][] lags, int block, int frame, double[] win)
    {
        int s0 = frame * Hop - Nf / 2;
        var sp = new Spectra { XL = new Complex[parts.Count][], XR = new Complex[parts.Count][] };
        (sp.PL, sp.PR) = StereoFft(pc, s0, win);
        for (int p = 0; p < parts.Count; p++)
            (sp.XL[p], sp.XR[p]) = StereoFft(parts[p], s0 - lags[p][block], win);
        return sp;
    }

    /// <summary>Per band: R = Σ x xᴴ and r = Σ x p* over a block's frames (left and right pooled).</summary>
    private sealed class Stats
    {
        public Complex[][,] R = null!;
        public Complex[][] r = null!;
    }

    private static Stats Accumulate(Spectra[] frames, int k)
    {
        var st = new Stats { R = new Complex[Bands][,], r = new Complex[Bands][] };
        Parallel.For(0, Bands, band =>
        {
            var R = new Complex[k, k];
            var r = new Complex[k];
            int lo = band * BandWidth, hi = Math.Min(Bins, lo + BandWidth);
            foreach (var f in frames)
                for (int bin = lo; bin < hi; bin++)
                    for (int ch = 0; ch < 2; ch++)
                    {
                        var X = ch == 0 ? f.XL : f.XR;
                        var P = ch == 0 ? f.PL[bin] : f.PR[bin];
                        for (int i = 0; i < k; i++)
                        {
                            var xi = X[i][bin];
                            r[i] += Complex.Conjugate(xi) * P;
                            for (int j = 0; j < k; j++) R[i, j] += Complex.Conjugate(xi) * X[j][bin];
                        }
                    }
            st.R[band] = R;
            st.r[band] = r;
        });
        return st;
    }

    /// <summary>The best gain and tone for each track, per band, from a block and half of each neighbour.</summary>
    private static Complex[][] Solve(Stats? before, Stats now, Stats? after, int k)
    {
        var h = new Complex[Bands][];
        for (int band = 0; band < Bands; band++)
        {
            var R = new Complex[k, k];
            var r = new Complex[k];
            void Add(Stats? s, double w)
            {
                if (s == null) return;
                for (int i = 0; i < k; i++)
                {
                    r[i] += w * s.r[band][i];
                    for (int j = 0; j < k; j++) R[i, j] += w * s.R[band][i, j];
                }
            }
            Add(before, 0.5); Add(now, 1); Add(after, 0.5);
            double trace = 0;
            for (int i = 0; i < k; i++) trace += R[i, i].Real;
            // A touch of loading keeps silent or near-identical tracks from blowing up.
            for (int i = 0; i < k; i++) R[i, i] += 1e-3 * R[i, i].Real + 1e-9 * (trace / Math.Max(1, k)) + 1e-12;
            h[band] = SolveLinear(R, r, k);
        }
        return h;
    }

    private static Complex[] SolveLinear(Complex[,] A, Complex[] b, int n)
    {
        var a = (Complex[,])A.Clone();
        var x = (Complex[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int i = c + 1; i < n; i++) if (a[i, c].Magnitude > a[piv, c].Magnitude) piv = i;
            if (a[piv, c].Magnitude < 1e-30) continue;
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int i = c + 1; i < n; i++)
            {
                var f = a[i, c] / a[c, c];
                if (f == Complex.Zero) continue;
                for (int j = c; j < n; j++) a[i, j] -= f * a[c, j];
                x[i] -= f * x[c];
            }
        }
        var res = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var s = x[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * res[j];
            res[i] = a[i, i].Magnitude < 1e-30 ? Complex.Zero : s / a[i, i];
        }
        return res;
    }

    /// <summary>All PC audio minus every matched track, back to samples (interleaved left/right, before the window).</summary>
    private static double[] Synthesise(Spectra f, Complex[][] h, int k)
    {
        var z = new Complex[Nf];
        for (int bin = 0; bin < Bins; bin++)
        {
            var hb = h[bin / BandWidth];
            Complex l = f.PL[bin], r = f.PR[bin];
            for (int i = 0; i < k; i++) { l -= hb[i] * f.XL[i][bin]; r -= hb[i] * f.XR[i][bin]; }
            // Two real signals in one transform: left + i·right.
            z[bin] = l + Complex.ImaginaryOne * r;
            if (bin > 0 && bin < Nf / 2)
                z[Nf - bin] = Complex.Conjugate(l) + Complex.ImaginaryOne * Complex.Conjugate(r);
        }
        Fft(z, inverse: true);
        var o = new double[2 * Nf];
        for (int n = 0; n < Nf; n++) { o[2 * n] = z[n].Real / Nf; o[2 * n + 1] = z[n].Imaginary / Nf; }
        return o;
    }

    /// <summary>A windowed frame of a stereo signal, both channels in one transform.</summary>
    private static (Complex[] L, Complex[] R) StereoFft(Sig s, int start, double[] win)
    {
        var z = new Complex[Nf];
        for (int n = 0; n < Nf; n++)
        {
            int t = start + n;
            if ((uint)t < (uint)s.Length) z[n] = new Complex((s.L(t) / 32768f) * win[n], (s.R(t) / 32768f) * win[n]);
        }
        Fft(z, inverse: false);
        var L = new Complex[Bins];
        var R = new Complex[Bins];
        for (int b = 0; b < Bins; b++)
        {
            var a = z[b];
            var c = Complex.Conjugate(z[(Nf - b) % Nf]);
            L[b] = (a + c) * 0.5;
            R[b] = (a - c) * new Complex(0, -0.5);
        }
        return (L, R);
    }

    private static readonly Complex[] Twiddles = MakeTwiddles();
    private static Complex[] MakeTwiddles()
    {
        var t = new Complex[Nf / 2];
        for (int i = 0; i < Nf / 2; i++) t[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / Nf);
        return t;
    }

    private static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int step = n / len, half = len / 2;
            for (int i = 0; i < n; i += len)
                for (int j = 0; j < half; j++)
                {
                    var w = Twiddles[j * step];
                    if (inverse) w = Complex.Conjugate(w);
                    var u = a[i + j];
                    var v = a[i + j + half] * w;
                    a[i + j] = u + v;
                    a[i + j + half] = u - v;
                }
        }
    }

    /* ---- files --------------------------------------------------------------- */

    /// <summary>The Unknown track, written a piece at a time as it's finished (16-bit stereo WAV).</summary>
    private sealed class WavWriter : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _fs;
        private readonly byte[] _buf = new byte[65536];
        private int _pos;
        private bool _complete;

        public WavWriter(string path, int frames)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _fs = new FileStream(path + ".part", FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(_fs, System.Text.Encoding.UTF8, leaveOpen: true);
            int data = frames * 4;
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
        }

        public void Write(double l, double r)
        {
            short sl = (short)Math.Clamp((float)l * 32767f, -32768f, 32767f);
            short sr = (short)Math.Clamp((float)r * 32767f, -32768f, 32767f);
            _buf[_pos++] = (byte)sl; _buf[_pos++] = (byte)(sl >> 8);
            _buf[_pos++] = (byte)sr; _buf[_pos++] = (byte)(sr >> 8);
            if (_pos == _buf.Length) { _fs.Write(_buf, 0, _pos); _pos = 0; }
        }

        /// <summary>All written: the file takes its real name.</summary>
        public void Complete()
        {
            if (_pos > 0) _fs.Write(_buf, 0, _pos);
            _pos = 0;
            _fs.Flush();
            _fs.Close();
            File.Move(_path + ".part", _path, true);
            _complete = true;
        }

        /// <summary>Stopped part way (cancelled): the half-written file goes.</summary>
        public void Dispose()
        {
            if (_complete) return;
            try { _fs.Dispose(); } catch { }
            try { File.Delete(_path + ".part"); } catch { }
        }
    }
}

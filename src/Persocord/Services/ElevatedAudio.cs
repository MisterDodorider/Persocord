using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Persocord.Services;

/// <summary>
/// Sound tracks for apps running as administrator. Windows won't let a normal
/// app record an administrator app's sound (it just hears silence), so for
/// those Persocord starts a small helper with administrator approval: itself,
/// in a special mode, with no window. It does one thing: record the sound of
/// the apps Persocord names, and send it straight back over a private pipe.
///
/// Approval is asked at most once per Persocord session, and only when such an
/// app actually plays sound. Declined, Persocord doesn't ask again that session
/// and the editor's Unknown track covers it instead. The helper only talks to
/// the Persocord that started it (checked by process and file), writes nothing
/// to disk, and closes when Persocord does.
/// </summary>
public static class ElevatedAudio
{
    public const string HelperArg = "--audio-helper";
    private const string DataPrefix = "Persocord.AudioData.";

    /// <summary>Allowed by the settings (Settings → Audio).</summary>
    public static volatile bool Enabled = true;

    /// <summary>The helper is running and connected.</summary>
    public static bool Running => _control is { IsConnected: true };

    /// <summary>Asked this session and turned down (or it couldn't start): not asked again.</summary>
    public static bool Declined { get; private set; }

    /// <summary>About to ask Windows for approval (so the person knows why the prompt appears).</summary>
    public static event Action<string>? Asking;

    private static NamedPipeServerStream? _control;
    private static bool _asked;
    private static StreamWriter? _commands;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /* ---- in Persocord --------------------------------------------------- */

    /// <summary>
    /// A capture of an administrator app's sound through the helper (starting
    /// it, with approval, if needed). Null when it can't be done: the normal
    /// capture is used instead, and the Unknown track covers what it misses.
    /// </summary>
    public static async Task<IWaveIn?> CaptureAsync(int pid, string appName)
    {
        if (!Enabled || Declined || AppAudio.SelfElevated) return null;
        if (!await EnsureHelperAsync(appName)) return null;

        var name = DataPrefix + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                             PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1 << 16, 1 << 16);
        try
        {
            await Gate.WaitAsync();
            try { await _commands!.WriteLineAsync($"capture {pid} {name}"); await _commands.FlushAsync(); }
            finally { Gate.Release(); }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await pipe.WaitForConnectionAsync(wait.Token);
            Log.Write($"{appName}: recording its sound through the administrator helper.");
            return new PipedCapture(pipe);
        }
        catch (Exception ex)
        {
            Log.Error($"administrator sound for {appName}", ex);
            pipe.Dispose();
            return null;
        }
    }

    private static async Task<bool> EnsureHelperAsync(string appName)
    {
        if (Running) return true;
        await Gate.WaitAsync();
        try
        {
            if (Running) return true;
            // Asked once per session at most: if the helper has since closed
            // (it shouldn't), the Unknown track covers the rest.
            if (Declined || _asked) return false;
            _asked = true;
            _control?.Dispose();
            var name = "Persocord.AudioHelper." + Guid.NewGuid().ToString("N");
            var control = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Asking?.Invoke(appName);
            Process? helper;
            try
            {
                helper = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = $"{HelperArg} {name} {Environment.ProcessId}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log.Write("Administrator sound helper: not allowed (approval declined). Not asking again this session.");
                Declined = true;
                control.Dispose();
                return false;
            }
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                // Gives up early if the helper closes without connecting
                // (approved as a different Windows account, say).
                if (helper != null)
                    _ = helper.WaitForExitAsync(wait.Token).ContinueWith(t => { if (t.IsCompletedSuccessfully) try { wait.Cancel(); } catch { } });
                try { await control.WaitForConnectionAsync(wait.Token); }
                catch (OperationCanceledException)
                {
                    Log.Write("Administrator sound helper didn't connect; not asking again this session.");
                    Declined = true;
                    control.Dispose();
                    return false;
                }
            }
            _control = control;
            _commands = new StreamWriter(control) { AutoFlush = false };
            var reports = new StreamReader(control);
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reports.ReadLineAsync()) != null) Log.Write("Administrator sound helper: " + line);
                }
                catch { }
            });
            Log.Write("Administrator sound helper started.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("administrator sound helper", ex);
            Declined = true;
            return false;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Sound arriving from the helper, as an ordinary capture (48 kHz, 16-bit, stereo).</summary>
    private sealed class PipedCapture : IWaveIn
    {
        private readonly NamedPipeServerStream _pipe;
        private volatile bool _stopped;
        public PipedCapture(NamedPipeServerStream pipe) => _pipe = pipe;

        public WaveFormat WaveFormat { get; set; } = new WaveFormat(48000, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording()
        {
            new Thread(() =>
            {
                Exception? error = null;
                var buf = new byte[19200];   // 100 ms
                int held = 0;                // bytes of an unfinished sample kept for the next read
                try
                {
                    while (!_stopped)
                    {
                        int n = _pipe.Read(buf, held, buf.Length - held);
                        if (n <= 0) break;
                        int total = held + n, whole = total - total % 4;   // whole stereo 16-bit samples only
                        if (whole > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buf, whole));
                        held = total - whole;
                        if (held > 0) Buffer.BlockCopy(buf, whole, buf, 0, held);
                    }
                }
                catch (Exception ex) { if (!_stopped) error = ex; }
                RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
            }) { IsBackground = true, Name = "Persocord administrator app sound", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public void StopRecording()
        {
            _stopped = true;
            try { _pipe.Dispose(); } catch { }
        }

        public void Dispose() => StopRecording();
    }

    /* ---- the helper ---------------------------------------------------------- */

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    /// <summary>Runs the helper (started by Persocord with administrator approval). No window, no files.</summary>
    public static int RunHelper(string[] args) =>
        // Off the program's first thread: Windows' sound objects are made and
        // used from the shared (multi-threaded) pool, as in Persocord itself.
        Task.Run(() => Helper(args)).GetAwaiter().GetResult();

    private static int Helper(string[] args)
    {
        try
        {
            if (args.Length < 3 || !Regex.IsMatch(args[1], @"^Persocord\.AudioHelper\.[0-9a-f]{32}$") ||
                !int.TryParse(args[2], out int parentPid)) return 2;
            var self = Environment.ProcessPath ?? "";

            using var control = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            control.Connect(10000);
            if (!FromPersocord(control, parentPid, self)) return 3;

            var report = new StreamWriter(control) { AutoFlush = true };
            var captures = new Dictionary<int, (ProcessLoopbackCapture Capture, NamedPipeClientStream Pipe)>();
            var gone = new CancellationTokenSource();
            // Closes when Persocord does.
            _ = Task.Run(() =>
            {
                try { using var p = Process.GetProcessById(parentPid); p.WaitForExit(); } catch { }
                gone.Cancel();
                try { control.Dispose(); } catch { }
            });

            using var reader = new StreamReader(control);
            string? line;
            while (!gone.IsCancellationRequested && (line = reader.ReadLine()) != null)
            {
                var m = Regex.Match(line, @"^capture (\d{1,7}) (Persocord\.AudioData\.[0-9a-f]{32})$");
                if (!m.Success) continue;
                int pid = int.Parse(m.Groups[1].Value);
                string pipeName = m.Groups[2].Value;
                try
                {
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    pipe.Connect(5000);
                    if (!FromPersocord(pipe, parentPid, self)) { pipe.Dispose(); continue; }
                    var capture = ProcessLoopbackCapture.IncludingAsync(pid, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    capture.DataAvailable += (_, e) =>
                    {
                        try { pipe.Write(e.Buffer, 0, e.BytesRecorded); }
                        catch
                        {
                            // Persocord stopped listening (the app's track was freed).
                            try { capture.StopRecording(); } catch { }
                        }
                    };
                    capture.StartRecording();
                    lock (captures)
                    {
                        if (captures.TryGetValue(pid, out var old)) { try { old.Capture.StopRecording(); old.Pipe.Dispose(); } catch { } }
                        captures[pid] = (capture, pipe);
                    }
                    report.WriteLine($"recording app {pid}");
                }
                catch (Exception ex)
                {
                    try { report.WriteLine($"couldn't record app {pid}: {ex.Message}"); } catch { }
                }
            }
            lock (captures)
                foreach (var c in captures.Values) { try { c.Capture.StopRecording(); c.Pipe.Dispose(); } catch { } }
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>The other end of a pipe is the Persocord that started this helper (same process, same program file).</summary>
    private static bool FromPersocord(PipeStream pipe, int parentPid, string selfPath)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint server) || server != parentPid) return false;
            var path = GameDetector.ProcessPath(parentPid);
            return path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

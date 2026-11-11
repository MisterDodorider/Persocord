using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Persocord.Services;

/// <summary>
/// Captures what every app is playing except one process tree — here,
/// Persocord itself, so its cue sounds never reach a recording.
///
/// Windows has offered this since Windows 11 and late Windows 10 builds,
/// through a special virtual device. NAudio 2.2 does not wrap it, so this
/// talks to Windows directly. It presents the same shape as NAudio's
/// captures, so the rest of the audio code treats it like any other device.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int ModeIncludeTargetTree = 0;
    private const int ModeExcludeTargetTree = 1;

    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint BufferFlagsSilent = 0x2;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly IAudioClient _client;
    private IAudioCaptureClient? _capture;
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private Thread? _thread;
    private volatile bool _running;

    public WaveFormat WaveFormat { get; set; } = new WaveFormat(48000, 16, 2);

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private ProcessLoopbackCapture(IAudioClient client) => _client = client;

    /// <summary>
    /// Opens a capture of everything except the given process and its
    /// children. Throws if this Windows does not support it.
    /// </summary>
    /// <summary>Captures only what one app (and anything it launched) plays.</summary>
    public static Task<ProcessLoopbackCapture> IncludingAsync(int processId, TimeSpan timeout) =>
        ActivateAsync(processId, include: true, timeout);

    /// <summary>Captures everything except what one app (and anything it launched) plays.</summary>
    public static Task<ProcessLoopbackCapture> ExcludingAsync(int processId, TimeSpan timeout) =>
        ActivateAsync(processId, include: false, timeout);

    private static async Task<ProcessLoopbackCapture> ActivateAsync(int processId, bool include, TimeSpan timeout)
    {
        var parameters = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = include ? ModeIncludeTargetTree : ModeExcludeTargetTree,
        };

        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                vt = 65,   // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<ActivationParams>(),
                pBlobData = blob,
            }, variant, false);

            // Asked from a pool thread (multi-threaded in COM terms), with a
            // handler Windows can see is agile (see IAgileObject below).
            var handler = new CompletionHandler();
            IActivateAudioInterfaceAsyncOperation? op = null;
            await Task.Run(() => ActivateAudioInterfaceAsync(VirtualDevice, IidAudioClient, variant, handler, out op));

            var done = await Task.WhenAny(handler.Result, Task.Delay(timeout));
            if (done != handler.Result)
            {
                // An answer that comes after giving up is let go of when it arrives.
                _ = handler.Result.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion) try { Marshal.ReleaseComObject(t.Result); } catch { }
                    Release(op);
                }, TaskScheduler.Default);
                throw new TimeoutException("Windows did not answer the capture request.");
            }

            // The request object holds on to what it made: released now, not
            // whenever the garbage collector gets round to it.
            Release(op);
            return new ProcessLoopbackCapture(await handler.Result);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void StartRecording()
    {
        if (_running) return;

        var f = WaveFormat;
        var wfx = new WaveFormatEx
        {
            wFormatTag = 1,   // PCM
            nChannels = (ushort)f.Channels,
            nSamplesPerSec = (uint)f.SampleRate,
            wBitsPerSample = (ushort)f.BitsPerSample,
            nBlockAlign = (ushort)(f.Channels * f.BitsPerSample / 8),
        };
        wfx.nAvgBytesPerSec = wfx.nSamplesPerSec * wfx.nBlockAlign;

        Check(_client.Initialize(0,
            StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm,
            200000, 0, ref wfx, IntPtr.Zero), "Initialize");

        Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");

        var iid = IidAudioCaptureClient;
        Check(_client.GetService(ref iid, out var service), "GetService");
        _capture = (IAudioCaptureClient)service;

        Check(_client.Start(), "Start");
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Persocord process loopback" };
        _thread.Start();
    }

    private void Loop()
    {
        Exception? error = null;
        int blockAlign = WaveFormat.BlockAlign;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond];

        try
        {
            while (_running)
            {
                _ready.WaitOne(100);
                if (!_running) break;

                while (_running && _capture!.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) != 0)
                        break;

                    int bytes = (int)frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];

                    if ((flags & BufferFlagsSilent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);

                    _capture.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                }
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            try { _client.Stop(); } catch { }
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    public void StopRecording()
    {
        _running = false;
        _ready.Set();
        try { _thread?.Join(1000); } catch { }
    }

    public void Dispose()
    {
        StopRecording();
        try { if (_capture != null) Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _ready.Dispose();
    }

    private static void Release(object? com)
    {
        try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new COMException($"Process loopback {step} failed (0x{hr:X8}).", hr);
    }

    /* ---- Windows declarations ------------------------------------------- */

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a blob, laid out for 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // ComImport, not ComVisible: .NET only shows Windows the interfaces of a
    // private type when they're imported COM interfaces. With ComVisible,
    // Windows asked the handler "are you agile?", got no answer, and refused
    // every request with 0x8000000E ("called at an unexpected time").
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Windows insists the completion handler can be called from any thread.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Result => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object iface);
                if (hr < 0) _tcs.TrySetException(new COMException($"Activation failed (0x{hr:X8}).", hr));
                else _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
            finally { Release(op); }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

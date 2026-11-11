using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Persocord.Views;

/// <summary>
/// A hover preview on VLC's engine that never opens a window: VLC decodes
/// (on the graphics card) and hands each frame over already scaled, and it's
/// drawn into an ordinary image. Loops from the given start, with sound.
/// </summary>
public sealed class HoverVlc : IDisposable
{
    private readonly VlcPlayer _mp;
    private readonly VlcMedia _media;
    private readonly WriteableBitmap _bitmap;
    private readonly int _w, _h, _stride;
    private readonly IntPtr _buffer;
    private readonly byte[] _frame;
    private readonly object _lock = new();
    private int _pending, _disposed;

    // Kept in fields so the garbage collector never frees them while VLC calls them.
    private readonly VlcPlayer.LibVLCVideoLockCb _lockCb;
    private readonly VlcPlayer.LibVLCVideoDisplayCb _displayCb;

    public HoverVlc(LibVLC vlc, string path, double start, int width, int height, Image target, int volume)
    {
        _w = width;
        _h = height;
        _stride = width * 4;
        _buffer = Marshal.AllocHGlobal(_stride * _h);
        _frame = new byte[_stride * _h];
        _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);
        target.Source = _bitmap;

        _mp = new VlcPlayer(vlc) { EnableHardwareDecoding = true, Volume = volume };
        Persocord.Services.Health.VlcOpened();
        // Undoes a mute left by an earlier version (shared by all of Persocord's sound).
        try { _mp.Mute = false; } catch { }
        _lockCb = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        _displayCb = (_, _) => OnFrame();
        _mp.SetVideoFormat("RV32", (uint)_w, (uint)_h, (uint)_stride);
        _mp.SetVideoCallbacks(_lockCb, null, _displayCb);

        _media = new VlcMedia(vlc, new Uri(path),
            ":input-repeat=65535",
            ":start-time=" + start.ToString("0.###", CultureInfo.InvariantCulture),
            ":no-video-title-show");
        _mp.Play(_media);
    }

    /// <summary>A frame is ready: copied out, then drawn (skipping any the screen can't keep up with).</summary>
    private void OnFrame()
    {
        if (_disposed != 0) return;
        lock (_lock) Marshal.Copy(_buffer, _frame, 0, _frame.Length);
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _bitmap.Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            if (_disposed != 0) return;
            lock (_lock) _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), _frame, _stride, 0);
        }));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        var mp = _mp;
        var media = _media;
        var buffer = _buffer;
        // Silent and paused at once (instant commands that can't stall): VLC's
        // stop can take a while, and a preview left looping its sound in the
        // meantime kept playing after its window had closed. Its sound track is
        // switched off and it pauses: never mute or volume, which in VLC set
        // the volume of ALL of Persocord's sound (one shared Windows session),
        // and silenced every later preview and the clip player.
        try { mp.SetAudioTrack(-1); mp.SetPause(true); } catch { }
        // VLC stops off the UI thread; the frame memory goes once it has.
        Persocord.Services.Health.VlcClosing();
        Task.Run(() =>
        {
            // The frame memory is VLC's to write into until it has stopped: if
            // stopping failed, it's left allocated (a few MB) rather than freed
            // under a decoder that may still be writing to it.
            bool stopped = false;
            try { mp.Stop(); stopped = true; } catch (Exception ex) { Persocord.Services.Log.Error("hover preview stop", ex); }
            try { mp.Dispose(); media.Dispose(); } catch { }
            if (stopped) Marshal.FreeHGlobal(buffer);
            Persocord.Services.Health.VlcClosed();
            // Its callbacks stay reachable until VLC has let go of them.
            GC.KeepAlive(this);
        });
    }
}

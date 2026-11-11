using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Persocord.Services;

/// <summary>
/// Persocord's own sound outputs (the editor and the preview players) use
/// WASAPI, Windows' modern audio path, as VLC does: it starts and stops
/// cleanly. The older waveOut path could leave some headset drivers looping
/// their last buffers after a stop. waveOut stays as a fallback.
/// </summary>
public static class SoundOut
{
    /// <summary>An output playing this sound: WASAPI, or waveOut if WASAPI can't be set up.</summary>
    public static IWavePlayer Open(IWaveProvider source, int latencyMs)
    {
        IWavePlayer? wasapi = null;
        try
        {
            wasapi = new WasapiOut(AudioClientShareMode.Shared, true, latencyMs);
            wasapi.Init(source);
            return wasapi;
        }
        catch (Exception ex)
        {
            try { wasapi?.Dispose(); } catch { }
            Log.Error("sound output (using waveOut instead)", ex);
            var wave = new WaveOutEvent { DesiredLatency = latencyMs * 2, NumberOfBuffers = 3 };
            wave.Init(source);
            return wave;
        }
    }

    public static IWavePlayer Open(ISampleProvider source, int latencyMs) =>
        Open(source.ToWaveProvider(), latencyMs);

    /// <summary>Seconds of sound the output has actually played since it started.</summary>
    public static double PlayedSeconds(IWavePlayer player)
    {
        try
        {
            if (player is IWavePosition pos && pos.OutputWaveFormat.AverageBytesPerSecond > 0)
                return pos.GetPosition() / (double)pos.OutputWaveFormat.AverageBytesPerSecond;
        }
        catch { }
        return 0;
    }
}

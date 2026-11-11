using System;
using System.Threading;

namespace Persocord;

/// <summary>How Persocord was asked to open.</summary>
public enum LaunchRequest { Default, Tray, Library, Record }

/// <summary>
/// Entry point. Runs before WPF loads, so when Persocord is already open a
/// second click on its icon hands over to the running copy and exits in a
/// few milliseconds instead of spinning up a whole UI framework first.
/// </summary>
public static class Program
{
    public const string LaunchSignal = "Persocord.Launch";
    public const string ShowSignal = "Persocord.ShowWindow";
    public const string RecordSignal = "Persocord.Record";
    public const string QuitSignal = "Persocord.Quit";

    [STAThread]
    public static int Main(string[] args)
    {
        // The administrator sound helper (started by a running Persocord):
        // no window, no single-copy check.
        if (args.Length > 0 && args[0] == Services.ElevatedAudio.HelperArg)
            return Services.ElevatedAudio.RunHelper(args);

        using var single = new Mutex(false, "Persocord.SingleInstance");
        bool first = TakeOver(single, args);

        if (!first)
        {
            var signal = RequestFrom(args) switch
            {
                LaunchRequest.Tray    => null,
                LaunchRequest.Library => ShowSignal,
                LaunchRequest.Record  => RecordSignal,
                _                     => LaunchSignal,
            };
            if (signal != null)
            {
                try
                {
                    using var handle = EventWaitHandle.OpenExisting(signal);
                    handle.Set();
                }
                catch { /* the running copy is still starting up */ }
            }
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Whether this copy gets to run. A normal launch only runs if nothing
    /// else is. A restart (from Settings) waits a few seconds for the old
    /// copy to finish closing instead of handing over to it.
    /// </summary>
    private static bool TakeOver(Mutex single, string[] args)
    {
        bool restarting = Array.Exists(args, a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase));
        try { return single.WaitOne(restarting ? 30000 : 0); }
        catch (AbandonedMutexException) { return true; }   // the old copy exited without letting go
    }

    public static LaunchRequest RequestFrom(string[] args)
    {
        foreach (var a in args)
        {
            if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase)) return LaunchRequest.Tray;
            if (a.Equals("--library", StringComparison.OrdinalIgnoreCase)) return LaunchRequest.Library;
            if (a.Equals("--record", StringComparison.OrdinalIgnoreCase)) return LaunchRequest.Record;
        }
        return LaunchRequest.Default;
    }
}

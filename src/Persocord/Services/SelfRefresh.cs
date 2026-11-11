using System;
using System.Diagnostics;

namespace Persocord.Services;

/// <summary>
/// Persocord's fresh start. Programs that only record while a game runs get
/// one every time a game closes; Persocord runs for days, so it gives itself
/// one: once it has been running 6 hours, an hour with nobody at the PC (no
/// keyboard, mouse or controller) restarts it. A quiet notice comes 10
/// minutes before; any input, or "Not now", puts it off for another hour.
/// It never happens in the middle of Persocord's own work (recording,
/// saving, the editor): it waits for that to finish. Always on.
/// </summary>
public sealed class SelfRefresh
{
    public static readonly TimeSpan MinUptime = TimeSpan.FromHours(6);
    public static readonly TimeSpan Away = TimeSpan.FromHours(1);
    public static readonly TimeSpan Notice = TimeSpan.FromMinutes(10);

    private readonly Func<string?> _busyWith;
    private readonly Action<bool> _warn;
    private readonly Action _restart;
    private readonly Stopwatch _up = Stopwatch.StartNew();
    private System.Windows.Threading.DispatcherTimer? _timer;
    private DateTime _notNowAt = DateTime.MinValue;
    private bool _warned, _restarting;
    private DateTime _warnedAt;
    private string? _waitingFor;

    /// <param name="busyWith">What Persocord is in the middle of, or null when it's free.</param>
    /// <param name="warn">Shows (true) or takes away (false) the 10-minute notice.</param>
    /// <param name="restart">Closes this copy and opens a fresh one.</param>
    public SelfRefresh(Func<string?> busyWith, Action<bool> warn, Action restart)
    {
        _busyWith = busyWith;
        _warn = warn;
        _restart = restart;
    }

    public void Start()
    {
        UserActivity.Start();
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => { try { Check(); } catch (Exception ex) { Log.Error("self-refresh", ex); } };
        _timer.Start();
    }

    /// <summary>"Not now" on the notice: the hour starts again from here.</summary>
    public void NotNow()
    {
        _notNowAt = DateTime.UtcNow;
        Log.Write("Refresh: put off for an hour (Not now)");
        Hide();
    }

    /// <summary>Nobody's input for this long (since "Not now", at most).</summary>
    public TimeSpan IdleFor
    {
        get
        {
            var idle = UserActivity.Idle;
            var sinceNotNow = DateTime.UtcNow - _notNowAt;
            return sinceNotNow < idle ? sinceNotNow : idle;
        }
    }

    public TimeSpan Uptime => _up.Elapsed;

    /// <summary>For the diagnostics report.</summary>
    public string Describe() =>
        $"up {(int)Uptime.TotalHours}h{Uptime.Minutes:00}m; no input for {Mins(IdleFor)} " +
        $"(keyboard and mouse {Mins(UserActivity.SinceKeyboardMouse)}, controller {Mins(UserActivity.SinceController)}); " +
        (Uptime < MinUptime ? $"can refresh after {(int)MinUptime.TotalHours} hours up" : $"refreshes after {(int)Away.TotalMinutes} min with no input") +
        (_waitingFor != null ? $"; waiting for: {_waitingFor}" : "");

    private static string Mins(TimeSpan t) => t.TotalMinutes < 1 ? "<1 min" : $"{(int)t.TotalMinutes} min";

    private void Check()
    {
        if (_restarting) return;
        var idle = IdleFor;
        // Back at the PC (or not up long enough): the notice goes, nothing else.
        if (Uptime < MinUptime || idle < Away - Notice)
        {
            if (_warned) Log.Write("Refresh: put off, someone's at the PC");
            Hide();
            _waitingFor = null;
            return;
        }
        if (!_warned)
        {
            _warned = true;
            _warnedAt = DateTime.UtcNow;
            Log.Write($"Refresh: no input for {(int)idle.TotalMinutes} min; refreshing in {(int)Notice.TotalMinutes} min unless someone's back");
            _warn(true);
        }
        // The notice always gets its full 10 minutes: after a long time away
        // (or reaching 6 hours up while nobody's there) it used to come and the
        // refresh straight after.
        if (idle < Away || DateTime.UtcNow - _warnedAt < Notice) return;

        var busy = _busyWith();
        if (busy != null)
        {
            if (busy != _waitingFor) Log.Write($"Refresh: waiting until Persocord is done ({busy})");
            _waitingFor = busy;
            return;
        }
        _restarting = true;
        Log.Write($"Refresh: restarting after {(int)Uptime.TotalHours} h up and {(int)idle.TotalMinutes} min with no input " +
                  $"(keyboard and mouse {Mins(UserActivity.SinceKeyboardMouse)}, controller {Mins(UserActivity.SinceController)})");
        _timer?.Stop();
        Hide();
        _restart();
    }

    private void Hide()
    {
        if (!_warned) return;
        _warned = false;
        _warn(false);
    }
}

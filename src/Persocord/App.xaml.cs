using System;
using System.Linq;
using System.Threading;
using System.Windows;
using H.NotifyIcon;
using Persocord.Models;
using Persocord.Services;
using Persocord.Views;

namespace Persocord;

public partial class App : Application
{
    private AppController? _controller;
    private TaskbarIcon? _tray;
    private string? _lastProblem;
    private DateTime _lastProblemAt;
    private bool _showingProblem;
    /// <summary>The icon the tray shows now (each new one replaces it, and the old one's handle is freed).</summary>
    private System.Drawing.Icon? _shownIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one copy runs; Program.Main has already handed any second
        // launch over to this one. DPI awareness lives in app.manifest.
        base.OnStartup(e);

        // VLC's player draws its controls in a helper window of its own. It
        // used to be titled "Persocord", so screen-share pickers (Discord,
        // Windows) listed two Persocord windows; sharing that one ended when
        // the player closed. Now it has no title and is a tool window, so
        // pickers skip it and only the real Persocord window can be shared.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && (w.GetType().Namespace?.StartsWith("LibVLCSharp") == true || (w.Title ?? "").Contains("LibVLCSharp")))
            {
                w.Title = "";
                w.ShowInTaskbar = false;
                HideFromPickers(w);
            }
        }));

        // Dark title bars on every window, before the first one opens.
        DarkChrome.Register();
        SliderDrag.Register();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("FATAL " + args.ExceptionObject);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("ERROR background task: " + args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // The same problem over and over (something that's drawn or ticks many
            // times a second) is logged and shown once a minute, never as a
            // stream of messages the window can't get out from under.
            var key = args.Exception.GetType().FullName + "|" + args.Exception.TargetSite?.Name;
            var now = DateTime.UtcNow;
            if (key == _lastProblem && now - _lastProblemAt < TimeSpan.FromMinutes(1)) return;
            _lastProblem = key;
            _lastProblemAt = now;
            Log.Write("ERROR unhandled: " + args.Exception);
            if (_showingProblem) return;
            _showingProblem = true;
            try
            {
                Dialog.Show(
                    "Persocord hit a problem and had to stop what it was doing.\n\n" +
                    args.Exception.Message + "\n\nDetails are in the log file.",
                    "Persocord", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            finally { _showingProblem = false; }
        };

        // How the last run ended (a crash leaves no line in the log by itself).
        CrashWatch.Started();

        // The look first (its own file, looks.json): the first time, it takes
        // the colours that used to live in settings.json, before settings are
        // read and saved without them.
        LookStore.Load();
        LookStore.Changed += () => Dispatcher.BeginInvoke(new Action(() => ThemeApplier.ApplyLive(LookStore.Current)));

        _controller = new AppController();

        // Read the saved settings now, so the accent and background come from
        // them. (The theme used to be set up before they were read, with the
        // defaults, which is why a saved colour didn't show after a restart.)
        _controller.Settings.Load();
        try { ThemeApplier.Apply(Resources.MergedDictionaries[0], LookStore.Current); }
        catch (Exception ex) { Log.Error("theme", ex); }

        // Opened by the installer after an update (or after one that didn't finish).
        Updates.JustUpdated = e.Args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));
        Updates.LastTryFailed = e.Args.Any(a => a.Equals("--update-failed", StringComparison.OrdinalIgnoreCase));

        // The tray first, so anything Startup reports has somewhere to appear.
        BuildTray();
        _controller.Startup();

        Listen(Program.LaunchSignal, () => _controller?.HandleLaunch(LaunchRequest.Default));
        Listen(Program.ShowSignal, () => _controller?.ShowLibrary());
        Listen(Program.RecordSignal, () => _controller?.HandleLaunch(LaunchRequest.Record));

        // Lets the installer close this copy cleanly before replacing the exe.
        Listen(Program.QuitSignal, () =>
        {
            _controller?.Shutdown();
            _tray?.Dispose();
            Shutdown();
        });

        var request = Program.RequestFrom(e.Args);
        // The very first start: the setup comes first (it opens the clip list when it's done).
        if (!_controller.Settings.Current.SetupDone) Views.SetupWindow.Open(_controller);
        else _controller.HandleLaunch(request);
        Log.Write("Persocord started (" + request + ")");
    }

    /// <summary>
    /// Starts a fresh copy and closes this one, so theme changes apply
    /// everywhere. The new copy opens the clip list.
    /// </summary>
    public void Restart() => Restart(true);

    /// <param name="showLibrary">Whether the new copy opens the clip list (otherwise it starts in the tray).</param>
    public void Restart(bool showLibrary)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = (showLibrary ? "--library" : "--tray") + " --restarted",
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("restart", ex);
            return;
        }
        // The new copy waits for this one to be gone.
        CloseNow("restart");
    }

    /// <summary>
    /// Closes this copy for something that needs it gone (a restart, an
    /// update). If closing ever hangs, it ends itself after 15 seconds, like
    /// End task would, so what's waiting always happens (its FFmpegs end with it).
    /// </summary>
    public void CloseNow(string why)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(15000);
            try { Log.Write($"Closing for {why} took too long; ended at once"); } catch { }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
        }) { IsBackground = true, Name = "Persocord close watchdog" };
        watchdog.Start();
        _controller?.Shutdown();
        _tray?.Dispose();
        Shutdown();
    }

    private static void HideFromPickers(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
        }
        catch (Exception ex) { Log.Error("player overlay window", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Waits for another launch of Persocord to ask for something.</summary>
    private void Listen(string name, Action action)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (true)
            {
                try { handle.WaitOne(); }
                catch { return; }
                Dispatcher.BeginInvoke(action);
            }
        }) { IsBackground = true, Name = "Persocord " + name }.Start();
    }

    private void BuildTray()
    {
        _tray = new TaskbarIcon { ToolTipText = "Persocord" };
        ShowIcon(LoadIcon());
        Views.LogoArt.Changed += RedrawTrayIcon;
        // Persocord's shortcuts (the taskbar pin among them) get the logo's colours,
        // once things are quiet after starting (an update puts back the plain ones).
        Dispatcher.BeginInvoke(new Action(Views.LogoArt.SyncShortcuts), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(Item("Record area", () => _ = _controller!.StartRegionRecordingAsync(false)));
        menu.Items.Add(Item("Record GIF", () => _ = _controller!.StartRegionRecordingAsync(true)));
        menu.Items.Add(Item("Save replay", () => _ = _controller!.SaveReplayAsync()));
        menu.Items.Add(Item("Turn replay on or off", () => _controller!.ToggleReplay()));
        // For a game Persocord didn't recognise (shown with "Only while a game runs").
        var countGame = Item("Count this as a game", () =>
        {
            if (_controller!.Games.LastApp is { } app) _controller.AddGame(app.Path);
        });
        menu.Items.Add(countGame);
        menu.Opened += (_, _) =>
        {
            try
            {
                var s = _controller!.Settings.Current;
                countGame.Visibility = s.ReplayGamesOnly ? Visibility.Visible : Visibility.Collapsed;
                if (!s.ReplayGamesOnly) return;
                var app = _controller.Games.LastApp;
                bool counted = app is { } a && (s.MyGames.Contains(a.Path, StringComparer.OrdinalIgnoreCase) ||
                                                 string.Equals(_controller.Games.Current?.ExePath, a.Path, StringComparison.OrdinalIgnoreCase));
                countGame.Header = app is not { } x ? "Count this as a game (open the game first)"
                                 : counted ? $"{x.Name} counts as a game"
                                 : $"Count {x.Name} as a game";
                countGame.IsEnabled = app != null && !counted;
            }
            catch (Exception ex) { Log.Error("tray menu", ex); }
        };
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item("Pause or resume", () => _controller!.TogglePause()));
        menu.Items.Add(Item("Finish recording", () => _ = _controller!.FinishAsync()));
        menu.Items.Add(Item("Cancel recording", () => _ = _controller!.CancelAsync()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item("My clips", () => _controller!.ShowLibrary()));
        menu.Items.Add(Item("Settings", () => _controller!.ShowSettings()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item("Quit", () =>
        {
            _controller!.Shutdown();
            _tray?.Dispose();
            Shutdown();
        }));

        _tray.ContextMenu = menu;

        // A tray icon built in code stays hidden until this is called; one
        // declared in XAML would not need it. Efficiency mode is off because it
        // lets Windows throttle the process, which a recorder cannot afford.
        _tray.ForceCreate(false);
        _tray.TrayLeftMouseUp += (_, _) =>
        {
            // A double-click is two clicks: the second, a moment after the
            // first, is let go rather than opening the clip list again while
            // it's still opening. (A double-click with the replay on took
            // Persocord down; nothing that comes from the tray can do that now.)
            long now = Environment.TickCount64;
            if (now - _lastTrayClick < 600) return;
            _lastTrayClick = now;
            try { _controller!.ShowLibrary(); }
            catch (Exception ex) { Log.Error("tray click", ex); }
        };

        _controller!.Notify += (title, message) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Persocord's own notification, or Windows' if they're switched off.
                var s = _controller!.Settings.Current;
                if (s.ToastsEnabled)
                {
                    try
                    {
                        Persocord.Views.Toast.Show(title, message, s);
                        _controller.PlayCue(CueKind.Notification);
                    }
                    catch { try { _tray?.ShowNotification(title, message); } catch { } }
                }
                else
                {
                    try { _tray?.ShowNotification(title, message); } catch { }
                }
            }));

        _controller.NotifyClip += (title, message, clipId) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var s = _controller!.Settings.Current;
                if (!s.ToastsEnabled) { try { _tray?.ShowNotification(title, message); } catch { } return; }
                try
                {
                    Persocord.Views.Toast.Show(title, message, s,
                        s.ToastClickAction == ToastClick.JustClose ? null : () => _controller.OpenFromNotification(clipId));
                    _controller.PlayCue(CueKind.Notification);
                }
                catch { try { _tray?.ShowNotification(title, message); } catch { } }
            }));

        _controller.StatusChanged += text =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_tray != null) _tray.ToolTipText = "Persocord — " + text;
            }));

        // The tray icon shows when the replay is on but not capturing: a red
        // dot on the icon and the reason in its tooltip, quietly.
        _controller.ReplayStateChanged += () =>
        {
            if (_tray == null || _controller == null) return;
            var trouble = _controller.Replay.IsRunning ? _controller.Replay.Trouble : null;
            bool waiting = _controller.ReplayWaiting;
            if (trouble == _trayTrouble && waiting == _trayWaiting) return;
            bool iconChanged = trouble != _trayTrouble;
            _trayTrouble = trouble;
            _trayWaiting = waiting;
            try
            {
                // Waiting for a game shows only in the hover text: the icon stays as it is.
                if (iconChanged) ShowIcon(trouble != null ? TroubleIcon() : LoadIcon());
                _tray.ToolTipText = trouble != null ? "Persocord — replay paused: " + trouble
                                  : waiting ? "Persocord — replay waiting for a game"
                                  : "Persocord";
            }
            catch (Exception ex) { Log.Error("tray icon", ex); }
        };

        // The notice before a fresh start: quiet (no sound), and it stays until
        // someone's back, "Not now" is pressed, or the refresh happens.
        _controller.RefreshWarning += show => Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _refreshNotice?.Dismiss();
                _refreshNotice = null;
                if (!show) return;
                const string title = "Persocord will refresh in 10 minutes";
                const string text = "It restarts after long use while you're away. Any input puts it off.";
                var s = _controller!.Settings.Current;
                if (s.ToastsEnabled) _refreshNotice = Persocord.Views.Toast.ShowLasting(title, text, s, "Not now", () => _controller.Refresh?.NotNow());
                else _tray?.ShowNotification(title, text, sound: false);
            }
            catch (Exception ex) { Log.Error("refresh notice", ex); }
        }));
    }

    /// <summary>The logo's colours changed (Customization): the tray shows the new one.</summary>
    private void RedrawTrayIcon()
    {
        if (_tray == null) return;
        try
        {
            var oldTrouble = _troubleIcon;
            _troubleIcon = null;
            ShowIcon(_trayTrouble != null ? TroubleIcon() : LoadIcon());
            // (If it was the one showing, ShowIcon retires it.)
            if (oldTrouble != null && !ReferenceEquals(oldTrouble, _shownIcon) && !ReferenceEquals(oldTrouble, _troubleIcon))
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                t.Tick += (_, _) => { t.Stop(); try { oldTrouble.Dispose(); } catch { } };
                t.Start();
            }
        }
        catch (Exception ex) { Log.Error("tray icon", ex); }
    }

    /// <summary>
    /// Shows an icon in the tray and frees the one before it (each icon holds a
    /// Windows handle; replaced ones used to be kept until Persocord closed).
    /// The red-dot icon is kept for reuse, and Windows' own icon is never freed.
    /// </summary>
    private void ShowIcon(System.Drawing.Icon? icon)
    {
        if (_tray == null || icon == null) return;
        // An icon whose Windows handle has gone can't be shown (it threw, and the
        // tray kept the wrong icon): the red-dot one is drawn again, or the plain
        // one shown instead.
        if (!Usable(icon))
        {
            if (ReferenceEquals(icon, _troubleIcon)) { _troubleIcon = null; icon = TroubleIcon(); }
            if (icon == null || !Usable(icon)) icon = LoadIcon();
            if (icon == null || !Usable(icon)) return;
        }
        var old = _shownIcon;
        _tray.Icon = icon;
        _shownIcon = icon;
        if (old != null && !ReferenceEquals(old, icon) && !ReferenceEquals(old, _troubleIcon) && !ReferenceEquals(old, System.Drawing.SystemIcons.Application))
        {
            // Freed a little later, not straight away: the tray can still be
            // drawing with it for a moment after the switch.
            var retired = old;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!ReferenceEquals(retired, _shownIcon) && !ReferenceEquals(retired, _troubleIcon))
                    try { retired.Dispose(); } catch { }
            };
            t.Start();
        }
    }

    private static bool Usable(System.Drawing.Icon icon)
    {
        try { return icon.Handle != IntPtr.Zero; }
        catch { return false; }
    }

    private long _lastTrayClick;
    private bool _trayWaiting;
    private Persocord.Views.Toast? _refreshNotice;
    private string? _trayTrouble;
    private System.Drawing.Icon? _troubleIcon;

    /// <summary>The usual icon with a small red dot in its corner.</summary>
    private System.Drawing.Icon? TroubleIcon()
    {
        if (_troubleIcon != null) return _troubleIcon;
        try
        {
            var baseIcon = LoadIcon();
            if (baseIcon == null) return null;
            using var owned = ReferenceEquals(baseIcon, System.Drawing.SystemIcons.Application) ? null : baseIcon;
            using var bmp = new System.Drawing.Bitmap(baseIcon.Width, baseIcon.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                float d = bmp.Width * 0.46f;
                var dot = new System.Drawing.RectangleF(bmp.Width - d - 0.5f, bmp.Height - d - 0.5f, d, d);
                using var ring = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 18, 20, 24));
                g.FillEllipse(ring, System.Drawing.RectangleF.Inflate(dot, 1.2f, 1.2f));
                using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0xE0, 0x59, 0x4D));
                g.FillEllipse(red, dot);
            }
            // A copy that owns its handle; the one GetHicon made is freed at once.
            var handle = bmp.GetHicon();
            try
            {
                using var temp = System.Drawing.Icon.FromHandle(handle);
                _troubleIcon = (System.Drawing.Icon)temp.Clone();
            }
            finally { DestroyIcon(handle); }
            return _troubleIcon;
        }
        catch (Exception ex) { Log.Error("tray trouble icon", ex); return null; }
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        // The logo in the look's colours, drawn at the tray's own size.
        if (Views.LogoArt.TrayIcon() is { } drawn) return drawn;

        // The icon travels inside the exe. The size asked for is the small
        // one Windows uses in the tray, which also picks the sharp 16/20/24
        // pixel artwork on scaled displays instead of a blurred 256.
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("persocord.ico");
            if (stream != null)
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Error("tray icon", ex); }

        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private static System.Windows.Controls.MenuItem Item(string header, Action action)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = header };
        mi.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Write($"ERROR menu '{header}': {ex}");
                Dialog.Show($"{header} failed.\n\n{ex.Message}\n\nDetails: {Paths.LogFile}",
                    "Persocord", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        return mi;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _controller?.Shutdown(); } catch { }
        try { _tray?.Dispose(); } catch { }
        Log.Write("Persocord exited");
        CrashWatch.Exited();
        base.OnExit(e);
    }
}

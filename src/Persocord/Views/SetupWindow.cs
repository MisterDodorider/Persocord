using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Persocord.Models;
using Persocord.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Persocord.Views;

/// <summary>
/// The first-run setup: where clips go, the replay and its quality, sound,
/// the keys, a quick look, then a real test of the replay key. Every page can
/// be skipped, and all of it lives in Settings afterwards.
/// </summary>
public class SetupWindow : Window
{
    private static SetupWindow? _open;

    /// <summary>Whether the setup is open.</summary>
    public static bool IsOpen => _open != null;

    private readonly AppController _app;
    /// <summary>What's shown: the current settings, with this setup's changes.</summary>
    private readonly AppSettings _s;
    /// <summary>Only what was changed here, applied onto the settings as they are when saving.</summary>
    /// (In order: a later change, or a settings code loaded later, wins.)
    private readonly List<(string Key, Action<AppSettings> Apply)> _changes = new();
    private Looks _look;
    private bool _lookTouched;
    private string _folder;
    private int _page;
    private bool _done;
    private readonly bool _firstRun;

    private readonly (string Name, Func<FrameworkElement> Build)[] _pages;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Grid _progress = new() { Height = 4, Margin = new Thickness(28, 18, 28, 0) };
    private readonly TextBlock _stepText = new() { FontSize = 12, Margin = new Thickness(28, 8, 28, 0) };
    private readonly Button _back = new() { Content = "Back", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { Content = "Next", MinWidth = 110 };
    private readonly Button _skipAll = new() { Content = "Skip setup", HorizontalAlignment = HorizontalAlignment.Left };

    // What this PC can encode, per format (filled in the background).
    private readonly Dictionary<VideoCodec, List<EncoderKind>> _have = new();
    private readonly DxgiTarget? _screen;

    // Mic level, while the sound page is open.
    private NAudio.CoreAudioApi.WasapiCapture? _meter;
    private Border? _meterBar;
    private double _shownDb = -90;
    private TextBlock? _hotNote;
    private DateTime _hotUntil;

    // The test at the end.
    private TextBlock? _tryStatus;
    private Action<string, string, string>? _onClip;
    private Action<string, string>? _onNotify;

    private static readonly int[] Lengths = { 15, 30, 60, 120, 300, 600 };
    private static readonly int[] Heights = { 0, 1440, 1080, 720 };
    private static readonly int[] FpsChoices = { 30, 60, 120, 144 };
    private static readonly (string Name, int Kbps)[] Bitrates =
    {
        ("Light", 8000), ("Balanced", 12000), ("High", 20000), ("Very high", 35000),
    };
    private static readonly (string Name, string Detail, int Level)[] Levels =
    {
        ("Smaller files", "Fine for sharing", 25), ("Balanced", "Recommended", 21), ("Sharp", "Bigger files", 18),
    };

    /// <summary>Ready-made looks: a background, an accent.</summary>
    private static readonly (string Name, string Detail, ThemeMode Mode, SurfaceStyle Surface, string Accent)[] LookPresets =
    {
        ("Persocord", "Graphite and gold", ThemeMode.Dark, SurfaceStyle.Graphite, "#D6B06A"),
        ("Midnight", "Navy and ice blue", ThemeMode.Dark, SurfaceStyle.Midnight, "#6FB4E8"),
        ("OLED", "True black and silver", ThemeMode.Dark, SurfaceStyle.OledBlack, "#C3C9D2"),
        ("Forest", "Deep green and mint", ThemeMode.Dark, SurfaceStyle.Forest, "#5CCB9E"),
        ("Plum", "Dark purple and rose", ThemeMode.Dark, SurfaceStyle.Plum, "#E0708A"),
        ("Paper", "Light, with indigo", ThemeMode.Light, SurfaceStyle.Paper, "#7C8CF0"),
    };

    /// <summary>Opens the setup (or brings it forward if it's already open).</summary>
    public static void Open(AppController app)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SetupWindow(app);
        _open.Show();
        _open.Activate();
    }

    private SetupWindow(AppController app)
    {
        _app = app;
        _firstRun = !app.Settings.Current.SetupDone;
        _s = app.Settings.Current.Clone();
        _look = LookStore.Saved.Clone();
        _folder = app.Library.Folder;
        var outputs = DxgiOutputs.All();
        _screen = outputs.FirstOrDefault(t => t.Desktop.X == 0 && t.Desktop.Y == 0) ?? outputs.FirstOrDefault();

        Title = "Set up Persocord";
        Width = 680;
        Height = 640;
        MinHeight = 520;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        _pages = new (string, Func<FrameworkElement>)[]
        {
            ("Welcome", Welcome), ("Clips", Clips), ("Replay", Replay), ("Sound", Sound),
            ("Keys", Keys), ("Look", LookPage), ("Try it", TryIt),
        };

        _next.Style = TryFindResource("Primary") as Style;
        _skipAll.Style = TryFindResource("Ghost") as Style;
        _skipAll.ToolTip = "Keeps what you've chosen so far; everything else stays as it is. It's all in Settings.";
        _back.Click += (_, _) => Go(_page - 1);
        _next.Click += async (_, _) =>
        {
            if (_page == _pages.Length - 1) { await FinishAsync(); Close(); }
            else Go(_page + 1);
        };
        _skipAll.Click += async (_, _) => { await FinishAsync(); Close(); };

        var footer = new DockPanel { Margin = new Thickness(28, 12, 28, 18), LastChildFill = false };
        DockPanel.SetDock(_skipAll, Dock.Left);
        footer.Children.Add(_skipAll);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_back);
        right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);

        _stepText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stepText, 1);
        Grid.SetRow(_scroll, 2);
        Grid.SetRow(footer, 3);
        root.Children.Add(_progress);
        root.Children.Add(_stepText);
        root.Children.Add(_scroll);
        root.Children.Add(footer);
        Content = root;

        for (int i = 0; i < _pages.Length; i++)
        {
            _progress.ColumnDefinitions.Add(new ColumnDefinition());
            var seg = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) };
            Grid.SetColumn(seg, i);
            _progress.Children.Add(seg);
        }

        PreviewKeyDown += (_, e) =>
        {
            // Enter moves on, unless a hotkey box is listening.
            if (e.Key == Key.Enter && Keyboard.FocusedElement is not HotkeyBox) { _next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };
        Closing += async (_, _) => { _closing = true; if (!_done) await FinishAsync(); };
        Closed += (_, _) =>
        {
            StopMeter();
            StopListening();
            _open = null;
            // The first time, the clip list is where things carry on, and a
            // word about the tray: closing windows never quits Persocord.
            if (_firstRun)
            {
                _app.ShowLibrary();
                _app.Say("Persocord is in your tray",
                    "It keeps running there, replay and keys included, even with every window closed. " +
                    "Right-click its icon for everything; if you can't see it, it's under the ^ arrow by the clock.");
            }
        };

        _ = LoadEncodersAsync();
        Go(0);
    }

    /* ---- moving between pages --------------------------------------------- */

    private void Go(int page)
    {
        page = Math.Clamp(page, 0, _pages.Length - 1);
        // Leaving the sound page: the mic is let go. (Its bar is forgotten
        // here, not in StopMeter: picking another mic stops and restarts the
        // meter, and the bar went dead after that.)
        StopMeter();
        _meterBar = null;
        _hotNote = null;
        StopListening();
        // Arriving at the test: everything chosen so far is put to use first.
        if (page == _pages.Length - 1 && _page != page) _ = ApplyAsync(final: false);
        _page = page;

        for (int i = 0; i < _progress.Children.Count; i++)
        {
            var seg = (Border)_progress.Children[i];
            seg.SetResourceReference(Border.BackgroundProperty, i <= page ? "Brass" : "Line");
        }
        _stepText.Text = $"Step {page + 1} of {_pages.Length}  ·  {_pages[page].Name}";
        _back.Visibility = page == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = page == 0 ? "Let's go" : page == _pages.Length - 1 ? "Finish" : "Next";
        _skipAll.Visibility = page == _pages.Length - 1 ? Visibility.Hidden : Visibility.Visible;
        Rebuild(keepScroll: false);
    }

    private void Rebuild(bool keepScroll = true)
    {
        double offset = keepScroll ? _scroll.VerticalOffset : 0;
        _scroll.Content = new Border { Padding = new Thickness(28, 18, 28, 12), Child = _pages[_page].Build() };
        // Picking something keeps the page where it was.
        Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /* ---- the pages -------------------------------------------------------- */

    private FrameworkElement Welcome()
    {
        var p = Page("Welcome to Persocord",
            "Let's go over the important parts: where your clips go, your replay, sound, and your keys. " +
            "It takes about a minute. Whatever is already set is shown as it is, and only what you change here changes.");
        // The logo over the welcome, in the look's colours.
        var logo = new Image { Source = LogoArt.Image, Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 0, 0, 10) };
        void Redraw() => logo.Source = LogoArt.Image;
        logo.Unloaded += (_, _) => LogoArt.Changed -= Redraw;
        logo.Loaded += (_, _) => { LogoArt.Changed -= Redraw; LogoArt.Changed += Redraw; Redraw(); };
        p.Children.Insert(0, logo);
        p.Children.Add(Card(
            Bullet("\uE714", "Replay", "Always keeps the last moments of your screen. Press a key and they become a clip."),
            Bullet("\uE7C5", "Record an area", "Pick any part of the screen and record just that."),
            Bullet("\uE8C8", "Ready to share", "Clips are copied as you save them: paste straight into Discord.")));
        p.Children.Add(Section("A few quick ones"));
        p.Children.Add(Check("Start Persocord when Windows starts", () => _s.StartWithWindows,
            v => Set(nameof(AppSettings.StartWithWindows), x => x.StartWithWindows = v), "So the replay is ready before you open a game."));
        p.Children.Add(Check("Copy replays when they're saved, ready to paste", () => _s.CopyReplayOnSave,
            v => Set(nameof(AppSettings.CopyReplayOnSave), x => x.CopyReplayOnSave = v), "Ctrl+V drops it into Discord or a chat."));
        p.Children.Add(Check("Copy area recordings and GIFs when they finish", () => _s.CopyFileOnFinish,
            v => Set(nameof(AppSettings.CopyFileOnFinish), x => x.CopyFileOnFinish = v)));

        p.Children.Add(Label("When you open Persocord from its icon"));
        p.Children.Add(Choices(new[] { ("Open the clip list", "Your clips and the replay"), ("Pick an area to record", "Straight to recording") },
            _s.OpenAction == OpenAction.SelectArea ? 1 : 0, i =>
            {
                var v = i == 1 ? OpenAction.SelectArea : OpenAction.ClipList;
                Set(nameof(AppSettings.OpenAction), x => x.OpenAction = v);
                Rebuild();
            }, width: 200));
        p.Children.Add(Hint("While recording, opening it saves the clip either way."));

        // A friend's setup in one go: their settings code (never their look,
        // folders or devices), shown on the next pages to look over.
        p.Children.Add(Section("Have a settings code?"));
        p.Children.Add(Hint("Paste one from a friend to start from their settings. You'll see everything it sets on the next pages, and can change any of it."));
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var load = new Button { Content = "Use it", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(load, Dock.Right);
        var box = new TextBox { Text = _codeText, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Starts with PERSO1-" };
        row.Children.Add(load);
        row.Children.Add(box);
        p.Children.Add(row);
        var result = Hint(_codeResult);
        p.Children.Add(result);
        load.Click += (_, _) =>
        {
            string code = box.Text.Trim();
            _codeText = code;
            if (code.Length == 0) return;
            try
            {
                var test = _s.Clone();
                int n = ShareCode.Import(code, test);
                if (n == 0) { _codeResult = "That code matches the defaults, so there was nothing to change."; }
                else
                {
                    Set("settings code", x => ShareCode.Import(code, x));
                    _codeResult = $"✓ Loaded {n} setting{(n == 1 ? "" : "s")} from the code. Nothing is saved until you finish.";
                }
            }
            catch (FormatException ex) { _codeResult = ex.Message; }
            Rebuild();
        };
        return p;
    }

    private FrameworkElement Clips()
    {
        var p = Page("Where your clips go", "Your Videos folder works well. Pick another drive if that one is small.");
        p.Children.Add(Label("Clips folder"));
        var row = new DockPanel();
        var change = new Button { Content = "Change", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(change, Dock.Right);
        var path = new TextBox { Text = _folder, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(change);
        row.Children.Add(path);
        p.Children.Add(row);
        change.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Where should clips be saved?",
                SelectedPath = Directory.Exists(_folder) ? _folder : _app.Library.Folder,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) { _folder = dlg.SelectedPath; Rebuild(); }
        };

        p.Children.Add(Section("Storage limit"));
        p.Children.Add(Hint("How much of the drive your clips may take before Persocord reminds you. Nothing is ever deleted on its own."));
        long total = DriveTotal(_folder);
        var readout = new TextBlock { FontSize = 13, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        readout.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        // Typed in GB (a slider over a whole drive moved in steps of tens of
        // GB). It's kept as a share of the drive, as Settings shows it.
        double totalGb = total / 1073741824.0;
        bool byGb = totalGb >= 1;
        var capRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var box = new TextBox
        {
            Width = 90, VerticalContentAlignment = VerticalAlignment.Center,
            Text = byGb ? Math.Round(totalGb * _s.MaxDiskPercent / 100).ToString("0") : _s.MaxDiskPercent.ToString("0.#"),
            ToolTip = byGb ? "How many GB your clips may take" : "How much of the drive, in percent",
        };
        var unit = new TextBlock { Text = byGb ? "GB" : "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        capRow.Children.Add(box);
        capRow.Children.Add(unit);
        p.Children.Add(capRow);
        void ShowCap(bool valid)
        {
            if (!valid) { readout.Text = byGb ? $"Type a number of GB, up to {totalGb * 0.9:0}." : "Type a number from 0.1 to 90."; return; }
            readout.Text = byGb
                ? $"≈ {_s.MaxDiskPercent:0.#}% of this {Gb(total)} drive"
                : $"{_s.MaxDiskPercent:0.#}% of the drive";
        }
        box.TextChanged += (_, _) =>
        {
            var t = box.Text.Trim().Replace(',', '.');
            if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0)
            { ShowCap(false); return; }
            double pct = Math.Round(Math.Clamp(byGb ? v / totalGb * 100 : v, 0.1, 90), 2);
            Set(nameof(AppSettings.MaxDiskPercent), x => x.MaxDiskPercent = pct);
            ShowCap(true);
        };
        ShowCap(true);
        p.Children.Add(readout);

        p.Children.Add(Section("Folders"));
        p.Children.Add(Check("Sort clips into a folder per game or app", () => _s.AutoSortIntoFolders,
            v => Set(nameof(AppSettings.AutoSortIntoFolders), x => x.AutoSortIntoFolders = v),
            "Folders are made for you. Desktop clips go in Desktop."));
        return p;
    }

    private FrameworkElement Replay()
    {
        var p = Page("Your replay", "It quietly keeps the last moments of your screen, so you can save something after it happens.");
        p.Children.Add(Check("Keep a replay running", () => _s.ReplayEnabled,
            v => { Set(nameof(AppSettings.ReplayEnabled), x => x.ReplayEnabled = v); Rebuild(); },
            "Starts with Persocord. Uses your graphics card's video encoder, so games barely notice."));
        if (!_s.ReplayEnabled)
        {
            p.Children.Add(Hint("You can switch it on any time from the clip list or Settings → Replay."));
            return p;
        }

        p.Children.Add(Label("When it runs"));
        p.Children.Add(Choices(new[] { ("Always", "Ready for anything on screen"), ("Only while a game runs", "Off the rest of the time") },
            _s.ReplayGamesOnly ? 1 : 0, i =>
            {
                bool games = i == 1;
                Set(nameof(AppSettings.ReplayGamesOnly), x => x.ReplayGamesOnly = games);
                Rebuild();
            }, width: 220));
        if (_s.ReplayGamesOnly)
            p.Children.Add(Hint("Some games aren't recognised. Add them from the tray menu while the game is open, " +
                                "and the replay runs for them from then on."));

        bool fewCores = Environment.ProcessorCount <= 12;
        var costNow = ReplayCost.Estimate(CostInput());
        // A choice's card names the biggest thing picking it would change.
        string Diff(Func<ReplayCost.Input, ReplayCost.Input> change, string fallback = "")
        {
            var d = ReplayCost.Delta(costNow, ReplayCost.Estimate(change(CostInput())), 1);
            return d.Length == 0 ? fallback : d;
        }
        p.Children.Add(Check("Lighter on the CPU", () => _s.ReplayLight,
            v => { Set(nameof(AppSettings.ReplayLight), x => x.ReplayLight = v); Rebuild(); },
            (fewCores ? $"Suggested for this PC ({Environment.ProcessorCount} threads). " : "") +
            "Keeps PC audio and the mic but not a track per app, encodes sound more lightly, and lets games go first. Turn on if games stutter."));

        // Anything set to a value these cards don't offer stays as it is,
        // and is named underneath, until a card is picked.
        p.Children.Add(Label("How much to keep"));
        int len = Array.IndexOf(Lengths, _s.ReplaySeconds);
        p.Children.Add(Choices(Lengths.Select(l => (l < 60 ? $"{l} s" : $"{l / 60} min", "")).ToArray(), len,
            i => { int v = Lengths[i]; Set(nameof(AppSettings.ReplaySeconds), x => x.ReplaySeconds = v); Rebuild(); }, width: 84));
        if (len < 0) p.Children.Add(Now(LengthText(_s.ReplaySeconds)));

        p.Children.Add(Section("Quality"));
        p.Children.Add(Label("Resolution"));
        int screenH = _screen?.Desktop.Height ?? 1080;
        var sizes = Heights.Where(h => h == 0 || h < screenH).ToArray();
        int shownH = _s.ReplayHeight <= 0 || _s.ReplayHeight >= screenH ? 0 : _s.ReplayHeight;
        int sizeAt = Array.IndexOf(sizes, shownH);
        p.Children.Add(Choices(sizes.Select((h, i) => (h == 0 ? $"{screenH}p" : $"{h}p",
                i == sizeAt ? (h == 0 ? "Your screen's size" : "") : Diff(c => c with { Height = h }, h == 0 ? "Your screen's size" : ""))).ToArray(), sizeAt,
            i => { int v = sizes[i]; Set(nameof(AppSettings.ReplayHeight), x => x.ReplayHeight = v); Rebuild(); }, width: 110));
        if (sizeAt < 0) p.Children.Add(Now($"{_s.ReplayHeight}p"));

        p.Children.Add(Label("Frame rate"));
        int fpsAt = Array.IndexOf(FpsChoices, _s.ReplayFps);
        p.Children.Add(Choices(FpsChoices.Select((f, i) => ($"{f} fps", i == fpsAt ? "" : Diff(c => c with { Fps = f }))).ToArray(), fpsAt,
            i => { int v = FpsChoices[i]; Set(nameof(AppSettings.ReplayFps), x => x.ReplayFps = v); Rebuild(); }, width: 84));
        if (fpsAt < 0) p.Children.Add(Now($"{_s.ReplayFps} fps"));

        p.Children.Add(Label("File size is set by"));
        p.Children.Add(Choices(new[] { ("A quality level", "Size follows the action"), ("A fixed bitrate", "Size you can predict") },
            _s.ReplayUseBitrate ? 1 : 0, i =>
            {
                bool fixedRate = i == 1;
                Set(nameof(AppSettings.ReplayUseBitrate), x => x.ReplayUseBitrate = fixedRate);
                Rebuild();
            }, width: 190));
        if (!_s.ReplayUseBitrate)
        {
            p.Children.Add(Label("Quality"));
            int level = Array.FindIndex(Levels, l => l.Level == _s.ReplayQuality);
            p.Children.Add(Choices(Levels.Select(l => (l.Name,
                $"{l.Detail} · ≈ {ReplayCost.Mb(ReplayCost.Estimate(CostInput() with { Quality = l.Level }).ClipMbPerMinute)}/min")).ToArray(), level, i =>
            {
                int v = Levels[i].Level;
                Set(nameof(AppSettings.ReplayQuality), x => x.ReplayQuality = v);
                Rebuild();
            }, width: 140));
            if (level < 0) p.Children.Add(Now($"quality level {_s.ReplayQuality}"));
        }
        else
        {
            p.Children.Add(Label("Bitrate"));
            int at = Array.FindIndex(Bitrates, b => b.Kbps == _s.ReplayBitrateKbps);
            p.Children.Add(Choices(Bitrates.Select(b => ($"{b.Kbps / 1000} Mbps",
                $"{b.Name} · {ReplayCost.Mb(ReplayCost.Estimate(CostInput() with { BitrateKbps = b.Kbps }).ClipMbPerMinute)}/min")).ToArray(), at, i =>
            {
                int v = Bitrates[i].Kbps;
                Set(nameof(AppSettings.ReplayBitrateKbps), x => x.ReplayBitrateKbps = v);
                Rebuild();
            }, width: 110));
            if (at < 0) p.Children.Add(Now($"{_s.ReplayBitrateKbps / 1000.0:0.#} Mbps"));
        }

        p.Children.Add(Label("Video format"));
        var codecs = new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 };
        var blurbs = new[] { "Plays everywhere", "Smaller", "Smallest" };
        p.Children.Add(Choices(codecs.Select((c, i) => (EncoderDetector.CodecName(c), blurbs[i] + (Works(c) ? "" : " · not on this PC"))).ToArray(),
            Array.IndexOf(codecs, _s.ReplayCodec), i =>
            {
                if (!Works(codecs[i])) return;
                var v = codecs[i];
                Set(nameof(AppSettings.ReplayCodec), x => x.ReplayCodec = v);
                Rebuild();
            }, width: 140));

        // What it costs, all of it, in one line.
        var guess = ReplayService.GuessVideoMemory(_screen, _s.ReplayHeight, _s.ReplayCodec, _s.ReplayEncoder, Have());
        var cost = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 14, 0, 0) };
        cost.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        cost.Text = ReplayCost.Summary(costNow) + $" · {ReplayCost.Mb(costNow.LoopMb)} of disk while on";
        p.Children.Add(cost);
        if (guess.Mb > _s.SafetyVramMb)
        {
            // Over the limit: the replay would keep restarting. Raised only if asked.
            int fit = Math.Min(8192, (int)Math.Ceiling(guess.Mb * 1.2 / 50) * 50);
            var warn = Hint("That's over your video memory limit, so the replay would keep restarting.");
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Brick");
            p.Children.Add(warn);
            var raise = new Button { Content = $"Raise the limit to {fit} MB", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            raise.Click += (_, _) => { Set(nameof(AppSettings.SafetyVramMb), x => x.SafetyVramMb = fit); Rebuild(); };
            p.Children.Add(raise);
        }
        p.Children.Add(Hint("Each card says what picking it would change. Resolution sets most of the video memory, frame rate the encoder's load, " +
                            "bitrate and length only the disk. Settings → Replay breaks it all down."));
        return p;
    }

    private FrameworkElement Sound()
    {
        StopMeter();   // rebuilt after a pick: one mic level at a time
        var p = Page("Sound", "What your clips should hear. Recordings and replays both use this.");
        var modes = new[] { AudioMode.Both, AudioMode.SystemOnly, AudioMode.MicOnly, AudioMode.None };
        var names = new[] { ("PC audio and mic", "Game and voice"), ("PC audio only", "Just the game"), ("Mic only", "Just your voice"), ("No sound", "Silent clips") };
        // Shown as the replay records (it's the one always running); area
        // recordings are named underneath when they're set differently.
        var replayMode = _s.ReplayTrackPc && _s.ReplayTrackMic ? AudioMode.Both : _s.ReplayTrackPc ? AudioMode.SystemOnly
                       : _s.ReplayTrackMic ? AudioMode.MicOnly : AudioMode.None;
        p.Children.Add(Choices(names, Array.IndexOf(modes, replayMode), i =>
        {
            var m = modes[i];
            Set(nameof(AppSettings.Audio), x => x.Audio = m);
            Set(nameof(AppSettings.ReplayTrackPc), x => x.ReplayTrackPc = m is AudioMode.Both or AudioMode.SystemOnly);
            Set(nameof(AppSettings.ReplayTrackMic), x => x.ReplayTrackMic = m is AudioMode.Both or AudioMode.MicOnly);
            Rebuild();
        }, width: 150));
        if (_s.Audio != replayMode)
            p.Children.Add(Hint($"Area recordings are set to {names[Array.IndexOf(modes, _s.Audio)].Item1.ToLowerInvariant()} " +
                                "(the control bar can switch sound on while recording). Picking a card sets both the same."));
        p.Children.Add(Hint("Each sound gets its own track, so you can turn one off in the editor."));
        if (_s.ReplayEnabled)
        {
            var c = ReplayCost.Estimate(CostInput());
            var line = Hint($"Replay with this sound: CPU ≈ {ReplayCost.Cpu(c.CpuPercent)} · RAM ≈ {c.RamMb} MB · " +
                            $"sound adds ≈ {ReplayCost.Mb(c.SoundKbps * 60 / 8 / 1000)} a minute ({c.LiveTracks} track{(c.LiveTracks == 1 ? "" : "s")}).");
            line.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            p.Children.Add(line);
        }

        if (replayMode is AudioMode.Both or AudioMode.MicOnly || _s.Audio is AudioMode.Both or AudioMode.MicOnly)
        {
            p.Children.Add(Section("Microphone"));
            var mics = AudioSource.ListMics();
            var combo = new ComboBox { Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add("Whatever Windows is using");
            foreach (var d in mics) combo.Items.Add(d.Name + (d.IsDefault ? "  (default)" : ""));
            int at = mics.FindIndex(d => d.Id == _s.MicDeviceId);
            combo.SelectedIndex = at < 0 ? 0 : at + 1;
            combo.SelectionChanged += (_, _) =>
            {
                string? id = combo.SelectedIndex <= 0 ? null : mics[combo.SelectedIndex - 1].Id;
                Set(nameof(AppSettings.MicDeviceId), x => x.MicDeviceId = id);
                StopMeter();
                StartMeter();
            };
            p.Children.Add(combo);
            p.Children.Add(Label("Level (talk to test)"));
            _meterBar = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(3) };
            _meterBar.SetResourceReference(Border.BackgroundProperty, "Brass");
            var track = new Border { Width = 380, Height = 8, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Child = _meterBar };
            track.SetResourceReference(Border.BackgroundProperty, "Line");
            p.Children.Add(track);
            // Its volume, right under it: the bar follows as it moves. A sensitive
            // mic that maxes out on breathing is turned down here.
            p.Children.Add(SliderRow(0, 200, _s.MicGainPercent, v =>
            {
                int pct = (int)Math.Round(v);
                Set(nameof(AppSettings.MicGainPercent), x => x.MicGainPercent = pct);
            }, "{0:0}%"));
            _hotNote = Hint("Your mic is reaching its maximum. A sensitive mic can max out on breathing or talking close to it: " +
                            "turn its volume down until the bar stays short of the end. Settings → Audio can also limit loudness.");
            _hotNote.SetResourceReference(TextBlock.ForegroundProperty, "Brick");
            _hotNote.Visibility = Visibility.Collapsed;
            p.Children.Add(_hotNote);
            p.Children.Add(Label("AI noise suppression"));
            var levels = new[] { ("Off", ""), ("Low", "Light touch"), ("Medium", "Recommended"), ("High", "Loud rooms") };
            if (_s.ReplayEnabled && _s.ReplayTrackMic)
            {
                // What it costs the replay: any level costs the same.
                var on = ReplayCost.Estimate(CostInput() with { Denoise = 2 });
                var off = ReplayCost.Estimate(CostInput() with { Denoise = 0 });
                string extra = $"+{ReplayCost.Cpu(on.CpuPercent - off.CpuPercent)} CPU";
                levels = levels.Select((l, i) => i == 0 ? l : (l.Item1, $"{l.Item2} · {extra}")).ToArray();
            }
            p.Children.Add(Choices(levels, Math.Clamp(_s.MicSuppression, 0, 3), i =>
            {
                Set(nameof(AppSettings.MicSuppression), x => x.MicSuppression = i);
                Rebuild();
            }, width: 110));
            p.Children.Add(Hint("Takes fans, hum and keyboard noise out of your voice. The level bar shows your mic before it. " +
                                "Sensitivity, loudness limit and the rest are in Settings → Audio."));
            StartMeter();
        }
        return p;
    }

    private FrameworkElement Keys()
    {
        var p = Page("Your keys", "Click a box, then press the keys you want. They work anywhere, even in a game.");
        HotkeyBox Key(string name, Func<string> get, Action<AppSettings, string> set)
        {
            var b = new HotkeyBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Text = get() };
            b.TextChanged += (_, _) =>
            {
                if (b.Text == "Press keys…" || b.Text.EndsWith("+…") || b.Text.Trim() == get()) return;
                string v = b.Text.Trim();
                Set(name, x => set(x, v));
                ShowClashes();
            };
            return b;
        }
        p.Children.Add(Label("Save the replay"));
        p.Children.Add(Key(nameof(AppSettings.HotkeySaveReplay), () => _s.HotkeySaveReplay, (x, v) => x.HotkeySaveReplay = v));
        p.Children.Add(Hint("The one you'll use most. Saves the last moments as a clip."));
        p.Children.Add(Label("Pick an area and record"));
        p.Children.Add(Key(nameof(AppSettings.HotkeyRegion), () => _s.HotkeyRegion, (x, v) => x.HotkeyRegion = v));
        p.Children.Add(Label("Turn the replay on or off (optional)"));
        p.Children.Add(Key(nameof(AppSettings.HotkeyToggleReplay), () => _s.HotkeyToggleReplay, (x, v) => x.HotkeyToggleReplay = v));
        _clash = Hint("");
        _clash.SetResourceReference(TextBlock.ForegroundProperty, "Brick");
        p.Children.Add(_clash);
        ShowClashes();
        p.Children.Add(Hint("Esc puts a box back, Backspace clears it. Pause, finish and cancel have no keys at first " +
                            "(the control bar does them); set them in Settings → Hotkeys if you like."));
        return p;
    }

    private TextBlock? _clash;
    private string _codeText = "", _codeResult = "";

    /// <summary>The same key twice, or a key NVIDIA's own overlay uses.</summary>
    private void ShowClashes()
    {
        if (_clash == null) return;
        var keys = new[] { _s.HotkeySaveReplay, _s.HotkeyRegion, _s.HotkeyToggleReplay, _s.HotkeyPause, _s.HotkeyStop, _s.HotkeyCancel,
                           _s.HotkeySaveReplay2, _s.HotkeySaveReplay3 }
            .Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        var twice = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var lines = new List<string>();
        if (twice.Count > 0) lines.Add($"{string.Join(", ", twice)} is set for two things; only one of them will work.");
        bool nvidia = _screen?.VendorId == 0x10DE || (_have.Values.Any(l => l.Contains(EncoderKind.Nvenc)));
        if (nvidia && keys.Any(k => k.Equals("Alt+F10", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Alt+F10 is also NVIDIA's own Instant Replay key. If that's switched on in the NVIDIA overlay, one of them won't get it; the last page checks.");
        _clash.Text = string.Join(" ", lines);
        _clash.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement LookPage()
    {
        var p = Page("Make it yours", "A quick look for Persocord. Skip it if you like; this and much more is in Settings → Customization.");
        p.Children.Add(Label("Ready-made looks"));
        var wrap = new WrapPanel();
        foreach (var preset in LookPresets)
        {
            bool on = _look.Mode == preset.Mode && _look.Surface == preset.Surface &&
                      string.Equals(_look.Accent, preset.Accent, StringComparison.OrdinalIgnoreCase);
            wrap.Children.Add(PresetCard(preset.Name, preset.Detail, preset.Mode, preset.Surface, preset.Accent, on, () =>
            {
                _look.Mode = preset.Mode;
                if (preset.Mode == ThemeMode.Light) _look.LightSurface = preset.Surface; else _look.DarkSurface = preset.Surface;
                _look.Accent = preset.Accent;
                Preview();
            }));
        }
        p.Children.Add(wrap);

        p.Children.Add(Label("Accent colour"));
        var swatches = new WrapPanel();
        foreach (var (name, hex) in ThemeApplier.AccentPresets)
        {
            bool on = string.Equals(_look.Accent, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Cursor = Cursors.Hand, ToolTip = name,
                BorderThickness = new Thickness(on ? 3 : 0),
            };
            dot.SetResourceReference(Border.BorderBrushProperty, "Ink");
            dot.MouseLeftButtonUp += (_, _) => { _look.Accent = hex; Preview(); };
            swatches.Children.Add(dot);
        }
        p.Children.Add(swatches);

        p.Children.Add(Label("Windows"));
        if (WindowLooks.GlassSupported)
        {
            var all = new[] { _look.ClipList, _look.SettingsWindow, _look.Editor, _look.Popups };
            p.Children.Add(Choices(new[] { ("Solid", "Clean and simple"), ("Glass", "Frosted, your desktop shows through") },
                all.All(w => w.Glass) ? 1 : 0, i => { foreach (var w in all) w.Glass = i == 1; Preview(); }, width: 190));
        }
        else p.Children.Add(Hint("Glass windows need Windows 11 (22H2 or later)."));
        p.Children.Add(Hint("You can change all of this later in Settings → Customization."));
        return p;
    }

    private FrameworkElement TryIt()
    {
        var p = Page("Try it", "Everything's saved. Here's a quick check that it all works.");
        var checks = new StackPanel();
        p.Children.Add(Card(checks));
        checks.Children.Add(Status(true, "Your settings are saved", "All of it can be changed in Settings."));
        var encoderRow = new ContentControl { Content = Status(null, "Checking your graphics card…", "") };
        checks.Children.Add(encoderRow);
        _ = CheckEncoderAsync(encoderRow);

        if (!_s.ReplayEnabled)
        {
            checks.Children.Add(Status(null, "The replay is off", "Switch it on any time from the clip list."));
            p.Children.Add(Section("Record an area"));
            p.Children.Add(KeyCaps(_s.HotkeyRegion));
            p.Children.Add(Hint("Press it whenever you like after finishing: pick an area, and it records."));
            return p;
        }

        if (_s.ReplayGamesOnly && !_app.Replay.IsRunning)
        {
            checks.Children.Add(Status(null, "The replay is waiting for a game", "It starts by itself when one runs."));
            p.Children.Add(Section("Save the replay"));
            p.Children.Add(KeyCaps(_s.HotkeySaveReplay));
            p.Children.Add(Hint("Press it during a game to save the last moments. If a game isn't recognised, " +
                                "add it from the tray menu while it's open."));
            return p;
        }
        checks.Children.Add(Status(true, "The replay is running", $"Keeping the last {LengthText(_s.ReplaySeconds)}."));
        var taken = _app.HotkeysTaken;
        if (taken.Count > 0)
            checks.Children.Add(Status(false, $"{string.Join(", ", taken)} {(taken.Count == 1 ? "is" : "are")} taken by another program",
                "Often NVIDIA's overlay or a game. Go back to Keys and pick another, or free it in that program."));
        p.Children.Add(Section("Press your replay key now"));
        if (string.IsNullOrWhiteSpace(_s.HotkeySaveReplay))
        {
            p.Children.Add(Hint("You haven't set a replay key. Go back to Keys to pick one."));
            return p;
        }
        p.Children.Add(KeyCaps(_s.HotkeySaveReplay));
        _tryStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 12, 0, 0),
            Text = "Getting the replay ready… (it restarts when its settings change)" };
        _tryStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(_tryStatus);
        p.Children.Add(Hint($"Later, {_s.HotkeyRegion} picks an area to record."));
        Listen();
        return p;
    }

    /* ---- the test --------------------------------------------------------- */

    private void Listen()
    {
        StopListening();
        _onClip = (title, _, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tryStatus == null || title != "Replay saved") return;
            _tryStatus.Text = "✓ It works! Your first replay is being saved. You'll find it in the clip list.";
            _tryStatus.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            _tryStatus.FontWeight = FontWeights.SemiBold;
        }));
        _onNotify = (title, message) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_tryStatus == null || !title.StartsWith("Replay", StringComparison.Ordinal) || title == "Replay saved") return;
            _tryStatus.Text = $"Not saved yet: {message} Wait a few seconds and press it again.";
            _tryStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brick");
        }));
        _app.NotifyClip += _onClip;
        _app.Notify += _onNotify;

        // Until the replay holds a few seconds (it may have just restarted
        // with the new settings), the page says so rather than inviting a
        // save that would come out empty.
        _ready = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var waitingSince = DateTime.UtcNow;
        _ready.Tick += (_, _) =>
        {
            if (_tryStatus == null) { _ready?.Stop(); return; }
            if (_app.Replay.SecondsHeld < 5)
            {
                // Still nothing after half a minute: say so instead of waiting forever.
                if ((DateTime.UtcNow - waitingSince).TotalSeconds > 30 && _tryStatus.Text.StartsWith("Getting", StringComparison.Ordinal))
                {
                    _tryStatus.Text = "The replay hasn't started yet. Finish here, then check Settings → Replay (Settings → About → diagnostics shows why).";
                    _tryStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brick");
                    _ready?.Stop();
                }
                return;
            }
            _ready?.Stop();
            if (_tryStatus.Text.StartsWith("Getting", StringComparison.Ordinal))
            {
                _tryStatus.Text = "Ready. Press it now.";
                _tryStatus.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            }
        };
        _ready.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _ready;

    private void StopListening()
    {
        if (_onClip != null) _app.NotifyClip -= _onClip;
        if (_onNotify != null) _app.Notify -= _onNotify;
        _onClip = null;
        _onNotify = null;
        _tryStatus = null;
        _ready?.Stop();
        _ready = null;
    }

    private async Task CheckEncoderAsync(ContentControl row)
    {
        try
        {
            var (encoder, codec) = await EncoderDetector.ResolveAsync(_s.ReplayEncoder, _s.ReplayCodec);
            row.Content = encoder == EncoderKind.X264
                ? Status(false, "No graphics-card encoder passed its test",
                    "Recording uses the CPU, which is heavier while gaming. Updating your graphics driver usually fixes it; " +
                    "Settings → Replay → Quality can test again.")
                : Status(true, $"{EncoderDetector.Friendly(encoder)} works", $"Recording {EncoderDetector.CodecName(codec)} on your graphics card.");
        }
        catch (Exception ex)
        {
            Log.Error("setup encoder check", ex);
            row.Content = Status(null, "Couldn't check the encoder", "Settings → Replay → Quality can test it.");
        }
    }

    private async Task LoadEncodersAsync()
    {
        try
        {
            foreach (var c in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
                _have[c] = await EncoderDetector.AvailableForAsync(c);
            if (_page == 2) Rebuild();
        }
        catch (Exception ex) { Log.Error("setup encoders", ex); }
    }

    private List<EncoderKind> Have() =>
        _have.TryGetValue(_s.ReplayCodec, out var have) ? have : new List<EncoderKind> { EncoderKind.Nvenc };

    /// <summary>The replay's choices as they stand in the setup, for working out what they cost.</summary>
    private ReplayCost.Input CostInput() => ReplayCost.Input.From(_s, _screen, Have());

    private bool Works(VideoCodec c) => c == VideoCodec.H264 || !_have.TryGetValue(c, out var list) || list.Count > 0;

    /* ---- saving ----------------------------------------------------------- */

    private bool _applying;
    private bool _closing;

    /// <summary>Puts the choices to use: settings, keys, the replay, the folder and the look.</summary>
    private async Task ApplyAsync(bool final)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            // Onto the settings as they are now, only what was changed here:
            // anything else (set before, or meanwhile elsewhere) stays untouched.
            var before = _app.Settings.Current.Clone();
            var s = before.Clone();
            foreach (var (_, apply) in _changes) apply(s);
            if (final) s.SetupDone = true;
            _app.Settings.Save(s);
            _app.ApplyAll(before);
            if (_lookTouched) LookStore.Save(_look);

            bool folderChanged = _folder.Length > 0 && !string.Equals(Path.GetFullPath(_folder),
                Path.GetFullPath(_app.Library.Folder), StringComparison.OrdinalIgnoreCase);
            if (folderChanged)
            {
                bool move = _app.Library.Items.Count > 0 && Dialog.Show(_closing ? null : this,
                    $"Move your {_app.Library.Items.Count} clip{(_app.Library.Items.Count == 1 ? "" : "s")} to the new folder?",
                    "New clip folder", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (move) await _app.Library.MoveToAsync(_folder);
                else _app.Library.SwitchTo(_folder);
            }
        }
        catch (Exception ex) { Log.Error("setup apply", ex); }
        finally { _applying = false; }
    }

    private async Task FinishAsync()
    {
        if (_done) return;
        _done = true;
        await ApplyAsync(final: true);
        // Marked done even if an earlier save was still busy (moving clips, say).
        if (!_app.Settings.Current.SetupDone)
        {
            var c = _app.Settings.Current.Clone();
            c.SetupDone = true;
            _app.Settings.Save(c);
        }
        Log.Write("Setup finished");
    }

    /// <summary>A change made here: shown at once, and the only kind of thing saved.</summary>
    private void Set(string key, Action<AppSettings> apply)
    {
        apply(_s);
        _changes.RemoveAll(c => c.Key == key);
        _changes.Add((key, apply));
    }

    /// <summary>A value the cards above don't offer, kept as it is.</summary>
    private static TextBlock Now(string what) => Hint($"Now: {what}. It stays like that unless you pick a card.");

    private static string LengthText(int seconds) =>
        seconds < 60 ? $"{seconds} seconds" : seconds % 60 == 0 ? $"{seconds / 60} minute{(seconds == 60 ? "" : "s")}" : $"{seconds} seconds";

    private void Preview()
    {
        _lookTouched = true;
        LookStore.Preview(_look);
        Rebuild();
    }

    /* ---- mic level -------------------------------------------------------- */

    private void StartMeter()
    {
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = !string.IsNullOrEmpty(_s.MicDeviceId)
                ? devices.GetDevice(_s.MicDeviceId)
                : devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
            _meter = new NAudio.CoreAudioApi.WasapiCapture(device);
            var format = _meter.WaveFormat;
            _meter.DataAvailable += (_, e) =>
            {
                float peak = 0;
                if (format.BitsPerSample == 32)
                    for (int i = 0; i + 3 < e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
                else if (format.BitsPerSample == 16)
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
                peak *= Math.Max(0.01f, _s.MicGainPercent / 100f);
                double db = peak > 1e-5 ? 20 * Math.Log10(peak) : -90;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_meterBar == null) return;
                    if (db >= -1) _hotUntil = DateTime.UtcNow.AddSeconds(8);
                    if (_hotNote != null)
                    {
                        var hot = DateTime.UtcNow < _hotUntil ? Visibility.Visible : Visibility.Collapsed;
                        if (_hotNote.Visibility != hot) _hotNote.Visibility = hot;
                    }
                    _shownDb = Math.Max(db, _shownDb - 1.5);
                    _meterBar.Width = Math.Clamp((_shownDb + 70) / 70, 0, 1) * 380;
                }));
            };
            _meter.StartRecording();
        }
        catch (Exception ex) { Log.Error("setup mic meter", ex); }
    }

    private void StopMeter()
    {
        try { _meter?.StopRecording(); _meter?.Dispose(); } catch { }
        _meter = null;
    }

    /* ---- building blocks -------------------------------------------------- */

    private static StackPanel Page(string title, string intro)
    {
        var p = new StackPanel { MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left };
        var t = new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        p.Children.Add(t);
        var i = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 16), LineHeight = 20 };
        i.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(i);
        return p;
    }

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        return t;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 12, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        return t;
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static FrameworkElement Check(string text, Func<bool> get, Action<bool> set, string? hint = null)
    {
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        var c = new CheckBox { Content = text, IsChecked = get() };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        p.Children.Add(c);
        if (hint != null) { var h = Hint(hint); h.Margin = new Thickness(22, 2, 0, 0); p.Children.Add(h); }
        return p;
    }

    private static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) stack.Children.Add(c);
        var b = new Border { Child = stack, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Panel");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        return b;
    }

    private static FrameworkElement Bullet(string glyph, string title, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brass");
        var words = new StackPanel();
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        words.Children.Add(t);
        var d = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        words.Children.Add(d);
        Grid.SetColumn(words, 1);
        g.Children.Add(icon);
        g.Children.Add(words);
        return g;
    }

    /// <summary>A tick, a warning, or a neutral dot, with a line of detail.</summary>
    private static FrameworkElement Status(bool? good, string title, string detail)
    {
        var row = Bullet(good == true ? "" : good == false ? "" : "", title, detail);
        if (row is Grid g && g.Children[0] is TextBlock icon && good != true)
            icon.SetResourceReference(TextBlock.ForegroundProperty, good == false ? "Brick" : "Muted");
        return row;
    }

    private static FrameworkElement KeyCaps(string combo)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                var plus = new TextBlock { Text = "+", FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                row.Children.Add(plus);
            }
            var cap = new Border
            {
                MinWidth = 46, Padding = new Thickness(14, 8, 14, 10), BorderThickness = new Thickness(1, 1, 1, 3), CornerRadius = new CornerRadius(8),
                Child = new TextBlock { Text = parts[i], FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            };
            cap.SetResourceReference(Border.BackgroundProperty, "Panel");
            cap.SetResourceReference(Border.BorderBrushProperty, "Brass");
            row.Children.Add(cap);
        }
        return row;
    }

    private static FrameworkElement Choices((string Title, string Detail)[] options, int selected, Action<int> pick, double width = 150)
    {
        var wrap = new WrapPanel();
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = options[i].Title, FontWeight = FontWeights.SemiBold });
            if (options[i].Detail.Length > 0)
            {
                var d = new TextBlock { Text = options[i].Detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                inner.Children.Add(d);
            }
            var card = new Border
            {
                Width = width, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 9, 12, 9),
                BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = inner,
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
            card.SetResourceReference(Border.BackgroundProperty, "Panel");
            if (i == selected) card.SetResourceReference(Border.BorderBrushProperty, "Brass");
            else card.BorderBrush = Brushes.Transparent;
            card.MouseLeftButtonUp += (_, _) => { if (index != selected) pick(index); };
            wrap.Children.Add(card);
        }
        return wrap;
    }

    private FrameworkElement PresetCard(string name, string detail, ThemeMode mode, SurfaceStyle surface, string accent, bool on, Action pick)
    {
        var sample = _look.Clone();
        sample.Mode = mode;
        var pal = ThemeApplier.For(sample, surface);
        var accentColour = (Color)ColorConverter.ConvertFromString(accent);
        var swatch = new Border
        {
            Height = 52, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(pal.Bezel),
            BorderBrush = new SolidColorBrush(pal.Line), BorderThickness = new Thickness(1),
            Child = new Border
            {
                Margin = new Thickness(8), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(pal.Panel),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Aa", Foreground = new SolidColorBrush(pal.Ink), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                        new Border { Width = 26, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(accentColour) },
                    },
                },
            },
        };
        var inner = new StackPanel();
        inner.Children.Add(swatch);
        inner.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 0) });
        var d = new TextBlock { Text = detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        inner.Children.Add(d);
        var card = new Border
        {
            Width = 180, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10), BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand, Child = inner,
        };
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        if (on) card.SetResourceReference(Border.BorderBrushProperty, "Brass");
        else card.BorderBrush = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) => { if (!on) pick(); };
        return card;
    }

    private static FrameworkElement SliderRow(double min, double max, double value, Action<double> changed, string format)
    {
        var g = new Grid { Width = 380, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { Text = string.Format(format, value), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        s.ValueChanged += (_, e) => { t.Text = string.Format(format, e.NewValue); changed(e.NewValue); };
        Grid.SetColumn(t, 1);
        g.Children.Add(s);
        g.Children.Add(t);
        return g;
    }

    private static long DriveTotal(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).TotalSize; }
        catch { return 0; }
    }

    private static string Gb(long b) => b < 1073741824L ? $"{b / 1048576.0:0} MB" : $"{b / 1073741824.0:0} GB";

}

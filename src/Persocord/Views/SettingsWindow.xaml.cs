using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private readonly AppSettings _draft;
    private CustomizationView? _custom;
    // What was last saved and applied: each save is compared with this. (The
    // draft becomes the live settings after the first save, so comparing
    // with those saw no change on a second save: a new accent colour, or a
    // replay change, was then never applied.)
    private AppSettings _applied;
    private List<AudioSource.Device> _mics = new();
    private List<AudioSource.Device> _outs = new();
    private string _originalFolder;

    /* ---- the microphone's level meter ------------------------------------- */

    private NAudio.CoreAudioApi.WasapiCapture? _meter;
    private double _shownDb = -90;
    private const double MeterWidth = 320, MeterFloor = -70;

    private void ShowMicValues()
    {
        TxtMicGate.Text = $"{SldMicGate.Value:0} dB";
        TxtMicLimiter.Text = $"{SldMicLimiter.Value:0} dB";
        MicGateMarker.Margin = new Thickness(Math.Clamp((SldMicGate.Value - MeterFloor) / -MeterFloor, 0, 1) * MeterWidth - 1, -3, 0, -3);
    }

    /// <summary>Listens to the chosen mic while Settings is open (shared, so a running replay is unaffected).</summary>
    private void StartMicMeter()
    {
        try
        {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            // The mic picked in the list (even before saving), so trying another
            // one shows its level at once.
            var id = SelectedDeviceId(_mics, CmbMicDevice.SelectedIndex)
                     ?? (CmbMicDevice.SelectedIndex > 0 ? _app.Settings.Current.MicDeviceId : null);
            var device = !string.IsNullOrEmpty(id)
                ? devices.GetDevice(id)
                : devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
            _meter = new NAudio.CoreAudioApi.WasapiCapture(device);
            var format = _meter.WaveFormat;
            _meterGain = (float)Math.Max(0.01, SldMicGain.Value / 100);
            _meter.DataAvailable += (_, e) =>
            {
                float peak = 0;
                if (format.BitsPerSample == 32)
                    for (int i = 0; i + 3 < e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
                else if (format.BitsPerSample == 16)
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
                // With the volume set below, as it moves (it used to be the saved
                // one, so turning a loud mic down didn't show until reopening).
                peak *= _meterGain;
                double db = peak > 1e-5 ? 20 * Math.Log10(peak) : -90;
                Dispatcher.BeginInvoke(new Action(() => ShowLevel(db)));
            };
            _meter.StartRecording();
        }
        catch (Exception ex) { Log.Error("mic meter", ex); }
    }

    private volatile float _meterGain = 1f;
    private DateTime _hotUntil;

    private void ShowLevel(double db)
    {
        // At (or within a hair of) the top: the mic is maxing out. Said under
        // the meter, and kept up for a few seconds after the last time.
        if (db >= -1) _hotUntil = DateTime.UtcNow.AddSeconds(8);
        var hot = DateTime.UtcNow < _hotUntil ? Visibility.Visible : Visibility.Collapsed;
        if (MicHotNote.Visibility != hot) MicHotNote.Visibility = hot;
        // Rises at once, falls gently, so it's easy to read.
        _shownDb = Math.Max(db, _shownDb - 1.5);
        MicMeterBar.Width = Math.Clamp((_shownDb - MeterFloor) / -MeterFloor, 0, 1) * MeterWidth;
        // Lit when it would get through the sensitivity line, dim when it would be silenced.
        MicMeterBar.Opacity = ChkMicGate.IsChecked != true || _shownDb >= SldMicGate.Value ? 1 : 0.4;
    }

    private void StopMicMeter()
    {
        try { _meter?.StopRecording(); _meter?.Dispose(); } catch { }
        _meter = null;
    }

    public SettingsWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        SldMicGate.ValueChanged += (_, _) => ShowMicValues();
        SldMicLimiter.ValueChanged += (_, _) => ShowMicValues();
        Loaded += (_, _) => StartMicMeter();
        CmbMicDevice.SelectionChanged += (_, _) => { if (IsLoaded) { StopMicMeter(); StartMicMeter(); } };
        Closed += (_, _) => StopMicMeter();
        // The replay's video memory, live while this window is open.
        _vramTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _vramTimer.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            // Measured on the timer only, so the figures cover even stretches of time.
            _measured = MeasureReplay() ?? (_app.Replay.ProcessIds().Length > 0 ? _measured : null);
            UpdateReplayVram();
        };
        Loaded += (_, _) => _vramTimer.Start();
        Closed += (_, _) => _vramTimer.Stop();
        _draft = app.Settings.Current.Clone();
        _applied = app.Settings.Current.Clone();
        _originalFolder = app.Library.Folder;

        FillFixedLists();
        LoadDevices();
        LoadValues();

        SldClickSize.ValueChanged += (_, _) => ClickSizeText.Text = $"{(int)SldClickSize.Value}px";
        SldSysGain.ValueChanged   += (_, _) => SysGainText.Text = $"{(int)SldSysGain.Value}%";
        SldMicGain.ValueChanged   += (_, _) =>
        {
            MicGainText.Text = $"{(int)SldMicGain.Value}%";
            _meterGain = (float)Math.Max(0.01, SldMicGain.Value / 100);
        };
        SldCueVolume.ValueChanged += (_, _) => CueVolumeText.Text = $"{(int)SldCueVolume.Value}%";
        CmbFps.SelectionChanged   += (_, _) => UpdateFpsNote();
        TxtDiskPercent.TextChanged += (_, _) => UpdateCapExplain();

        HookCostInputs();
        UpdateReplayEstimate();
        _ = DescribeEncodersAsync();
        ShowDisk();
        UpdateCapExplain();
    }

    /* ------------------------------------------------------------------ */

    private static readonly int[] FpsChoices = { 24, 30, 48, 60, 90, 120, 144, 165, 240 };
    private static readonly int[] CountdownChoices = { 0, 1, 3, 5, 10 };
    private static readonly int[] AudioBitrates = { 96, 128, 160, 192, 256, 320 };

    private static readonly int[] ReplayLengths = { 15, 30, 60, 120, 300, 600 };
    private static readonly int[] ReplayHeights = { 0, 1440, 1080, 720 };
    private static readonly int[] ReplayFpsChoices = { 30, 60, 120, 144 };
    /// <summary>The screens in the replay list: Windows' name, the monitor's own ID, where it sits.</summary>
    private readonly System.Collections.Generic.List<(string Name, string Id, string Rect)> _replayScreens = new();
    /// <summary>The same screens as DirectX sees them (null: the main screen, or one not connected now).</summary>
    private readonly System.Collections.Generic.List<DxgiTarget?> _replayTargets = new();
    private readonly System.Windows.Threading.DispatcherTimer _vramTimer;
    private DxgiTarget? _mainTarget;

    private static int Nearest(int[] choices, int value)
    {
        int best = 0;
        for (int i = 1; i < choices.Length; i++)
            if (Math.Abs(choices[i] - value) < Math.Abs(choices[best] - value)) best = i;
        return best;
    }

    /* ---- search ----------------------------------------------------------------- */

    private readonly List<(TabItem Tab, FrameworkElement Element)> _hits = new();
    private int _hit;

    private void SettingsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        var q = SettingsSearch.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _hits.Clear();
        _hit = 0;
        MarkTabs();
        if (q.Length < 2) { SearchResult.Text = ""; return; }

        foreach (var tab in Tabs.Items.OfType<TabItem>())
        {
            if (((tab.Tag ?? tab.Header) as string)?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) _hits.Add((tab, tab));
            if (tab.Content is DependencyObject root)
                foreach (var el in Descendants(root).OfType<FrameworkElement>())
                    if (Matches(el, q)) _hits.Add((tab, el));
        }
        MarkTabs();
        if (_hits.Count == 0) { SearchResult.Text = "No matches"; return; }
        ShowHit(0);
    }

    private void SettingsSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _hits.Count > 0) { ShowHit((_hit + 1) % _hits.Count); e.Handled = true; }
        if (e.Key == Key.Escape) { SettingsSearch.Text = ""; e.Handled = true; }
    }

    /// <summary>Tabs holding matches stand out, with how many each has.</summary>
    private void MarkTabs()
    {
        foreach (var tab in Tabs.Items.OfType<TabItem>())
        {
            tab.Tag ??= tab.Header;   // the plain name, kept once
            int n = _hits.Count(h => h.Tab == tab);
            if (n == 0)
            {
                tab.Header = tab.Tag;
                tab.ClearValue(ForegroundProperty);
                tab.ClearValue(FontWeightProperty);
            }
            else
            {
                tab.Header = $"{tab.Tag}  · {n}";
                tab.SetResourceReference(ForegroundProperty, "AccentText");
                tab.FontWeight = FontWeights.SemiBold;
            }
        }
    }

    /// <summary>A setting's label, heading, description, tooltip or keywords.</summary>
    private static bool Matches(FrameworkElement el, string q)
    {
        string? text = el switch
        {
            CheckBox c => c.Content as string,
            Label l => l.Content as string,
            TextBlock t => t.Text,
            Expander x => x.Header as string,
            Button b => b.Content as string,
            _ => null,
        };
        return new[] { text, el.ToolTip as string, el.Tag as string }
            .Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>Opens the tab and section holding a match, scrolls to it and flashes it.</summary>
    private void ShowHit(int i)
    {
        _hit = i;
        var (tab, el) = _hits[i];
        SearchResult.Text = _hits.Count > 1 ? $"{i + 1} of {_hits.Count}  ·  Enter for next" : "1 match";
        tab.IsSelected = true;
        for (var p = LogicalTreeHelper.GetParent(el); p != null; p = LogicalTreeHelper.GetParent(p))
            if (p is Expander x) x.IsExpanded = true;
        if (el == tab) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            el.BringIntoView();
            var flash = new System.Windows.Media.Animation.DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(450))
            {
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
            };
            el.BeginAnimation(OpacityProperty, flash);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /* ---- diagnostics ---------------------------------------------------------- */

    private string? _diagPath;

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagButton.IsEnabled = false;
        DiagStatus.Text = "Checking everything… (a few seconds)";
        try
        {
            _diagPath = await Diagnostics.SaveAndCopyAsync(_app);
            DiagStatus.Text = "Report copied: paste it into a chat. It's also saved as a file.";
            DiagFolder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("diagnostics", ex);
            DiagStatus.Text = "The report couldn't be made; the log has the reason.";
        }
        finally { DiagButton.IsEnabled = true; }
    }

    private void DiagFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_diagPath != null) ShareService.ShowInExplorer(_diagPath);
    }

    /* ---- export, import, reset ---------------------------------------------- */

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Persocord settings {DateTime.Now:yyyy-MM-dd}.json",
            Filter = "Persocord settings (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _app.Settings.Export(dlg.FileName);
            BackupStatus.Text = "Saved to " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("export settings", ex);
            BackupStatus.Text = "The file couldn't be written there.";
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Persocord settings (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var before = _app.Settings.Current.Clone();
        var err = _app.Settings.Import(dlg.FileName);
        if (err != null) { BackupStatus.Text = err; return; }
        Reopen(before);
    }

    /// <summary>A settings code: only settings, never the look (that has its own code under Customization).</summary>
    private async void CopySettingsCode_Click(object sender, RoutedEventArgs e)
    {
        var code = ShareCode.Export(_app.Settings.Current);
        bool ok = await ShareService.CopyTextAsync(code);
        BackupStatus.Text = ok
            ? $"Settings code copied ({code.Length} characters). Your folders, devices and look aren't in it."
            : "Couldn't reach the clipboard. Try again.";
    }

    private void ImportSettingsCode_Click(object sender, RoutedEventArgs e)
    {
        var code = Dialog.AskCode(this, "Import a settings code",
            "Paste the settings code a friend sent you (it starts with PERSO1-). Your folders, devices and look stay as they are.");
        if (code == null) return;

        var before = _app.Settings.Current.Clone();
        var incoming = before.Clone();
        int count;
        try { count = ShareCode.Import(code, incoming); }
        catch (FormatException ex) { Dialog.Show(this, ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (count == 0) { BackupStatus.Text = "That code matches the default settings, so there was nothing to change."; return; }
        _app.Settings.Save(incoming);
        Reopen(before);
        Dialog.Show($"Imported {count} setting{(count == 1 ? "" : "s")}. They're in effect now.", "Import",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var answer = Dialog.Show(this,
            "Put every option back to how Persocord ships?\n\nYour clips and the clips folder stay exactly as they are.",
            "Reset all settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        // A second, final check: this can't be undone.
        var really = Dialog.Show(this,
            "Last check: every setting (hotkeys, replay, sounds, everything) goes back to default, and this can't be undone. Your look (colours and style) stays; it has its own reset under Customization.\n\n" +
            "Tip: Export settings first if you might want them back.\n\nReset now?",
            "Reset all settings — are you sure?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (really != MessageBoxResult.Yes) return;
        var before = _app.Settings.Current.Clone();
        _app.Settings.ResetToDefaults();
        Reopen(before);
    }

    /// <summary>Applies the new settings everywhere and shows this window afresh with them.</summary>
    private void Reopen(AppSettings before)
    {
        var app = _app;
        app.ApplyAll(before);
        Close();
        app.ShowSettings();
    }

    private void ReplayEstimate_Changed(object sender, RoutedEventArgs e) => UpdateReplayEstimate();

    private double ToastSecondsValue() =>
        double.TryParse(TxtToastSeconds.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.5, 30) : 4;

    /// <summary>Shows a notification with the choices on screen, before saving.</summary>
    private void TestToast_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings.Current.Clone();
        s.ToastPosition = (ToastPosition)Math.Max(0, CmbToastPos.SelectedIndex);
        s.ToastSeconds = ToastSecondsValue();
        Toast.Show("Replay saved", "The last 30 seconds of your game. Copied, ready to paste.", s);
    }

    /* ---- what the replay costs ------------------------------------------------ */

    /// <summary>The replay's choices as they stand on this page (saved or not).</summary>
    private ReplayCost.Input? CostInput()
    {
        if (CmbReplayRes == null || CmbReplayFps == null || CmbReplayCodec == null || CmbReplayEncoder == null ||
            CmbReplayLength == null || CmbReplayLength.SelectedIndex < 0) return null;
        int pick = Math.Max(0, CmbReplayScreen?.SelectedIndex ?? 0);
        var screen = (pick < _replayTargets.Count ? _replayTargets[pick] : null) ?? _mainTarget;
        var chosen = CmbReplayEncoder.SelectedItem is System.Windows.Controls.ComboBoxItem ci && ci.Tag is EncoderKind ck ? ck : EncoderKind.Auto;
        // What works for this format: the encoders not greyed out in the list.
        var have = CmbReplayEncoder.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .Where(i => i.IsEnabled && i.Tag is EncoderKind k && k != EncoderKind.Auto)
            .Select(i => (EncoderKind)i.Tag).ToList();
        if (have.Count == 0) have.Add(EncoderKind.X264);
        int.TryParse(TxtReplayQuality?.Text, out var q);
        return new ReplayCost.Input(
            screen,
            ReplayHeights[Math.Max(0, CmbReplayRes.SelectedIndex)],
            ReplayFpsChoices[Math.Max(0, CmbReplayFps.SelectedIndex)],
            ReplaySecondsChosen(),
            (VideoCodec)Math.Max(0, CmbReplayCodec.SelectedIndex),
            chosen, have,
            CmbReplayRate?.SelectedIndex == 1, (int)Math.Round(Mbps(TxtReplayBitrate?.Text) * 1000), q > 0 ? q : 21,
            ChkRtPc?.IsChecked == true, ChkRtMic?.IsChecked == true,
            ChkAppTracks?.IsChecked == true, Math.Max(0, CmbMaxApps?.SelectedIndex ?? 3) + 1,
            Math.Max(0, CmbMicSuppression?.SelectedIndex ?? 0),
            ChkReplayLight?.IsChecked == true);
    }

    private bool _costHooked;
    private bool _logoHooked;

    /// <summary>Everything on other pages that changes the replay's cost updates it too.</summary>
    private void HookCostInputs()
    {
        if (_costHooked) return;
        _costHooked = true;
        foreach (var c in new[] { ChkReplayLight, ChkRtPc, ChkRtMic, ChkAppTracks })
        {
            c.Checked += (_, _) => UpdateReplayEstimate();
            c.Unchecked += (_, _) => UpdateReplayEstimate();
        }
        CmbMaxApps.SelectionChanged += (_, _) => UpdateReplayEstimate();
        CmbMicSuppression.SelectionChanged += (_, _) => UpdateReplayEstimate();
        TxtSafeRam.TextChanged += (_, _) => UpdateReplayEstimate();
    }

    /// <summary>What the chosen replay costs, every way it costs, and what each other choice would change.</summary>
    private void UpdateReplayEstimate()
    {
        if (ReplayEstimate == null || CmbReplayLength.SelectedIndex < 0) return;
        if (ReplayCustomRow != null)
            ReplayCustomRow.Visibility = IsCustomLength ? Visibility.Visible : Visibility.Collapsed;
        var input = CostInput();
        if (input == null) return;
        var now = ReplayCost.Estimate(input);

        // Disk: the size line, and how the rate setting in use moves it.
        string rate;
        if (input.FixedRate)
        {
            var steps = new[] { 4000, 6000, 8000, 12000, 20000, 30000 }
                .Select(k => $"{k / 1000} → {ReplayCost.Mb(ReplayCost.Estimate(input with { BitrateKbps = k }).ClipMbPerMinute)}");
            rate = "Bitrate only changes file size (MB a minute): " + string.Join(" · ", steps) + ".";
        }
        else
        {
            var steps = new[] { 16, 18, 21, 24, 28 }
                .Select(l => $"{l} → {ReplayCost.Mb(ReplayCost.Estimate(input with { Quality = l }).ClipMbPerMinute)}");
            rate = "Quality only changes sharpness and file size (MB a minute, typical game): " + string.Join(" · ", steps) + ".";
        }
        ReplayEstimate.Text =
            $"≈ {now.VideoKbps / 1000:0.#} Mbps picture + {now.SoundKbps / 1000:0.##} Mbps sound ({now.LiveTracks} track{(now.LiveTracks == 1 ? "" : "s")}) · " +
            $"each replay ≈ {ReplayCost.Mb(now.ReplayMb)} · ≈ {ReplayCost.Mb(now.ClipMbPerMinute)} a minute · " +
            $"{ReplayCost.Mb(now.LoopMb)} of disk while on.\n{rate}";

        ShowCostRows(input, now);
        LabelChoices(input, now);
        ShowSoundCosts(input, now);
        UpdateReplayVram();
        UpdateRecordLikeNote();
    }

    /// <summary>The card's rows: each cost, a bar against its limit, and what sets it.</summary>
    private void ShowCostRows(ReplayCost.Input input, ReplayCost.Result r)
    {
        if (ReplayCostRows == null) return;
        int vramLimit = int.TryParse(TxtSafeVram?.Text, out var lv) ? Math.Clamp(lv, 128, 8192) : 512;
        int ramLimit = int.TryParse(TxtSafeRam?.Text, out var lr) ? Math.Max(64, lr) : 500;
        string apps = input.AppTracks && !input.Light ? "app tracks, " : "";
        var rows = new (string Name, string Value, double? Fill, string SetBy)[]
        {
            ("Video memory", $"≈ {r.VramMb} MB", r.VramMb / (double)vramLimit,
                $"of the {vramLimit} MB limit · set by resolution, format, encoder"),
            r.CpuEncoder
                ? ("Graphics card", "not used", (double?)null, "encoding on the CPU instead")
                : ("Video encoder", $"≈ {r.EncoderPercent:0}% busy", r.EncoderPercent / 100,
                   "the card's own encoder, not the part games use · set by resolution, frame rate, format"),
            ("CPU", $"≈ {ReplayCost.Cpu(r.CpuPercent)}", r.CpuPercent / 100,
                $"of all {Environment.ProcessorCount} threads · set by {(r.CpuEncoder || r.Copied ? "encoder, resolution, frame rate, " : "frame rate, ")}{apps}mic noise suppression, Lighter on the CPU"),
            ("Memory (RAM)", $"≈ {r.RamMb} MB", r.RamMb / (double)ramLimit,
                $"of the {ramLimit} MB limit · set by {(r.Copied || r.CpuEncoder ? "resolution, encoder, " : "")}{apps}sound tracks"),
            ("Disk while on", $"≈ {ReplayCost.Mb(r.LoopMb)}", null,
                "the loop kept on disk · set by length, bitrate or quality, frame rate, sound tracks"),
            ("Each replay", $"≈ {ReplayCost.Mb(r.ReplayMb)}", null,
                $"≈ {ReplayCost.Mb(r.ClipMbPerMinute)} a minute · set by length, bitrate or quality, resolution, frame rate"),
        };

        var g = ReplayCostRows;
        g.Children.Clear();
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            var (name, value, fill, setBy) = rows[i];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = name, FontSize = 12.5, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Top };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            Place(g, label, i, 0);
            if (fill is double f)
            {
                double clamped = Math.Clamp(f, 0, 1);
                var bar = new System.Windows.Shapes.Rectangle { Width = Math.Max(2, clamped * 110), HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 2, RadiusY = 2 };
                bar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, f > 0.85 ? "Brick" : "Brass");
                var track = new Border { Width = 110, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                                         VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0), Child = bar, ClipToBounds = true };
                track.SetResourceReference(Border.BackgroundProperty, "Line");
                Place(g, track, i, 1);
            }
            var val = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Top };
            val.SetResourceReference(TextBlock.ForegroundProperty, fill is > 1 ? "Brick" : "Ink");
            Place(g, val, i, 2);
            var why = new TextBlock { Text = setBy, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 3) };
            why.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Place(g, why, i, 3);
        }

        if (ReplayLightCost != null)
        {
            var other = ReplayCost.Estimate(input with { Light = !input.Light });
            double saved = Math.Abs(other.CpuPercent - r.CpuPercent);
            int ram = Math.Abs(other.RamMb - r.RamMb);
            ReplayLightCost.Text = input.Light
                ? $"On: saving about {ReplayCost.Cpu(saved)} CPU and {ram} MB RAM here."
                : $"Would save about {ReplayCost.Cpu(saved)} CPU and {ram} MB RAM here.";
        }
        if (SafetyTypical != null)
            SafetyTypical.Text = $"With your replay settings: about {ReplayCost.Cpu(r.CpuPercent)} CPU, {r.RamMb} MB memory " +
                                 $"and {r.VramMb} MB video memory (the Replay page breaks it down).";

        static void Place(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
    }

    /// <summary>Each other choice in the replay's lists, with what picking it would change.</summary>
    private void LabelChoices(ReplayCost.Input input, ReplayCost.Result now)
    {
        void Mark(System.Windows.Controls.ComboBox box, Func<int, ReplayCost.Input?> change)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (box.Items[i] is not System.Windows.Controls.ComboBoxItem item || item.Content is not CostOption opt) continue;
                if (i == box.SelectedIndex) { opt.Delta = "current"; continue; }
                var alt = item.IsEnabled ? change(i) : null;
                if (alt == null) { opt.Delta = ""; continue; }
                var d = ReplayCost.Delta(now, ReplayCost.Estimate(alt));
                opt.Delta = d.Length == 0 ? "about the same" : d;
            }
        }
        int screenH = input.Screen?.Desktop.Height ?? 1080;
        Mark(CmbReplayRes, i => ReplayHeights[i] != 0 && ReplayHeights[i] >= screenH ? null : input with { Height = ReplayHeights[i] });
        Mark(CmbReplayFps, i => input with { Fps = ReplayFpsChoices[i] });
        Mark(CmbReplayLength, i => i < ReplayLengths.Length ? input with { Seconds = ReplayLengths[i] } : null);
        Mark(CmbReplayCodec, i => input with { Codec = (VideoCodec)i });
        Mark(CmbReplayEncoder, i => CmbReplayEncoder.Items[i] is System.Windows.Controls.ComboBoxItem ci && ci.Tag is EncoderKind k
            ? input with { Chosen = k } : null);
        Mark(CmbMaxApps, i => input.AppTracks && !input.Light ? input with { MaxApps = i + 1 } : null);
        Mark(CmbMicSuppression, i => input.Mic ? input with { Denoise = i } : null);
    }

    /// <summary>The Audio page's choices that cost the replay something, said where they're made.</summary>
    private void ShowSoundCosts(ReplayCost.Input input, ReplayCost.Result now)
    {
        string Diff(ReplayCost.Input other)
        {
            var d = ReplayCost.Delta(now, ReplayCost.Estimate(other), 4);
            return d.Length == 0 ? "about the same" : d;
        }
        if (AppTracksCost != null)
            AppTracksCost.Text = input.Light
                ? "Off while \"Lighter on the CPU\" is on (Replay page)."
                : input.AppTracks
                    ? $"Replay cost: every app slot is recorded all the time. Turning this off: {Diff(input with { AppTracks = false })}."
                    : $"Replay cost of turning this on ({input.MaxApps} app slots): {Diff(input with { AppTracks = true })}.";
        if (MicSuppressionCost != null)
            MicSuppressionCost.Text = !input.Mic
                ? "The replay's mic is off, so it costs nothing there."
                : input.Denoise > 0
                    ? $"Replay cost of the noise suppression: turning it off would be {Diff(input with { Denoise = 0 })}."
                    : $"Turning it on: {Diff(input with { Denoise = 2 })} (any level costs the same).";
        if (ReplayTracksCost != null)
        {
            var parts = new List<string>();
            if (input.Pc) parts.Add($"PC audio off: {Diff(input with { Pc = false })}");
            else parts.Add($"PC audio on: {Diff(input with { Pc = true })}");
            if (input.Mic) parts.Add($"mic off: {Diff(input with { Mic = false })}");
            else parts.Add($"mic on: {Diff(input with { Mic = true })}");
            ReplayTracksCost.Text = string.Join(". ", parts) + ".";
        }
    }

    // Measured use of the running replay (its FFmpegs), between two timer ticks.
    private readonly Dictionary<int, TimeSpan> _cpuWas = new();
    private (double Cpu, double RamMb)? _measured;
    private DateTime _cpuAt;

    /// <summary>CPU (% of the whole CPU) and memory (MB) the replay's FFmpegs use right now; null until two samples exist.</summary>
    private (double Cpu, double RamMb)? MeasureReplay()
    {
        var ids = _app.Replay.ProcessIds();
        if (ids.Length == 0) { _cpuWas.Clear(); return null; }
        var t = DateTime.UtcNow;
        double secs = (t - _cpuAt).TotalSeconds;
        double busy = 0, ram = 0;
        bool all = true;
        foreach (var id in ids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(id);
                var cpu = p.TotalProcessorTime;
                ram += p.WorkingSet64 / 1048576.0;
                if (_cpuWas.TryGetValue(id, out var was)) busy += (cpu - was).TotalSeconds;
                else all = false;
                _cpuWas[id] = cpu;
            }
            catch { all = false; }
        }
        foreach (var gone in _cpuWas.Keys.Where(k => !ids.Contains(k)).ToList()) _cpuWas.Remove(gone);
        _cpuAt = t;
        if (!all || secs <= 0.5 || secs > 30) return null;
        return (busy / secs / Math.Max(1, Environment.ProcessorCount) * 100, ram);
    }

    /// <summary>
    /// Video memory by resolution, to compare; what the running replay really
    /// uses; and a warning when the choices go past the video memory limit.
    /// </summary>
    private void UpdateReplayVram()
    {
        if (_app == null || ReplayVram == null || ReplayVramSizes == null || ReplayVramWarn == null) return;
        var input = CostInput();
        if (input == null) return;
        var now = ReplayCost.Estimate(input);
        int limit = int.TryParse(TxtSafeVram?.Text, out var lv) ? Math.Clamp(lv, 128, 8192) : 512;
        var g = ReplayService.GuessVideoMemory(input.Screen, input.Height, input.Codec, input.Chosen, input.Have);
        var replay = _app.Replay;
        bool running = replay.VideoMemoryNowMb > 0;
        bool same = running && replay.RunningPlan == g.Key;
        string card = input.Screen != null && input.Screen.VideoMemoryBytes > 0
            ? $" The card has {input.Screen.VideoMemoryBytes / 1073741824.0:0.#} GB." : "";

        // While it runs, the real figures (the settings it's running with may differ from these).
        var m = _measured;
        if (running || m != null)
        {
            var bits = new List<string>();
            if (m is { } mm) bits.Add($"CPU {ReplayCost.Cpu(mm.Cpu)}");
            if (m is { } mr) bits.Add($"RAM {mr.RamMb:0} MB");
            if (running) bits.Add($"video memory {replay.VideoMemoryNowMb:0} MB (most {replay.VideoMemoryPeakMb:0} MB)");
            ReplayVram.Text = "Measured now: " + string.Join(" · ", bits) +
                              (same ? "." : ", with the settings it's running with (save to switch).") + card;
            ReplayVram.Visibility = Visibility.Visible;
        }
        else
        {
            ReplayVram.Text = card.Trim();
            ReplayVram.Visibility = card.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Video memory at each size, side by side, to plan with.
        var sizes = new List<string>();
        int screenH = input.Screen?.Desktop.Height ?? 1080;
        foreach (var hgt in ReplayHeights)
        {
            if (hgt != 0 && hgt >= screenH) continue;
            var o = ReplayService.GuessVideoMemory(input.Screen, hgt, input.Codec, input.Chosen, input.Have);
            string name = hgt == 0 ? $"{screenH}p" : $"{hgt}p";
            sizes.Add(hgt == input.Height ? $"{name} ≈ {o.Mb} (this)" : $"{name} ≈ {o.Mb}");
        }
        string how = g.Copied || g.Encoder == EncoderKind.X264
            ? "Frames go through memory here, so the card holds little; the CPU does more instead."
            : g.FullSize
                ? "At the screen's own size the encoder queues full screen frames, the biggest share; a smaller resolution cuts it most."
                : "Resolution decides most of it; frame rate, bitrate and length barely change it.";
        ReplayVramSizes.Text = $"Video memory by resolution: {string.Join(" · ", sizes)} MB. {how}";

        // With these exact settings running, the measured figure says it best.
        int expect = same ? (int)Math.Round(replay.VideoMemoryPeakMb) : now.VramMb;
        bool over = expect > limit;
        bool close = !over && expect > limit * 0.85;
        ReplayVramWarn.Visibility = over || close ? Visibility.Visible : Visibility.Collapsed;
        ReplayVramWarn.Text = over
            ? $"Over the {limit} MB video memory limit: the replay would keep restarting and lose what it held. Raise the limit (Safety limits) to about {RoundUp(expect * 1.2)} MB, or pick a smaller resolution."
            : $"Close to the {limit} MB video memory limit. About {RoundUp(expect * 1.2)} MB leaves room for busy moments.";
        if (SafeVramNote != null)
            SafeVramNote.Text = $"Your replay quality needs about {expect} MB" + (over ? " — more than this limit." : ".");
    }

    private static int RoundUp(double mb) => (int)Math.Ceiling(mb / 50) * 50;

    private bool IsCustomLength => CmbReplayLength.SelectedIndex == ReplayLengths.Length;

    /// <summary>A preset, or the custom number of seconds (5 to 3600).</summary>
    private int ReplaySecondsChosen() => IsCustomLength
        ? (int.TryParse(TxtReplayCustom?.Text?.Trim(), out var v) ? Math.Clamp(v, 5, 3600) : 300)
        : ReplayLengths[Math.Clamp(CmbReplayLength.SelectedIndex, 0, ReplayLengths.Length - 1)];

    private static double Mbps(string? text) =>
        double.TryParse((text ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 200) : 0;

    /// <summary>Area recordings: a quality level or a fixed bitrate (only the one in use can be typed in).</summary>
    private void RecordRate_Changed(object sender, RoutedEventArgs e)
    {
        if (RecordQualityPanel == null || RecordBitratePanel == null || CmbRecordRate == null) return;
        bool fixedRate = CmbRecordRate.SelectedIndex == 1;
        RecordQualityPanel.IsEnabled = !fixedRate;
        RecordBitratePanel.IsEnabled = fixedRate;
        RecordQualityPanel.Opacity = fixedRate ? 0.4 : 1;
        RecordBitratePanel.Opacity = fixedRate ? 1 : 0.4;
        UpdateRecordEstimate();
    }

    /// <summary>Area recordings follow the replay's quality, or have their own.</summary>
    private void RecordLikeReplay_Changed(object sender, RoutedEventArgs e)
    {
        if (RecordQualityOwn == null || ChkRecordLikeReplay == null) return;
        RecordQualityOwn.Visibility = ChkRecordLikeReplay.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        UpdateRecordLikeNote();
        UpdateRecordEstimate();
    }

    private void RecordEstimate_Changed(object sender, RoutedEventArgs e) => UpdateRecordEstimate();

    /// <summary>What "same as the replay" means right now, spelled out.</summary>
    private void UpdateRecordLikeNote()
    {
        if (RecordLikeNote == null || CmbReplayCodec == null || CmbReplayRes == null || CmbReplayFps == null) return;
        bool like = ChkRecordLikeReplay?.IsChecked == true;
        if (!like) { RecordLikeNote.Text = "Off: area recordings use the quality below."; return; }
        int h = ReplayHeights[Math.Max(0, CmbReplayRes.SelectedIndex)];
        string codec = EncoderDetector.CodecName((VideoCodec)Math.Max(0, CmbReplayCodec.SelectedIndex));
        string size = h == 0 ? "the area's own size" : $"up to {h}p";
        int fps = ReplayFpsChoices[Math.Max(0, CmbReplayFps.SelectedIndex)];
        string rate = CmbReplayRate?.SelectedIndex == 1
            ? $"{Mbps(TxtReplayBitrate?.Text):0.#} Mbps"
            : $"quality level {(int.TryParse(TxtReplayQuality?.Text, out var q) ? q : 21)}";
        RecordLikeNote.Text = $"{codec} · {size} · {fps} fps · {rate}, from Replay → Quality. One place to set both.";
    }

    /// <summary>A rough idea of what an area recording takes, for a full-screen area.</summary>
    private void UpdateRecordEstimate()
    {
        if (RecordEstimate == null || CmbResolution == null || CmbFps == null || CmbRecordCodec == null) return;
        int screenH = _mainTarget?.Desktop.Height ?? 1080;
        int height = CmbResolution.SelectedIndex switch { 1 => 1440, 2 => 1080, 3 => 720, _ => screenH };
        height = Math.Min(height, screenH);
        int fps = FpsChoices[Math.Max(0, CmbFps.SelectedIndex)];
        bool fixedRate = CmbRecordRate?.SelectedIndex == 1;
        double mbps = Mbps(TxtBitrate?.Text);
        int.TryParse(TxtCrf?.Text, out var q);
        if (q <= 0) q = 19;
        double codecFactor = CmbRecordCodec.SelectedIndex switch { 1 => 0.7, 2 => 0.6, _ => 1.0 };
        double kbps = fixedRate && mbps > 0 ? mbps * 1000
            : height * (16.0 / 9) * height * fps * 0.07 / 1000 * Math.Pow(1.12, 21 - q) * codecFactor;
        RecordEstimate.Text = $"For a full-screen area: ≈ {kbps / 1000:0.#} Mbps · ≈ {kbps * 60 / 8 / 1000:0} MB a minute. Smaller areas take less.";
    }

    private void ReplayRate_Changed(object sender, RoutedEventArgs e)
    {
        if (QualityPanel == null || BitratePanel == null) return;
        bool fixedRate = CmbReplayRate.SelectedIndex == 1;
        // Only the one in use can be typed in; the other is greyed out.
        QualityPanel.IsEnabled = !fixedRate;
        BitratePanel.IsEnabled = fixedRate;
        QualityPanel.Opacity = fixedRate ? 0.4 : 1;
        BitratePanel.Opacity = fixedRate ? 1 : 0.4;
        UpdateReplayEstimate();
    }

    private EncoderKind _replayEncoderChoice = EncoderKind.Auto;

    private async void RecheckEncoders_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b) { b.IsEnabled = false; b.Content = "Checking…"; }
        try
        {
            await EncoderDetector.AvailableAsync(forceRescan: true);
            await EncoderDetector.AvailableForAsync(VideoCodec.H264, forceRescan: true);
            await FillReplayEncodersAsync();
            var failed = EncoderDetector.Failures.Where(f => !f.Key.StartsWith("lib")).ToList();
            BackupStatus.Text = "";
            if (sender is System.Windows.Controls.Button b2)
                b2.ToolTip = failed.Count == 0 ? "Every graphics-card encoder passed."
                    : string.Join("\n", failed.Select(f => $"{f.Key}: {f.Value}"));
        }
        finally
        {
            if (sender is System.Windows.Controls.Button b3) { b3.IsEnabled = true; b3.Content = "Check encoders again"; }
        }
    }

    private void ReplayCodec_Changed(object sender, RoutedEventArgs e)
    {
        if (CmbReplayEncoder == null) return;
        if (CmbReplayEncoder.SelectedItem is System.Windows.Controls.ComboBoxItem cur && cur.Tag is EncoderKind k)
            _replayEncoderChoice = k;
        _ = FillReplayEncodersAsync();
        UpdateReplayEstimate();
    }

    /// <summary>The encoders for the chosen format; ones this PC can't use are greyed out.</summary>
    private async System.Threading.Tasks.Task FillReplayEncodersAsync()
    {
        var codec = (VideoCodec)Math.Max(0, CmbReplayCodec.SelectedIndex);
        var have = await EncoderDetector.AvailableForAsync(codec);
        CmbReplayEncoder.Items.Clear();
        foreach (var k in new[] { EncoderKind.Auto, EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv, EncoderKind.X264 })
        {
            bool ok = k == EncoderKind.Auto || have.Contains(k);
            string label = k == EncoderKind.Auto ? "Automatic (best available)"
                : k == EncoderKind.X264 ? "CPU (" + EncoderDetector.FFmpegName(k, codec) + ")"
                : EncoderDetector.Friendly(k);
            string? why = null;
            if (!ok) lock (EncoderDetector.Failures) EncoderDetector.Failures.TryGetValue(EncoderDetector.FFmpegName(k, codec), out why);
            var item = Choice(ok ? label : label + "  (not on this PC)", k);
            item.IsEnabled = ok;
            // Why it didn't pass its test, in FFmpeg's words.
            item.ToolTip = why == null ? null : "Test result: " + why;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(item, true);
            CmbReplayEncoder.Items.Add(item);
            if (k == _replayEncoderChoice && ok) CmbReplayEncoder.SelectedItem = item;
        }
        if (CmbReplayEncoder.SelectedItem == null) CmbReplayEncoder.SelectedIndex = 0;
        UpdateReplayEstimate();
    }

    /// <summary>A list choice that can also show what picking it would change.</summary>
    private System.Windows.Controls.ComboBoxItem Choice(string label, object? tag = null) => new()
    {
        Content = new CostOption(label),
        ContentTemplate = (DataTemplate)FindResource("CostOptionTemplate"),
        Tag = tag,
    };

    private void FillFixedLists()
    {
        for (int n = 1; n <= 10; n++) CmbMaxApps.Items.Add(Choice(n == 1 ? "1 app" : $"{n} apps"));
        foreach (var c in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
            CmbReplayCodec.Items.Add(Choice(EncoderDetector.CodecName(c)));
        foreach (var l in ReplayLengths)
            CmbReplayLength.Items.Add(Choice(l < 60 ? $"Last {l} seconds" : $"Last {l / 60} minute{(l == 60 ? "" : "s")}"));
        CmbReplayLength.Items.Add(Choice("Custom…"));
        foreach (var h in ReplayHeights) CmbReplayRes.Items.Add(Choice(h == 0 ? "The screen's own size" : $"{h}p"));
        foreach (var f in ReplayFpsChoices) CmbReplayFps.Items.Add(Choice($"{f} fps"));
        // The noise suppression levels (written in the page) get the same treatment.
        var levels = CmbMicSuppression.Items.OfType<System.Windows.Controls.ComboBoxItem>().Select(i => i.Content?.ToString() ?? "").ToList();
        CmbMicSuppression.Items.Clear();
        foreach (var l in levels) CmbMicSuppression.Items.Add(Choice(l));

        CmbReplayScreen.Items.Add("Main screen");
        _replayScreens.Add(("", "", ""));
        _replayTargets.Add(null);
        var outputs = DxgiOutputs.All();
        _mainTarget = outputs.FirstOrDefault(t => t.Desktop.X == 0 && t.Desktop.Y == 0) ?? outputs.FirstOrDefault();
        foreach (var t in outputs)
        {
            var (id, name) = MonitorIdentity.Of(t.DeviceName);
            var label = t.DeviceName.Replace(@"\\.\", "");
            CmbReplayScreen.Items.Add($"{(name.Length > 0 && !name.StartsWith("Generic") ? name + "  ·  " : "")}{t.Desktop.Width}×{t.Desktop.Height}  ({label})");
            _replayScreens.Add((t.DeviceName, id, MonitorIdentity.RectOf(t.Desktop)));
            _replayTargets.Add(t);
        }

        foreach (var f in FpsChoices) CmbFps.Items.Add($"{f} fps");
        foreach (var c in CountdownChoices)
            CmbCountdown.Items.Add(c == 0 ? "Start instantly" : $"{c} second{(c == 1 ? "" : "s")}");
        foreach (var b in AudioBitrates) CmbAudioBitrate.Items.Add(b.ToString());

        foreach (var k in new[] { EncoderKind.Auto, EncoderKind.Nvenc, EncoderKind.Qsv,
                                  EncoderKind.Amf, EncoderKind.X264 })
            CmbEncoder.Items.Add(EncoderDetector.Friendly(k));

        foreach (var p in new[]
                 {
                     "p1 — fastest", "p2", "p3", "p4 — balanced",
                     "p5 — recommended", "p6", "p7 — best quality"
                 })
            CmbNvencPreset.Items.Add(p);
    }

    private void LoadDevices()
    {
        _mics = AudioSource.ListMics();
        _outs = AudioSource.ListOutputs();

        CmbMicDevice.Items.Clear();
        CmbSysDevice.Items.Clear();
        CmbMicDevice.Items.Add("Whatever Windows is using");
        CmbSysDevice.Items.Add("Whatever Windows is using");

        foreach (var d in _mics) CmbMicDevice.Items.Add(d.Name + (d.IsDefault ? "  (default)" : ""));
        foreach (var d in _outs) CmbSysDevice.Items.Add(d.Name + (d.IsDefault ? "  (default)" : ""));
    }

    private void LoadValues()
    {
        var s = _draft;

        TxtFolder.Text = _app.Library.Folder;
        TxtDiskPercent.Text = s.MaxDiskPercent.ToString("0.##");
        ChkRecycle.IsChecked = s.DeleteToRecycleBin;
        ChkShrinkGpu.IsChecked = s.ShrinkOnGpu;

        ChkCopyOnFinish.IsChecked = s.CopyFileOnFinish;
        ChkKeepOriginal.IsChecked = s.KeepOriginalWhenShrinking;
        TxtCompressPresets.Text = string.Join(", ", s.CompressPresetsMb);
        ChkShowLibrary.IsChecked = s.ShowLibraryOnFinish;
        ChkToasts.IsChecked = s.ToastsEnabled;
        CmbToastPos.SelectedIndex = (int)s.ToastPosition;
        CmbToastClick.SelectedIndex = (int)s.ToastClickAction;
        ChkCopyReplay.IsChecked = s.CopyReplayOnSave;
        TxtToastSeconds.Text = s.ToastSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        ChkReplayOn.IsChecked = s.ReplayEnabled;
        CmbReplayWhen.SelectedIndex = s.ReplayGamesOnly ? 1 : 0;
        _removedGames.Clear();
        BuildMyGames();
        if (Array.IndexOf(ReplayLengths, s.ReplaySeconds) >= 0)
            CmbReplayLength.SelectedIndex = Array.IndexOf(ReplayLengths, s.ReplaySeconds);
        else
            CmbReplayLength.SelectedIndex = ReplayLengths.Length;   // Custom
        TxtReplayCustom.Text = s.ReplaySeconds.ToString();
        ChkReplayStartOver.IsChecked = s.ReplayStartOverAfterSave;
        CmbReplayRes.SelectedIndex = Math.Max(0, Array.IndexOf(ReplayHeights, s.ReplayHeight));
        CmbReplayFps.SelectedIndex = Nearest(ReplayFpsChoices, s.ReplayFps);
        // The chosen monitor by its own ID (then position, then name). If it's
        // off right now it stays listed, so saving never swaps the choice.
        int screen = _replayScreens.FindIndex(x => s.ReplayMonitorId.Length > 0 && x.Id == s.ReplayMonitorId);
        if (screen < 0) screen = _replayScreens.FindIndex(x => s.ReplayMonitorRect.Length > 0 && x.Rect == s.ReplayMonitorRect);
        if (screen < 0) screen = _replayScreens.FindIndex(x => s.ReplayMonitor.Length > 0 && string.Equals(x.Name, s.ReplayMonitor, StringComparison.OrdinalIgnoreCase));
        if (screen < 0 && (s.ReplayMonitorId.Length > 0 || s.ReplayMonitor.Length > 0))
        {
            CmbReplayScreen.Items.Add("Chosen screen (not connected now)");
            _replayScreens.Add((s.ReplayMonitor, s.ReplayMonitorId, s.ReplayMonitorRect));
            _replayTargets.Add(null);
            screen = _replayScreens.Count - 1;
        }
        CmbReplayScreen.SelectedIndex = Math.Max(0, screen);
        TxtReplayQuality.Text = s.ReplayQuality.ToString();
        TxtReplayBitrate.Text = (s.ReplayBitrateKbps / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        CmbReplayRate.SelectedIndex = s.ReplayUseBitrate ? 1 : 0;
        _replayEncoderChoice = s.ReplayEncoder;
        CmbReplayCodec.SelectedIndex = (int)s.ReplayCodec;
        _ = FillReplayEncodersAsync();
        TxtReplaySync.Text = s.ReplaySoundSyncMs.ToString();
        TxtSafeCpu.Text = s.SafetyCpuPercent.ToString();
        ChkSafeCpuHard.IsChecked = s.SafetyCpuHardCap;
        ChkReplayLight.IsChecked = s.ReplayLight;
        TxtSafeRam.Text = s.SafetyRamMb.ToString();
        TxtSafeVram.Text = s.SafetyVramMb.ToString();
        ChkSafeGpu.IsChecked = s.SafetyGpuAdaptive;
        TxtVramFull.Text = s.SafetyVramFullPercent.ToString();
        CmbRecordCodec.SelectedIndex = (int)s.RecordCodec;
        ChkReplayCursor.IsChecked = s.ReplayCursor;
        ChkReplayFullRange.IsChecked = s.ReplayScaleFullRange;
        ChkRtPc.IsChecked = s.ReplayTrackPc;
        ChkRtMic.IsChecked = s.ReplayTrackMic;
        ChkAppTracks.IsChecked = s.AppTracks;
        CmbMaxApps.SelectedIndex = Math.Clamp(s.MaxAppTracks, 1, 10) - 1;
        ChkElevatedApps.IsChecked = s.ElevatedAppTracks;
        TxtHkToggleReplay.Text = s.HotkeyToggleReplay;
        TxtHkEdSplit.Text = s.EditorKeySplit;
        TxtHkEdLeft.Text = s.EditorKeyCutLeft;
        TxtHkEdRight.Text = s.EditorKeyCutRight;
        TxtHkReplay.Text = s.HotkeySaveReplay;
        TxtHkReplay2.Text = s.HotkeySaveReplay2;
        TxtReplaySec2.Text = s.ReplaySeconds2.ToString();
        TxtHkReplay3.Text = s.HotkeySaveReplay3;
        TxtReplaySec3.Text = s.ReplaySeconds3.ToString();
        UpdateReplayEstimate();
        AboutVersion.Text = $"Version {Paths.AppVersion}";
        AboutLogo.Source = LogoArt.Image;
        if (!_logoHooked)
        {
            _logoHooked = true;
            void Redraw() => AboutLogo.Source = LogoArt.Image;
            LogoArt.Changed += Redraw;
            Closed += (_, _) => LogoArt.Changed -= Redraw;
        }
        ChkAutoSort.IsChecked = s.AutoSortIntoFolders;
        ChkStartup.IsChecked = s.StartWithWindows;
        ChkMicGate.IsChecked = s.MicNoiseGate;
        ChkMicLimiter.IsChecked = s.MicLimiter;
        SldMicGate.Value = Math.Clamp(s.MicGateDb, -70, -20);
        SldMicLimiter.Value = Math.Clamp(s.MicLimiterDb, -24, -3);
        CmbMicSuppression.SelectedIndex = Math.Clamp(s.MicSuppression, 0, 3);
        ShowMicValues();

        CmbFps.SelectedIndex = Math.Max(0, Array.IndexOf(FpsChoices, s.Fps));
        CmbResolution.SelectedIndex = s.Resolution switch
        {
            ResolutionMode.Fixed1440p => 1,
            ResolutionMode.Fixed1080p => 2,
            ResolutionMode.Fixed720p  => 3,
            _ => 0,
        };
        ChkRecordLikeReplay.IsChecked = s.RecordLikeReplay;
        RecordLikeReplay_Changed(ChkRecordLikeReplay, new RoutedEventArgs());
        CmbBackend.SelectedIndex = s.Backend switch
        {
            CaptureBackend.Ddagrab => 1,
            CaptureBackend.Gdigrab => 2,
            _ => 0,
        };

        ChkCursor.IsChecked = s.ShowCursor;
        ChkClicks.IsChecked = s.ShowClickHighlights;
        SldClickSize.Value = s.ClickHighlightSize;
        ClickSizeText.Text = $"{s.ClickHighlightSize}px";

        ChkBar.IsChecked = s.ShowRecordingBar;
        ChkBorder.IsChecked = s.ShowRecordingBorder;

        CmbCountdown.SelectedIndex = Math.Max(0, Array.IndexOf(CountdownChoices, s.CountdownSeconds));
        ChkRememberRegion.IsChecked = s.RememberLastRegion;

        CmbAudio.SelectedIndex = (int)s.Audio;
        SelectDevice(CmbSysDevice, _outs, s.SystemDeviceId, "Your chosen output");
        SelectDevice(CmbMicDevice, _mics, s.MicDeviceId, "Your chosen mic");
        SldSysGain.Value = s.SystemGainPercent;
        SldMicGain.Value = s.MicGainPercent;
        SysGainText.Text = $"{s.SystemGainPercent}%";
        MicGainText.Text = $"{s.MicGainPercent}%";
        CmbAudioBitrate.SelectedIndex = Math.Max(0, Array.IndexOf(AudioBitrates, s.AudioBitrateKbps));

        CmbEncoder.SelectedIndex = (int)s.Encoder;
        // A bitrate of 0 has always meant "let the quality level decide".
        CmbRecordRate.SelectedIndex = s.VideoBitrateKbps > 0 ? 1 : 0;
        TxtBitrate.Text = (s.VideoBitrateKbps > 0 ? s.VideoBitrateKbps / 1000.0 : 20).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        RecordRate_Changed(CmbRecordRate, new RoutedEventArgs());
        CmbNvencPreset.SelectedIndex = Math.Clamp(ParsePreset(s.NvencPreset) - 1, 0, 6);
        TxtCrf.Text = s.QualityCq.ToString();

        CmbOpenAction.SelectedIndex = s.OpenAction == OpenAction.ClipList ? 1 : 0;

        ChkCues.IsChecked = s.PlaySounds;
        SldCueVolume.Value = s.CueVolume;
        CueVolumeText.Text = $"{s.CueVolume}%";

        _linkView = new LinkOptionsView(s.LinkDefaults ?? new LinkChoice(), null);
        LinkDefaultsHost.Content = _linkView;
        ChkUploadAfter.IsChecked = s.UploadAfterRecording;
        BuildCueRows();

        CustomHost.Content = _custom = new CustomizationView(this);


        TxtTitleTemplate.Text = s.TitleTemplate;
        TxtMaxLength.Text = s.MaxLengthSeconds.ToString();
        TxtWarnAt.Text = s.WarnAtSeconds.ToString();

        TxtHkRegion.Text = s.HotkeyRegion;
        TxtHkPause.Text = s.HotkeyPause;
        TxtHkStop.Text = s.HotkeyStop;
        TxtHkCancel.Text = s.HotkeyCancel;

        HotkeyNote.Text =
            "Hotkeys are optional. Everything they do is also on the control " +
            "bar that appears while recording.";

        UpdateFpsNote();
        UpdateRecordEstimate();
    }

    /// <summary>
    /// Selects the saved device. One that isn't plugged in right now stays
    /// chosen (named as such), so saving Settings with a headset switched
    /// off doesn't quietly swap it for the default.
    /// </summary>
    private static void SelectDevice(System.Windows.Controls.ComboBox combo, List<AudioSource.Device> list, string? id, string label)
    {
        int at = IndexOfDevice(list, id);
        if (at == 0 && !string.IsNullOrEmpty(id))
        {
            list.Add(new AudioSource.Device(id, label + " (not connected now)", false));
            combo.Items.Add(label + " (not connected now)");
            at = list.Count;
        }
        combo.SelectedIndex = at;
    }

    private static int IndexOfDevice(List<AudioSource.Device> list, string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i + 1;
        return 0;
    }

    private static int ParsePreset(string p) =>
        p.Length >= 2 && int.TryParse(p[1..], out var n) ? n : 5;

    /* ------------------------------------------------------------------ */

    private void UpdateFpsNote()
    {
        if (CmbFps.SelectedIndex < 0) return;
        int fps = FpsChoices[CmbFps.SelectedIndex];
        int refresh = _app.PrimaryRefreshRate;

        FpsNote.Text = fps > refresh
            ? $"Your display runs at {refresh} Hz, so recording is capped there. " +
              "Anything higher would just repeat frames and make bigger files."
            : $"Your display runs at {refresh} Hz.";
    }

    /// <summary>
    /// A percentage means nothing without the drive behind it, so the actual
    /// ceiling in gigabytes is spelled out as the number is typed.
    /// </summary>
    private void UpdateCapExplain()
    {
        var d = _app.Library.Disk();
        if (d.DriveTotal <= 0) { CapExplain.Text = "The drive could not be read."; return; }

        double pct = double.TryParse(TxtDiskPercent.Text?.Trim(), out var v)
            ? v : _draft.MaxDiskPercent;
        pct = Math.Clamp(pct, 0.1, 90);
        long cap = (long)(d.DriveTotal * pct / 100.0);

        CapExplain.Text = $"≈ {Gb(cap)} of this {Gb(d.DriveTotal)} drive.";
    }

    private static string Gb(long b) => b switch
    {
        <= 0 => "—",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0} MB",
        _ => $"{b / 1073741824.0:0.0} GB",
    };

    private async System.Threading.Tasks.Task DescribeEncodersAsync()
    {
        var have = await EncoderDetector.AvailableAsync();
        EncoderNote.Text = "Available: " + string.Join(", ", have.Select(EncoderDetector.Friendly));
    }

    private void ShowDisk()
    {
        var d = _app.Library.Disk();
        DiskNow.Text = d.Cap <= 0
            ? "The drive could not be read."
            : $"Using {Gb(d.Used)} of {Gb(d.Cap)} allowed. " +
              $"{_app.Library.Items.Count} clips. {Gb(d.DriveFree)} free on the drive.";
    }

    /* ---- buttons ------------------------------------------------------ */

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where should clips be saved?",
            SelectedPath = Directory.Exists(TxtFolder.Text) ? TxtFolder.Text : _app.Library.Folder,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtFolder.Text = dlg.SelectedPath;
            UpdateCapExplain();
        }
    }

    /// <summary>The first-run setup again. Settings closes first, so it can't save over what the setup chooses.</summary>
    private void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        Close();
        SetupWindow.Open(_app);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        var sysId = SelectedDeviceId(_outs, CmbSysDevice.SelectedIndex);
        var micId = SelectedDeviceId(_mics, CmbMicDevice.SelectedIndex);
        LoadDevices();
        SelectDevice(CmbSysDevice, _outs, sysId, "Your chosen output");
        SelectDevice(CmbMicDevice, _mics, micId, "Your chosen mic");
        SaveNote.Text = "Device list refreshed.";
    }

    private void CheckDisk_Click(object sender, RoutedEventArgs e) => ShowDisk();

    private void License_Click(object sender, RoutedEventArgs e) =>
        OpenDoc("LICENSE.md", "https://polyformproject.org/licenses/noncommercial/1.0.0/");

    private void Notices_Click(object sender, RoutedEventArgs e) =>
        OpenDoc("THIRD-PARTY-NOTICES.md", null);

    /// <summary>Opens a document that ships beside the app (or the web copy, if it's missing).</summary>
    private void OpenDoc(string name, string? fallbackUrl)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        string? target = System.IO.File.Exists(file) ? file : fallbackUrl;
        if (target == null)
        {
            Dialog.Show($"{name} isn't next to Persocord. It's in the folder Persocord was built from.", "Persocord");
            return;
        }
        try
        {
            // Markdown files often have no app tied to them: Notepad always works.
            if (target == file && !HasHandler(".md"))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true })?.Dispose();
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error("open " + name, ex); }
    }

    private static bool HasHandler(string extension)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(extension);
            return key?.GetValue("") is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        _app.Library.EnsureFolder();
        ShareService.ShowInExplorer(Path.Combine(_app.Library.Folder, "."));
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Paths.LogFile)) File.WriteAllText(Paths.LogFile, "");
            Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { SaveNote.Text = ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /* ---- your games -------------------------------------------------------- */

    private readonly HashSet<string> _removedGames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The games added by hand, each with a Remove button (removed for good when saved).</summary>
    private void BuildMyGames()
    {
        MyGamesList.Children.Clear();
        var games = _app.Settings.Current.MyGames.Where(g => !_removedGames.Contains(g)).ToList();
        if (games.Count == 0)
        {
            var none = new TextBlock { Text = "None added. Games Persocord recognises don't need adding.", FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            MyGamesList.Children.Add(none);
            return;
        }
        foreach (var path in games)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
            var remove = new Button { Content = "Remove", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) => { _removedGames.Add(path); BuildMyGames(); };
            var name = new TextBlock { Text = GameDetector.NameOf(path), VerticalAlignment = VerticalAlignment.Center, ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.Children.Add(remove);
            row.Children.Add(name);
            MyGamesList.Children.Add(row);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _draft;
        // The look is saved on its own (it lives apart from the settings).
        _custom?.Commit();
        // Set beside the GIF button, maybe while this window was open.
        var now = _app.Settings.Current;
        (s.GifMaxSeconds, s.GifFps, s.GifWidth) = (now.GifMaxSeconds, now.GifFps, now.GifWidth);
        // Things Persocord keeps up to date by itself while this window is open
        // (where the area and the bar were, the list's order, the player's volume,
        // the graphics chip the replay settled on…) aren't put back to how they
        // were when it opened.
        s.LastRegion = now.LastRegion;
        s.LastBarPosition = now.LastBarPosition;
        s.LibrarySort = now.LibrarySort;
        s.PlayerVolume = now.PlayerVolume;
        s.CaptureOnScreenGpu = now.CaptureOnScreenGpu;
        s.LinkWarningAcknowledged = now.LinkWarningAcknowledged;
        s.LibraryId = now.LibraryId;
        s.SetupDone = now.SetupDone;
        s.SettingsVersion = now.SettingsVersion;

        s.MaxDiskPercent = ParseDouble(TxtDiskPercent.Text, 10, 0.1, 90);
        s.DeleteToRecycleBin = ChkRecycle.IsChecked == true;
        s.ShrinkOnGpu = ChkShrinkGpu.IsChecked == true;
        var presets = TxtCompressPresets.Text.Split(',', ';', ' ')
            .Select(p => int.TryParse(p.Trim(), out var v) ? v : 0)
            .Where(v => v > 0 && v <= 100000).Distinct().OrderBy(v => v).Take(10).ToList();
        if (presets.Count > 0) s.CompressPresetsMb = presets;

        s.CopyFileOnFinish = ChkCopyOnFinish.IsChecked == true;
        s.KeepOriginalWhenShrinking = ChkKeepOriginal.IsChecked == true;
        s.ShowLibraryOnFinish = ChkShowLibrary.IsChecked == true;
        s.ToastsEnabled = ChkToasts.IsChecked == true;
        s.ToastClickAction = (ToastClick)Math.Max(0, CmbToastClick.SelectedIndex);
        s.CopyReplayOnSave = ChkCopyReplay.IsChecked == true;
        s.ToastPosition = (ToastPosition)Math.Max(0, CmbToastPos.SelectedIndex);
        s.ToastSeconds = ToastSecondsValue();
        s.ReplayEnabled = ChkReplayOn.IsChecked == true;
        s.ReplayGamesOnly = CmbReplayWhen.SelectedIndex == 1;
        // Games added from the tray while this was open stay; ones removed here go.
        s.MyGames = now.MyGames.Where(g => !_removedGames.Contains(g)).ToList();
        s.ReplaySeconds = ReplaySecondsChosen();
        s.ReplayStartOverAfterSave = ChkReplayStartOver.IsChecked == true;
        s.ReplayHeight = ReplayHeights[Math.Max(0, CmbReplayRes.SelectedIndex)];
        s.ReplayFps = ReplayFpsChoices[Math.Max(0, CmbReplayFps.SelectedIndex)];
        var pickedScreen = _replayScreens[Math.Max(0, CmbReplayScreen.SelectedIndex)];
        s.ReplayMonitor = pickedScreen.Name;
        s.ReplayMonitorId = pickedScreen.Id;
        s.ReplayMonitorRect = pickedScreen.Rect;
        s.ReplayQuality = int.TryParse(TxtReplayQuality.Text, out var rq) ? Math.Clamp(rq, 10, 40) : 21;
        var rmb = Mbps(TxtReplayBitrate.Text);
        if (rmb > 0) s.ReplayBitrateKbps = (int)Math.Round(rmb * 1000);
        s.ReplayUseBitrate = CmbReplayRate.SelectedIndex == 1;
        s.ReplayCodec = (VideoCodec)Math.Max(0, CmbReplayCodec.SelectedIndex);
        s.ReplayEncoder = CmbReplayEncoder.SelectedItem is System.Windows.Controls.ComboBoxItem ei && ei.Tag is EncoderKind ek ? ek : EncoderKind.Auto;
        s.ReplaySoundSyncMs = int.TryParse(TxtReplaySync.Text, out var sync) ? Math.Clamp(sync, -10000, 10000) : 0;
        s.RecordCodec = (VideoCodec)Math.Max(0, CmbRecordCodec.SelectedIndex);
        s.SafetyCpuPercent = int.TryParse(TxtSafeCpu.Text, out var sc) ? Math.Clamp(sc, 5, 100) : 50;
        s.SafetyCpuHardCap = ChkSafeCpuHard.IsChecked == true;
        s.ReplayLight = ChkReplayLight.IsChecked == true;
        s.SafetyRamMb = int.TryParse(TxtSafeRam.Text, out var sr) ? Math.Clamp(sr, 256, 8192) : 1024;
        s.SafetyVramMb = int.TryParse(TxtSafeVram.Text, out var sv) ? Math.Clamp(sv, 128, 8192) : 512;
        s.SafetyGpuAdaptive = ChkSafeGpu.IsChecked == true;
        s.SafetyVramFullPercent = int.TryParse(TxtVramFull.Text, out var vf) ? Math.Clamp(vf, 50, 100) : 95;
        s.ReplayCursor = ChkReplayCursor.IsChecked == true;
        s.ReplayScaleFullRange = ChkReplayFullRange.IsChecked == true;
        s.ReplayTrackPc = ChkRtPc.IsChecked == true;
        s.ReplayTrackMic = ChkRtMic.IsChecked == true;
        s.AppTracks = ChkAppTracks.IsChecked == true;
        s.MaxAppTracks = Math.Max(0, CmbMaxApps.SelectedIndex) + 1;
        s.ElevatedAppTracks = ChkElevatedApps.IsChecked == true;
        Services.ElevatedAudio.Enabled = s.ElevatedAppTracks;
        s.HotkeyToggleReplay = TxtHkToggleReplay.Text.Trim();
        s.EditorKeySplit = TxtHkEdSplit.Text.Trim();
        s.EditorKeyCutLeft = TxtHkEdLeft.Text.Trim();
        s.EditorKeyCutRight = TxtHkEdRight.Text.Trim();
        s.HotkeySaveReplay = TxtHkReplay.Text.Trim();
        s.HotkeySaveReplay2 = TxtHkReplay2.Text.Trim();
        s.ReplaySeconds2 = int.TryParse(TxtReplaySec2.Text, out var r2) ? Math.Clamp(r2, 5, 3600) : 30;
        s.HotkeySaveReplay3 = TxtHkReplay3.Text.Trim();
        s.ReplaySeconds3 = int.TryParse(TxtReplaySec3.Text, out var r3) ? Math.Clamp(r3, 5, 3600) : 60;
        s.AutoSortIntoFolders = ChkAutoSort.IsChecked == true;
        s.PlaySounds = ChkCues.IsChecked == true;
        s.CueVolume = (int)SldCueVolume.Value;
        if (_linkView != null) s.LinkDefaults = _linkView.Choice;
        s.UploadAfterRecording = ChkUploadAfter.IsChecked == true;
        s.OpenAction = CmbOpenAction.SelectedIndex == 1 ? OpenAction.ClipList : OpenAction.SelectArea;
        // Cue choices are written straight into the draft as they change.
        s.StartWithWindows = ChkStartup.IsChecked == true;
        s.MicNoiseGate = ChkMicGate.IsChecked == true;
        s.MicLimiter = ChkMicLimiter.IsChecked == true;
        s.MicGateDb = (int)SldMicGate.Value;
        s.MicLimiterDb = (int)SldMicLimiter.Value;
        s.MicSuppression = Math.Max(0, CmbMicSuppression.SelectedIndex);

        s.Fps = FpsChoices[Math.Max(0, CmbFps.SelectedIndex)];
        s.Resolution = CmbResolution.SelectedIndex switch
        {
            1 => ResolutionMode.Fixed1440p,
            2 => ResolutionMode.Fixed1080p,
            3 => ResolutionMode.Fixed720p,
            _ => ResolutionMode.Native,
        };
        s.RecordLikeReplay = ChkRecordLikeReplay.IsChecked == true;
        s.Backend = CmbBackend.SelectedIndex switch
        {
            1 => CaptureBackend.Ddagrab,
            2 => CaptureBackend.Gdigrab,
            _ => CaptureBackend.Auto,
        };

        s.ShowCursor = ChkCursor.IsChecked == true;
        s.ShowClickHighlights = ChkClicks.IsChecked == true;
        s.ClickHighlightSize = (int)SldClickSize.Value;

        s.ShowRecordingBar = ChkBar.IsChecked == true;
        s.ShowRecordingBorder = ChkBorder.IsChecked == true;

        s.CountdownSeconds = CountdownChoices[Math.Max(0, CmbCountdown.SelectedIndex)];
        s.RememberLastRegion = ChkRememberRegion.IsChecked == true;

        s.Audio = (AudioMode)Math.Max(0, CmbAudio.SelectedIndex);
        s.SystemDeviceId = SelectedDeviceId(_outs, CmbSysDevice.SelectedIndex);
        s.MicDeviceId = SelectedDeviceId(_mics, CmbMicDevice.SelectedIndex);
        s.SystemGainPercent = (int)SldSysGain.Value;
        s.MicGainPercent = (int)SldMicGain.Value;
        s.AudioBitrateKbps = AudioBitrates[Math.Max(0, CmbAudioBitrate.SelectedIndex)];

        s.Encoder = (EncoderKind)Math.Max(0, CmbEncoder.SelectedIndex);
        s.VideoBitrateKbps = CmbRecordRate.SelectedIndex == 1 ? Math.Max(1000, (int)Math.Round(Mbps(TxtBitrate.Text) * 1000)) : 0;
        s.NvencPreset = "p" + (Math.Max(0, CmbNvencPreset.SelectedIndex) + 1);
        s.QualityCq = ParseInt(TxtCrf.Text, 19, 10, 40);


        s.TitleTemplate = TxtTitleTemplate.Text.Trim();
        s.MaxLengthSeconds = ParseInt(TxtMaxLength.Text, 120, 0, 14400);
        s.WarnAtSeconds = ParseInt(TxtWarnAt.Text, 105, 0, 14400);
        if (s.WarnAtSeconds is > 0 and < 5) s.WarnAtSeconds = 5;

        // A warning that fires after the recording has already stopped is no
        // warning at all.
        if (s.MaxLengthSeconds > 0 && s.WarnAtSeconds > 0 && s.WarnAtSeconds >= s.MaxLengthSeconds)
            s.WarnAtSeconds = Math.Max(5, (int)(s.MaxLengthSeconds * 0.875));

        s.HotkeyRegion = TxtHkRegion.Text.Trim();
        s.HotkeyPause = TxtHkPause.Text.Trim();
        s.HotkeyStop = TxtHkStop.Text.Trim();
        s.HotkeyCancel = TxtHkCancel.Text.Trim();

        var wanted = TxtFolder.Text.Trim();
        bool folderChanged = wanted.Length > 0 && !string.Equals(
            Path.GetFullPath(wanted), Path.GetFullPath(_originalFolder),
            StringComparison.OrdinalIgnoreCase);
        // A new folder: asked once whether the clips come along.
        bool moveClips = false;
        if (folderChanged && _app.Library.Items.Count > 0)
        {
            var answer = Dialog.Show(this,
                $"Move your {_app.Library.Items.Count} clip{(_app.Library.Items.Count == 1 ? "" : "s")} to the new folder?\n\n" +
                "Yes moves them. No uses the new folder as it is and leaves your clips where they are.",
                "New clip folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                TxtFolder.Text = _originalFolder;
                folderChanged = false;
            }
            else moveClips = answer == MessageBoxResult.Yes;
        }

        var before = _applied;

        // A copy is what's saved: this window's own choices (the sound rows edit
        // them as they change) never become the live settings before Save.
        _app.Settings.Save(s.Clone());
        _applied = s.Clone();
        _app.ApplyHotkeys();
        _app.ApplyReplay(before);

        if (folderChanged && !moveClips)
        {
            // Just point at the new folder; nothing is moved.
            _app.Library.SwitchTo(wanted);
            _originalFolder = wanted;
        }
        else if (folderChanged)
        {
            SaveNote.Text = "Moving clips…";
            IsEnabled = false;
            var ok = await _app.Library.MoveToAsync(wanted);
            IsEnabled = true;
            if (ok) _originalFolder = wanted;

            if (!ok)
                Dialog.Show(
                    "The clips could not all be moved. Nothing was deleted, so check " +
                    "both folders before recording again; the log has the details.",
                    "Persocord", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // New accent or background: applied on the spot, to every open window.

        // Stays open: a quiet confirmation instead, so more can be changed.
        SaveNote.Text = "✓ Saved";
        SaveNote.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentText");
        var stamp = ++_saveStamp;
        await System.Threading.Tasks.Task.Delay(3000);
        if (stamp == _saveStamp) { SaveNote.Text = ""; SaveNote.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty); }
    }

    private int _saveStamp;

    /* ---- sound cues ---------------------------------------------------- */

    /// <summary>
    /// One row per cue: on or off, which sound, a button to hear it, and a
    /// way to pick any sound file. Built in code since all five rows are
    /// the same shape.
    /// </summary>
    private LinkOptionsView? _linkView;

    private void BuildCueRows()
    {
        CueRows.Children.Clear();
        var windowsSounds = CuePlayer.WindowsSounds();

        foreach (CueKind kind in Enum.GetValues(typeof(CueKind)))
        {
            var cue = _draft.CueFor(kind);

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var check = new System.Windows.Controls.CheckBox
            {
                Content = AppSettings.CueLabel(kind),
                IsChecked = cue.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked += (_, _) => cue.Enabled = true;
            check.Unchecked += (_, _) => cue.Enabled = false;

            var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(8, 0, 8, 0) };
            FillSoundChoices(combo, windowsSounds, cue.Sound);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string value)
                    cue.Sound = value;
            };

            var preview = new System.Windows.Controls.Button
            {
                Content = "Preview",
                Padding = new Thickness(10, 3, 10, 3),
                ToolTip = "Play this sound at the volume above",
            };
            preview.Click += (_, _) =>
            {
                if (CuePlayer.Shared.Play(cue.Sound, (float)(SldCueVolume.Value / 100.0)) == TimeSpan.Zero)
                    SaveNote.Text = "That sound could not be played.";
            };

            var browse = new System.Windows.Controls.Button
            {
                Content = "…",
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                ToolTip = "Use a sound file of your own",
            };
            browse.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose a sound for: " + AppSettings.CueLabel(kind),
                    Filter = "Sound files|*.wav;*.mp3;*.m4a;*.wma|All files|*.*",
                };
                if (dlg.ShowDialog(this) == true)
                {
                    cue.Sound = dlg.FileName;
                    FillSoundChoices(combo, windowsSounds, cue.Sound);
                }
            };

            System.Windows.Controls.Grid.SetColumn(check, 0);
            System.Windows.Controls.Grid.SetColumn(combo, 1);
            System.Windows.Controls.Grid.SetColumn(preview, 2);
            System.Windows.Controls.Grid.SetColumn(browse, 3);
            grid.Children.Add(check);
            grid.Children.Add(combo);
            grid.Children.Add(preview);
            grid.Children.Add(browse);
            CueRows.Children.Add(grid);
        }
    }

    private static void FillSoundChoices(System.Windows.Controls.ComboBox combo,
                                         List<string> windowsSounds, string current)
    {
        combo.Items.Clear();
        System.Windows.Controls.ComboBoxItem? selected = null;

        // A sound of the user's own, or a default missing from this
        // Windows install, still shows so it is clear what is set.
        bool known = windowsSounds.Any(n => n.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (!known && !string.IsNullOrWhiteSpace(current))
        {
            var label = Path.IsPathRooted(current) ? Path.GetFileName(current) : current;
            if (CuePlayer.Resolve(current) == null) label += " (not found)";
            selected = new System.Windows.Controls.ComboBoxItem { Content = label, Tag = current };
            combo.Items.Add(selected);
        }

        foreach (var name in windowsSounds)
        {
            var item = new System.Windows.Controls.ComboBoxItem
            {
                Content = Path.GetFileNameWithoutExtension(name),
                Tag = name,
            };
            combo.Items.Add(item);
            if (name.Equals(current, StringComparison.OrdinalIgnoreCase)) selected = item;
        }

        combo.SelectedItem = selected;
    }

    private static string? SelectedDeviceId(List<AudioSource.Device> list, int index) =>
        index <= 0 || index - 1 >= list.Count ? null : list[index - 1].Id;

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text?.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private static double ParseDouble(string text, double fallback, double min, double max) =>
        double.TryParse(text?.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;
}

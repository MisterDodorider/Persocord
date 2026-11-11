using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// A row in the clip list. The record itself stays a plain data object, so
/// everything the list needs to draw — thumbnail bitmap, whether the clip is
/// too big to attach — is worked out here instead.
/// </summary>
public class ClipRow : INotifyPropertyChanged
{
    public ClipRecord Clip { get; }
    private readonly LibraryService _library;
    private long _shareLimit;
    private readonly Func<string, string?> _uploadStatus;

    public ClipRow(ClipRecord clip, LibraryService library, long shareLimit, Func<string, string?> uploadStatus)
    {
        Clip = clip;
        _library = library;
        _shareLimit = shareLimit;
        _uploadStatus = uploadStatus;
    }

    /* ---- links and privacy ---- */
    public bool Private => Clip.Private;
    public string? UploadStatus => _uploadStatus(Clip.Id);
    public bool IsUploading => UploadStatus != null;
    public bool CanUpload => !Clip.Private && !IsUploading;
    public bool HasLink => Clip.Link != null && !Clip.Link.Expired && !IsUploading;
    public bool ShowLinkLine => HasLink || IsUploading;
    public bool CanKill => HasLink && Clip.Link!.CanKill;
    public string LinkText => Clip.Link == null ? "" :
        Clip.Link.Url.Replace("https://", "").Replace("http://", "");
    public string LinkTimeLeft => Clip.Link == null ? "" : $"{Clip.Link.Service}  ·  {Clip.Link.TimeLeftText}";

    /// <summary>Re-reads everything, for countdowns and upload progress.</summary>
    public void Touch() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public string Title => Clip.Title;
    public string DurationText => Clip.DurationText;
    public string SizeText => Clip.SizeText;
    public DateTime Created => Clip.Created;
    public bool Favourite => Clip.Favourite;
    public string? Note => Clip.Note;
    public string Kind => Clip.IsGif ? "GIF" : "no preview";

    /// <summary>Where the clip lives, shown while searching across folders.</summary>
    public string FolderLabel { get; set; } = "";
    public System.Collections.Generic.IReadOnlyList<string> Tags => Clip.Tags ?? new System.Collections.Generic.List<string>();
    public bool CanEdit => !Clip.IsGif;
    public string FilePath => _library.PathOf(Clip);

    public bool NeedsShrink => _shareLimit > 0 && Clip.Size > _shareLimit && !Clip.IsGif;

    /// <summary>Compress Size works on any video clip.</summary>
    public bool CanCompress => !Clip.IsGif;

    /// <summary>Red when the file will be refused as an attachment.</summary>
    private static readonly Brush TooBig = Frozen(Color.FromRgb(0xE0, 0x59, 0x4D));
    private static readonly Brush Normal = Frozen(Color.FromRgb(0x8A, 0x90, 0x9A));
    public Brush SizeBrush => NeedsShrink ? TooBig : Normal;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>A brief "Copied" on the link pill after clicking it.</summary>
    public string CopiedFlash { get; private set; } = "";

    public async void FlashCopied()
    {
        CopiedFlash = "Copied";
        Raise(nameof(CopiedFlash));
        await System.Threading.Tasks.Task.Delay(1600);
        CopiedFlash = "";
        Raise(nameof(CopiedFlash));
    }

    /* ---- thumbnail: decoded in the background, cached across refreshes ---- */

    private BitmapSource? _thumb;
    private string? _thumbFor;
    private bool _thumbLoading;

    /// <summary>The placeholder shows only once loading is over and nothing came back.</summary>
    public bool NoThumb => !_thumbLoading && Thumb == null;

    public BitmapSource? Thumb
    {
        get
        {
            // Windows' own thumbnail (as in Explorer); Persocord makes one
            // only for a clip Windows can't show.
            var path = "shell:" + FilePath;
            if (path == _thumbFor) return _thumb;

            _thumbFor = path;
            _thumb = null;
            if (path == null) return null;

            var cached = ThumbCache.TryGet(path);
            if (cached != null) { _thumb = cached; return cached; }

            // Decoding on the UI thread made scrolling stutter as each row
            // appeared; now the row draws at once and the picture follows.
            _thumbLoading = true;
            _ = LoadThumbAsync(path);
            return null;
        }
    }

    // A big folder opening at once asked for every thumbnail together (and an
    // FFmpeg for each one Windows couldn't make); three at a time keeps the PC
    // and the list responsive, and the visible rows still fill in quickly.
    private static readonly System.Threading.SemaphoreSlim ThumbGate = new(3);

    private async System.Threading.Tasks.Task LoadThumbAsync(string path)
    {
        BitmapSource? bmp;
        await ThumbGate.WaitAsync();
        try
        {
            if (_thumbFor != path) return;   // scrolled away or changed meanwhile
            bmp = await ThumbCache.LoadAsync(path);
            if (bmp == null && !Clip.IsGif)
            {
                // Windows has none (HEVC without its extension, say): our own.
                var own = _library.ThumbPathOf(Clip);
                if (own == null || !File.Exists(own))
                {
                    own = await MediaTools.MakeThumbnailAsync(FilePath, "", Clip.Id);
                    if (own != null && !Clip.Tags.Contains("preview")) { Clip.ThumbName = own; _library.Save(); }
                }
                if (own != null) bmp = await ThumbCache.LoadAsync(own);
            }
        }
        finally { ThumbGate.Release(); }
        if (_thumbFor != path) return;   // the clip changed while this was loading
        _thumb = bmp;
        _thumbLoading = false;
        Raise(nameof(Thumb));
        Raise(nameof(NoThumb));
    }

    /// <summary>Picks up changes to the clip after an in-place refresh.</summary>
    public void Update(long shareLimit)
    {
        _shareLimit = shareLimit;
        Touch();
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Clip thumbnails, decoded once off the UI thread and shared by every row
/// and refresh. Keyed by path and modification time, so a rebuilt thumbnail
/// is picked up.
/// </summary>
internal static class ThumbCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> Cache = new();

    private static string Key(string path)
    {
        var real = path.StartsWith("shell:") ? path[6..] : path;
        try { return path + "|" + File.GetLastWriteTimeUtc(real).Ticks; }
        catch { return path; }
    }

    // Kept to the most recent few hundred (a long-running Persocord with
    // thousands of clips otherwise held every thumbnail it ever showed).
    private const int Most = 400;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Order = new();

    private static readonly object StoreLock = new();

    private static void Store(string path, BitmapSource bmp)
    {
        var key = Key(path);
        // Up to three thumbnails load at once: stored one at a time.
        lock (StoreLock)
        {
            // An older picture of the same file (before an edit) isn't needed.
            foreach (var k in Cache.Keys)
                if (k != key && k.StartsWith(path + "|", StringComparison.Ordinal)) Cache.TryRemove(k, out _);
            if (Cache.TryAdd(key, bmp)) Order.Enqueue(key); else Cache[key] = bmp;
            while (Cache.Count > Most && Order.TryDequeue(out var oldest)) Cache.TryRemove(oldest, out _);
            // Names of pictures already replaced (a clip edited, its thumbnail redone)
            // stay in the order list; it's tidied before it can grow without end.
            if (Order.Count > Most * 2)
            {
                var live = Order.Where(Cache.ContainsKey).Distinct().ToList();
                Order.Clear();
                foreach (var k in live) Order.Enqueue(k);
            }
        }
    }

    public static BitmapSource? TryGet(string path) =>
        Cache.TryGetValue(Key(path), out var hit) ? hit : null;

    public static System.Threading.Tasks.Task<BitmapSource?> LoadAsync(string path) =>
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (path.StartsWith("shell:"))
                {
                    // Asked for at about the size it's shown (the default, 480 pixels, kept
                    // four times the memory for every thumbnail of a clip from outside).
                    var shell = ShellThumb.Get(path[6..], 256);
                    if (shell != null) Store(path, shell);
                    return shell;
                }
                if (!File.Exists(path)) return null;
                // Reading the bytes first releases the file at once, so a
                // clip can be deleted or moved while its row is on screen.
                var bytes = File.ReadAllBytes(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 208;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();   // frozen images can cross from this thread to the UI
                Store(path, bmp);
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Error("thumb load", ex);
                return (BitmapSource?)null;
            }
        });
}

public partial class LibraryWindow : Window
{
    private readonly AppController _app;
    private readonly ObservableCollection<ClipRow> _rows = new();
    private Point _dragStart;

    public LibraryWindow(AppController app)
    {
        InitializeComponent();
        _app = app;

        List.ItemsSource = _rows;

        UpdateReplayToggle();
        _app.ReplayStateChanged += UpdateReplayToggle;
        Closed += (_, _) => _app.ReplayStateChanged -= UpdateReplayToggle;
        // Delete removes the selected clips (after asking).
        List.PreviewKeyDown += async (_, e) =>
        {
            // Not while a name is being typed.
            if (e.Key != Key.Delete || PlayerHost.Visibility == Visibility.Visible || e.OriginalSource is TextBox) return;
            e.Handled = true;
            await DeleteSelectedAsync();
        };

        // The player (and VLC's overlay window with its controls) goes with
        // this window, whether it's closed or hidden to the tray.
        Closing += (_, _) => _player?.Close();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) _player?.Close(); };

        // A preview never keeps playing behind a closed or background window.
        Closed += (_, _) => StopHoverPreview();
        Deactivated += (_, _) => StopHoverPreview();
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => StopHoverPreview()));

        foreach (var label in SortLabels) SortBox.Items.Add(label);
        SortBox.SelectedIndex = (int)_app.Settings.Current.LibrarySort;

        Title = $"Persocord {Updates.CurrentLabel}";

        Updates.Changed += OnUpdates;
        Closed += (_, _) => Updates.Changed -= OnUpdates;
        ShowUpdate();

        _app.StatusChanged += OnStatus;
        _app.LibraryRefreshed += Refresh;
        _app.Library.Changed += Refresh;
        _app.Library.FolderTrouble += OnFolderTrouble;
        _app.Library.FolderRecovered += _ => Dispatcher.InvokeAsync(() =>
        {
            Alert.Visibility = Visibility.Collapsed;
            Refresh();
        });

        EmptyHint.Text = $"Press {_app.Settings.Current.HotkeyRegion} anywhere, or use the button below.";

        _app.LinkProgress += OnLinkProgress;

        // Keeps countdowns current and clears links once they expire.
        _linkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _linkTimer.Tick += (_, _) => TickLinks();
        _linkTimer.Start();

        _setupTimer.Tick += (_, _) => ShowSetup();
        _setupTimer.Start();

        Refresh();
    }

    private readonly System.Windows.Threading.DispatcherTimer _linkTimer;

    /* ---- a new version ------------------------------------------------------ */

    private void OnUpdates() => Dispatcher.BeginInvoke(new Action(ShowUpdate));

    /// <summary>The banner at the top: a new version, its download, or "updated" right after one.</summary>
    private void ShowUpdate()
    {
        var r = Updates.Available;
        if (_app.UpdateDismissed || (r == null && !Updates.JustUpdated))
        {
            UpdateBar.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateBar.Visibility = Visibility.Visible;
        UpdateNotes.Visibility = Visibility.Visible;

        if (r == null)
        {
            UpdateTitle.Text = $"Persocord is updated to {Updates.CurrentLabel}";
            UpdateText.Text = "What's new shows everything that changed.";
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButton.Visibility = Visibility.Collapsed;
            UpdateClose.ToolTip = "Close";
            return;
        }

        if (Updates.Progress is double p)
        {
            UpdateTitle.Text = $"Downloading Persocord {r.Label}... {p:0%}";
            UpdateText.Text = "Then Persocord closes, installs it and opens again by itself. It takes a few minutes.";
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.Value = p;
            UpdateButton.Visibility = Visibility.Collapsed;
            UpdateNotes.Visibility = Visibility.Collapsed;
            UpdateClose.ToolTip = "Cancel the download";
            return;
        }

        bool installs = _app.Settings.Current.UpdateInstallsItself && r.ZipUrl != null;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.Content = installs ? "Update" : "Download";
        UpdateClose.ToolTip = "Hide until Persocord starts again";
        UpdateTitle.Text = $"Persocord {r.Label} is out";
        UpdateText.Text = Updates.Problem
            ?? (Updates.LastTryFailed ? $"The last update didn't finish, so you still have {Updates.CurrentLabel}. Press Update to try again."
                : installs ? $"You have {Updates.CurrentLabel}. Update installs it and reopens Persocord."
                : $"You have {Updates.CurrentLabel}. Download opens its page.");
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try { await _app.UpdateNowAsync(); }
        catch (Exception ex) { Log.Error("update", ex); Updates.Say("The update didn't work. Use What's new to get it by hand."); }
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e) =>
        Updates.OpenPage(Updates.Available?.Page ?? Updates.ReleasesPage);

    private void UpdateClose_Click(object sender, RoutedEventArgs e)
    {
        if (Updates.Downloading) { Updates.CancelDownload(); return; }
        _app.UpdateDismissed = true;
        ShowUpdate();
    }
    private readonly System.Windows.Threading.DispatcherTimer _setupTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>The "setting up" bar over the clips, while Persocord is still getting ready after starting.</summary>
    private void ShowSetup()
    {
        var (done, total, now) = StartupWork.State();
        if (now == null || StartupWork.Age > StartupWork.Longest)
        {
            SetupBar.Visibility = Visibility.Collapsed;
            _setupTimer.Stop();
            return;
        }
        // Not flashed up for the moment it takes when everything's ready already.
        if (StartupWork.Age < TimeSpan.FromMilliseconds(700)) return;
        SetupBar.Visibility = Visibility.Visible;
        SetupText.Text = $"Setting up Persocord: {now}… Previews may be slow until this is done.";
        SetupCount.Text = $"{Math.Min(done + 1, total)} of {total}";
        SetupProgress.Value = total == 0 ? 0 : (double)done / total;
    }

    private void OnLinkProgress(string clipId, string? _)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var r in _rows)
                if (r.Clip.Id == clipId) r.Touch();
        }));
    }

    private void TickLinks()
    {
        bool expired = false;
        foreach (var r in _rows)
        {
            if (r.Clip.Link != null && r.Clip.Link.Expired) { r.Clip.Link = null; expired = true; if (!IsVisible) r.Touch(); }
            // Countdowns are only redrawn when there's someone to see them.
            if (IsVisible) r.Touch();
        }
        if (expired) _app.Library.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window should not quit the app; the tray icon is home.
        e.Cancel = true;
        // While a clip is being watched, the X closes just the clip.
        if (_player != null) { _player.Close(); return; }
        Hide();
    }

    /* ------------------------------------------------------------------ */

    public void Refresh()
    {
        // Never block whoever raised the change (often a background thread).
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }

        var q = Search.Text?.Trim() ?? "";
        long limit = 0;   // no attachment limit any more; Compress Size is always available

        var all = _app.Library.Items.ToList();

        // A preview folder's videos; the gallery (every folder at once, like
        // searching); or the current folder's own clips.
        // A level with folders shows its folders (filling the area), plus an
        // "Unsorted" tile for the clips at this level that aren't in one.
        var here = all.Where(c => string.Equals(LibraryService.FolderOf(c), _folder, StringComparison.OrdinalIgnoreCase)).ToList();
        bool atLevel = q.Length == 0 && !_gallery && _preview == null;
        bool tiles = atLevel && !_loose && HasFolderTiles();
        var scope = _preview != null ? PreviewClips(_preview)
            : _gallery || q.Length > 0 ? all
            : tiles ? new System.Collections.Generic.List<ClipRecord>()
            : here;
        BuildCrumbs();
        BuildFolderTiles(tiles, all, here.Count);
        // The folders take the whole area; the clip list steps aside.
        FolderScroll.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;

        var searched = scope
            .Where(c => q.Length == 0
                     || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.App.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || (c.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                     || (c.Tags?.Any(t => t.Contains(q.TrimStart('#'), StringComparison.OrdinalIgnoreCase)) ?? false))
            .ToList();

        BuildFilterBar(searched);
        var wanted = Sorted(searched.Where(c => Matches(c, _filter))).ToList();

        // Update in place: rows that still exist are kept (with their loaded
        // thumbnails and scroll position), only the differences move.
        var existing = new System.Collections.Generic.Dictionary<ClipRecord, ClipRow>();
        foreach (var r in _rows) existing[r.Clip] = r;

        var target = new System.Collections.Generic.List<ClipRow>(wanted.Count);
        foreach (var c in wanted)
        {
            var folderLabel = (q.Length > 0 || _gallery) && _preview == null && LibraryService.FolderOf(c).Length > 0 ? "in " + LibraryService.FolderOf(c) : "";
            if (existing.TryGetValue(c, out var keep)) { keep.FolderLabel = folderLabel; keep.Update(limit); target.Add(keep); }
            else target.Add(new ClipRow(c, _app.Library, limit, _app.UploadStatusOf) { FolderLabel = folderLabel });
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i], target[i])) continue;
            int at = _rows.IndexOf(target[i]);
            if (at >= 0) _rows.Move(at, i);
            else _rows.Insert(i, target[i]);
        }
        while (_rows.Count > target.Count) _rows.RemoveAt(_rows.Count - 1);

        bool any = _rows.Count > 0;
        bool folders = FolderTiles.Children.Count > 0;
        // "No clips yet" only when there's truly nothing: not in a folder,
        // not filtering, and no folders to open.
        EmptyState.Visibility = !any && !folders && _folder.Length == 0 && q.Length == 0 && _filter == "all"
            ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = any && !tiles ? Visibility.Visible : Visibility.Collapsed;

        UpdateDisk();
        UpdateAlert();
    }

    private void UpdateDisk()
    {
        var d = _app.Library.Disk();

        double trackWidth = 160;
        DiskFill.Width = Math.Max(0, Math.Min(trackWidth, trackWidth * d.FractionOfCap));
        DiskMark.Margin = new Thickness(trackWidth * d.WarnFraction, 0, 0, 0);

        var brass = (Brush)FindResource("Brass");
        var brick = (Brush)FindResource("Brick");
        var muted = (Brush)FindResource("Muted");

        // The bar and the number both go red, because a colour change alone is
        // easy to miss on a strip eight pixels tall.
        DiskFill.Fill = d.OverWarn ? brick : brass;
        DiskText.Foreground = d.OverWarn ? brick : muted;

        DiskText.Text = d.Cap <= 0
            ? "drive unavailable"
            : $"{Gb(d.Used)} of {Gb(d.Cap)}" + (d.OverCap ? "  ·  at limit" : "");

        DiskText.ToolTip =
            $"Allowed: {_app.Settings.Current.MaxDiskPercent:0.#}% of a {Gb(d.DriveTotal)} drive.\n" +
            $"Warning at {Gb(d.WarnAt)}.\n" +
            $"{Gb(d.DriveFree)} free on the drive overall.";
    }

    private static string Gb(long b) => b switch
    {
        <= 0 => "—",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0} MB",
        _ => $"{b / 1073741824.0:0.0} GB",
    };

    private void UpdateAlert()
    {
        if (_app.Library.FolderExists)
        {
            Alert.Visibility = Visibility.Collapsed;
            return;
        }
        AlertText.Text = $"The clips folder is missing:  {_app.Library.Folder}";
        Alert.Visibility = Visibility.Visible;
    }

    private void OnFolderTrouble(FolderStatus status, string message) =>
        Dispatcher.InvokeAsync(() =>
        {
            AlertText.Text = message.Replace("\n\n", "  ").Replace("\n", "  ");
            Alert.Visibility = Visibility.Visible;
        });

    private void OnStatus(string text) => Dispatcher.InvokeAsync(() => Status.Text = text);

    /* ---- dragging a clip into Discord ---------------------------------- */

    private void List_MouseDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Without a threshold every click on a button would start a drag.
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (e.OriginalSource is DependencyObject src && FindParent<Button>(src) != null) return;

        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ClipRow row) return;
        if (!File.Exists(row.FilePath)) return;

        try
        {
            // A file drop is the same payload Explorer produces, so Discord
            // and every other app treat it as a dragged-in file.
            var data = new DataObject(DataFormats.FileDrop, new[] { row.FilePath });

            // Inside Persocord, the same drag moves clips between folders:
            // every selected clip when the dragged one is part of a selection.
            var ids = List.SelectedItems.OfType<ClipRow>().Contains(row)
                ? List.SelectedItems.OfType<ClipRow>().Select(r => r.Clip.Id).ToArray()
                : new[] { row.Clip.Id };
            data.SetData(ClipIdsFormat, string.Join("|", ids));

            DragDrop.DoDragDrop(item, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex) { Log.Error("drag out", ex); }
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /* ---- row actions ---------------------------------------------------- */

    private static ClipRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as ClipRow;

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;

        if (!File.Exists(r.FilePath)) { Status.Text = "That file is gone."; Refresh(); return; }

        Status.Text = await ShareService.CopyFileAsync(r.FilePath)
            ? "Copied. Ctrl+V in Discord."
            : "Windows would not let go of the clipboard. Try again.";
    }

    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        var mb = CompressDialog.Ask(this, r.Clip.Title, r.Clip.Size, _app.Settings.Current.CompressPresetsMb, out var replace);
        if (mb == null) return;
        Status.Text = $"Compressing to {mb} MB…";
        await _app.CompressAsync(r.Clip, mb.Value * 1048576L, keepOriginal: !replace);
        Refresh();
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        await _app.UploadAsync(r.Clip, this);
        r.Touch();
    }

    private async void UploadMenu_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        await _app.UploadWithMenuAsync(r.Clip, this);
        r.Touch();
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r?.Clip.Link == null) return;
        AppController.CopyLink(r.Clip.Link.Url);
        r.FlashCopied();
        Status.Text = "Link copied. Ctrl+V in Discord.";
    }

    /* ---- the ⋯ menu (also on right-click) ------------------------------- */

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null || sender is not FrameworkElement anchor) return;
        OpenMenu(r, anchor);
    }

    private void List_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ClipRow r) return;
        if (!item.IsSelected) { List.SelectedItems.Clear(); item.IsSelected = true; }
        OpenMenu(r, null);
        e.Handled = true;
    }

    private void OpenMenu(ClipRow r, FrameworkElement? anchor)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, RoutedEventHandler click, bool enabled = true, bool? check = null)
        {
            var mi = new MenuItem { Header = header, Tag = r, IsEnabled = enabled };
            if (check.HasValue) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += click;
            menu.Items.Add(mi);
            return mi;
        }

        if (r.Clip.Tags.Contains(PreviewTag))
        {
            Item("Play", Play_Click);
            Item("Show in Explorer", (_, _) => ShareService.ShowInExplorer(r.FilePath));
            Item("Copy file", (_, _) => { _ = ShareService.CopyFileAsync(r.FilePath); Status.Text = "Copied, ready to paste."; });
            menu.Items.Add(new Separator());
            Item("Delete file…", (_, _) => DeletePreviewFile(r));
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
            return;
        }

        Item("Play", Play_Click);
        Item("Edit…", Edit_Click, r.CanEdit);
        Item("Share as link…", UploadMenu_Click, r.CanUpload);
        if (!r.Clip.IsGif) Item("Compress Size…", Compress_Click);
        menu.Items.Add(new Separator());
        Item("Keep (skip cleanup)", Fav_Click, check: r.Favourite);
        Item("Private (never upload)", Private_Click, check: r.Private);
        Item("Rename…", Rename_Click);
        Item("Tags…", Tags_Click);
        Item("Move to…", MoveTo_Click);
        if (!r.Clip.IsGif && !r.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            Item("Convert to MP4", Convert_Click);
        Item("Show in folder", ShowInFolder_Click);
        menu.Items.Add(new Separator());
        var del = Item("Delete", Delete_Click);
        del.SetResourceReference(ForegroundProperty, "Brick");

        if (anchor != null)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        menu.IsOpen = true;
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r != null) ShareService.ShowInExplorer(r.FilePath);
    }

    private async void KillLink_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r?.Clip.Link == null) return;

        var ok = Dialog.Show(this,
            "Take this link down now? Anyone opening it afterwards gets nothing.\n\n" +
            "Copies people already downloaded, or that Discord has cached, can't be recalled.",
            "Kill link", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        await _app.KillLinkAsync(r.Clip);
        r.Touch();
    }

    private void Private_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        _app.TogglePrivate(r.Clip);
        r.Touch();
        Status.Text = r.Clip.Private
            ? "Private: this clip can't be uploaded." + (r.Clip.Link != null ? " Its existing link still works until it expires or is killed." : "")
            : "No longer private.";
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r != null) _app.OpenEditor(r.Clip);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only there when it can do something; otherwise it's clutter.
        int videos = List.SelectedItems.OfType<ClipRow>().Count(r => !r.Clip.IsGif);
        JoinButton.Visibility = videos >= 2 ? Visibility.Visible : Visibility.Collapsed;
        JoinButton.Content = $"Join {videos} clips";
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var picked = List.SelectedItems.OfType<ClipRow>().Select(r => r.Clip).ToList();
        await _app.JoinAsync(picked, this);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r != null) OpenInPlayer(r);
    }

    /// <summary>
    /// Persocord's own player, with the clips as listed for next/previous.
    /// GIFs still open in Windows' viewer.
    /// </summary>
    private void OpenInPlayer(ClipRow r)
    {
        StopHoverPreview();
        if (r.Clip.IsGif) { ShareService.Play(r.FilePath); return; }
        var clips = _rows.Where(x => !x.Clip.IsGif).Select(x => x.Clip).ToList();
        ShowPlayer(clips, Math.Max(0, clips.IndexOf(r.Clip)));
    }

    private PlayerView? _player;

    /// <summary>
    /// Plays clips right here, over the list (so a screen share of this
    /// window keeps showing them). Back brings the list back.
    /// </summary>
    public async void ShowPlayer(System.Collections.Generic.IReadOnlyList<ClipRecord> clips, int index)
    {
        if (clips.Count == 0) return;
        StopHoverPreview();
        // The engine may still be loading (the first time on a PC it builds
        // a cache of its plugins): wait for it without freezing the window.
        bool ready = PlayerView.Engine != null;
        if (!ready)
        {
            Status.Text = "Loading the player…";
            ready = await PlayerView.WarmUpAsync();
            Status.Text = "";
        }
        if (!ready)
        {
            // Without VLC's engine, Windows' player rather than nothing.
            ShareService.Play(_app.Library.PathOf(clips[Math.Clamp(index, 0, clips.Count - 1)]));
            return;
        }
        _player?.Close();
        _player = new PlayerView(_app, clips, index, () =>
        {
            PlayerHost.Content = null;
            PlayerHost.Visibility = Visibility.Collapsed;
            _player = null;
            WindowLooks.SetShowingVideo(this, false);
            List.Focus();
        });
        // Video can't be drawn into a see-through window: solid while it plays.
        WindowLooks.SetShowingVideo(this, true);
        PlayerHost.Content = _player;
        PlayerHost.Visibility = Visibility.Visible;
    }

    /* ---- hover preview: the last 30 seconds -------------------------------- */

    private readonly System.Windows.Threading.DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly System.Collections.Generic.Dictionary<string, MediaInfo> _probeCache = new();
    private System.Windows.Controls.Primitives.Popup? _hoverPopup;
    private FramePlayer? _hoverPlayer;
    private ClipRow? _hoverRow;
    private FrameworkElement? _hoverAnchor;
    private int _hoverStamp;

    private void Thumb_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipRow r || r.Clip.IsGif) return;
        StopHoverPreview();
        _hoverRow = r;
        _hoverAnchor = fe;
        _hoverTimer.Stop();
        _hoverTimer.Tick -= HoverTick;
        _hoverTimer.Tick += HoverTick;
        _hoverTimer.Start();
    }

    private void Thumb_MouseLeave(object sender, MouseEventArgs e) => StopHoverPreview();

    private async void HoverTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        var r = _hoverRow;
        var anchor = _hoverAnchor;
        if (r == null || anchor == null || !anchor.IsMouseOver) return;
        int stamp = ++_hoverStamp;

        var path = r.FilePath;
        var probeKey = TimelineRender.Identity(path);
        if (!_probeCache.TryGetValue(probeKey, out var info))
        {
            var probed = await ClipEditor.ProbeAsync(path);
            if (probed == null) return;
            if (_probeCache.Count > 300) _probeCache.Clear();
            _probeCache[probeKey] = info = probed;
        }
        // Still the same hover?
        if (stamp != _hoverStamp || _hoverRow != r || !anchor.IsMouseOver) return;

        double start = Math.Max(0, info.Duration - 30);
        var title = new TextBlock
        {
            Text = (info.Duration > 30 ? "Last 30 s  ·  " : "") + r.Clip.Title, FontSize = 11.5,
            Margin = new Thickness(10, 7, 10, 7), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 364,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        // Fitted to the clip's shape, inside 384 × 216.
        double aspect = info.Width > 0 && info.Height > 0 ? (double)info.Width / info.Height : 16.0 / 9;
        int pw = aspect >= 384.0 / 216 ? 384 : (int)Math.Round(216 * aspect);
        int ph = aspect >= 384.0 / 216 ? (int)Math.Round(384 / aspect) : 216;
        pw -= pw % 2; ph -= ph % 2;

        var img = new Image { Width = pw, Height = ph, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var picture = new Grid { Width = 384, Height = 216, Background = Brushes.Black };
        picture.Children.Add(img);
        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(title);
        title.Margin = new Thickness(12, 8, 12, 9);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = stack, ClipToBounds = true,
            Margin = new Thickness(10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");

        // VLC decodes (on the graphics card) and hands each frame over as a
        // picture drawn here: no window of VLC's own can ever appear, and the
        // card keeps its rounded corners. Short clips play from the start.
        // VLC once it's loaded; until then (it's never waited for here) the
        // built-in preview.
        // Never two at once: one still here (however it got left) goes first,
        // rather than playing on unseen.
        var oldVlc = _hoverVlc; _hoverVlc = null; oldVlc?.Dispose();
        var oldPlayer = _hoverPlayer; _hoverPlayer = null; oldPlayer?.Dispose();
        if (_hoverPopup != null) { _hoverPopup.IsOpen = false; _hoverPopup = null; }

        if (PlayerView.Engine is { } vlc)
            _hoverVlc = new HoverVlc(vlc, path, start, pw, ph, img,
                Math.Clamp(_app.Settings.Current.PlayerVolume, 0, 100));
        else
        {
            _ = PlayerView.WarmUpAsync();
            var player = new FramePlayer(path, info, img, 640, 360)
            {
                Volume = Math.Clamp(_app.Settings.Current.PlayerVolume, 0, 100) / 100.0,
            };
            player.Ended += () => { if (_hoverPlayer == player) { player.Seek(start); player.Play(); } };
            _hoverPlayer = player;
            player.Seek(start);
            player.Play();
        }

        _hoverPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
            HorizontalOffset = 4, AllowsTransparency = true, Child = card, IsOpen = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
    }

    private HoverVlc? _hoverVlc;


    private void StopHoverPreview()
    {
        _hoverTimer.Stop();
        _hoverStamp++;
        _hoverRow = null;
        _hoverAnchor = null;
        var p = _hoverPlayer;
        _hoverPlayer = null;
        p?.Dispose();
        var hv = _hoverVlc;
        _hoverVlc = null;
        hv?.Dispose();
        if (_hoverPopup != null) { _hoverPopup.IsOpen = false; _hoverPopup = null; }
    }

    private void Fav_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        r.Clip.Favourite = !r.Clip.Favourite;
        _app.Library.Save();
        Status.Text = r.Clip.Favourite
            ? "Kept. Cleanup will skip this clip."
            : "No longer protected from cleanup.";
        Refresh();
    }

    /* ---- the name (click to rename), tags (click to edit), thumbnail (click to play) ---- */

    private void Title_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is TextBlock t) t.TextDecorations = TextDecorations.Underline;
    }

    private void Title_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is TextBlock t) t.TextDecorations = null;
    }

    private void Title_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) { e.Handled = true; return; }
        if (sender is not TextBlock t || t.DataContext is not ClipRow r || t.Parent is not Grid g) return;
        var box = g.Children.OfType<TextBox>().FirstOrDefault();
        if (box == null) return;
        e.Handled = true;
        box.Text = r.Clip.Title;
        box.Tag = r;
        t.Visibility = Visibility.Hidden;
        box.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter) { FinishRename(box, save: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { FinishRename(box, save: false); e.Handled = true; }
    }

    private void TitleBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.Visibility == Visibility.Visible) FinishRename(box, save: true);
    }

    private void FinishRename(TextBox box, bool save)
    {
        box.Visibility = Visibility.Collapsed;
        if (box.Parent is Grid g)
            foreach (var t in g.Children.OfType<TextBlock>()) t.Visibility = Visibility.Visible;
        if (!save || box.Tag is not ClipRow r) return;
        var name = box.Text.Trim();
        box.Tag = null;
        if (name.Length == 0 || name == r.Clip.Title) return;
        if (r.Clip.Tags.Contains(PreviewTag)) return;   // a preview folder's file keeps its own name
        RenameWithFile(r, name);
    }

    private void TagsArea_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipRow r || r.Clip.Tags.Contains(PreviewTag)) return;
        // Taken before the list sees it (the list used to swallow the first
        // click to select the row, and a second counted as a double-click).
        e.Handled = true;
        if (e.ClickCount != 1) return;
        // Tags_Click finds its clip in the sender's Tag (as the ⋯ menu passes it).
        Dispatcher.BeginInvoke(new Action(() => Tags_Click(new Border { Tag = r }, new RoutedEventArgs())));
    }

    private Point _thumbDown;

    private void Thumb_Down(object sender, MouseButtonEventArgs e) => _thumbDown = e.GetPosition(this);

    /// <summary>Clicking the picture plays the clip (dragging it into Discord doesn't).</summary>
    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipRow r) return;
        if ((e.GetPosition(this) - _thumbDown).Length > 6) return;
        e.Handled = true;
        OpenInPlayer(r);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;

        var name = RenameDialog.Ask(this, r.Clip.Title);
        if (string.IsNullOrWhiteSpace(name) || name == r.Clip.Title) return;

        RenameWithFile(r, name.Trim());
    }

    /// <summary>The clip and its file both take the new name.</summary>
    private void RenameWithFile(ClipRow r, string name)
    {
        StopHoverPreview();   // the preview may hold the file open
        bool fileToo = _app.Library.RenameClip(r.Clip, name);
        Status.Text = fileToo ? "Renamed." : "Renamed in Persocord. The file is in use, so it kept its old name.";
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;

        var answer = Dialog.Show(
            $"Delete “{r.Clip.Title}”?\n\n" +
            (_app.Settings.Current.DeleteToRecycleBin
                ? "It goes to the Recycle Bin, so it can be restored from there."
                : "The file is deleted for good; it does not go to the Recycle Bin."),
            "Persocord", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _app.ReleaseClip(r.Clip);
        await _app.Library.RemoveAsync(r.Clip);
        Status.Text = _app.Settings.Current.DeleteToRecycleBin ? "Moved to the Recycle Bin." : "Deleted.";
        Refresh();
    }

    private async System.Threading.Tasks.Task DeleteSelectedAsync()
    {
        var rows = List.SelectedItems.OfType<ClipRow>().ToList();
        if (rows.Count == 0) return;
        bool bin = _app.Settings.Current.DeleteToRecycleBin;
        var answer = Dialog.Show(
            (rows.Count == 1 ? $"Delete “{rows[0].Clip.Title}”?" : $"Delete {rows.Count} clips?") + "\n\n" +
            (bin ? "They go to the Recycle Bin." : "They're deleted for good."),
            "Persocord", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        foreach (var r in rows)
        {
            if (r.Clip.Tags.Contains(PreviewTag))
                try { LibraryService.DeleteClipFile(r.FilePath, bin); } catch (Exception ex) { Log.Error("delete preview file", ex); }
            else
            {
                _app.ReleaseClip(r.Clip);
                await _app.Library.RemoveAsync(r.Clip);
            }
        }
        if (_preview != null) _previewCache.Remove(_preview);
        Status.Text = bin ? "Moved to the Recycle Bin." : "Deleted.";
        Refresh();
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is ClipRow r) OpenInPlayer(r);
    }

    /* ---- top bar and banner --------------------------------------------- */

    /* ---- the replay switch -------------------------------------------------- */

    private void UpdateReplayToggle()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(UpdateReplayToggle)); return; }
        bool on = _app.Replay.IsRunning;
        if (_app.ReplayWaiting)
        {
            // Set to games only, with no game running: on, but waiting.
            ReplayText.Text = "Waiting for a game";
            ReplayDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Muted");
            ReplayToggle.ToolTip = "The replay starts by itself when a game runs (Settings → Replay). Click to turn the replay off.";
            return;
        }
        // On, but not capturing right now: shown here (and on the tray icon)
        // rather than by repeated notifications.
        var trouble = on ? _app.Replay.Trouble : null;
        ReplayText.Text = !on ? "Replay off" : trouble != null ? "Replay paused" : "Replay on";
        ReplayDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, !on ? "Faint" : trouble != null ? "Brick" : "Brass");
        ReplayToggle.ToolTip = !on
            ? "Not recording the screen. Click to start the replay again."
            : trouble != null
                ? $"Not capturing right now: {trouble}. It stays on and keeps trying by itself. Click to turn the replay off."
                : $"Keeping the last {_app.Settings.Current.ReplaySeconds} s of the screen. Click to stop recording it (remembered after restarts).";
    }

    private void ReplayToggle_Click(object sender, RoutedEventArgs e) => _app.ToggleReplay();

    /* ---- pointing at one clip ---------------------------------------------- */

    /// <summary>
    /// Brings one clip into view: opens its folder, clears anything hiding
    /// it, selects it, scrolls to it and pulses it briefly so it's easy to spot.
    /// </summary>
    /// <summary>Quiet: no hover preview, the player paused (the editor is opening).</summary>
    public void Hush()
    {
        StopHoverPreview();
        _player?.Pause();
    }

    /// <summary>Stops anything here that has a clip's file open (before the file is replaced).</summary>
    public void LetGoOf(string path)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => LetGoOf(path)); return; }
        StopHoverPreview();
        if (_player != null && string.Equals(_player.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
            _player.Close();
    }

    public void Reveal(ClipRecord clip)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Reveal(clip))); return; }

        Search.Text = "";
        _filter = "all";
        _folder = LibraryService.FolderOf(clip);
        _loose = true;   // the clip itself, even where its level has folders
        Refresh();

        var row = _rows.FirstOrDefault(r => r.Clip.Id == clip.Id);
        if (row == null) return;
        List.SelectedItems.Clear();
        List.SelectedItem = row;
        List.ScrollIntoView(row);

        // Once it's on screen, a short pulse.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
            {
                item.Focus();
                var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(260))
                {
                    AutoReverse = true,
                    RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
                };
                item.BeginAnimation(OpacityProperty, pulse);
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /* ---- folders ----------------------------------------------------------- */

    private const string ClipIdsFormat = "PersocordClipIds";
    private string _folder = "";
    /// <summary>Showing the clips at this level that aren't in a folder ("Unsorted").</summary>
    private bool _loose;

    /* ---- the gallery and preview folders --------------------------------- */

    private const string PreviewTag = "preview";
    private bool _gallery;
    private string? _preview;
    private readonly System.Collections.Generic.Dictionary<string, (DateTime At, System.Collections.Generic.List<ClipRecord> Clips)> _previewCache = new();

    /// <summary>
    /// A preview folder's videos, as clips that exist only here: nothing is
    /// moved, copied or indexed. Re-read at most every few seconds.
    /// </summary>
    private System.Collections.Generic.List<ClipRecord> PreviewClips(string folder)
    {
        if (_previewCache.TryGetValue(folder, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < 5) return hit.Clips;
        var list = new System.Collections.Generic.List<ClipRecord>();
        try
        {
            if (Directory.Exists(folder))
                // A folder Windows won't open no longer cuts the list short, and
                // links to elsewhere (which can loop back on themselves) aren't followed.
                foreach (var f in Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true, IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                         }))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".mp4" or ".mkv" or ".mov" or ".webm" or ".m4v" or ".avi" or ".gif")) continue;
                    var fi = new FileInfo(f);
                    list.Add(new ClipRecord
                    {
                        Id = "pv" + Paths.StableHash(f.ToLowerInvariant()),
                        Title = Path.GetFileNameWithoutExtension(f),
                        FileName = f,   // absolute: stays where it is
                        Created = fi.LastWriteTime,
                        Size = fi.Length,
                        State = ClipState.Ready,
                        Tags = new() { PreviewTag },
                    });
                }
        }
        catch (Exception ex) { Log.Error("preview folder", ex); }
        list = list.OrderByDescending(c => c.Created).ToList();
        _previewCache[folder] = (DateTime.UtcNow, list);
        return list;
    }

    private void AddPreview_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "A folder whose videos to show here (they stay where they are)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var s = _app.Settings.Current;
        if (!s.PreviewFolders.Any(f => string.Equals(f, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
            s.PreviewFolders.Add(dlg.SelectedPath);
        _app.Settings.Save(s);
        _gallery = false; _folder = ""; _preview = null; _loose = false;
        Refresh();
    }

    private void PreviewFolderMenu(string folder)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Show in Explorer" };
        open.Click += (_, _) => ShareService.ShowInExplorer(Path.Combine(folder, "."));
        var remove = new MenuItem { Header = "Remove from Persocord (files stay)" };
        remove.Click += (_, _) =>
        {
            var s = _app.Settings.Current;
            s.PreviewFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _app.Settings.Save(s);
            _previewCache.Remove(folder);
            if (_preview == folder) _preview = null;
            Refresh();
        };
        menu.Items.Add(open);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private void DeletePreviewFile(ClipRow r)
    {
        bool bin = _app.Settings.Current.DeleteToRecycleBin;
        var answer = Dialog.Show($"Delete “{Path.GetFileName(r.FilePath)}”?\n\n" + (bin ? "It goes to the Recycle Bin." : "It's deleted for good."),
            "Persocord", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try { LibraryService.DeleteClipFile(r.FilePath, bin); }
        catch (Exception ex) { Log.Error("delete preview file", ex); }
        if (_preview != null) _previewCache.Remove(_preview);
        Refresh();
    }

    public void OpenFolder(string relative)
    {
        _gallery = false;
        _preview = null;
        _folder = relative;
        _loose = false;
        _filter = "all";
        List.SelectedItems.Clear();
        Refresh();
    }

    /// <summary>"Clips › Roblox › Ranked": each part opens that level, and takes dropped clips.</summary>
    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var parts = _folder.Length == 0 ? Array.Empty<string>() : _folder.Split(Path.DirectorySeparatorChar);

        void Crumb(string label, string target, bool current)
        {
            var t = new TextBlock
            {
                Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = current ? Cursors.Arrow : Cursors.Hand, Padding = new Thickness(4, 2, 4, 2),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, current ? "Ink" : "Muted");
            if (!current)
            {
                t.MouseLeftButtonUp += (_, _) => OpenFolder(target);
                t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            MakeDropTarget(t, target);
            Crumbs.Children.Add(t);
        }

        void Chevron()
        {
            var c = new TextBlock { Text = "›", Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            c.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            Crumbs.Children.Add(c);
        }

        // The gallery: every clip from every folder, newest first.
        var g = new TextBlock
        {
            Text = "▦  Gallery", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            FontWeight = _gallery ? FontWeights.SemiBold : FontWeights.Normal, Padding = new Thickness(4, 2, 12, 2),
            ToolTip = "Every clip from every folder",
        };
        g.SetResourceReference(TextBlock.ForegroundProperty, _gallery ? "Brass" : "Muted");
        g.MouseLeftButtonUp += (_, _) => { _gallery = true; _preview = null; _folder = ""; _loose = false; List.SelectedItems.Clear(); Refresh(); };
        Crumbs.Children.Add(g);
        var bar = new TextBlock { Text = "|", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        Crumbs.Children.Add(bar);

        bool inFolders = !_gallery && _preview == null;
        bool unsorted = inFolders && _loose && HasFolderTiles();
        Crumb("Clips", "", inFolders && parts.Length == 0 && !unsorted);
        if (_preview != null)
        {
            Chevron();
            var t = new TextBlock { Text = "👁  " + Path.GetFileName(_preview.TrimEnd('\\')), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            Crumbs.Children.Add(t);
        }
        else if (inFolders)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                Chevron();
                Crumb(parts[i], string.Join(Path.DirectorySeparatorChar, parts.Take(i + 1)), i == parts.Length - 1 && !unsorted);
            }
            if (unsorted)
            {
                Chevron();
                var u = new TextBlock { Text = "Unsorted", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
                u.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                Crumbs.Children.Add(u);
            }
        }
    }

    /// <summary>Whether this level has folders to show (its own, or the preview folders at the top).</summary>
    private bool HasFolderTiles() =>
        _app.Library.SubfoldersOf(_folder).Any() || (_folder.Length == 0 && _app.Settings.Current.PreviewFolders.Count > 0);

    private void BuildFolderTiles(bool show, System.Collections.Generic.List<ClipRecord> all, int looseCount)
    {
        FolderTiles.Children.Clear();
        if (!show) return;

        // First: the clips at this level that aren't in any folder.
        if (looseCount > 0) FolderTiles.Children.Add(FolderTile(_folder, looseCount, unsorted: true));

        foreach (var folder in _app.Library.SubfoldersOf(_folder))
        {
            var prefix = folder + Path.DirectorySeparatorChar;
            int count = all.Count(c => c.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            FolderTiles.Children.Add(FolderTile(folder, count));
        }
        if (_folder.Length == 0)
            foreach (var pf in _app.Settings.Current.PreviewFolders.ToList())
                FolderTiles.Children.Add(FolderTile(pf, PreviewClips(pf).Count, preview: true));
    }

    private FrameworkElement FolderTile(string folder, int count, bool preview = false, bool unsorted = false)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1 3 Q 1 1 3 1 L 8 1 L 10 3.5 L 19 3.5 Q 21 3.5 21 5.5 L 21 15 Q 21 17 19 17 L 3 17 Q 1 17 1 15 Z"),
            Width = 22, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, preview || unsorted ? "Muted" : "Brass");
        if (unsorted)
            // A stack of clips rather than a folder.
            icon.Data = Geometry.Parse("M 4 1 L 18 1 Q 19 1 19 2 L 19 3 L 3 3 L 3 2 Q 3 1 4 1 Z M 2 5 L 20 5 Q 21 5 21 6 L 21 16 Q 21 17 20 17 L 2 17 Q 1 17 1 16 L 1 6 Q 1 5 2 5 Z");

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = unsorted ? "Unsorted" : (preview ? "👁 " : "") + Path.GetFileName(folder.TrimEnd('\\')), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
        var sub = new TextBlock { Text = (count == 1 ? "1 video" : $"{count} videos").Replace("video", preview ? "video" : "clip") + (preview ? " · preview" : ""), FontSize = 11.5 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);

        var tile = new Border
        {
            Width = 210, Height = 58, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 0, 12, 0),
            Margin = new Thickness(6, 0, 6, 8), Child = row, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            ToolTip = unsorted ? "Clips that aren't in any folder · click to open"
                    : preview ? folder : "Click to open · drop clips here to move them",
        };
        tile.SetResourceReference(Border.BackgroundProperty, "Panel");
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "CardHover");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "Panel");
        // One click opens it.
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1) return;
            if (unsorted) { _loose = true; List.SelectedItems.Clear(); Refresh(); }
            else if (preview) { _preview = folder; _gallery = false; List.SelectedItems.Clear(); Refresh(); }
            else OpenFolder(folder);
            e.Handled = true;
        };
        if (unsorted)
        {
            // Dropping clips here takes them out of their folder, to this level.
            MakeDropTarget(tile, folder);
            return tile;
        }
        if (preview)
        {
            tile.MouseRightButtonUp += (_, e) => { PreviewFolderMenu(folder); e.Handled = true; };
            return tile;
        }
        tile.MouseRightButtonUp += (_, e) => { FolderMenu(folder); e.Handled = true; };
        MakeDropTarget(tile, folder);
        return tile;
    }

    /// <summary>Clips dragged from the list and dropped here move into <paramref name="folder"/>.</summary>
    private void MakeDropTarget(FrameworkElement target, string folder)
    {
        target.AllowDrop = true;
        void Glow(bool on)
        {
            if (target is Border b)
            {
                if (on) b.SetResourceReference(Border.BorderBrushProperty, "Brass");
                else b.BorderBrush = Brushes.Transparent;
            }
        }
        target.DragEnter += (_, e) => { if (e.Data.GetDataPresent(ClipIdsFormat)) Glow(true); };
        target.DragLeave += (_, _) => Glow(false);
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(ClipIdsFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        target.Drop += async (_, e) =>
        {
            Glow(false);
            if (e.Data.GetData(ClipIdsFormat) is not string ids) return;
            e.Handled = true;
            var clips = _app.Library.Items.Where(c => ids.Split('|').Contains(c.Id)).ToList();
            await MoveClipsAsync(clips, folder);
        };
    }

    private async System.Threading.Tasks.Task MoveClipsAsync(System.Collections.Generic.List<ClipRecord> clips, string folder)
    {
        int moved = 0;
        foreach (var c in clips)
            if (await System.Threading.Tasks.Task.Run(() => _app.Library.MoveClip(c, folder))) moved++;
        var where = folder.Length == 0 ? "All clips" : Path.GetFileName(folder);
        Status.Text = moved == clips.Count
            ? $"Moved {moved} clip{(moved == 1 ? "" : "s")} to {where}."
            : $"Moved {moved} of {clips.Count}; the rest couldn't be moved (in use?).";
        Refresh();
    }

    private void FolderMenu(string folder)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Open", () => OpenFolder(folder));
        Item("Rename…", () =>
        {
            var name = RenameDialog.Ask(this, Path.GetFileName(folder));
            if (string.IsNullOrWhiteSpace(name)) return;
            var err = _app.Library.RenameFolder(folder, name);
            Status.Text = err ?? "Folder renamed.";
            Refresh();
        });
        Item("Show in Explorer", () => ShareService.ShowInExplorer(Path.Combine(_app.Library.Folder, folder, ".")));
        menu.Items.Add(new Separator());
        var del = Item("Delete (empty folders only)", () =>
        {
            var err = _app.Library.DeleteFolder(folder);
            Status.Text = err ?? "Folder deleted.";
            Refresh();
        });
        del.SetResourceReference(ForegroundProperty, "Brick");
        menu.IsOpen = true;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = RenameDialog.Ask(this, "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        _app.Library.EnsureFolder();
        var err = _app.Library.CreateFolder(_folder, name);
        Status.Text = err ?? $"Folder \"{name.Trim()}\" made.";
        Refresh();
    }

    private async void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        var picked = List.SelectedItems.OfType<ClipRow>().Contains(r)
            ? List.SelectedItems.OfType<ClipRow>().Select(x => x.Clip).ToList()
            : new System.Collections.Generic.List<ClipRecord> { r.Clip };
        var target = FolderPicker.Ask(this, _app.Library.AllFolders(), LibraryService.FolderOf(r.Clip));
        if (target != null) await MoveClipsAsync(picked, target);
    }

    /* ---- filters and sorting --------------------------------------------- */

    private string _filter = "all";

    private static readonly string[] SortLabels = { "Newest first", "Oldest first", "Largest first", "Longest first", "Name" };

    private static bool Matches(ClipRecord c, string filter) => filter switch
    {
        "all" => true,
        "kept" => c.Favourite,
        "link" => c.Link != null && !c.Link.Expired,
        "private" => c.Private,
        "edited" => c.IsEdited,
        "imported" => c.IsImported,
        _ when filter.StartsWith("tag:") =>
            c.Tags?.Any(t => string.Equals(t, filter[4..], StringComparison.OrdinalIgnoreCase)) ?? false,
        _ => true,
    };

    private System.Collections.Generic.IEnumerable<ClipRecord> Sorted(System.Collections.Generic.IEnumerable<ClipRecord> clips) =>
        _app.Settings.Current.LibrarySort switch
        {
            ClipSort.Oldest => clips.OrderBy(c => c.Created),
            ClipSort.Largest => clips.OrderByDescending(c => c.Size),
            ClipSort.Longest => clips.OrderByDescending(c => c.DurationMs),
            ClipSort.Name => clips.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => clips.OrderByDescending(c => c.Created),
        };

    /// <summary>
    /// The row of filter chips, each with its count. Kinds with no clips stay
    /// hidden so the row only shows what's useful; every tag gets a chip.
    /// </summary>
    private void BuildFilterBar(System.Collections.Generic.List<ClipRecord> clips)
    {
        FilterBar.Children.Clear();

        // A tag filter whose last clip lost the tag falls back to everything.
        if (_filter.StartsWith("tag:") && !clips.Any(c => Matches(c, _filter)))
            _filter = "all";

        void Chip(string key, string label, int count, bool always = false)
        {
            if (count == 0 && !always && _filter != key) return;
            bool on = _filter == key;

            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            text.Inlines.Add(new System.Windows.Documents.Run(label));
            var n = new System.Windows.Documents.Run("  " + count) { FontSize = 11.5 };
            n.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, on ? "Brass" : "Faint");
            text.Inlines.Add(n);
            text.SetResourceReference(TextBlock.ForegroundProperty, on ? "Brass" : "Muted");

            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 5, 12, 6),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Child = text,
            };
            chip.SetResourceReference(Border.BackgroundProperty, on ? "BrassSoft" : "ButtonFill");
            chip.MouseEnter += (_, _) => { if (!on) chip.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            chip.MouseLeave += (_, _) => { if (!on) chip.SetResourceReference(Border.BackgroundProperty, "ButtonFill"); };
            chip.MouseLeftButtonUp += (_, _) => { _filter = key; Refresh(); };
            FilterBar.Children.Add(chip);
        }

        Chip("all", "All", clips.Count, always: true);
        Chip("kept", "Kept", clips.Count(c => c.Favourite));
        Chip("link", "With link", clips.Count(c => c.Link != null && !c.Link.Expired));
        Chip("private", "Private", clips.Count(c => c.Private));
        Chip("edited", "Edited", clips.Count(c => c.IsEdited));
        Chip("imported", "Imported", clips.Count(c => c.IsImported));

        var tags = clips.SelectMany(c => c.Tags ?? new System.Collections.Generic.List<string>())
                        .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key);
        foreach (var g in tags) Chip("tag:" + g.Key, "#" + g.Key, g.Count());

    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex < 0) return;
        var s = _app.Settings.Current.Clone();
        s.LibrarySort = (ClipSort)SortBox.SelectedIndex;
        if (s.LibrarySort == _app.Settings.Current.LibrarySort) { Refresh(); return; }
        _app.Settings.Save(s);
        Refresh();
    }

    /* ---- tags ------------------------------------------------------------ */

    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r == null) return;
        var known = _app.Library.Items.SelectMany(c => c.Tags ?? new System.Collections.Generic.List<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var result = TagsDialog.Ask(this, r.Clip.Title, r.Clip.Tags ?? new System.Collections.Generic.List<string>(), known);
        if (result == null) return;
        r.Clip.Tags = result;
        _app.Library.Save();
        Status.Text = result.Count == 0 ? "Tags cleared." : "Tags saved.";
        Refresh();
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender);
        if (r != null) await _app.ConvertToMp4Async(r.Clip, this);
    }

    // The list is filtered once typing pauses (150 ms), not on every key.
    private System.Windows.Threading.DispatcherTimer? _searchDelay;

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_searchDelay == null)
        {
            _searchDelay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(); };
        }
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await System.Threading.Tasks.Task.Delay(180);   // let the window actually go away
        await _app.StartRegionRecordingAsync(false);
    }

    private async void RecordGif_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await System.Threading.Tasks.Task.Delay(180);
        await _app.StartRegionRecordingAsync(true);
    }

    private static readonly int[] GifWidths = { 360, 480, 640, 800, 0 };
    private static readonly int[] GifFpsChoices = { 10, 15, 20, 25, 30 };

    /// <summary>The GIF's own options, next to its button: picked from a few sizes, saved as they're picked.</summary>
    private void GifOptions_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Width = 330 };
        var pop = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = GifOptionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 6, StaysOpen = false, AllowsTransparency = true,
        };

        void Save(Action<AppSettings> change)
        {
            var c = _app.Settings.Current.Clone();
            change(c);
            _app.Settings.Save(c);
            Build();
        }

        TextBlock Heading(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return t;
        }

        // One row of chips; a value of someone's own that isn't offered shows as its own chip.
        WrapPanel Chips(int[] options, int current, Func<int, string> name, Action<int> pick)
        {
            var wrap = new WrapPanel();
            var all = options.Contains(current) ? options : options.Append(current).ToArray();
            foreach (var v in all)
            {
                bool on = v == current;
                var chip = new Border
                {
                    Padding = new Thickness(10, 5, 10, 6), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = name(v) + (options.Contains(v) ? "" : " (yours)"), FontSize = 12.5 },
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Panel");
                if (on) chip.SetResourceReference(Border.BorderBrushProperty, "Brass");
                else chip.BorderBrush = Brushes.Transparent;
                chip.MouseLeftButtonUp += (_, _) => { if (!on) pick(v); };
                wrap.Children.Add(chip);
            }
            return wrap;
        }

        void Build()
        {
            var s = _app.Settings.Current;
            panel.Children.Clear();
            panel.Children.Add(Heading("Width"));
            panel.Children.Add(Chips(GifWidths, s.GifWidth > 0 ? s.GifWidth : 0, w => w == 0 ? "Area's size" : $"{w} px",
                w => Save(c => c.GifWidth = w)));
            panel.Children.Add(Heading("Frame rate"));
            panel.Children.Add(Chips(GifFpsChoices, s.GifFps, f => $"{f} fps", f => Save(c => c.GifFps = f)));

            panel.Children.Add(Heading("Longest GIF"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var seconds = new TextBox { Text = s.GifMaxSeconds.ToString(), Width = 60 };
            row.Children.Add(seconds);
            var unit = new TextBlock { Text = "seconds", FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            unit.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            row.Children.Add(unit);
            panel.Children.Add(row);
            seconds.LostKeyboardFocus += (_, _) =>
            {
                if (int.TryParse(seconds.Text.Trim(), out var v) && Math.Clamp(v, 1, 120) != _app.Settings.Current.GifMaxSeconds)
                {
                    var c = _app.Settings.Current.Clone();
                    c.GifMaxSeconds = Math.Clamp(v, 1, 120);
                    _app.Settings.Save(c);
                }
            };

            var note = new TextBlock { Text = "Smaller and slower GIFs are much lighter to send. GIFs are larger than MP4s either way.",
                FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(note);
        }

        Build();
        var card = new Border { Child = panel, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        pop.Child = card;
        // The length box saves when it loses focus; closing the box counts.
        pop.Closed += (_, _) => Keyboard.ClearFocus();
        pop.IsOpen = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        _app.Library.EnsureFolder();
        ShareService.ShowInExplorer(Path.Combine(_app.Library.Folder, "."));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private async void Locate_Click(object sender, RoutedEventArgs e)
    {
        AlertText.Text = "Searching for the folder…";
        var found = await _app.Library.SearchForFolderAsync();
        if (found == null)
            AlertText.Text = "Could not find it. Choose a folder, or record again to start fresh.";
        Refresh();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where are your clips?",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _app.Settings.Current.LibraryFolder = dlg.SelectedPath;
        _app.Settings.Save();
        _app.Library.EnsureFolder();
        _app.Library.Load();
        await System.Threading.Tasks.Task.Yield();
        Refresh();
    }
}

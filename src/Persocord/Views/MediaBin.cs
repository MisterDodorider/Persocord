using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// The editor's media list, on the left: "Added" (pictures, videos and
/// sounds brought into this edit by pasting, dropping or importing) and "My
/// clips" (Persocord's clip list). Double-click (or drag onto the timeline)
/// to use one; files dropped here are added to the list.
/// </summary>
public sealed class MediaBin : Border
{
    /// <summary>What a dragged entry carries: its path and its title, separated by a tab.</summary>
    public const string Format = "PersocordMedia";

    public sealed class Entry : INotifyPropertyChanged
    {
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public string Meta { get; set; } = "";
        public string Glyph { get; init; } = "";
        /// <summary>A library clip's own thumbnail file, if it has one.</summary>
        public string? ThumbFile { get; init; }
        private ImageSource? _thumb;
        private bool _asked;

        /// <summary>The thumbnail: asked for the first time it's shown, then arrives a moment later.</summary>
        public ImageSource? Thumb
        {
            get
            {
                if (!_asked)
                {
                    _asked = true;
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Load));
                }
                return _thumb;
            }
        }

        private void Load()
        {
            _thumb = Thumbs.Get(ThumbFile ?? Path);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Thumbnails for the lists, kept for the session (small, and a few hundred at most).</summary>
    private static class Thumbs
    {
        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
        public static ImageSource? Get(string path)
        {
            if (Cache.TryGetValue(path, out var t)) return t;
            ImageSource? img = null;
            try
            {
                if (EditorMedia.KindOf(path) == MediaKind.Image && File.Exists(path))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = 160;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    img = bi;
                }
                else if (EditorMedia.KindOf(path) != MediaKind.Audio) img = ShellThumb.Get(path, 160);
            }
            catch { }
            if (Cache.Count > 200) Cache.Clear();
            Cache[path] = img;
            return img;
        }
    }

    private readonly ObservableCollection<Entry> _added = new();
    private readonly ObservableCollection<Entry> _clips = new();
    private readonly ListBox _list = new();
    private readonly Button _addedTab = new() { Content = "Added" }, _clipsTab = new() { Content = "My clips" };
    private readonly TextBox _search = new() { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(4, 6, 4, 0) };
    private readonly Func<IEnumerable<ClipRecord>> _library;
    private readonly Func<ClipRecord, string> _pathOf, _thumbOf;
    private bool _showClips;
    private Point? _press;

    /// <summary>Use this one: at the playhead.</summary>
    public event Action<string, string>? Use;
    /// <summary>"Import…" was pressed.</summary>
    public event Action? ImportRequested;
    /// <summary>Files were dropped on the list (to be brought in).</summary>
    public event Action<string[]>? FilesDropped;
    /// <summary>The Added list changed (kept with the edit).</summary>
    public event Action? Changed;

    public MediaBin(Func<IEnumerable<ClipRecord>> library, Func<ClipRecord, string> pathOf, Func<ClipRecord, string?> thumbOf)
    {
        _library = library;
        _pathOf = pathOf;
        _thumbOf = c => thumbOf(c) ?? "";
        Padding = new Thickness(10);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Panel");
        SetResourceReference(BorderBrushProperty, "Line");
        SetResourceReference(CornerRadiusProperty, "RadiusCard");
        AllowDrop = true;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in new[] { _addedTab, _clipsTab })
        {
            b.SetResourceReference(StyleProperty, "Ghost");
            b.Padding = new Thickness(10, 4, 10, 4);
            b.Margin = new Thickness(0, 0, 4, 0);
            tabs.Children.Add(b);
        }
        var import = new Button { ToolTip = "Bring in pictures, videos or sounds (or paste with Ctrl+V, or drop files anywhere)" };
        import.SetResourceReference(StyleProperty, "Ghost");
        import.Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 };
        import.Padding = new Thickness(8, 4, 8, 4);
        import.Click += (_, _) => ImportRequested?.Invoke();
        var head = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(import, Dock.Right);
        head.Children.Add(import);
        head.Children.Add(tabs);

        _search.TextChanged += (_, _) => FillClips();
        _search.ToolTip = "Search your clips";
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_list, true);
        _list.ItemTemplate = Template();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (Hit(e.OriginalSource) is { } en) Use?.Invoke(en.Path, en.Title);
        };
        _list.PreviewMouseLeftButtonDown += (_, e) => _press = e.GetPosition(_list);
        _list.PreviewMouseMove += (_, e) =>
        {
            if (_press is not { } at || e.LeftButton != MouseButtonState.Pressed) { _press = null; return; }
            var p = e.GetPosition(_list);
            if (Math.Abs(p.X - at.X) < 6 && Math.Abs(p.Y - at.Y) < 6) return;
            _press = null;
            if (Hit(e.OriginalSource) is not { } en) return;
            var data = new DataObject(Format, en.Path + "\t" + en.Title);
            try { DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy); } catch { }
        };
        _list.ContextMenuOpening += (_, e) =>
        {
            if (Hit(e.OriginalSource) is not { } en) { e.Handled = true; return; }
            var menu = new ContextMenu();
            var use = new MenuItem { Header = "Add at the playhead" };
            use.Click += (_, _) => Use?.Invoke(en.Path, en.Title);
            menu.Items.Add(use);
            if (!_showClips)
            {
                var remove = new MenuItem { Header = "Remove from this list" };
                remove.Click += (_, _) => { _added.Remove(en); Refresh(); Changed?.Invoke(); };
                menu.Items.Add(remove);
            }
            _list.ContextMenu = menu;
        };
        _list.ContextMenu = new ContextMenu();

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_empty, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(_search);
        body.Children.Add(_empty);
        body.Children.Add(_list);
        Child = body;

        _addedTab.Click += (_, _) => ShowTab(false);
        _clipsTab.Click += (_, _) => ShowTab(true);
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) { e.Handled = true; FilesDropped?.Invoke(files); }
        };
        ShowTab(false);
    }

    private static Entry? Hit(object source)
    {
        for (var d = source as DependencyObject; d != null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: Entry en }) return en;
        return null;
    }

    private static DataTemplate Template() => (DataTemplate)XamlReader.Parse(
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<DockPanel Margin='0,3,0,3'>" +
        "  <Border DockPanel.Dock='Left' Width='64' Height='36' CornerRadius='4' Background='#FF000000' ClipToBounds='True'>" +
        "    <Grid><TextBlock Text='{Binding Glyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='14' Foreground='#80FFFFFF'" +
        "                     HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
        "      <Image Source='{Binding Thumb}' Stretch='UniformToFill'/></Grid>" +
        "  </Border>" +
        "  <StackPanel Margin='8,0,0,0' VerticalAlignment='Center'>" +
        "    <TextBlock Text='{Binding Title}' TextTrimming='CharacterEllipsis' FontSize='12'/>" +
        "    <TextBlock Text='{Binding Meta}' TextTrimming='CharacterEllipsis' FontSize='10.5' Opacity='0.6'/>" +
        "  </StackPanel>" +
        "</DockPanel></DataTemplate>");

    private void ShowTab(bool clips)
    {
        _showClips = clips;
        Mark(_addedTab, !clips);
        Mark(_clipsTab, clips);
        _search.Visibility = clips ? Visibility.Visible : Visibility.Collapsed;
        if (clips) FillClips(); else Refresh();
    }

    private static void Mark(Button b, bool on)
    {
        if (on) { b.SetResourceReference(Control.BackgroundProperty, "BrassSoft"); b.SetResourceReference(Control.ForegroundProperty, "AccentText"); }
        else { b.ClearValue(Control.BackgroundProperty); b.ClearValue(Control.ForegroundProperty); }
    }

    private void Refresh()
    {
        if (_showClips) return;
        _list.ItemsSource = _added;
        _empty.Text = _added.Count == 0 ? "Paste (Ctrl+V), drop or import pictures, videos and sounds: they're kept here for this edit." : "";
        _empty.Visibility = _added.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillClips()
    {
        if (!_showClips) return;
        var q = _search.Text.Trim();
        _clips.Clear();
        foreach (var c in _library()
                     .Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 c.App.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 c.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(c => c.Created))
        {
            var t = TimeSpan.FromMilliseconds(c.DurationMs);
            var thumb = _thumbOf(c);
            _clips.Add(new Entry
            {
                Path = _pathOf(c), Title = c.Title,
                Meta = $"{(t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"))}  ·  {c.Created:d MMM}{(c.App.Length > 0 ? "  ·  " + c.App : "")}",
                ThumbFile = thumb.Length > 0 && File.Exists(thumb) ? thumb : null,
                Glyph = c.IsGif ? "" : "",
            });
        }
        _list.ItemsSource = _clips;
        _empty.Text = _clips.Count == 0 ? (q.Length > 0 ? "No clips match." : "No other clips yet.") : "";
        _empty.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Added list (kept with the edit).</summary>
    public IEnumerable<(string Path, string Title)> Added => _added.Select(e => (e.Path, e.Title));

    /// <summary>Puts something on the Added list (once), and shows that list.</summary>
    public void Add(string path, string title, MediaInfo? info)
    {
        var kind = EditorMedia.KindOf(path);
        var existing = _added.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _added.Remove(existing);
        string meta = kind switch
        {
            MediaKind.Image => info is { Width: > 0 } ? $"Picture  ·  {info.Width}×{info.Height}" : "Picture",
            MediaKind.Audio => info != null ? $"Sound  ·  {Length(info.Duration)}" : "Sound",
            _ => info != null ? $"Video  ·  {Length(info.Duration)}{(info.Width > 0 ? $"  ·  {info.Width}×{info.Height}" : "")}" : "Video",
        };
        _added.Insert(0, new Entry
        {
            Path = path, Title = title, Meta = meta,
            Glyph = kind switch { MediaKind.Image => "", MediaKind.Audio => "", _ => "" },
        });
        if (_showClips) ShowTab(false); else Refresh();
        Changed?.Invoke();
    }

    private static string Length(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

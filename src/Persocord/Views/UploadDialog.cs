using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// The full upload menu, opened from the arrow beside Upload. Also shown in
/// place of a one-click upload until the link warning has been acknowledged.
/// </summary>
public class UploadDialog : Window
{
    private readonly LinkOptionsView _options;
    private readonly CheckBox _gotIt = new() { Margin = new Thickness(0, 8, 0, 0),
        Content = "Got it. Show this as a small note from now on." };
    private readonly CheckBox _makeDefault = new() { Margin = new Thickness(0, 12, 0, 0),
        Content = "Use these as my defaults for one-click Upload" };
    private readonly Button _go = new() { Content = "Upload and copy link", MinWidth = 150, IsDefault = true };

    public LinkChoice Result => _options.Choice;
    public bool MakeDefault => _makeDefault.IsChecked == true;
    public bool WarningAcknowledged => _gotIt.IsChecked == true;

    public UploadDialog(ClipRecord clip, LinkChoice start, bool warningAcknowledged)
    {
        Title = "Share as link";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = clip.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var meta = new TextBlock { Text = $"{clip.DurationText}  ·  {clip.SizeText}", Margin = new Thickness(0, 2, 0, 14) };
        meta.SetResourceReference(ForegroundProperty, "Muted");
        root.Children.Add(meta);

        _options = new LinkOptionsView(start, clip.Size, clip.IsGif);
        _options.Changed += () => _go.IsEnabled = _options.Service != null;
        root.Children.Add(_options);

        root.Children.Add(Warning(warningAcknowledged));
        root.Children.Add(_makeDefault);

        _go.IsEnabled = _options.Service != null;
        _go.SetResourceReference(StyleProperty, "Primary");
        _go.Click += (_, _) => { DialogResult = true; Close(); };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_go);
        root.Children.Add(buttons);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height * 0.9,
        };
    }

    private UIElement Warning(bool acknowledged)
    {
        const string body =
            "Anyone who gets this link can watch and download the clip. It isn't encrypted: " +
            "links can be forwarded, guessed, or kept by Discord after the host deletes them. " +
            "Never share recordings of passwords, logins or anything private this way, and " +
            "prefer the shortest duration that does the job.";

        if (acknowledged)
        {
            var note = new TextBlock
            {
                Text = "Links are public to anyone who has them. Shorter is safer.",
                FontSize = 11.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
            };
            note.SetResourceReference(ForegroundProperty, "Muted");
            return note;
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Before you share", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(_gotIt);

        var box = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xE0, 0x59, 0x4D)),
            Child = panel,
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Brick");
        return box;
    }
}

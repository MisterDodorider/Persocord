using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Persocord.Views;

/// <summary>
/// A dark stand-in for Windows' message box, which is bright white and
/// jarring in a dark app. Same calls, same answers.
/// </summary>
public class Dialog : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxResult _escape;

    private Dialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        Title = caption;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "Ink");

        _escape = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };
        _result = _escape;

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x35, 0x3D)),
            Margin = new Thickness(18),
            Padding = new Thickness(22, 20, 22, 18),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var body = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = Glyph(image);
        if (glyph != null) head.Children.Add(glyph);
        var title = new TextBlock
        {
            Text = caption, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Display");
        head.Children.Add(title);
        body.Children.Add(head);

        var message = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), LineHeight = 19,
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Body");   // follows light or dark
        body.Children.Add(message);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        foreach (var (label, result, primary) in ButtonsFor(buttons))
        {
            var b = new Button { Content = label, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            if (primary) b.SetResourceReference(StyleProperty, "Primary");
            b.Click += (_, _) => { _result = result; DialogResult = true; };
            row.Children.Add(b);
        }
        body.Children.Add(row);

        card.Child = body;
        Content = card;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = _escape; DialogResult = false; e.Handled = true; }
        };
    }

    private static (string, MessageBoxResult, bool)[] ButtonsFor(MessageBoxButton b) => b switch
    {
        MessageBoxButton.OKCancel => new[] { ("Cancel", MessageBoxResult.Cancel, false), ("OK", MessageBoxResult.OK, true) },
        MessageBoxButton.YesNo => new[] { ("No", MessageBoxResult.No, false), ("Yes", MessageBoxResult.Yes, true) },
        MessageBoxButton.YesNoCancel => new[]
        {
            ("Cancel", MessageBoxResult.Cancel, false), ("No", MessageBoxResult.No, false), ("Yes", MessageBoxResult.Yes, true),
        },
        _ => new[] { ("OK", MessageBoxResult.OK, true) },
    };

    private static FrameworkElement? Glyph(MessageBoxImage image)
    {
        (string mark, Color colour)? look = image switch
        {
            MessageBoxImage.Warning => ("!", Color.FromRgb(0xD6, 0xB0, 0x6A)),
            MessageBoxImage.Error => ("✕", Color.FromRgb(0xE0, 0x59, 0x4D)),
            MessageBoxImage.Question => ("?", ThemeApplier.Current.Ink),
            MessageBoxImage.Information => ("i", ThemeApplier.Current.Ink),
            _ => null,
        };
        if (look is not { } l) return null;

        return new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 11, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, l.colour.R, l.colour.G, l.colour.B)),
            Child = new TextBlock
            {
                Text = l.mark, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = new SolidColorBrush(l.colour),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static MessageBoxResult Show(Window? owner, string text, string caption = "Persocord",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var d = new Dialog(text, caption, buttons, image);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w is not Dialog);

        if (owner != null && owner.IsVisible)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // No window to sit on (a question from the tray, say): centre
            // on screen and stay on top so it isn't lost behind a game.
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            d.Topmost = true;
            d.ShowInTaskbar = true;
        }

        d.ShowDialog();
        return d._result;
    }

    public static MessageBoxResult Show(string text, string caption = "Persocord",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        Show(null, text, caption, buttons, image);

    /// <summary>A box to paste a share code into (settings or look). The text pasted, or null if cancelled.</summary>
    public static string? AskCode(Window owner, string title, string prompt)
    {
        var box = new System.Windows.Controls.TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, VerticalContentAlignment = VerticalAlignment.Top };
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner,
        };
        dlg.SetResourceReference(Window.BackgroundProperty, "Bezel");
        dlg.SetResourceReference(Window.ForegroundProperty, "Ink");
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(box);
        var go = new System.Windows.Controls.Button { Content = "Import", MinWidth = 90, IsDefault = true };
        go.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        go.Click += (_, _) => dlg.DialogResult = true;
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(go);
        root.Children.Add(row);
        dlg.Content = root;
        dlg.Loaded += (_, _) => box.Focus();
        return dlg.ShowDialog() == true ? box.Text : null;
    }
}

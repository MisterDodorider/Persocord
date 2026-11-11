using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Persocord.Views;

/// <summary>
/// Compress Size: pick one of the quick sizes, or type any exact size, and
/// the clip gets a copy that fits it.
/// </summary>
public class CompressDialog : Window
{
    private readonly TextBox _custom = new() { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
    private int? _result;

    private CompressDialog(string clipName, long currentBytes, IReadOnlyList<int> presets)
    {
        Title = "Compress Size";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Compress to what size?", FontSize = 15, FontWeight = FontWeights.SemiBold });
        var now = new TextBlock
        {
            Text = $"{clipName}  ·  now {currentBytes / 1048576.0:0.#} MB",
            Margin = new Thickness(0, 4, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5,
        };
        now.SetResourceReference(ForegroundProperty, "Muted");
        root.Children.Add(now);

        // Quick sizes: one click compresses. Sizes the clip is already under are greyed out.
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var mb in presets)
        {
            var b = new Button
            {
                Content = $"{mb} MB", MinWidth = 72, Margin = new Thickness(0, 0, 8, 8),
                IsEnabled = mb * 1048576L < currentBytes,
            };
            b.Click += (_, _) => { _result = mb; DialogResult = true; };
            chips.Children.Add(b);
        }
        root.Children.Add(chips);

        var customLabel = new TextBlock { Text = "Or an exact size", FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        customLabel.SetResourceReference(ForegroundProperty, "Muted");
        root.Children.Add(customLabel);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_custom);
        row.Children.Add(new TextBlock { Text = "MB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        var go = new Button { Content = "Compress", MinWidth = 100, IsDefault = true };
        go.SetResourceReference(StyleProperty, "Primary");
        go.Click += (_, _) => Accept();
        row.Children.Add(go);
        root.Children.Add(row);

        _replace.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_replace);

        var hint = new TextBlock
        {
            Text = "Untick to keep both. Big reductions lower the resolution a little to keep it clean.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 12, 0, 0),
        };
        hint.SetResourceReference(ForegroundProperty, "Faint");
        root.Children.Add(hint);

        Content = root;
        Loaded += (_, _) => _custom.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Accept()
    {
        var text = _custom.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) && mb >= 1 && mb <= 100000)
        {
            _result = (int)Math.Round(mb);
            DialogResult = true;
        }
        else
        {
            _custom.SetResourceReference(BorderBrushProperty, "Brick");
            _custom.Focus();
        }
    }

    private readonly CheckBox _replace = new() { Content = "Replace original", IsChecked = true };

    /// <summary>The chosen size in MB (null if cancelled), and whether the original is replaced.</summary>
    public static int? Ask(Window owner, string clipName, long currentBytes, IReadOnlyList<int> presets, out bool replace)
    {
        var d = new CompressDialog(clipName, currentBytes, presets) { Owner = owner };
        d.ShowDialog();
        replace = d._replace.IsChecked == true;
        return d._result;
    }
}

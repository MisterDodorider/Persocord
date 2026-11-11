using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Persocord.Views;

/// <summary>
/// Edits one clip's tags: type new ones separated by commas, or tap tags
/// already used on other clips to add or remove them.
/// </summary>
public class TagsDialog : Window
{
    private readonly TextBox _box = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly WrapPanel _known = new() { Margin = new Thickness(0, 12, 0, 0) };
    private List<string>? _result;

    private TagsDialog(string clipTitle, IEnumerable<string> current, IEnumerable<string> known)
    {
        Title = "Tags";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = clipTitle, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var hint = new TextBlock { Text = "Separate tags with commas, like: funny, ranked, with Sam", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(StyleProperty, "Meta");
        root.Children.Add(hint);

        _box.Text = string.Join(", ", current);
        _box.TextChanged += (_, _) => PaintKnown();
        root.Children.Add(_box);

        foreach (var tag in known)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Tag = tag,
                Child = new TextBlock { Text = "#" + tag, FontSize = 12 },
            };
            chip.MouseLeftButtonUp += (_, _) => Toggle(tag);
            _known.Children.Add(chip);
        }
        if (_known.Children.Count > 0) root.Children.Add(_known);
        PaintKnown();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
        save.SetResourceReference(StyleProperty, "Primary");
        save.Click += (_, _) => { _result = Parse(_box.Text); DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    private static List<string> Parse(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Trim())
            .Where(t => t.Length > 0 && t.Length <= 32)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Toggle(string tag)
    {
        var tags = Parse(_box.Text);
        var existing = tags.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (existing != null) tags.Remove(existing); else tags.Add(tag);
        _box.Text = string.Join(", ", tags);
        _box.CaretIndex = _box.Text.Length;
    }

    /// <summary>Tags already on this clip are shown lit.</summary>
    private void PaintKnown()
    {
        var on = new HashSet<string>(Parse(_box.Text), StringComparer.OrdinalIgnoreCase);
        foreach (Border chip in _known.Children)
        {
            bool lit = on.Contains((string)chip.Tag);
            chip.SetResourceReference(Border.BackgroundProperty, lit ? "BrassSoft" : "ButtonFill");
            ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, lit ? "Brass" : "Muted");
        }
    }

    /// <summary>The new tags, or null if cancelled.</summary>
    public static List<string>? Ask(Window owner, string clipTitle, IEnumerable<string> current, IEnumerable<string> known)
    {
        var d = new TagsDialog(clipTitle, current, known) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Persocord.Views;

/// <summary>Picks a folder in the library to move clips into.</summary>
public class FolderPicker : Window
{
    private readonly ListBox _list = new() { Height = 260, Margin = new Thickness(0, 10, 0, 0) };
    private string? _result;

    private FolderPicker(IReadOnlyList<string> folders, string current)
    {
        Title = "Move to";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Move to which folder?", FontWeight = FontWeights.SemiBold });

        // The top of the library, then every folder, indented by depth.
        var entries = new List<(string path, string label)> { ("", "All clips (top level)") };
        entries.AddRange(folders.Select(f =>
            (f, new string(' ', 4 * f.Count(ch => ch == Path.DirectorySeparatorChar)) + Path.GetFileName(f))));

        foreach (var (path, label) in entries)
        {
            var item = new ListBoxItem
            {
                Content = label + (path == current ? "   (here now)" : ""),
                Tag = path, Padding = new Thickness(10, 7, 10, 7),
                IsEnabled = path != current,
            };
            item.SetResourceReference(ForegroundProperty, "Ink");
            item.MouseDoubleClick += (_, _) => { _result = path; DialogResult = true; };
            _list.Items.Add(item);
        }
        _list.SetResourceReference(BackgroundProperty, "Field");
        root.Children.Add(_list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var move = new Button { Content = "Move", MinWidth = 90, IsDefault = true };
        move.SetResourceReference(StyleProperty, "Primary");
        move.Click += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem it) { _result = (string)it.Tag; DialogResult = true; }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(move);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>The chosen folder ("" for the top level), or null if cancelled.</summary>
    public static string? Ask(Window owner, IReadOnlyList<string> folders, string current)
    {
        var d = new FolderPicker(folders, current) { Owner = owner };
        d.ShowDialog();
        return d._result;
    }
}

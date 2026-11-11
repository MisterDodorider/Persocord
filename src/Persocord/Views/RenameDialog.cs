using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Persocord.Views;

/// <summary>A one-field prompt, since WPF has no built-in input box.</summary>
public class RenameDialog : Window
{
    private readonly TextBox _box = new();

    private RenameDialog(string current)
    {
        Title = "Rename clip";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        _box.Text = current;
        _box.Margin = new Thickness(0, 0, 0, 12);

        var ok = new Button { Content = "Save", Width = 84, IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };

        var cancel = new Button
        {
            Content = "Cancel", Width = 84, IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { _box, buttons },
        };

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string current)
    {
        var d = new RenameDialog(current) { Owner = owner };
        return d.ShowDialog() == true ? d._box.Text : null;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Persocord.Views;

public enum SaveChoice { Cancel, First, Second }

/// <summary>A question with two real answers and a way out.</summary>
public class SaveChoiceDialog : Window
{
    private SaveChoice _choice = SaveChoice.Cancel;

    private SaveChoiceDialog(string title, string message, string first, string second)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bezel");
        SetResourceReference(ForegroundProperty, "Ink");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var b2 = new Button { Content = second, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        var b1 = new Button { Content = first, MinWidth = 130, IsDefault = true };
        b1.SetResourceReference(StyleProperty, "Primary");

        b1.Click += (_, _) => { _choice = SaveChoice.First; DialogResult = true; };
        b2.Click += (_, _) => { _choice = SaveChoice.Second; DialogResult = true; };

        buttons.Children.Add(cancel);
        buttons.Children.Add(b2);
        buttons.Children.Add(b1);
        root.Children.Add(buttons);
        Content = root;
    }

    public static SaveChoice Ask(Window owner, string title, string message, string first, string second)
    {
        var d = new SaveChoiceDialog(title, message, first, second) { Owner = owner };
        d.ShowDialog();
        return d._choice;
    }
}

using System.Windows;

namespace Persocord.Views;

/// <summary>
/// Gives every window its look as it loads: title bar colours (dark or light,
/// matching the chosen background), and the window's glass, background and
/// see-through level from Customization (see <see cref="WindowLooks"/>).
/// </summary>
internal static class DarkChrome
{
    /// <summary>Applies to every window as it loads, including dialogs and ones made in code.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply(sender as Window)));

    public static void Apply(Window? window) => WindowLooks.Apply(window);
}

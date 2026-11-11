using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Persocord.Views;

/// <summary>
/// Every slider in Persocord follows the mouse from wherever it's pressed on
/// the bar, not only when the little handle is grabbed: press anywhere and
/// drag. Registered once for the whole app.
/// </summary>
public static class SliderDrag
{
    /// <summary>A drag on a slider began (instead of Thumb.DragStarted, which no longer fires).</summary>
    public static readonly RoutedEvent StartedEvent = EventManager.RegisterRoutedEvent(
        "Started", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SliderDrag));

    /// <summary>The mouse let go of a slider.</summary>
    public static readonly RoutedEvent EndedEvent = EventManager.RegisterRoutedEvent(
        "Ended", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SliderDrag));

    private static readonly DependencyProperty DraggingProperty =
        DependencyProperty.RegisterAttached("Dragging", typeof(bool), typeof(SliderDrag), new PropertyMetadata(false));

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up));
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.LostMouseCaptureEvent, new MouseEventHandler(Lost));
    }

    private static bool Dragging(Slider s) => (bool)s.GetValue(DraggingProperty);

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s || !s.IsEnabled || e.Handled) return;
        if (!s.CaptureMouse()) return;
        s.SetValue(DraggingProperty, true);
        s.RaiseEvent(new RoutedEventArgs(StartedEvent, s));
        Follow(s, e);
        e.Handled = true;
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        if (sender is Slider s && Dragging(s) && s.IsMouseCaptured) Follow(s, e);
    }

    private static void Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s || !Dragging(s)) return;
        Follow(s, e);
        s.ReleaseMouseCapture();   // ends the drag (below)
        e.Handled = true;
    }

    private static void Lost(object sender, MouseEventArgs e)
    {
        if (sender is not Slider s || !Dragging(s)) return;
        s.SetValue(DraggingProperty, false);
        s.RaiseEvent(new RoutedEventArgs(EndedEvent, s));
    }

    /// <summary>The value under the mouse, snapped to the slider's steps if it has them.</summary>
    private static void Follow(Slider s, MouseEventArgs e)
    {
        double v;
        if (s.Template?.FindName("PART_Track", s) is Track track && track.ActualWidth > 0 && track.ActualHeight > 0)
            v = track.ValueFromPoint(e.GetPosition(track));
        else
        {
            var at = e.GetPosition(s);
            double f = s.Orientation == Orientation.Horizontal
                ? at.X / Math.Max(1, s.ActualWidth)
                : 1 - at.Y / Math.Max(1, s.ActualHeight);
            v = s.Minimum + Math.Clamp(f, 0, 1) * (s.Maximum - s.Minimum);
            if (s.IsDirectionReversed) v = s.Maximum - (v - s.Minimum);
        }
        if (s.IsSnapToTickEnabled && s.TickFrequency > 0)
            v = s.Minimum + Math.Round((v - s.Minimum) / s.TickFrequency) * s.TickFrequency;
        s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
    }
}

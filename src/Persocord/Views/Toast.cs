using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Persocord's own notifications, replacing Windows' plain ones: a small
/// dark card that glides in, stays for the chosen time (at most), and fades
/// away. They never take focus, so a game keeps its keyboard and mouse, and
/// several stack neatly. A click dismisses one.
/// </summary>
public sealed class Toast : Window
{
    private const double CardWidth = 340;
    private const double Gap = 10;
    private const double Edge = 16;

    private static readonly List<Toast> Open = new();

    private readonly ToastPosition _pos;
    private readonly Border _card;
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _timer;
    private bool _closing;
    private Action? _onClick;

    public static void Show(string title, string message, AppSettings s, Action? onClick = null)
    {
        // Oldest goes first when too many pile up.
        while (Open.Count >= 4) Open[0].Dismiss();
        var t = new Toast(title, message, s.ToastPosition, Math.Clamp(s.ToastSeconds, 0.5, 30),
                          Math.Clamp(LookStore.Current.ToastSizePercent, 75, 150) / 100.0);
        t._onClick = onClick;
        Open.Add(t);
        t.Show();
        Place(animate: false, only: t);
    }

    /// <summary>
    /// A notice that stays until it's taken away (Dismiss) or clicked, with one
    /// action written on it; a click anywhere on it does that action.
    /// </summary>
    public static Toast ShowLasting(string title, string message, AppSettings s, string action, Action onAction)
    {
        while (Open.Count >= 4) Open[0].Dismiss();
        var t = new Toast(title, message, s.ToastPosition, 0,
                          Math.Clamp(LookStore.Current.ToastSizePercent, 75, 150) / 100.0, action);
        t._onClick = onAction;
        Open.Add(t);
        t.Show();
        Place(animate: false, only: t);
        return t;
    }

    /// <param name="seconds">How long it stays; 0: until it's dismissed.</param>
    /// <param name="action">A word or two shown as a button on it (the whole card does it).</param>
    private Toast(string title, string message, ToastPosition pos, double seconds, double scale, string? action = null)
    {
        _pos = pos;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth * scale + 24;  // room for the shadow
        Opacity = 0;

        // Icon, title and message.
        var icon = new Image { Source = AppIcon(), Width = 22, Height = 22, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var titleText = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var body = new TextBlock { Text = message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var text = new StackPanel();
        text.Children.Add(titleText);
        if (!string.IsNullOrWhiteSpace(message)) text.Children.Add(body);
        if (action != null)
        {
            var button = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0),
                Padding = new Thickness(12, 4, 12, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = action, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
            };
            button.SetResourceReference(Border.BorderBrushProperty, "Brass");
            ((TextBlock)button.Child).SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            text.Children.Add(button);
        }

        var row = new DockPanel { Margin = new Thickness(16, 13, 16, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        // A thin line that shrinks as the time runs out.
        var progress = new Border { Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = CardWidth, CornerRadius = new CornerRadius(1) };
        progress.SetResourceReference(Border.BackgroundProperty, "Brass");
        progress.Opacity = 0.55;

        // An accent stripe down the left edge.
        var stripe = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12), CornerRadius = new CornerRadius(0, 2, 2, 0) };
        stripe.SetResourceReference(Border.BackgroundProperty, "Brass");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(row);
        grid.Children.Add(stripe);
        Grid.SetRow(progress, 1);
        grid.Children.Add(progress);

        _card = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = grid,
            Margin = new Thickness(12),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            RenderTransform = _slide,
            // The chosen size: everything in the card scales together.
            LayoutTransform = new ScaleTransform(scale, scale),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Bezel");
        _card.SetResourceReference(Border.BorderBrushProperty, "Line");
        // A click takes you to the clip (when there is one), and dismisses.
        _card.MouseLeftButtonUp += (_, _) =>
        {
            var act = _onClick;
            Dismiss();
            try { act?.Invoke(); } catch (Exception ex) { Services.Log.Error("notification click", ex); }
        };
        Content = _card;

        SourceInitialized += (_, _) => NoFocus();
        Loaded += (_, _) =>
        {
            // Glide in from the nearest edge while fading up.
            bool top = _pos is ToastPosition.TopLeft or ToastPosition.TopCenter or ToastPosition.TopRight;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top ? -14 : 14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            if (seconds > 0) progress.BeginAnimation(WidthProperty, new DoubleAnimation(CardWidth, 0, TimeSpan.FromSeconds(seconds)));
        };
        if (seconds <= 0) progress.Visibility = Visibility.Collapsed;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(seconds, 1)) };
        _timer.Tick += (_, _) => Dismiss();
        if (seconds > 0) _timer.Start();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        Open.Remove(this);

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        _slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, _pos is ToastPosition.BottomLeft or ToastPosition.TopLeft ? -10 : 10, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
        Place(animate: true, only: null);
    }

    /// <summary>Places the open notifications, newest nearest the edge.</summary>
    private static void Place(bool animate, Toast? only)
    {
        var area = SystemParameters.WorkArea;
        double offset = 0;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            t.UpdateLayout();
            double h = t.ActualHeight > 0 ? t.ActualHeight : 96;
            double w = t.Width;

            double left = t._pos switch
            {
                ToastPosition.BottomLeft or ToastPosition.TopLeft => area.Left + Edge - 12,
                ToastPosition.BottomCenter or ToastPosition.TopCenter => area.Left + (area.Width - w) / 2,
                _ => area.Right - w - Edge + 12,
            };
            bool top = t._pos is ToastPosition.TopLeft or ToastPosition.TopCenter or ToastPosition.TopRight;
            double y = top ? area.Top + Edge - 12 + offset : area.Bottom - h - Edge + 12 - offset;
            offset += h - 24 + Gap;

            t.Left = left;
            if (animate && t != only && Math.Abs(t.Top - y) > 0.5)
                t.BeginAnimation(TopProperty, new DoubleAnimation(t.Top, y, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            t.Top = y;
        }
    }

    /// <summary>The logo, in the look's colours.</summary>
    private static ImageSource? AppIcon() => LogoArt.Image;

    /// <summary>Never takes focus, and stays out of Alt+Tab.</summary>
    private void NoFocus()
    {
        var h = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        SetWindowLongPtr(h, -20, new IntPtr(ex | 0x08000000 | 0x80));   // no-activate, tool window
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}

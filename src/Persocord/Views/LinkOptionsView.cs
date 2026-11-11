using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Persocord.Models;
using Persocord.Services;

namespace Persocord.Views;

/// <summary>
/// Picks how a link should behave: duration, kill-link, and which hosts are
/// allowed. Always shows which host will actually be used, and greys out
/// anything the current choices or the file's size rule out. The per-host
/// detail stays folded away until asked for.
/// </summary>
public class LinkOptionsView : StackPanel
{
    private readonly LinkChoice _choice;
    private readonly long? _size;
    private readonly bool _isGif;

    /// <summary>What will actually be uploaded, after any shrinking.</summary>
    private long? Eff => LinkCatalog.EffectiveSize(_choice, _size, _isGif);

    /// <summary>The big-clip choice applies: always when setting defaults.</summary>
    private bool ShowBig => !_isGif && (_size == null || _size.Value > LinkCatalog.DiscordInlineLimit);

    private readonly StackPanel _big = new() { Margin = new Thickness(0, 0, 0, 14) };
    private readonly WrapPanel _durations = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _kill = new() { Margin = new Thickness(0, 12, 0, 0),
        Content = "I want to be able to kill the link early" };
    private readonly Border _verdict = new() { Margin = new Thickness(0, 12, 0, 0),
        Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _verdictText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _moreToggle = new() { Margin = new Thickness(0, 10, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3) };
    private readonly StackPanel _details = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    public event Action? Changed;

    /// <summary>The choices as they stand.</summary>
    public LinkChoice Choice => _choice.Copy();

    /// <summary>The host the current choices land on, or null if none can.</summary>
    public ServiceInfo? Service => LinkCatalog.Pick(_choice, Eff);

    /// <param name="size">The clip's size, or null when setting defaults.</param>
    public LinkOptionsView(LinkChoice start, long? size, bool isGif = false)
    {
        _choice = start.Copy();
        _size = size;
        _isGif = isGif;
        LinkChoice.Tidy(_choice);

        Children.Add(_big);

        Children.Add(new TextBlock { Text = "How long should the link work?", FontWeight = FontWeights.Medium });
        Children.Add(_durations);

        // No host still offered can take a link down early (only Oshi could).
        _kill.Visibility = LinkCatalog.Services.Any(s => s.CanKill) ? Visibility.Visible : Visibility.Collapsed;
        _kill.IsChecked = _choice.NeedKill;
        _kill.Checked += (_, _) => { _choice.NeedKill = true; Refresh(); };
        _kill.Unchecked += (_, _) => { _choice.NeedKill = false; Refresh(); };
        Children.Add(_kill);

        _verdict.Child = _verdictText;
        Children.Add(_verdict);

        _moreToggle.Click += (_, _) =>
        {
            _details.Visibility = _details.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
        };
        Children.Add(_moreToggle);
        Children.Add(_details);

        Refresh();
    }

    private static Brush Res(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static Brush Brass => Res("Brass", Color.FromRgb(0xD6, 0xB0, 0x6A));
    private static Brush Brick => Res("Brick", Color.FromRgb(0xE0, 0x59, 0x4D));
    private static Brush Muted => Res("Muted", Color.FromRgb(0x8A, 0x90, 0x9A));
    private static Brush Line => Res("Line", Color.FromRgb(0x2E, 0x33, 0x3B));
    private static Brush Ink => Res("Ink", Color.FromRgb(0xE8, 0xEA, 0xED));
    private static Brush Card => Res("Panel", Color.FromRgb(0x19, 0x1C, 0x20));

    private void Refresh()
    {
        BuildBig();
        BuildDurations();
        BuildVerdict();
        BuildDetails();
        _moreToggle.Content = _details.Visibility == Visibility.Visible
            ? "▾  Hide hosts and details" : "▸  Show hosts and details";
        Changed?.Invoke();
    }

    /* ---- clips too big for Discord to play inline ----------------------- */

    private void BuildBig()
    {
        _big.Children.Clear();
        _big.Visibility = ShowBig ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowBig) return;

        _big.Children.Add(new TextBlock
        {
            Text = _size.HasValue
                ? $"This clip is {LinkCatalog.SizeText(_size.Value)}. Discord only plays linked videos under about 80 MB inline."
                : "For clips over about 80 MB, the most Discord plays inline from a link:",
            FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap,
        });

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(BigChip(BigClipMode.Shrink, "Shrink to play inline",
            "Re-encoded to about 75 MB. Private, plays in the chat, a little softer."));
        row.Children.Add(BigChip(BigClipMode.FullViaX266, "Full size via x266.mov",
            "Plays in the chat at full quality through x266.mov, which logs links for 30 days."));
        row.Children.Add(BigChip(BigClipMode.FullPlain, "Full size, plain link",
            "Full quality, but shows as a link to click rather than a player."));
        _big.Children.Add(row);
    }

    private Border BigChip(BigClipMode mode, string title, string detail)
    {
        bool selected = _choice.BigClip == mode;
        var chip = new Border
        {
            Width = 196,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = selected ? Brass : Line,
            Background = selected ? new SolidColorBrush(Color.FromArgb(0x33, 0xD6, 0xB0, 0x6A)) : Card,
            Cursor = Cursors.Hand,
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Foreground = Ink,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal });
        text.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        chip.Child = text;
        chip.MouseLeftButtonUp += (_, _) => { _choice.BigClip = mode; Refresh(); };
        return chip;
    }

    private string BigNote()
    {
        if (!ShowBig || !_size.HasValue) return "";
        return _choice.BigClip switch
        {
            BigClipMode.Shrink => "  ·  shrinks to ~75 MB first",
            BigClipMode.FullViaX266 => "  ·  played through x266.mov",
            _ => "  ·  won't play inline",
        };
    }

    /* ---- durations ------------------------------------------------------ */

    private void BuildDurations()
    {
        _durations.Children.Clear();
        foreach (var d in LinkCatalog.Durations)
        {
            var host = LinkCatalog.Pick(_choice, Eff, d.Key);
            bool usable = host != null;
            bool selected = d.Key == _choice.Duration;
            long max = LinkCatalog.MaxFor(_choice, d.Key);

            var chip = new Border
            {
                Width = 96,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = selected ? Brass : Line,
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x33, 0xD6, 0xB0, 0x6A)) : Card,
                Opacity = usable ? 1 : 0.35,
                Cursor = usable ? Cursors.Hand : Cursors.No,
                ToolTip = usable
                    ? $"Uploads to {host!.Name}"
                    : "Not possible with these choices" + (_size.HasValue ? " for a file this size" : ""),
            };

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Label, Foreground = Ink, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal });
            text.Children.Add(new TextBlock
            {
                Text = max > 0 ? $"up to {LinkCatalog.SizeText(max)}" : "unavailable",
                Foreground = Muted, FontSize = 11,
            });
            chip.Child = text;

            if (usable)
            {
                var key = d.Key;
                chip.MouseLeftButtonUp += (_, _) => { _choice.Duration = key; Refresh(); };
            }
            _durations.Children.Add(chip);
        }
    }

    /* ---- the host that will be used ------------------------------------- */

    private void BuildVerdict()
    {
        var host = LinkCatalog.Pick(_choice, Eff);
        var dur = LinkCatalog.Duration(_choice.Duration)!;

        if (host != null)
        {
            _verdict.BorderBrush = Brass;
            _verdict.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xD6, 0xB0, 0x6A));
            _verdictText.Foreground = Ink;
            _verdictText.Text =
                $"Uploads to {host.Name}  ·  " +
                (dur.Length.HasValue ? $"gone after {dur.Label.ToLowerInvariant()}" : "stays up while opened at least once every 2 years") + "  ·  " +
                (host.CanKill ? "can be killed early" : "can't be killed early") + "  ·  " +
                $"up to {LinkCatalog.SizeText(host.MaxBytes)}" + BigNote();

            // Uploads fall through to the next host if one fails; say so.
            var backups = LinkCatalog.Candidates(_choice, Eff).Skip(1).Select(h => h.Name).ToList();
            if (backups.Count > 0)
                _verdictText.Text += $"\nIf it fails: {string.Join(", then ", backups)}.";
            if (host.Id == LinkService.Catbox)
                _verdictText.Text += "\n⚠ Catbox is very slow: expect a long upload, and Discord may give up and show a plain link instead of a player.";
            if (LinkCatalog.Failures.TryGetValue(host.Id, out var why))
                _verdictText.Text += $"\n{host.Name} failed earlier: {why}.";
        }
        else
        {
            _verdict.BorderBrush = Brick;
            _verdict.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xE0, 0x59, 0x4D));
            _verdictText.Foreground = Ink;
            _verdictText.Text = "No selected host can do this. " + WhyNot(dur);
        }
    }

    private string WhyNot(LinkDuration dur)
    {
        if (_choice.Services.Count == 0) return "Tick at least one host under the details.";
        var eff = Eff;
        if (eff.HasValue && LinkCatalog.Services.All(s => eff.Value > s.MaxBytes))
            return "This clip is bigger than any host takes; try Compress Size first.";
        return $"Pick a different duration than {dur.Label.ToLowerInvariant()}" +
               (_choice.NeedKill ? ", or untick the kill-link option." : ".");
    }

    /* ---- per-host details ----------------------------------------------- */

    private void BuildDetails()
    {
        _details.Children.Clear();
        if (_details.Visibility != Visibility.Visible) return;

        _details.Children.Add(new TextBlock
        {
            Text = "Hosts Persocord may use (tick the ones you're happy with)",
            Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6),
        });

        var chosen = LinkCatalog.Pick(_choice, Eff);
        foreach (var s in LinkCatalog.Services)
        {
            bool ticked = _choice.Services.Contains(s.Id);
            var blocker = LinkCatalog.Blocker(s, _choice, Eff, _choice.Duration);
            bool isChosen = chosen?.Id == s.Id;

            var box = new CheckBox
            {
                IsChecked = ticked,
                Content = s.Name + (isChosen ? "   ← will be used" : ""),
                FontWeight = isChosen ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var id = s.Id;
            box.Checked += (_, _) => { if (!_choice.Services.Contains(id)) _choice.Services.Add(id); Refresh(); };
            box.Unchecked += (_, _) => { _choice.Services.Remove(id); Refresh(); };

            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = isChosen ? Brass : Line,
                // Greyed when the current choices rule it out, but still
                // tickable, since ticking is about which hosts you trust.
                Opacity = ticked && blocker != null ? 0.5 : 1,
            };

            var inner = new StackPanel();
            inner.Children.Add(box);
            inner.Children.Add(new TextBlock
            {
                Text = $"Up to {LinkCatalog.SizeText(s.MaxBytes)}  ·  {LinkCatalog.DurationsText(s)}  ·  " +
                       (s.CanKill ? "kill link available" : "no kill link"),
                Foreground = Muted, FontSize = 11.5, Margin = new Thickness(22, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            inner.Children.Add(new TextBlock
            {
                Text = s.Blurb, Foreground = Muted, FontSize = 11.5,
                Margin = new Thickness(22, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            if (LinkCatalog.Failures.TryGetValue(s.Id, out var failed))
                inner.Children.Add(new TextBlock
                {
                    Text = "Failed earlier this session: " + failed,
                    Foreground = Brick, FontSize = 11.5, Margin = new Thickness(22, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            if (ticked && blocker != null && blocker != "not selected")
                inner.Children.Add(new TextBlock
                {
                    Text = "Not usable right now: " + blocker,
                    Foreground = Brick, FontSize = 11.5, Margin = new Thickness(22, 2, 0, 0),
                });

            card.Child = inner;
            _details.Children.Add(card);
        }
    }
}

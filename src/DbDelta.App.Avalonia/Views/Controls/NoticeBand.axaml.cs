using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// Reusable full-width notification strip: accent fill + rule, glyph, wrapping
/// message and an optional trailing action. Used by the shell's error banner
/// and by the stale-results banner. See CLAUDE.md UI rule #3 (DRY) for why the
/// markup lives here rather than being copied per band.
/// </summary>
public partial class NoticeBand : UserControl
{
    /// <summary>Border and glyph colour — Danger for errors, Warning for advisories.</summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<NoticeBand, IBrush?>(nameof(Accent));

    /// <summary>The soft fill behind the strip, paired with <see cref="Accent"/>.</summary>
    public static readonly StyledProperty<IBrush?> AccentSoftProperty =
        AvaloniaProperty.Register<NoticeBand, IBrush?>(nameof(AccentSoft));

    /// <summary>Message colour — the readable-on-soft-fill member of the ramp.</summary>
    public static readonly StyledProperty<IBrush?> AccentForegroundProperty =
        AvaloniaProperty.Register<NoticeBand, IBrush?>(nameof(AccentForeground));

    /// <summary>The glyph geometry drawn at the leading edge.</summary>
    public static readonly StyledProperty<Geometry?> IconPathProperty =
        AvaloniaProperty.Register<NoticeBand, Geometry?>(nameof(IconPath));

    /// <summary>The text of the notice.</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<NoticeBand, string?>(nameof(Message));

    /// <summary>Optional trailing content, typically a single action button.</summary>
    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<NoticeBand, object?>(nameof(Trailing));

    public NoticeBand()
    {
        InitializeComponent();
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? AccentSoft
    {
        get => GetValue(AccentSoftProperty);
        set => SetValue(AccentSoftProperty, value);
    }

    public IBrush? AccentForeground
    {
        get => GetValue(AccentForegroundProperty);
        set => SetValue(AccentForegroundProperty, value);
    }

    public Geometry? IconPath
    {
        get => GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }
}

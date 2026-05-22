using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// Reusable button content: shows an icon + idle label by default, and a
/// slim indeterminate ProgressBar.spinner + busy label when <see cref="IsBusy"/>
/// is true. Drop inside any Button to get the canonical DbDelta "loading"
/// state without copy-pasting the spinner markup. See CLAUDE.md UI rule #3
/// (DRY) for why this lives as a single component.
/// </summary>
public partial class LoadingContent : UserControl
{
    /// <summary>The icon geometry shown at rest. Stroke colour inherits from
    /// the owning Button's <c>Foreground</c> so it follows the same hover
    /// contrast flip rule (CLAUDE.md UI rule #1).</summary>
    public static readonly StyledProperty<Geometry?> IconPathProperty =
        AvaloniaProperty.Register<LoadingContent, Geometry?>(nameof(IconPath));

    /// <summary>Label shown next to the icon at rest (e.g. "Scansiona").</summary>
    public static readonly StyledProperty<string> IdleTextProperty =
        AvaloniaProperty.Register<LoadingContent, string>(nameof(IdleText), defaultValue: string.Empty);

    /// <summary>Label shown next to the spinner while busy. Default
    /// "In corso…" — change only for surfaces where another verb is clearer.</summary>
    public static readonly StyledProperty<string> BusyTextProperty =
        AvaloniaProperty.Register<LoadingContent, string>(nameof(BusyText), defaultValue: "In corso…");

    /// <summary>Drives the idle/busy switch.</summary>
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<LoadingContent, bool>(nameof(IsBusy));

    public LoadingContent()
    {
        InitializeComponent();
    }

    public Geometry? IconPath
    {
        get => GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public string IdleText
    {
        get => GetValue(IdleTextProperty);
        set => SetValue(IdleTextProperty, value);
    }

    public string BusyText
    {
        get => GetValue(BusyTextProperty);
        set => SetValue(BusyTextProperty, value);
    }

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
}

using Avalonia;
using Avalonia.Controls;

namespace DbDelta.App.Views.Controls;

/// <summary>
/// One "Ultima modifica" cell for the results grid. Renders the timestamp and,
/// when this side is the more recently changed of the two, marks it with an
/// arrow and bold weight. Used by both the source and the target column — see
/// CLAUDE.md UI rule #3 (DRY).
/// </summary>
public partial class LastModifiedCell : UserControl
{
    /// <summary>Preformatted timestamp; empty for kinds with no modify_date.</summary>
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<LastModifiedCell, string>(nameof(Text), defaultValue: string.Empty);

    /// <summary>True when this side carries the more recent change.</summary>
    public static readonly StyledProperty<bool> IsNewerProperty =
        AvaloniaProperty.Register<LastModifiedCell, bool>(nameof(IsNewer));

    /// <summary>Tooltip explaining the marker, including the two-clocks caveat.</summary>
    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<LastModifiedCell, string?>(nameof(Tip));

    public LastModifiedCell()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsNewer
    {
        get => GetValue(IsNewerProperty);
        set => SetValue(IsNewerProperty, value);
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }
}

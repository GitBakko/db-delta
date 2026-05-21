using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DbDelta.App.Views;

/// <summary>
/// Static value converters used by the result grid. Centralised here so views
/// can reference them via <c>{x:Static v:Converters.StatusToStripBrush}</c>.
/// </summary>
public static class Converters
{
    /// <summary>
    /// Maps a <c>DifferenceDto.Status</c> string ("OnlyInA" / "OnlyInB" /
    /// "Different" / "Identical") to the matching diff-strip brush from
    /// <c>Styles/Tokens.axaml</c>.
    /// </summary>
    public static readonly IValueConverter StatusToStripBrush = new FuncValueConverter<string?, IBrush?>(static status =>
    {
        string key = status switch
        {
            "Different" => "DiffModifiedBrush",
            "OnlyInA" => "DiffOnlySourceBrush",
            "OnlyInB" => "DiffOnlyTargetBrush",
            _ => "DiffIdenticalBrush",
        };
        return Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
               && value is IBrush brush
            ? brush
            : Brushes.Transparent;
    });

    /// <summary>
    /// Returns true when the bound <c>Status</c> equals the converter parameter.
    /// Used to drive style-class toggles on the status badge.
    /// </summary>
    public static readonly IValueConverter IsStatus = new FuncValueConverter<string?, string?, bool>(static (status, target) =>
        string.Equals(status, target, System.StringComparison.Ordinal));

    /// <summary>
    /// Maps the raw <c>DifferenceStatus</c> name (e.g. <c>"OnlyInA"</c>) to a
    /// human-friendly Italian label shown in the badge.
    /// </summary>
    public static readonly IValueConverter StatusToDisplayName = new FuncValueConverter<string?, string?>(static status => status switch
    {
        "OnlyInA" => "Solo in origine",
        "OnlyInB" => "Solo in destinazione",
        "Different" => "Modificato",
        "Identical" => "Identico",
        _ => status,
    });

    /// <summary>Masks <c>password=</c> / <c>pwd=</c> in any connection-string preview.</summary>
    public static readonly IValueConverter RedactConnectionString = new FuncValueConverter<string?, string?>(static value =>
        value is null ? null : DbDelta.Persistence.Util.ConnectionStringRedactor.Redact(value));
}

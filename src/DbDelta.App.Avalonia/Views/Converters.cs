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
        string.Equals(status, target, StringComparison.Ordinal));
}

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbDelta.Core.Diff;

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
        value is null ? null : Persistence.Util.ConnectionStringRedactor.Redact(value));

    /// <summary>
    /// Converts a hex colour string (e.g. <c>"#0054BD"</c>) to a fresh
    /// <see cref="SolidColorBrush"/>. Returns <c>null</c> for null / empty
    /// / unparseable input — the bound control will fall back to its
    /// default Background.
    /// </summary>
    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush?>(static hex =>
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return null;
        }
    });

    /// <summary>
    /// Returns <see cref="FontWeight.Bold"/> when <c>true</c>, otherwise
    /// <see cref="FontWeight.Normal"/>. Used to bold source-only / target-only
    /// object names in the results grid.
    /// </summary>
    public static readonly IValueConverter BoolToFontWeight = new FuncValueConverter<bool, FontWeight>(
        static bold => bold ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>
    /// Maps a SQL Server major version number to a version-branded stroke colour
    /// for the DB-cylinder icon. Returns the default cobalt brush when the version
    /// is null or unrecognised.
    /// </summary>
    public static readonly IValueConverter MajorVersionToBrush = new FuncValueConverter<int?, IBrush?>(static major =>
    {
        string hex = major switch
        {
            16 => "#DD2F44",
            15 => "#B81E5C",
            14 => "#2E84CB",
            13 => "#1B6CA1",
            12 => "#2E7A2E",
            _ => "#5C6BC0",
        };
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return new SolidColorBrush(Color.Parse("#5C6BC0"));
        }
    });

    /// <summary>
    /// Maps a <see cref="LineStatus"/> to the background brush for the SOURCE pane row.
    /// Added → transparent; Removed → #FFCED3 (crimson soft); Modified → #CFE2FF (primary soft).
    /// </summary>
    public static readonly IValueConverter LineStatusToSourceBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Removed => new SolidColorBrush(Color.Parse("#FFCED3")),
            LineStatus.Modified => new SolidColorBrush(Color.Parse("#CFE2FF")),
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Added => Brushes.Transparent,
            _ => Brushes.Transparent,
        });

    /// <summary>
    /// Maps a <see cref="LineStatus"/> to the background brush for the TARGET pane row.
    /// Added → #CDFFD6 (emerald soft); Removed → transparent; Modified → #CFE2FF (primary soft).
    /// </summary>
    public static readonly IValueConverter LineStatusToTargetBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Added => new SolidColorBrush(Color.Parse("#CDFFD6")),
            LineStatus.Modified => new SolidColorBrush(Color.Parse("#CFE2FF")),
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Removed => Brushes.Transparent,
            _ => Brushes.Transparent,
        });
}

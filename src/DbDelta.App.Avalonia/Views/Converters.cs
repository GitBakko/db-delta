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

    /// <summary>True when the bound integer is greater than zero. Used to
    /// drive <c>IsVisible</c> on per-type badges so empty buckets disappear.</summary>
    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(static value => value > 0);

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
    /// Maps an italian status group key ("Diversi", "Solo destinazione",
    /// "Solo provenienza", "Identici") to the matching status colour brush
    /// used on the left stripe of grouped row headers.
    /// Anything else falls through to the neutral grey identical brush.
    /// </summary>
    public static readonly IValueConverter StatusKeyToBrush = new FuncValueConverter<object?, IBrush?>(static key =>
    {
        string hex = (key as string) switch
        {
            "Diversi" => "#0064C8",            // cyan (matches DifferenceRow Different)
            "Solo destinazione" => "#B31220",  // crimson
            "Solo provenienza" => "#007339",   // emerald
            "Identici" => "#9097A0",           // neutral grey
            _ => "#9097A0",
        };
        try
        {
            return new SolidColorBrush(Color.Parse(hex));
        }
        catch
        {
            return Brushes.Transparent;
        }
    });

    /// <summary>
    /// Maps a <see cref="LineStatus"/> to the background brush for the SOURCE pane row.
    /// Source-side semantics (user spec): "additions" on the source pane are
    /// painted GREEN. Both <see cref="LineStatus.Removed"/> (line present only
    /// in source) and <see cref="LineStatus.Modified"/> (line differs between
    /// sides) qualify — a substitution renders green-on-source and red-on-target
    /// on the same row.
    /// </summary>
    public static readonly IValueConverter LineStatusToSourceBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Removed => new SolidColorBrush(Color.Parse("#CDFFD6")),  // emerald soft
            LineStatus.Modified => new SolidColorBrush(Color.Parse("#CDFFD6")), // emerald soft
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Added => Brushes.Transparent,
            _ => Brushes.Transparent,
        });

    /// <summary>
    /// Maps a <see cref="LineStatus"/> to the background brush for the TARGET pane row.
    /// Target-side semantics: "deletions" on the target pane (lines that
    /// shouldn't be there) are painted RED. Both <see cref="LineStatus.Added"/>
    /// (line present only in target) and <see cref="LineStatus.Modified"/>
    /// qualify.
    /// </summary>
    public static readonly IValueConverter LineStatusToTargetBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Added => new SolidColorBrush(Color.Parse("#FFCED3")),    // crimson soft
            LineStatus.Modified => new SolidColorBrush(Color.Parse("#FFCED3")), // crimson soft
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Removed => Brushes.Transparent,
            _ => Brushes.Transparent,
        });

    /// <summary>
    /// Returns the centre-column action icon kind for a given line status:
    /// "arrow" for inserts/modifications coming FROM source TO target,
    /// "x" for target-only lines that should be removed, "" otherwise.
    /// Bound by the diff viewer's slim centre column.
    /// </summary>
    public static readonly IValueConverter LineStatusToCenterIcon =
        new FuncValueConverter<LineStatus, string?>(static status => status switch
        {
            LineStatus.Removed => "arrow",
            LineStatus.Modified => "arrow",
            LineStatus.Added => "x",
            LineStatus.Unchanged => null,
            _ => null,
        });

    /// <summary>
    /// True when the bound <c>string</c> equals the converter parameter.
    /// Used to switch icon rendering in the diff viewer centre column.
    /// </summary>
    public static readonly IValueConverter StringEquals =
        new FuncValueConverter<string?, string?, bool>(static (value, parameter) =>
            string.Equals(value, parameter, StringComparison.Ordinal));
}

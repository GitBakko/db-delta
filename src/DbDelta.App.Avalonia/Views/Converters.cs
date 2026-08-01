using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;

namespace DbDelta.App.Views;

/// <summary>
/// Tri-state for a grid group's "select all" box: <see langword="true"/> when
/// every selectable row in the group is ticked, <see langword="false"/> when
/// none is, <see langword="null"/> when only some are.
/// </summary>
/// <remarks>
/// A group header's data context is the <see cref="DataGridCollectionViewGroup"/>,
/// which raises nothing when a row inside it is ticked — so binding to the group
/// alone would paint the box once and then let it go stale. The second value is
/// the view-model's selected-row count, which changes on every tick: it is not
/// read, it exists to make the binding re-evaluate. Removing it silently freezes
/// every group header at whatever it showed when the grid was built.
/// </remarks>
public sealed class GroupSelectionStateConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values[0] is not DataGridCollectionViewGroup group)
        {
            return false;
        }

        int total = 0;
        int selected = 0;
        foreach (object? item in group.Items)
        {
            if (item is not DifferenceRowViewModel row || !row.IsSelectable) { continue; }
            total++;
            if (row.IsSelected) { selected++; }
        }
        return total == 0 || selected == 0
            ? false
            : selected == total ? true : (bool?)null;
    }
}

/// <summary>
/// Static value converters used by the result grid. Centralised here so views
/// can reference them via <c>{x:Static v:Converters.StatusToStripBrush}</c>.
/// </summary>
public static class Converters
{
    /// <summary>Shared instance of <see cref="GroupSelectionStateConverter"/>.</summary>
    public static readonly IMultiValueConverter GroupSelectionState = new GroupSelectionStateConverter();

    /// <summary>
    /// True when a grid group holds at least one row that can be ticked. Drives
    /// the group's select-all box away from the "Identici" group, whose rows
    /// carry no DDL and are not selectable — a box that can never do anything
    /// is worse than no box, because it invites a click that does nothing.
    /// </summary>
    public static readonly IValueConverter GroupHasSelectableRows =
        new FuncValueConverter<object?, bool>(static group =>
            group is DataGridCollectionViewGroup g
            && g.Items.OfType<DifferenceRowViewModel>().Any(r => r.IsSelectable));

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

    /// <summary>Extracts the filename (without extension) from a full path.
    /// Used by the project MRU combo so the visible label is concise while
    /// the tooltip shows the full path.</summary>
    public static readonly IValueConverter PathToFileName =
        new FuncValueConverter<string?, string?>(static path =>
            string.IsNullOrWhiteSpace(path) ? path : Path.GetFileNameWithoutExtension(path));

    /// <summary>Formats a UTC timestamp as Italian local-time
    /// <c>dd/MM/yyyy HH:mm</c>. Used by the MRU list and the load dialog
    /// to show "salvato il …" timestamps.</summary>
    public static readonly IValueConverter DateToItalianLocal =
        new FuncValueConverter<DateTime, string?>(static utc =>
            utc.ToLocalTime().ToString(
                "dd/MM/yyyy HH:mm",
                CultureInfo.GetCultureInfo("it-IT")));

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
    /// Paints a server message by what the server called it: crimson for an
    /// error, ordinary body colour for a PRINT. Resolved from the app resources
    /// so it follows the theme.
    /// </summary>
    public static readonly IValueConverter MessageSeverityBrush =
        new FuncValueConverter<bool, IBrush?>(static isError => Resource(isError ? "DangerBrush" : "FgBrush"));

    /// <summary>
    /// Paints the last-run pill by its verdict: emerald when the run succeeded,
    /// crimson when it did not. A failed run that reads like every other pill in
    /// the strip is one nobody notices.
    /// </summary>
    public static readonly IValueConverter RunOutcomeBrush =
        new FuncValueConverter<bool, IBrush?>(static ok => Resource(ok ? "SuccessBrush" : "DangerBrush"));

    private static IBrush? Resource(string key) =>
        Application.Current?.TryFindResource(key, out object? found) == true ? found as IBrush : null;

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
    /// Resolves a theme-aware brush from the application resources by key.
    /// Returns Transparent when the key isn't found — used by the diff-line
    /// and row-status converters below so a missing token never crashes.
    /// </summary>
    private static IBrush ResolveBrush(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
        && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    /// <summary>
    /// Maps a <see cref="LineStatus"/> to the background brush for the SOURCE
    /// pane row. Theme-aware (round-15): looks up <c>DiffSourceLineBrush</c>
    /// which resolves to a soft emerald tint in light mode and a deep
    /// emerald shade in dark mode (so the white code text stays legible).
    /// </summary>
    public static readonly IValueConverter LineStatusToSourceBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Removed => ResolveBrush("DiffSourceLineBrush"),
            LineStatus.Modified => ResolveBrush("DiffSourceLineBrush"),
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Added => Brushes.Transparent,
            _ => Brushes.Transparent,
        });

    /// <summary>
    /// Mirror of <see cref="LineStatusToSourceBackground"/> for the TARGET
    /// pane. Uses <c>DiffTargetLineBrush</c> — soft crimson in light mode,
    /// deep crimson in dark.
    /// </summary>
    public static readonly IValueConverter LineStatusToTargetBackground =
        new FuncValueConverter<LineStatus, IBrush?>(static status => status switch
        {
            LineStatus.Added => ResolveBrush("DiffTargetLineBrush"),
            LineStatus.Modified => ResolveBrush("DiffTargetLineBrush"),
            LineStatus.Unchanged => Brushes.Transparent,
            LineStatus.Removed => Brushes.Transparent,
            _ => Brushes.Transparent,
        });

    /// <summary>
    /// Maps a <c>DifferenceRowViewModel.Status</c> string to the
    /// theme-aware row-hover background brush. Used by the DataGrid
    /// :pointerover style in <c>ResultsGridView</c>. The brushes themselves
    /// are defined in <c>Themes.axaml</c> and re-resolve on theme change.
    /// </summary>
    public static readonly IValueConverter StatusToRowHoverBrush =
        new FuncValueConverter<string?, IBrush?>(static status => ResolveBrush(status switch
        {
            "Different" => "RowModifiedHoverBrush",
            "OnlyInB" => "RowOnlyDestHoverBrush",
            "OnlyInA" => "RowOnlyProvHoverBrush",
            _ => "RowIdenticalHoverBrush",
        }));

    /// <summary>
    /// Mirror of <see cref="StatusToRowHoverBrush"/> for the :selected state.
    /// Slightly more saturated tint than hover so selection stands out.
    /// </summary>
    public static readonly IValueConverter StatusToRowSelectedBrush =
        new FuncValueConverter<string?, IBrush?>(static status => ResolveBrush(status switch
        {
            "Different" => "RowModifiedSelectedBrush",
            "OnlyInB" => "RowOnlyDestSelectedBrush",
            "OnlyInA" => "RowOnlyProvSelectedBrush",
            _ => "RowIdenticalSelectedBrush",
        }));

    /// <summary>
    /// Returns <c>true</c> when the given <see cref="LineStatus"/> should
    /// render the diff viewer's centre-column icon identified by the
    /// <c>ConverterParameter</c> ("arrow" or "x"). The previous converter
    /// returned a string which Avalonia's <c>IsVisible</c> binding coerces to
    /// <c>true</c> for any non-null value — that made both icons visible on
    /// every changed row. Use this bool converter instead.
    /// </summary>
    public static readonly IValueConverter LineStatusMatchesIcon =
        new FuncValueConverter<LineStatus, string?, bool>(static (status, target) =>
        {
            string? icon = status switch
            {
                LineStatus.Removed => "arrow",
                LineStatus.Modified => "arrow",
                LineStatus.Added => "x",
                LineStatus.Unchanged => null,
                _ => null,
            };
            return string.Equals(icon, target, StringComparison.Ordinal);
        });

    /// <summary>
    /// True when the bound <c>LineStatus</c> represents a source-side
    /// "addition" (Removed or Modified — these are lines visible in the source
    /// pane that need to flow to the target). Drives the per-row green
    /// indicator in the source gutter.
    /// </summary>
    public static readonly IValueConverter IsSourceIndicator =
        new FuncValueConverter<LineStatus, bool>(static status =>
            status is LineStatus.Removed or LineStatus.Modified);

    /// <summary>
    /// True when the bound <c>LineStatus</c> represents a target-side
    /// "deletion" (Added or Modified — content currently in the target pane
    /// that needs to be removed or replaced). Drives the per-row red indicator
    /// in the target gutter.
    /// </summary>
    public static readonly IValueConverter IsTargetIndicator =
        new FuncValueConverter<LineStatus, bool>(static status =>
            status is LineStatus.Added or LineStatus.Modified);
}

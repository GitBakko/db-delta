using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.Diff;
using DbDelta.Core.Reports;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Wraps a <see cref="DifferenceDto"/> for display in <c>ResultsGridView</c>.
/// Carries the env colour, the deploy-selection checkbox state, and the
/// computed brush hex used for the checkbox accent colour.
/// Also holds the underlying <see cref="DifferencePair"/> so that the deploy
/// pipeline can produce proper DDL without a secondary index lookup.
/// </summary>
public sealed partial class DifferenceRowViewModel(DifferencePair pair, DifferenceDto dto, string envColorHex) : ObservableObject
{
    /// <summary>
    /// The raw pair from the comparison engine; used by <c>DeployScriptBuilder</c>.
    /// </summary>
    public DifferencePair Pair { get; } = pair;

    public DifferenceDto Dto { get; } = dto;

    public string EnvColorHex { get; } = envColorHex;

    /// <summary>
    /// Deploy-selection flag. Identical rows are non-selectable (they have
    /// nothing to deploy) so the checkbox is hidden via
    /// <see cref="IsSelectable"/>. Default for all other rows is unchecked —
    /// the user opts in explicitly to what they want aligned.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Accent hex for the row's checkbox, driven by diff status.
    /// Modified = cyan, OnlyOnTarget = crimson, OnlyOnSource = emerald.
    /// </summary>
    public string SelectionBrushHex => Dto.Status switch
    {
        "Different" => "#0064C8", // cyan
        "OnlyInB" => "#B31220", // crimson
        "OnlyInA" => "#007339", // emerald
        _ => "#9097A0", // neutral
    };

    // Row hover/selected tints moved to theme-aware brushes in Themes.axaml
    // (round-15). The XAML side now uses StatusToRowHoverBrush /
    // StatusToRowSelectedBrush converters on Status so light + dark modes
    // both render legible row washes.

    // Convenience pass-throughs used by grid column bindings.
    public string Kind => Dto.Kind;
    public string SchemaName => Dto.SchemaName;
    public string ObjectName => Dto.ObjectName;
    public string Status => Dto.Status;

    /// <summary>
    /// Schema-qualified display name, e.g. "dbo.Orders".
    /// </summary>
    public string QualifiedName =>
        string.IsNullOrEmpty(Dto.SchemaName)
            ? Dto.ObjectName
            : $"{Dto.SchemaName}.{Dto.ObjectName}";

    public DateTime? LastModifiedSource => Dto.LastModifiedSource;
    public DateTime? LastModifiedTarget => Dto.LastModifiedTarget;

    private static readonly System.Globalization.CultureInfo s_itIt =
        System.Globalization.CultureInfo.GetCultureInfo("it-IT");

    // sys.objects.modify_date is the DB SERVER's local clock — render it
    // verbatim. A ToLocalTime() here would shift it into the CLIENT timezone
    // and show a wrong time whenever client and server zones differ.
    public string LastModifiedSourceDisplay =>
        Dto.LastModifiedSource.HasValue
            ? Dto.LastModifiedSource.Value.ToString("dd/MM/yyyy HH:mm", s_itIt)
            : string.Empty;

    public string LastModifiedTargetDisplay =>
        Dto.LastModifiedTarget.HasValue
            ? Dto.LastModifiedTarget.Value.ToString("dd/MM/yyyy HH:mm", s_itIt)
            : string.Empty;

    /// <summary>
    /// Italian display label for <see cref="Kind"/>. Plural forms chosen so the
    /// same label reads correctly in the "Tipo di oggetto" group header (e.g.
    /// "Tabelle (5)") and in the per-row "Tipo entità" column.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="KindCatalog"/> instead of carrying its own copy
    /// of the table. The copy had already fallen behind twice — TableType and
    /// then Schema were added to the catalog and never here, so those rows
    /// rendered the raw English kind and sorted last while every other
    /// user-facing artefact showed "Tipi tabella" / "Schemi" in their proper
    /// place.
    /// </remarks>
    public string KindDisplayName => KindCatalog.DisplayLabel(Kind);

    /// <summary>
    /// Deterministic sort key for the "Tipo di oggetto" column / group
    /// ordering, used by the grid's SortDescriptions so alphabetical sorting
    /// cannot re-order the categories. Same single source of truth as
    /// <see cref="KindDisplayName"/>; the requested relative order (Tabelle →
    /// Viste → Procedure → Funzioni → Trigger → rest) is the catalog's order.
    /// </summary>
    public int KindOrder => KindCatalog.SortOrder(Kind);

    /// <summary>True when the object exists only in the source database.</summary>
    public bool IsSourceOnly => Dto.Status == "OnlyInA";

    /// <summary>True when the object exists only in the target database.</summary>
    public bool IsTargetOnly => Dto.Status == "OnlyInB";

    /// <summary>True when the row represents a non-difference (object exists
    /// and is identical on both sides). Used to hide the deploy checkbox —
    /// identical rows have nothing to align.</summary>
    public bool IsIdentical => Dto.Status == "Identical";

    /// <summary>Inverse of <see cref="IsIdentical"/>. Drives the checkbox
    /// <c>IsVisible</c> binding in the results grid.</summary>
    public bool IsSelectable => !IsIdentical;

    /// <summary>True iff the "Nome (orig)" cell should display the object name.
    /// Hidden when the object exists only in the target — keeps the missing
    /// side empty so the gap is visually obvious.</summary>
    public bool HasSourceName => !IsTargetOnly;

    /// <summary>Mirror of <see cref="HasSourceName"/> for the target column.</summary>
    public bool HasTargetName => !IsSourceOnly;

    /// <summary>
    /// Deterministic sort key used to order status groups in the results grid:
    /// Diversi (0), Solo destinazione (1), Solo provenienza (2), Identici (3).
    /// </summary>
    public int StatusOrder => Dto.Status switch
    {
        "Different" => 0,
        "OnlyInB" => 1,
        "OnlyInA" => 2,
        "Identical" => 3,
        _ => 99,
    };

    /// <summary>Group label of the Identical rows in the "Tipo di differenza"
    /// grouping. Shared with the results-grid code-behind, which initialises
    /// that group COLLAPSED (identical rows are noise the user opts into).</summary>
    public const string IdenticalGroupLabel = "Identici";

    /// <summary>Italian display label for the row's status — used as the
    /// grouping key so the headers render localised text directly.</summary>
    public string StatusDisplayItalian => Dto.Status switch
    {
        "Different" => "Diversi",
        "OnlyInB" => "Solo destinazione",
        "OnlyInA" => "Solo provenienza",
        "Identical" => IdenticalGroupLabel,
        _ => Dto.Status,
    };
}

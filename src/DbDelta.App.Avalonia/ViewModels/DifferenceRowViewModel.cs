using CommunityToolkit.Mvvm.ComponentModel;
using DbDelta.Core.Diff;
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

    /// <summary>Soft tint hex for the row background on hover (subtle wash of
    /// the status colour). Picked from the design-system 100-step ramps.</summary>
    public string RowHoverHex => Dto.Status switch
    {
        "Different" => "#EAF1FB", // primary 100-ish
        "OnlyInB" => "#FBE1E5",   // crimson 100-ish
        "OnlyInA" => "#DAF3E1",   // emerald 100
        _ => "#F4F5F8",            // neutral
    };

    /// <summary>Slightly more saturated tint than <see cref="RowHoverHex"/>
    /// used when the row is selected — distinguishes selection from hover.</summary>
    public string RowSelectedHex => Dto.Status switch
    {
        "Different" => "#CFE2FF", // primary 200-ish
        "OnlyInB" => "#FFCED3",   // crimson 200
        "OnlyInA" => "#ADDFBD",   // emerald 200
        _ => "#E5E7EB",            // neutral
    };

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

    public DateTime? LastModifiedSourceUtc => Dto.LastModifiedSourceUtc;
    public DateTime? LastModifiedTargetUtc => Dto.LastModifiedTargetUtc;

    private static readonly System.Globalization.CultureInfo s_itIt =
        System.Globalization.CultureInfo.GetCultureInfo("it-IT");

    public string LastModifiedSourceDisplay =>
        Dto.LastModifiedSourceUtc.HasValue
            ? Dto.LastModifiedSourceUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", s_itIt)
            : string.Empty;

    public string LastModifiedTargetDisplay =>
        Dto.LastModifiedTargetUtc.HasValue
            ? Dto.LastModifiedTargetUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", s_itIt)
            : string.Empty;

    /// <summary>Italian display label for <see cref="Kind"/>. Plural forms
    /// chosen so the same label reads correctly in the "Tipo di oggetto"
    /// group header (e.g. "Tabelle (5)") and stays consistent across the
    /// per-row "Tipo entità" column.</summary>
    public string KindDisplayName => Kind switch
    {
        "Table" => "Tabelle",
        "View" => "Viste",
        "Procedure" => "Procedure",
        "Function" => "Funzioni",
        "Trigger" => "Trigger",
        _ => Kind,
    };

    /// <summary>
    /// Deterministic sort key for the "Tipo di oggetto" column / group
    /// ordering. User-requested fixed order:
    ///   0 Tabelle → 1 Viste → 2 Procedure → 3 Funzioni → 4 Trigger → 99 rest.
    /// Used by the grid's SortDescriptions so alphabetical sorting cannot
    /// re-order these categories.
    /// </summary>
    public int KindOrder => Kind switch
    {
        "Table" => 0,
        "View" => 1,
        "Procedure" => 2,
        "Function" => 3,
        "Trigger" => 4,
        _ => 99,
    };

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

    /// <summary>Italian display label for the row's status — used as the
    /// grouping key so the headers render localised text directly.</summary>
    public string StatusDisplayItalian => Dto.Status switch
    {
        "Different" => "Diversi",
        "OnlyInB" => "Solo destinazione",
        "OnlyInA" => "Solo provenienza",
        "Identical" => "Identici",
        _ => Dto.Status,
    };
}

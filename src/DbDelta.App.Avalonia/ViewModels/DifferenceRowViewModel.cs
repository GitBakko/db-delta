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

    [ObservableProperty]
    private bool _isSelected = true;

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

    public string LastModifiedSourceDisplay =>
        Dto.LastModifiedSourceUtc.HasValue
            ? Dto.LastModifiedSourceUtc.Value.ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

    public string LastModifiedTargetDisplay =>
        Dto.LastModifiedTargetUtc.HasValue
            ? Dto.LastModifiedTargetUtc.Value.ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

    /// <summary>Italian display label for <see cref="Kind"/>.</summary>
    public string KindDisplayName => Kind switch
    {
        "Table" => "Tabella",
        "View" => "Vista",
        "Procedure" => "Procedura",
        "Function" => "Funzione",
        "Trigger" => "Trigger",
        _ => Kind,
    };

    /// <summary>True when the object exists only in the source database.</summary>
    public bool IsSourceOnly => Dto.Status == "OnlyInA";

    /// <summary>True when the object exists only in the target database.</summary>
    public bool IsTargetOnly => Dto.Status == "OnlyInB";
}

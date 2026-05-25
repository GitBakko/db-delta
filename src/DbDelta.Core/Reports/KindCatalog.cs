using DbDelta.Core.Diff;

namespace DbDelta.Core.Reports;

/// <summary>
/// Single source of truth for how the twelve supported object Kinds present
/// themselves in user-facing artefacts (HTML report sections, JSON report
/// ordering). Mirrors the canonical sort order documented in the M0 spec and
/// the Italian display labels used throughout the Avalonia UI.
/// </summary>
public static class KindCatalog
{
    public static readonly IReadOnlyList<string> KnownKinds =
    [
        "Table",
        "View",
        "Procedure",
        "Function",
        "Trigger",
        "Sequence",
        "Synonym",
        "UserDefinedType",
        "TableType",
        "User",
        "Role",
        "Permission",
    ];

    private static readonly Dictionary<string, int> KindOrder = BuildKindOrder();

    private static readonly Dictionary<string, string> ItalianLabels = new(StringComparer.Ordinal)
    {
        ["Table"] = "Tabelle",
        ["View"] = "Viste",
        ["Procedure"] = "Procedure",
        ["Function"] = "Funzioni",
        ["Trigger"] = "Trigger",
        ["Sequence"] = "Sequenze",
        ["Synonym"] = "Sinonimi",
        ["UserDefinedType"] = "Tipi utente",
        ["TableType"] = "Tipi tabella",
        ["User"] = "Utenti",
        ["Role"] = "Ruoli",
        ["Permission"] = "Permessi",
    };

    /// <summary>Canonical sort index for a Kind. Unknown kinds sort last.</summary>
    public static int SortOrder(string kind) =>
        KindOrder.TryGetValue(kind, out int order) ? order : int.MaxValue;

    /// <summary>Italian plural label for the Kind. Falls back to the raw kind.</summary>
    public static string DisplayLabel(string kind) =>
        ItalianLabels.TryGetValue(kind, out string? label) ? label : kind;

    /// <summary>
    /// Visual sort weight for a <see cref="DifferenceStatus"/>. Differences
    /// (and the two only-on-one-side flavours) bubble to the top; identical
    /// rows sink to the bottom.
    /// </summary>
    public static int StatusOrder(DifferenceStatus status) => status switch
    {
        DifferenceStatus.Different => 0,
        DifferenceStatus.OnlyInB => 1,
        DifferenceStatus.OnlyInA => 2,
        DifferenceStatus.Identical => 3,
        _ => int.MaxValue,
    };

    private static Dictionary<string, int> BuildKindOrder()
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        for (int i = 0; i < KnownKinds.Count; i++)
        {
            map[KnownKinds[i]] = i;
        }
        return map;
    }
}

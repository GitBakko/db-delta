namespace DbDelta.Core.ObjectModel;

/// <summary>
/// One family of catalog objects a comparison did not look at, and how many of
/// them the endpoint holds.
/// </summary>
/// <param name="Key">
/// Catalog key — either a <c>sys.objects.type_desc</c> value or one of the
/// synthetic keys the census defines for things that are not rows in
/// <c>sys.objects</c> (non-rowstore indexes, extended properties, and the table
/// attributes that are read but not compared).
/// </param>
/// <param name="Count">How many the endpoint holds. Always greater than zero.</param>
public readonly record struct UnexaminedGroup(string Key, int Count);

/// <summary>
/// What a comparison did NOT examine, so the verdict can say so instead of
/// implying it looked at everything.
/// </summary>
/// <remarks>
/// <para>
/// DbDelta models thirteen object kinds. Everything outside them passes through
/// invisibly, and the failure that produces is silent in the worst direction:
/// what is never read cannot be compared, and — worse — cannot be put back by a
/// script that drops the object holding it. Non-rowstore indexes were the first
/// family to make that concrete and are now read for exactly that reason; the
/// rest of this list is still invisible.
/// </para>
/// <para>
/// This does not fix that. It makes the tool say which families it skipped, so
/// "nessuna differenza" reads as "none among the kinds I compare" rather than
/// "none". The census is per endpoint; <see cref="Merge"/> combines the two.
/// </para>
/// </remarks>
public sealed record UnexaminedCensus(IReadOnlyList<UnexaminedGroup> Groups)
{
    /// <summary>Nothing skipped — also the default for hand-built fixtures.</summary>
    public static readonly UnexaminedCensus Empty = new([]);

    /// <summary>True when the comparison covered every object the endpoint holds.</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>
    /// Combines two endpoints' censuses, keyed by family, taking the LARGER of
    /// the two counts rather than their sum: in the common case the same object
    /// exists on both sides, so adding them would report twice what is there.
    /// The larger side is the honest figure for "at least this much was skipped".
    /// </summary>
    public static UnexaminedCensus Merge(UnexaminedCensus? source, UnexaminedCensus? target)
    {
        Dictionary<string, int> byKey = new(StringComparer.Ordinal);
        foreach (UnexaminedGroup g in (source ?? Empty).Groups.Concat((target ?? Empty).Groups))
        {
            byKey[g.Key] = byKey.TryGetValue(g.Key, out int existing) ? Math.Max(existing, g.Count) : g.Count;
        }

        return byKey.Count == 0
            ? Empty
            : new([.. byKey.OrderByDescending(kv => kv.Value)
                           .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                           .Select(kv => new UnexaminedGroup(kv.Key, kv.Value))]);
    }

    /// <summary>
    /// The caveat as one sentence, or an empty string when nothing was skipped.
    /// Rendered by the app banner, the HTML report and the CLI alike.
    /// </summary>
    public string Summary => IsEmpty
        ? string.Empty
        : "Il confronto esamina 13 tipologie di oggetti: un esito «nessuna differenza» vale solo per quelle. "
          + "Non esaminati: "
          + string.Join(", ", Groups.Select(g => $"{g.Count} {LabelFor(g.Key)}"))
          + ".";

    /// <summary>
    /// Italian label for a family. Unknown keys fall back to the raw catalog
    /// value in lower case — a new SQL Server object type should show up as
    /// something readable rather than not show up at all.
    /// </summary>
    public static string LabelFor(string key) =>
        Labels.TryGetValue(key ?? string.Empty, out string? label)
            ? label
            : (key ?? string.Empty).Replace('_', ' ').ToLowerInvariant();

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        // sys.objects.type_desc values outside the thirteen modelled kinds
        ["AGGREGATE_FUNCTION"] = "aggregazioni CLR",
        ["CLR_SCALAR_FUNCTION"] = "funzioni scalari CLR",
        ["CLR_STORED_PROCEDURE"] = "procedure CLR",
        ["CLR_TABLE_VALUED_FUNCTION"] = "funzioni tabellari CLR",
        ["CLR_TRIGGER"] = "trigger CLR",
        ["EXTENDED_STORED_PROCEDURE"] = "stored procedure estese",
        ["RULE"] = "regole",
        ["SERVICE_QUEUE"] = "code di Service Broker",

        // Catalogs that are not sys.objects at all
        ["ASSEMBLY"] = "assembly CLR",
        ["PARTITION_FUNCTION"] = "funzioni di partizione",
        ["PARTITION_SCHEME"] = "schemi di partizione",
        ["XML_SCHEMA_COLLECTION"] = "raccolte di schemi XML",
        ["FULLTEXT_CATALOG"] = "cataloghi full-text",

        // Read but deliberately not modelled
        // Detected and compared by name and columns since the reader was
        // widened; what stays unexamined are their type-specific options, and
        // DbDelta never writes a CREATE for one.
        ["INDEX_NON_ROWSTORE"] = "opzioni di indici non rowstore (columnstore, XML, spaziali, hash)",
        ["INDEX_ON_VIEW"] = "indici su viste indicizzate",
        ["EXTENDED_PROPERTY"] = "proprietà estese",
        ["DDL_TRIGGER_DATABASE"] = "trigger DDL di database",
        ["TEMPORAL_TABLE"] = "tabelle temporali (versionamento non confrontato)",
        ["MEMORY_OPTIMIZED_TABLE"] = "tabelle memory-optimized (opzioni non confrontate)",
        ["MASKED_COLUMN"] = "colonne con Dynamic Data Masking",
        // Compared by columns and keys like any table type, and DbDelta refuses
        // to write one rather than emitting a disk-based copy. What stays
        // unexamined is the rest: which keys are HASH and their BUCKET_COUNT,
        // neither of which the model carries.
        ["MEMORY_OPTIMIZED_TABLE_TYPE"] =
            "tipi tabella memory-optimized (non scrivibili; hash e bucket count non confrontati)",
    };
}

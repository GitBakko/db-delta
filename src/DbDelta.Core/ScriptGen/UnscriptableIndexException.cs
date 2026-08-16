using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when script generation would have to write an index DbDelta can read
/// but not express — a columnstore, XML, spatial or hash index.
/// </summary>
/// <remarks>
/// <para>
/// The alternative to throwing is worse than an error, which is why this exists
/// at all. <c>IndexScriptEmitter</c> writes exactly one shape,
/// <c>CREATE [UNIQUE] [NON]CLUSTERED INDEX … (cols) INCLUDE … WHERE …</c>. Run
/// that over a columnstore index and it produces a perfectly valid statement
/// for a DIFFERENT index; skip it silently and a table rebuild — CREATE
/// <c>_tmp</c>, copy the rows, DROP the original — takes the index with the
/// table and nothing puts it back, under a green banner.
/// </para>
/// <para>
/// Generation runs to completion before a single batch is sent, so a throw here
/// stops the deploy with no SQL executed. Callers surface it as a refusal, not
/// as a crash: the CLI exits 30 (<c>ScriptGenerationFailure</c>) and the app
/// shows the error banner.
/// </para>
/// </remarks>
public sealed class UnscriptableIndexException(
    string schema, string table, string indexName, string? typeDesc)
    : Exception($"Refusing to script {Sql.Q(schema, table)}: DbDelta reads index "
              + $"{Sql.Q(indexName)} ({typeDesc ?? "unknown type"}) but cannot write a CREATE for it, "
              + "so the deploy would leave the target without an index it is supposed to have.")
{
    public string Schema { get; } = schema;

    public string Table { get; } = table;

    public string IndexName { get; } = indexName;

    /// <summary><see cref="TableIndex.TypeDesc"/> of the index that stopped the run.</summary>
    public string? TypeDesc { get; } = typeDesc;

    /// <summary>
    /// Throws when <paramref name="index"/> is not one of the two rowstore
    /// shapes. The single guard every emission path calls.
    /// </summary>
    public static void ThrowIfNotRowstore(string schema, string table, TableIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.IsRowstore) { return; }
        throw new UnscriptableIndexException(schema, table, index.Name, index.TypeDesc);
    }
}

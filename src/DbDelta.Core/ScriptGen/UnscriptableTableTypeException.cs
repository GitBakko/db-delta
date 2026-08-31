using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when a memory-optimized table type must be created, a shape
/// <see cref="TableTypeUdtScriptEmitter"/> does not write.
/// </summary>
/// <remarks>
/// <para>
/// This is the fourth of the same family, and it fails the way
/// <see cref="UnscriptableUserException"/> does: not by producing an invalid
/// statement, but a valid one that means something else. Without the refusal
/// the emitter writes a plain <c>CREATE TYPE … AS TABLE (…)</c> with a
/// <c>PRIMARY KEY NONCLUSTERED</c> — which runs, and leaves a <b>disk-based</b>
/// type where the source has a memory-optimized one. Verified on
/// <c>mssql/server:2022-latest</c>: replaying that rewrite next to the original
/// gives two types whose <c>sys.table_types.is_memory_optimized</c> read 1 and
/// 0. Every procedure taking it as a table-valued parameter then runs against
/// the wrong storage engine, under a green banner.
/// </para>
/// <para>
/// The shape is a whole-object refusal rather than a per-index one because the
/// discriminator is the type's own flag, not the index. Measured, not assumed:
/// a memory-optimized type may key itself on a plain range index, whose
/// <c>sys.indexes</c> row is indistinguishable from a disk-based type's, while
/// a HASH index on a disk-based type is rejected outright (Msg 1750). What the
/// emitter cannot write is not one clause but three at once — the
/// <c>WITH (MEMORY_OPTIMIZED = ON)</c>, the <c>HASH</c> keyword and the
/// <c>BUCKET_COUNT</c> — and none of the three has a place in the model.
/// </para>
/// <para>
/// <c>DROP TYPE</c> stays exempt, as it does for
/// <see cref="UnscriptableIndexException"/>: dropping a type DbDelta cannot
/// write is still the right half of a convergence, and refusing it would strand
/// a target that only has to lose the object.
/// </para>
/// <para>
/// Generation runs to completion before a single batch is sent, so a throw here
/// stops the deploy with no SQL executed. Callers surface it as a refusal, not
/// as a crash: the CLI exits 30 (<c>ScriptGenerationFailure</c>) and the app
/// shows the error banner. The way out is to leave the type out of the run and
/// deploy it by hand, which is also the only way to choose a bucket count.
/// </para>
/// </remarks>
public sealed class UnscriptableTableTypeException(TableTypeUdt tableType)
    : Exception($"Refusing to script the table type {Sql.Q(tableType.Schema, tableType.Name)}: it is "
              + "memory-optimized, and DbDelta writes no MEMORY_OPTIMIZED clause, no HASH index and "
              + "no BUCKET_COUNT, so the statement would create a disk-based type of the same name.")
{
    /// <summary>The table type that stopped the run.</summary>
    public TableTypeUdt TableType { get; } = tableType;

    public string Schema { get; } = tableType.Schema;

    public string Name { get; } = tableType.Name;

    /// <summary>
    /// Throws when the type is memory-optimized. The single guard every
    /// emission path calls — the deploy script and the diff viewer's body both
    /// reach <see cref="TableTypeUdtScriptEmitter.EmitCreate"/>.
    /// </summary>
    public static void ThrowIfMemoryOptimized(TableTypeUdt tableType)
    {
        ArgumentNullException.ThrowIfNull(tableType);
        if (!tableType.IsMemoryOptimized) { return; }
        throw new UnscriptableTableTypeException(tableType);
    }
}

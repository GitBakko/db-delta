namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A non-PK / non-UQ index on a table. PK and UQ indexes are modeled as
/// <see cref="PrimaryKey"/> / <see cref="UniqueConstraint"/> on the parent
/// <see cref="Table"/> — they are NOT duplicated here.
///
/// Named <c>TableIndex</c> rather than <c>Index</c> to avoid clashing with
/// <see cref="Index"/> (the BCL range-indexer type).
/// </summary>
/// <param name="Name">Index name.</param>
/// <param name="IsUnique">Whether it enforces uniqueness.</param>
/// <param name="IsClustered">Whether it is the clustered index.</param>
/// <param name="FilterExpression">The WHERE of a filtered index, or null.</param>
/// <param name="KeyColumns">Key columns, in order, with their direction.</param>
/// <param name="IncludedColumns">INCLUDE columns, in order.</param>
/// <param name="DataCompression">
/// <c>NONE</c> / <c>ROW</c> / <c>PAGE</c>, from
/// <c>sys.partitions.data_compression_desc</c>. Null means the source did not
/// say and is read as NONE — see <see cref="Compression"/>. Read from the FIRST
/// partition: DbDelta does not model per-partition compression, so a table
/// compressed unevenly across partitions is scripted as its first one.
/// </param>
/// <param name="TypeDesc">
/// <c>sys.indexes.type_desc</c> verbatim — <c>CLUSTERED</c>,
/// <c>NONCLUSTERED</c>, <c>NONCLUSTERED COLUMNSTORE</c>, <c>XML</c>,
/// <c>SPATIAL</c>, <c>NONCLUSTERED HASH</c>. Last in the parameter list only so
/// the existing positional constructions keep compiling. Null means a
/// hand-built model that never said, and is read as rowstore — see
/// <see cref="IsRowstore"/>.
/// </param>
public sealed record TableIndex(
    string Name,
    bool IsUnique,
    bool IsClustered,
    string? FilterExpression,
    IReadOnlyList<IndexColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? DataCompression = null,
    string? TypeDesc = null)
{
    /// <summary>
    /// True when this index is one of the two shapes <c>IndexScriptEmitter</c>
    /// can write: a rowstore CLUSTERED or NONCLUSTERED index.
    /// </summary>
    /// <remarks>
    /// The reader carries every index type now, but the emitter still speaks
    /// only <c>CREATE [UNIQUE] [NON]CLUSTERED INDEX … (cols) INCLUDE … WHERE …</c>.
    /// Writing that statement for a columnstore, XML, spatial or hash index
    /// produces valid-looking SQL for a DIFFERENT index, so every emission path
    /// asks this first and refuses rather than guessing. A false here is what
    /// stops a table rebuild from dropping an index nothing would put back.
    /// </remarks>
    public bool IsRowstore =>
        TypeDesc is null
        || TypeDesc.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase)
        || TypeDesc.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase);
}

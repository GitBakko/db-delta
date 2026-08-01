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
public sealed record TableIndex(
    string Name,
    bool IsUnique,
    bool IsClustered,
    string? FilterExpression,
    IReadOnlyList<IndexColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? DataCompression = null);

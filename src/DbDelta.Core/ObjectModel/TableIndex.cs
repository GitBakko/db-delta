namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A non-PK / non-UQ index on a table. PK and UQ indexes are modeled as
/// <see cref="PrimaryKey"/> / <see cref="UniqueConstraint"/> on the parent
/// <see cref="Table"/> — they are NOT duplicated here.
///
/// Named <c>TableIndex</c> rather than <c>Index</c> to avoid clashing with
/// <see cref="System.Index"/> (the BCL range-indexer type).
/// </summary>
public sealed record TableIndex(
    string Name,
    bool IsUnique,
    bool IsClustered,
    string? FilterExpression,
    IReadOnlyList<IndexColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns);

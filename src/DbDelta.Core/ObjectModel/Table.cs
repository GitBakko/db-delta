namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user table (sys.tables row) and everything that hangs off it: columns,
/// table-level constraints (PK/FK/UQ/CK/DEFAULT), and indexes.
/// </summary>
/// <param name="Schema">Owning schema.</param>
/// <param name="Name">Table name.</param>
/// <param name="Columns">Columns, in ordinal order.</param>
/// <param name="Constraints">Table-level constraints (PK/FK/UQ/CK/DEFAULT).</param>
/// <param name="Indexes">Non-PK / non-UQ indexes.</param>
/// <param name="ModifyDate">Last modification date, server-local.</param>
/// <param name="DataCompression">
/// Compression of the table's own rows — the heap or the clustered index,
/// <c>index_id &lt;= 1</c>. Separate from <see cref="TableIndex.DataCompression"/>
/// because each nonclustered index carries its own setting and they routinely
/// differ. Null reads as NONE; see <see cref="Compression"/>.
/// </param>
public sealed record Table(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Constraint> Constraints,
    IReadOnlyList<TableIndex> Indexes,
    DateTime? ModifyDate = null,
    string? DataCompression = null)
{
    /// <summary>
    /// Convenience constructor that creates a table with no constraints or
    /// indexes — used by M1 callers and tests that only care about columns.
    /// </summary>
    public Table(string schema, string name, IReadOnlyList<Column> columns)
        : this(schema, name, columns, [], []) { }

    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Table");
}

/// <summary>
/// Tuple identifying an object across two schemas being compared.
/// </summary>
public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind);

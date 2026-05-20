namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user table (sys.tables row) and everything that hangs off it: columns,
/// table-level constraints (PK/FK/UQ/CK/DEFAULT), and indexes.
/// </summary>
public sealed record Table(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Constraint> Constraints,
    IReadOnlyList<TableIndex> Indexes)
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

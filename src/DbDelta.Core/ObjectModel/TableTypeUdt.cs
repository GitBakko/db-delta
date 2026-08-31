namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server table-type user-defined type (sys.types where
/// <c>is_user_defined=1</c> AND <c>is_table_type=1</c>). The column list is
/// the contract surface — table types are most often passed as table-valued
/// parameters to stored procedures, so the column shape is the part schemas
/// diff over.
/// </summary>
/// <remarks>
/// <para>
/// The keys are here because SQL Server has no ALTER for a table type: every
/// change is DROP + CREATE, so anything the model does not carry is dropped by
/// the deploy and never put back. Worse, it was dropped *silently* — with the
/// keys outside the model the re-read compared equal on columns alone and
/// reported Identical, so nothing surfaced the loss and no second run could
/// repair it. Found by the 2026-08-31 Redgate parity audit, R1.
/// </para>
/// <para>
/// <b>Every constraint below is system-named, and that is not a convention.</b>
/// <c>CREATE TYPE … AS TABLE</c> rejects a <c>CONSTRAINT</c> clause outright —
/// "Incorrect syntax near the keyword 'CONSTRAINT'" — so the names
/// <c>sys.key_constraints</c> and <c>sys.check_constraints</c> report are the
/// server's own, derived from an <c>object_id</c> two servers cannot agree on.
/// They are never a pairing key and never written back. An inline
/// inline <c>INDEX</c> is the one exception: its name is the
/// user's, so it is compared and emitted.
/// </para>
/// <para>
/// All four are <c>init</c> properties rather than positional members so every
/// existing construction still compiles and still means "no keys".
/// </para>
/// </remarks>
public sealed record TableTypeUdt(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "TableType");

    /// <summary>
    /// The <c>PRIMARY KEY</c>, the <c>UNIQUE</c> constraints and the inline
    /// <c>INDEX</c> declarations, in catalog order — all three are rows of
    /// <c>sys.indexes</c> on the type table and all three carry a per-column
    /// sort direction, so they are one list of <see cref="TableIndex"/> rather
    /// than three shapes that would each have to re-learn the direction.
    /// </summary>
    public IReadOnlyList<TableIndex> Keys { get; init; } = [];

    /// <summary><c>CHECK</c> constraints, in catalog order.</summary>
    public IReadOnlyList<CheckConstraint> CheckConstraints { get; init; } = [];

    /// <summary>Convenience over <see cref="Keys"/>; a table type has at most one.</summary>
    public TableIndex? PrimaryKey => Keys.FirstOrDefault(k => k.IsPrimaryKey);
}

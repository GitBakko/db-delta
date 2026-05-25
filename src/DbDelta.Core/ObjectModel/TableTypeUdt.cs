namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server table-type user-defined type (sys.types where
/// <c>is_user_defined=1</c> AND <c>is_table_type=1</c>). The column list is
/// the contract surface — table types are most often passed as table-valued
/// parameters to stored procedures, so the column shape is the part schemas
/// diff over. v1 captures columns; PK / unique / check constraints embedded
/// in the table type definition are out of scope (rare in practice and easy
/// to add later by extending this record).
/// </summary>
public sealed record TableTypeUdt(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "TableType");
}

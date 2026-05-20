namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Action SQL Server takes when a referenced row is updated/deleted.
/// </summary>
public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault,
}

/// <summary>
/// `FOREIGN KEY` constraint. Always columnar; cross-database references are
/// out-of-scope for v1.
/// </summary>
public sealed record ForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate,
    bool IsDisabled,
    bool IsNotForReplication) : Constraint(Name)
{
    public override string Kind => "ForeignKey";
}

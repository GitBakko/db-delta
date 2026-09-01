namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `UNIQUE` constraint (not the same as a unique index — see <see cref="Index"/>).
/// </summary>
/// <remarks>
/// Carries a direction per column for the same reason <see cref="PrimaryKey"/>
/// does, and it was lost the same way.
/// </remarks>
public sealed record UniqueConstraint(
    string Name,
    IReadOnlyList<IndexColumn> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "UniqueConstraint";
}

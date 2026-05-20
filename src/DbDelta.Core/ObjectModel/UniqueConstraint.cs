namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `UNIQUE` constraint (not the same as a unique index — see <see cref="Index"/>).
/// </summary>
public sealed record UniqueConstraint(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "UniqueConstraint";
}

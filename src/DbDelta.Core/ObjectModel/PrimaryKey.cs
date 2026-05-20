namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `PRIMARY KEY` constraint. Column ordering is significant and is preserved.
/// </summary>
public sealed record PrimaryKey(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "PrimaryKey";
}

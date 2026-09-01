namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `PRIMARY KEY` constraint. Column ordering is significant and is preserved,
/// and so is each column's direction.
/// </summary>
/// <remarks>
/// <see cref="Columns"/> was a list of names, so <c>PRIMARY KEY (A ASC, B
/// DESC)</c> was read back as all-ascending: the comparison then called two
/// different keys Identical, and the next rebuild wrote the flattened form —
/// measured, <c>is_descending_key</c> 1 before and 0 after, with no error at
/// any point. A bare string still converts to an ascending column, which is
/// what T-SQL means by <c>(A, B)</c>.
/// </remarks>
public sealed record PrimaryKey(
    string Name,
    IReadOnlyList<IndexColumn> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "PrimaryKey";
}

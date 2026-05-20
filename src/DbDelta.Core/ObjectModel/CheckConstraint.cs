namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `CHECK` constraint. <see cref="Expression"/> is verbatim T-SQL captured
/// from <c>sys.check_constraints.definition</c>.
/// </summary>
public sealed record CheckConstraint(
    string Name,
    string Expression,
    bool IsDisabled,
    bool IsNotForReplication) : Constraint(Name)
{
    public override string Kind => "CheckConstraint";
}

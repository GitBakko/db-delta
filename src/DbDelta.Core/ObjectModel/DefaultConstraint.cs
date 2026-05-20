namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Named `DEFAULT` constraint. The expression is also surfaced on
/// <see cref="Column.DefaultExpression"/> for convenience, but this record is
/// the source of truth for the constraint's name (which matters for ALTER /
/// DROP CONSTRAINT emission).
/// </summary>
public sealed record DefaultConstraint(
    string Name,
    string ColumnName,
    string Expression) : Constraint(Name)
{
    public override string Kind => "DefaultConstraint";
}

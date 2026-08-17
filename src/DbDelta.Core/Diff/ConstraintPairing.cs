using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Diff;

/// <summary>
/// Decides which constraint on one side <em>is</em> which constraint on the
/// other — the question that comes before "did it change".
/// </summary>
/// <remarks>
/// <para>
/// A constraint the DDL named is the same constraint on both sides when it
/// carries the same NAME. One SQL Server named itself never can be: the suffix
/// of <c>DF__Ordini__Stato__3B75D760</c> is derived from that constraint's own
/// <c>object_id</c>, so the same schema deployed twice produces two names that
/// disagree by construction. Paired by name, every table carrying an inline
/// DEFAULT or an unnamed CHECK/PK is Different forever and the script drops the
/// target's hash to add the source's — on production primary keys.
/// </para>
/// <para>
/// So a system-named constraint is paired by SHAPE instead: the columns it
/// covers, the column it defaults, the expression it checks. That is its only
/// stable identity across two servers. When no shape matches, the pair simply
/// does not exist, which the callers already handle — the old one is dropped
/// and the new one added, exactly as for a constraint that disappeared.
/// </para>
/// <para>
/// Shape here is IDENTITY, deliberately narrower than the shape-equality each
/// caller applies afterwards. A PK on the same columns that flipped to
/// NONCLUSTERED is the same primary key, changed; a DEFAULT on the same column
/// with a new expression is the same default, changed. Folding those into the
/// pairing key would turn every change into a non-pair, which the callers also
/// handle correctly (drop + add) but which would stop them from ever reporting
/// "the same constraint, changed".
/// </para>
/// <para>
/// Foreign keys are excluded and always pair by name. Their emission side is
/// keyed on the name in three separate structures in
/// <c>ScriptGenerator</c> (the orchestrated re-add set, the drop dedupe set and
/// the FK delta), and pairing them differently HERE alone would leave the
/// engine and the emitter disagreeing about the same key.
/// </para>
/// </remarks>
internal static class ConstraintPairing
{
    /// <summary>
    /// The constraint in <paramref name="candidates"/> that is the same
    /// constraint as <paramref name="constraint"/>, or <see langword="null"/>
    /// when the other side has none.
    /// </summary>
    // ponytail: linear scan — a table carries a handful of constraints, and the
    // callers already walk both lists. Key the shape-paired ones into a lookup
    // if a table ever shows up with hundreds.
    public static Constraint? Match(
        Constraint constraint,
        IReadOnlyList<Constraint> candidates,
        StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(names);

        bool byShape = PairsByShape(constraint);
        foreach (Constraint other in candidates)
        {
            // Both sides have to agree on HOW they pair before they can pair at
            // all. A source column carrying an inline DEFAULT and a target
            // column carrying "CONSTRAINT DF_Stato DEFAULT" are genuinely two
            // different constraints: the target really does hold a name the
            // source never asked for, and saying so is what makes the script
            // drop it and let the server mint its own.
            if (PairsByShape(other) != byShape) { continue; }

            bool match = byShape
                ? ShapeEqual(constraint, other, names)
                : names.Equals(other.Name, constraint.Name);
            if (match) { return other; }
        }
        return null;
    }

    /// <summary>
    /// True when SQL Server named <paramref name="c"/> itself and its name is
    /// therefore not a pairing key. Foreign keys are excluded — see the class
    /// remarks.
    /// </summary>
    public static bool PairsByShape(Constraint c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c is not ForeignKey && c.IsSystemNamed;
    }

    private static bool ShapeEqual(Constraint a, Constraint b, StringComparer names) => (a, b) switch
    {
        (PrimaryKey pa, PrimaryKey pb) => pa.Columns.SequenceEqual(pb.Columns, names),
        (UniqueConstraint ua, UniqueConstraint ub) => ua.Columns.SequenceEqual(ub.Columns, names),
        (CheckConstraint ca, CheckConstraint cb) =>
            BodyNormalizer.ExpressionsEqual(ca.Expression, cb.Expression),
        // A column holds at most one DEFAULT, so the column IS the identity.
        (DefaultConstraint da, DefaultConstraint db) => names.Equals(da.ColumnName, db.ColumnName),
        _ => false,
    };
}

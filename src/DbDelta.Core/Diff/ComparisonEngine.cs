using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair tables by identity, then within each pair
/// compare columns, constraints, and indexes per options. Pure → no I/O.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        var bByIdentity = b.Tables.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        List<DifferencePair> pairs = [];
        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Table? sideA);
            bByIdentity.TryGetValue(id, out Table? sideB);
            DifferenceStatus status = ClassifyTable(sideA, sideB, options);
            pairs.Add(new DifferencePair(id, status, sideA, sideB));
        }

        return new ComparisonResult(pairs);
    }

    private static DifferenceStatus ClassifyTable(Table? a, Table? b, ComparisonOptions options)
    {
        if (a is null && b is not null)
        {
            return DifferenceStatus.OnlyInB;
        }

        if (a is not null && b is null)
        {
            return DifferenceStatus.OnlyInA;
        }

        if (a is null || b is null)
        {
            return DifferenceStatus.Identical;
        }

        bool columnsDiffer = !ColumnsEqual(a.Columns, b.Columns, options);
        bool constraintsDiffer = !options.HasFlag(ComparisonOptions.IgnoreKeys)
            && !ConstraintsEqual(a.Constraints, b.Constraints);
        bool indexesDiffer = !options.HasFlag(ComparisonOptions.IgnoreIndexes)
            && !IndexesEqual(a.Indexes, b.Indexes);

        return columnsDiffer || constraintsDiffer || indexesDiffer
            ? DifferenceStatus.Different
            : DifferenceStatus.Identical;
    }

    private static bool ColumnsEqual(
        IReadOnlyList<Column> ax,
        IReadOnlyList<Column> bx,
        ComparisonOptions options)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name);
        foreach (Column col in ax)
        {
            if (!bByName.TryGetValue(col.Name, out Column? other))
            {
                return false;
            }

            if (col.DataType != other.DataType)
            {
                return false;
            }

            if (col.IsNullable != other.IsNullable)
            {
                return false;
            }

            if (col.IsIdentity != other.IsIdentity)
            {
                return false;
            }

            if (col.IsIdentity && (col.IdentitySeed != other.IdentitySeed
                || col.IdentityIncrement != other.IdentityIncrement))
            {
                return false;
            }

            if ((col.DefaultExpression ?? string.Empty) != (other.DefaultExpression ?? string.Empty))
            {
                return false;
            }

            if ((col.ComputedExpression ?? string.Empty) != (other.ComputedExpression ?? string.Empty))
            {
                return false;
            }

            if (col.IsPersistedComputed != other.IsPersistedComputed)
            {
                return false;
            }

            if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConstraintsEqual(
        IReadOnlyList<Constraint> ax,
        IReadOnlyList<Constraint> bx)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name);
        foreach (Constraint left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out Constraint? right))
            {
                return false;
            }

            if (left.Kind != right.Kind)
            {
                return false;
            }

            if (!ConstraintShapeEqual(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConstraintShapeEqual(Constraint left, Constraint right) => left switch
    {
        PrimaryKey pk when right is PrimaryKey other =>
            pk.IsClustered == other.IsClustered && pk.Columns.SequenceEqual(other.Columns),
        UniqueConstraint uq when right is UniqueConstraint other =>
            uq.IsClustered == other.IsClustered && uq.Columns.SequenceEqual(other.Columns),
        ForeignKey fk when right is ForeignKey other =>
            fk.Columns.SequenceEqual(other.Columns)
            && fk.ReferencedSchema == other.ReferencedSchema
            && fk.ReferencedTable == other.ReferencedTable
            && fk.ReferencedColumns.SequenceEqual(other.ReferencedColumns)
            && fk.OnDelete == other.OnDelete
            && fk.OnUpdate == other.OnUpdate
            && fk.IsDisabled == other.IsDisabled
            && fk.IsNotForReplication == other.IsNotForReplication,
        CheckConstraint ck when right is CheckConstraint other =>
            ck.Expression == other.Expression
            && ck.IsDisabled == other.IsDisabled
            && ck.IsNotForReplication == other.IsNotForReplication,
        DefaultConstraint df when right is DefaultConstraint other =>
            df.ColumnName == other.ColumnName && df.Expression == other.Expression,
        _ => false,
    };

    private static bool IndexesEqual(
        IReadOnlyList<TableIndex> ax,
        IReadOnlyList<TableIndex> bx)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(i => i.Name);
        foreach (TableIndex left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out TableIndex? right))
            {
                return false;
            }

            if (left.IsUnique != right.IsUnique)
            {
                return false;
            }

            if (left.IsClustered != right.IsClustered)
            {
                return false;
            }

            if ((left.FilterExpression ?? string.Empty) != (right.FilterExpression ?? string.Empty))
            {
                return false;
            }

            if (left.KeyColumns.Count != right.KeyColumns.Count)
            {
                return false;
            }

            for (int i = 0; i < left.KeyColumns.Count; i++)
            {
                if (left.KeyColumns[i].Name != right.KeyColumns[i].Name)
                {
                    return false;
                }
                if (left.KeyColumns[i].IsDescending != right.KeyColumns[i].IsDescending)
                {
                    return false;
                }
            }

            if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns))
            {
                return false;
            }
        }

        return true;
    }
}

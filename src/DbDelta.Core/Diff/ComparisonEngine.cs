using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair objects by identity and classify their status.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        Dictionary<ObjectIdentity, Table> aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        Dictionary<ObjectIdentity, Table> bByIdentity = b.Tables.ToDictionary(t => t.Identity);
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
            return DifferenceStatus.Identical; // both null — impossible
        }

        return ColumnsEqual(a.Columns, b.Columns, options)
            ? DifferenceStatus.Identical
            : DifferenceStatus.Different;
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

        Dictionary<string, Column> bByName = bx.ToDictionary(c => c.Name);
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

            if ((col.DefaultExpression ?? string.Empty) != (other.DefaultExpression ?? string.Empty))
            {
                return false;
            }

            // ForceColumnOrder option: also require ordinal match
            if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal)
            {
                return false;
            }
        }

        return true;
    }
}

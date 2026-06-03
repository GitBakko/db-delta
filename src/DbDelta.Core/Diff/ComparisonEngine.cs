using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair tables + modules by identity, then within each pair
/// compare per options. Pure → no I/O.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        List<DifferencePair> pairs = [];
        pairs.AddRange(CompareTables(a, b, options));
        pairs.AddRange(CompareModules(a.Views, b.Views));
        pairs.AddRange(CompareModules(a.Procedures, b.Procedures));
        pairs.AddRange(CompareModules(a.Functions, b.Functions));
        pairs.AddRange(CompareTriggers(a.Triggers, b.Triggers));
        pairs.AddRange(CompareSequences(a.Sequences, b.Sequences));
        pairs.AddRange(CompareSynonyms(a.Synonyms, b.Synonyms));
        pairs.AddRange(CompareUserDefinedTypes(a.UserDefinedTypes, b.UserDefinedTypes));
        pairs.AddRange(CompareTableTypeUdts(a.TableTypeUdts, b.TableTypeUdts));
        pairs.AddRange(CompareUsers(a.Users, b.Users));
        pairs.AddRange(CompareRoles(a.Roles, b.Roles));
        pairs.AddRange(ComparePermissions(a.Permissions, b.Permissions));

        return new ComparisonResult(pairs);
    }

    // ── M6: Users / Roles / Permissions ────────────────────────────────────

    private static IEnumerable<DifferencePair> CompareUsers(
        IReadOnlyList<DatabaseUser> ax, IReadOnlyList<DatabaseUser> bx)
    {
        var aByIdentity = ax.ToDictionary(u => u.Identity);
        var bByIdentity = bx.ToDictionary(u => u.Identity);
        HashSet<ObjectIdentity> all = [.. aByIdentity.Keys];
        all.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in all.OrderBy(i => i.ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            aByIdentity.TryGetValue(id, out DatabaseUser? sideA);
            bByIdentity.TryGetValue(id, out DatabaseUser? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => UsersEqual(sideA, sideB) ? DifferenceStatus.Identical : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool UsersEqual(DatabaseUser a, DatabaseUser b) =>
        string.Equals(a.TypeCode, b.TypeCode, StringComparison.Ordinal)
        && string.Equals(a.LoginName, b.LoginName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.DefaultSchema, b.DefaultSchema, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<DifferencePair> CompareRoles(
        IReadOnlyList<DatabaseRole> ax, IReadOnlyList<DatabaseRole> bx)
    {
        var aByIdentity = ax.ToDictionary(r => r.Identity);
        var bByIdentity = bx.ToDictionary(r => r.Identity);
        HashSet<ObjectIdentity> all = [.. aByIdentity.Keys];
        all.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in all.OrderBy(i => i.ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            aByIdentity.TryGetValue(id, out DatabaseRole? sideA);
            bByIdentity.TryGetValue(id, out DatabaseRole? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => RolesEqual(sideA, sideB) ? DifferenceStatus.Identical : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool RolesEqual(DatabaseRole a, DatabaseRole b)
    {
        if (!string.Equals(a.OwnerName, b.OwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string[] aMembers = [.. a.Members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)];
        string[] bMembers = [.. b.Members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)];
        if (aMembers.Length != bMembers.Length) { return false; }
        for (int i = 0; i < aMembers.Length; i++)
        {
            if (!string.Equals(aMembers[i], bMembers[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<DifferencePair> ComparePermissions(
        IReadOnlyList<Permission> ax, IReadOnlyList<Permission> bx)
    {
        // Permissions are pure presence/absence — there's nothing to "modify"
        // about a single row beyond the row existing on one side or both. Use
        // the DiffKey string as the pairing identity so identical
        // (Grantee+Action+Target) rows on both sides classify as Identical.
        var aByKey =
            ax.GroupBy(p => p.DiffKey).ToDictionary(g => g.Key, g => g.First());
        var bByKey =
            bx.GroupBy(p => p.DiffKey).ToDictionary(g => g.Key, g => g.First());
        HashSet<string> allKeys = [.. aByKey.Keys];
        allKeys.UnionWith(bByKey.Keys);

        foreach (string key in allKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            aByKey.TryGetValue(key, out Permission? sideA);
            bByKey.TryGetValue(key, out Permission? sideB);
            Permission anchor = sideA ?? sideB!;
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => DifferenceStatus.Identical,
            };
            yield return new DifferencePair(anchor.Identity, status, sideA, sideB);
        }
    }

    // ── M5: Sequence / Synonym / UserDefinedType ──────────────────────────

    private static IEnumerable<DifferencePair> CompareSequences(
        IReadOnlyList<Sequence> ax,
        IReadOnlyList<Sequence> bx)
    {
        var aByIdentity = ax.ToDictionary(s => s.Identity);
        var bByIdentity = bx.ToDictionary(s => s.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Sequence? sideA);
            bByIdentity.TryGetValue(id, out Sequence? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => SequencesEqual(sideA, sideB) ? DifferenceStatus.Identical : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool SequencesEqual(Sequence a, Sequence b) =>
        string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)
        && a.StartValue == b.StartValue
        && a.Increment == b.Increment
        && a.MinValue == b.MinValue
        && a.MaxValue == b.MaxValue
        && a.IsCycling == b.IsCycling
        && a.IsCached == b.IsCached
        && a.CacheSize == b.CacheSize;

    private static IEnumerable<DifferencePair> CompareSynonyms(
        IReadOnlyList<Synonym> ax,
        IReadOnlyList<Synonym> bx)
    {
        var aByIdentity = ax.ToDictionary(s => s.Identity);
        var bByIdentity = bx.ToDictionary(s => s.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Synonym? sideA);
            bByIdentity.TryGetValue(id, out Synonym? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => string.Equals(sideA.BaseObjectName, sideB.BaseObjectName, StringComparison.OrdinalIgnoreCase)
                    ? DifferenceStatus.Identical
                    : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static IEnumerable<DifferencePair> CompareUserDefinedTypes(
        IReadOnlyList<UserDefinedType> ax,
        IReadOnlyList<UserDefinedType> bx)
    {
        var aByIdentity = ax.ToDictionary(t => t.Identity);
        var bByIdentity = bx.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out UserDefinedType? sideA);
            bByIdentity.TryGetValue(id, out UserDefinedType? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => UdtsEqual(sideA, sideB) ? DifferenceStatus.Identical : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool UdtsEqual(UserDefinedType a, UserDefinedType b) =>
        string.Equals(a.BaseTypeName, b.BaseTypeName, StringComparison.OrdinalIgnoreCase)
        && a.MaxLength == b.MaxLength
        && a.Precision == b.Precision
        && a.Scale == b.Scale
        && a.IsNullable == b.IsNullable;

    private static IEnumerable<DifferencePair> CompareTableTypeUdts(
        IReadOnlyList<TableTypeUdt> ax,
        IReadOnlyList<TableTypeUdt> bx)
    {
        var aByIdentity = ax.ToDictionary(t => t.Identity);
        var bByIdentity = bx.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out TableTypeUdt? sideA);
            bByIdentity.TryGetValue(id, out TableTypeUdt? sideB);
            DifferenceStatus status = (sideA, sideB) switch
            {
                (null, null) => DifferenceStatus.Identical,
                (null, _) => DifferenceStatus.OnlyInB,
                (_, null) => DifferenceStatus.OnlyInA,
                _ => TableTypeUdtsEqual(sideA, sideB) ? DifferenceStatus.Identical : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool TableTypeUdtsEqual(TableTypeUdt a, TableTypeUdt b)
    {
        if (a.Columns.Count != b.Columns.Count) { return false; }
        var bByName = b.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (Column ac in a.Columns)
        {
            if (!bByName.TryGetValue(ac.Name, out Column? bc)) { return false; }
            if (!string.Equals(ac.DataType, bc.DataType, StringComparison.OrdinalIgnoreCase)) { return false; }
            if (ac.IsNullable != bc.IsNullable) { return false; }
            if (ac.Ordinal != bc.Ordinal) { return false; }
            // M13-PARITY.5 #32 — UDTT column collation participates in equality
            // for the same reason it does on plain tables.
            if (!string.Equals(ac.Collation, bc.Collation, StringComparison.OrdinalIgnoreCase)) { return false; }
        }
        return true;
    }

    private static IEnumerable<DifferencePair> CompareTables(Database a, Database b, ComparisonOptions options)
    {
        var aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        var bByIdentity = b.Tables.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Table? sideA);
            bByIdentity.TryGetValue(id, out Table? sideB);
            DifferenceStatus status = ClassifyTable(sideA, sideB, options);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static IEnumerable<DifferencePair> CompareModules<TModule>(
        IReadOnlyList<TModule> ax,
        IReadOnlyList<TModule> bx)
        where TModule : Module
    {
        var aByIdentity = ax.ToDictionary(m => m.Identity);
        var bByIdentity = bx.ToDictionary(m => m.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out TModule? sideA);
            bByIdentity.TryGetValue(id, out TModule? sideB);
            DifferenceStatus status = ClassifyModule(sideA, sideB);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static DifferenceStatus ClassifyModule(Module? a, Module? b)
    {
        // After CompareModules' union-of-keys iteration at least one side is non-null;
        // the both-null case is unreachable in practice but treated as Identical for safety.
        if (a is null)
        {
            return b is null ? DifferenceStatus.Identical : DifferenceStatus.OnlyInB;
        }
        if (b is null)
        {
            return DifferenceStatus.OnlyInA;
        }

        // Encrypted bodies are opaque — we cannot prove equality, so we err on the side
        // of Different. Same when only one side is encrypted.
        if (a.IsEncrypted || b.IsEncrypted)
        {
            return DifferenceStatus.Different;
        }

        // Reconcile the embedded CREATE-name with the catalog identity first: a
        // module renamed via sp_rename keeps its pre-rename name frozen in the
        // stored definition, which would otherwise read as a body difference even
        // though the modules are semantically identical (SQL Server resolves by
        // catalog identity, not by the name baked into the definition text).
        string? na = BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(a.Body, a.Schema, a.Name));
        string? nb = BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(b.Body, b.Schema, b.Name));
        return string.Equals(na, nb, StringComparison.Ordinal)
            ? DifferenceStatus.Identical
            : DifferenceStatus.Different;
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

            // M13-PARITY.5 #32 — explicit column collation must match. Compare
            // the raw value; the DB-default fallback is the script generator's
            // concern (it decides when to *emit* COLLATE). Two columns with the
            // same explicit collation are equal regardless of either DB's
            // default, and two columns with NULL collation are equal too
            // (matches the previous "ignore collation" behaviour for
            // non-string columns).
            if (!string.Equals(col.Collation, other.Collation, StringComparison.OrdinalIgnoreCase))
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

    private static IEnumerable<DifferencePair> CompareTriggers(
        IReadOnlyList<Trigger> ax,
        IReadOnlyList<Trigger> bx)
    {
        var aByIdentity = ax.ToDictionary(m => m.Identity);
        var bByIdentity = bx.ToDictionary(m => m.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Trigger? sideA);
            bByIdentity.TryGetValue(id, out Trigger? sideB);
            DifferenceStatus status = ClassifyTrigger(sideA, sideB);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static DifferenceStatus ClassifyTrigger(Trigger? a, Trigger? b)
    {
        // Reuse the module-body classification first.
        DifferenceStatus body = ClassifyModule(a, b);
        if (body != DifferenceStatus.Identical)
        {
            return body;
        }
        // Body is byte-equal AND neither side is encrypted — drop into the
        // trigger-specific state check. Both `a` and `b` are guaranteed
        // non-null here because ClassifyModule returns Identical only when
        // both sides are present.
        return a!.IsDisabled != b!.IsDisabled
            || a.IsNotForReplication != b.IsNotForReplication
            || !string.Equals(a.ParentSchema, b.ParentSchema, StringComparison.Ordinal)
            || !string.Equals(a.ParentTable, b.ParentTable, StringComparison.Ordinal)
            ? DifferenceStatus.Different
            : DifferenceStatus.Identical;
    }
}

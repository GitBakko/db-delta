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

        // The target's collation decides the pairing, because the target is the
        // one that will execute the DDL: if it cannot tell [dbo].[Clienti] from
        // [dbo].[CLIENTI] then neither may we, or we hand it a DROP for a table
        // it is about to be told to CREATE. See NameComparison.
        ObjectIdentityComparer ids = new(NameComparison.ForCollation(b.DefaultCollation));

        List<DifferencePair> pairs = [];
        pairs.AddRange(CompareSchemas(a.Schemas, b.Schemas, ids));
        pairs.AddRange(CompareTables(a, b, options, ids));
        pairs.AddRange(CompareModules(a.Views, b.Views, ids));
        pairs.AddRange(CompareModules(a.Procedures, b.Procedures, ids));
        pairs.AddRange(CompareModules(a.Functions, b.Functions, ids));
        pairs.AddRange(CompareTriggers(a.Triggers, b.Triggers, ids));
        pairs.AddRange(CompareSequences(a.Sequences, b.Sequences, ids));
        pairs.AddRange(CompareSynonyms(a.Synonyms, b.Synonyms, ids));
        pairs.AddRange(CompareUserDefinedTypes(a.UserDefinedTypes, b.UserDefinedTypes, ids));
        pairs.AddRange(CompareTableTypeUdts(a.TableTypeUdts, b.TableTypeUdts, ids));
        pairs.AddRange(CompareUsers(a.Users, b.Users, ids));
        pairs.AddRange(CompareRoles(a.Roles, b.Roles, ids));
        pairs.AddRange(ComparePermissions(a.Permissions, b.Permissions));

        return new ComparisonResult(pairs) { NameComparer = ids.Names };
    }

    /// <summary>
    /// Builds the per-side identity map for one kind.
    /// </summary>
    /// <remarks>
    /// Two objects in the SAME database can only collide under
    /// <paramref name="ids"/> when that database is case-sensitive and the
    /// target is not — it holds both <c>dbo.Clienti</c> and <c>dbo.CLIENTI</c>,
    /// and the target could never hold both. Keeping one and discarding the
    /// other would make the survivor look like a modification and the loser
    /// vanish silently, so this refuses instead: an unrepresentable comparison
    /// has to be visible.
    /// </remarks>
    private static Dictionary<ObjectIdentity, T> MapByIdentity<T>(
        IEnumerable<T> items,
        Func<T, ObjectIdentity> identityOf,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, T> map = new(ids);
        foreach (T item in items)
        {
            ObjectIdentity id = identityOf(item);
            if (!map.TryAdd(id, item))
            {
                throw new InvalidOperationException(
                    $"'{id.SchemaName}.{id.ObjectName}' ({id.Kind}) exists more than once under the "
                    + "target's collation, which is case-insensitive: one database holds two objects "
                    + "whose names differ only by case, and the other could never hold both. Compare "
                    + "against a target whose collation matches the source, or rename one of them.");
            }
        }
        return map;
    }

    // ── Schemas ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs schemas by name, reporting only the ones that exist on a single
    /// side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Schemas were read and then thrown away, so a source-only schema was
    /// never reported and no <c>CREATE SCHEMA</c> was ever emitted — any object
    /// living in a schema the target lacked made the deploy fail on its first
    /// statement with Msg 2760.
    /// </para>
    /// <para>
    /// Unlike every other kind, schemas present on BOTH sides are deliberately
    /// not emitted as Identical pairs. A schema is modelled by its name alone,
    /// so a pair that matches by identity is equal by construction: the row
    /// could never carry information, and <c>dbo</c> would appear in every
    /// comparison anyone ever runs. Revisit this if the model gains an owner
    /// (<c>sys.schemas.principal_id</c>), which would make Different reachable
    /// and the pair worth reporting.
    /// </para>
    /// <para>
    /// The reader already filters out the system schemas (sys,
    /// INFORMATION_SCHEMA, guest, db_*), so nothing here can emit a
    /// <c>DROP SCHEMA [db_datareader]</c>.
    /// </para>
    /// </remarks>
    private static IEnumerable<DifferencePair> CompareSchemas(
        IReadOnlyList<Schema> ax, IReadOnlyList<Schema> bx, ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, Schema> aByIdentity = MapByIdentity(ax, s => s.Identity, ids);
        Dictionary<ObjectIdentity, Schema> bByIdentity = MapByIdentity(bx, s => s.Identity, ids);
        HashSet<ObjectIdentity> all = new(aByIdentity.Keys, ids);
        all.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in all.OrderBy(i => i.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            bool inA = aByIdentity.TryGetValue(id, out Schema? sideA);
            bool inB = bByIdentity.TryGetValue(id, out Schema? sideB);
            if (inA && inB) { continue; }
            yield return new DifferencePair(
                id,
                inA ? DifferenceStatus.OnlyInA : DifferenceStatus.OnlyInB,
                sideA,
                sideB);
        }
    }

    // ── M6: Users / Roles / Permissions ────────────────────────────────────

    private static IEnumerable<DifferencePair> CompareUsers(
        IReadOnlyList<DatabaseUser> ax, IReadOnlyList<DatabaseUser> bx, ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, DatabaseUser> aByIdentity = MapByIdentity(ax, u => u.Identity, ids);
        Dictionary<ObjectIdentity, DatabaseUser> bByIdentity = MapByIdentity(bx, u => u.Identity, ids);
        HashSet<ObjectIdentity> all = new(aByIdentity.Keys, ids);
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
        IReadOnlyList<DatabaseRole> ax, IReadOnlyList<DatabaseRole> bx, ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, DatabaseRole> aByIdentity = MapByIdentity(ax, r => r.Identity, ids);
        Dictionary<ObjectIdentity, DatabaseRole> bByIdentity = MapByIdentity(bx, r => r.Identity, ids);
        HashSet<ObjectIdentity> all = new(aByIdentity.Keys, ids);
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
        IReadOnlyList<Sequence> bx,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, Sequence> aByIdentity = MapByIdentity(ax, s => s.Identity, ids);
        Dictionary<ObjectIdentity, Sequence> bByIdentity = MapByIdentity(bx, s => s.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
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
        IReadOnlyList<Synonym> bx,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, Synonym> aByIdentity = MapByIdentity(ax, s => s.Identity, ids);
        Dictionary<ObjectIdentity, Synonym> bByIdentity = MapByIdentity(bx, s => s.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
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
        IReadOnlyList<UserDefinedType> bx,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, UserDefinedType> aByIdentity = MapByIdentity(ax, t => t.Identity, ids);
        Dictionary<ObjectIdentity, UserDefinedType> bByIdentity = MapByIdentity(bx, t => t.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
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
        IReadOnlyList<TableTypeUdt> bx,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, TableTypeUdt> aByIdentity = MapByIdentity(ax, t => t.Identity, ids);
        Dictionary<ObjectIdentity, TableTypeUdt> bByIdentity = MapByIdentity(bx, t => t.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
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
                _ => TableTypeUdtsEqual(sideA, sideB, ids.Names)
                    ? DifferenceStatus.Identical
                    : DifferenceStatus.Different,
            };
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static bool TableTypeUdtsEqual(TableTypeUdt a, TableTypeUdt b, StringComparer names)
    {
        if (a.Columns.Count != b.Columns.Count) { return false; }
        var bByName = b.Columns.ToDictionary(c => c.Name, names);
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

    private static IEnumerable<DifferencePair> CompareTables(
        Database a,
        Database b,
        ComparisonOptions options,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, Table> aByIdentity = MapByIdentity(a.Tables, t => t.Identity, ids);
        Dictionary<ObjectIdentity, Table> bByIdentity = MapByIdentity(b.Tables, t => t.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Table? sideA);
            bByIdentity.TryGetValue(id, out Table? sideB);
            DifferenceStatus status = ClassifyTable(sideA, sideB, options, ids.Names);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static IEnumerable<DifferencePair> CompareModules<TModule>(
        IReadOnlyList<TModule> ax,
        IReadOnlyList<TModule> bx,
        ObjectIdentityComparer ids)
        where TModule : Module
    {
        Dictionary<ObjectIdentity, TModule> aByIdentity = MapByIdentity(ax, m => m.Identity, ids);
        Dictionary<ObjectIdentity, TModule> bByIdentity = MapByIdentity(bx, m => m.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
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

    private static DifferenceStatus ClassifyTable(
        Table? a,
        Table? b,
        ComparisonOptions options,
        StringComparer names)
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

        bool columnsDiffer = !ColumnsEqual(a.Columns, b.Columns, options, names);
        bool constraintsDiffer = !options.HasFlag(ComparisonOptions.IgnoreKeys)
            && !ConstraintsEqual(a.Constraints, b.Constraints, names);
        bool indexesDiffer = !options.HasFlag(ComparisonOptions.IgnoreIndexes)
            && !IndexesEqual(a.Indexes, b.Indexes, names);

        return columnsDiffer || constraintsDiffer || indexesDiffer
            ? DifferenceStatus.Different
            : DifferenceStatus.Identical;
    }

    private static bool ColumnsEqual(
        IReadOnlyList<Column> ax,
        IReadOnlyList<Column> bx,
        ComparisonOptions options,
        StringComparer names)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name, names);
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

            // Whitespace-normalized: sys.* stores these re-formatted, with
            // server-dependent whitespace — see BodyNormalizer.ExpressionsEqual.
            if (!BodyNormalizer.ExpressionsEqual(col.DefaultExpression, other.DefaultExpression))
            {
                return false;
            }

            if (!BodyNormalizer.ExpressionsEqual(col.ComputedExpression, other.ComputedExpression))
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
        IReadOnlyList<Constraint> bx,
        StringComparer names)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name, names);
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

            if (!ConstraintShapeEqual(left, right, names))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConstraintShapeEqual(
        Constraint left,
        Constraint right,
        StringComparer names) => left switch
        {
            PrimaryKey pk when right is PrimaryKey other =>
                pk.IsClustered == other.IsClustered && pk.Columns.SequenceEqual(other.Columns, names),
            UniqueConstraint uq when right is UniqueConstraint other =>
                uq.IsClustered == other.IsClustered && uq.Columns.SequenceEqual(other.Columns, names),
            ForeignKey fk when right is ForeignKey other =>
                fk.Columns.SequenceEqual(other.Columns, names)
                && names.Equals(fk.ReferencedSchema, other.ReferencedSchema)
                && names.Equals(fk.ReferencedTable, other.ReferencedTable)
                && fk.ReferencedColumns.SequenceEqual(other.ReferencedColumns, names)
                && fk.OnDelete == other.OnDelete
                && fk.OnUpdate == other.OnUpdate
                && fk.IsDisabled == other.IsDisabled
                && fk.IsNotForReplication == other.IsNotForReplication,
            CheckConstraint ck when right is CheckConstraint other =>
                BodyNormalizer.ExpressionsEqual(ck.Expression, other.Expression)
                && ck.IsDisabled == other.IsDisabled
                && ck.IsNotForReplication == other.IsNotForReplication,
            DefaultConstraint df when right is DefaultConstraint other =>
                names.Equals(df.ColumnName, other.ColumnName)
                && BodyNormalizer.ExpressionsEqual(df.Expression, other.Expression),
            _ => false,
        };

    private static bool IndexesEqual(
        IReadOnlyList<TableIndex> ax,
        IReadOnlyList<TableIndex> bx,
        StringComparer names)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(i => i.Name, names);
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

            if (!BodyNormalizer.ExpressionsEqual(left.FilterExpression, right.FilterExpression))
            {
                return false;
            }

            if (left.KeyColumns.Count != right.KeyColumns.Count)
            {
                return false;
            }

            for (int i = 0; i < left.KeyColumns.Count; i++)
            {
                if (!names.Equals(left.KeyColumns[i].Name, right.KeyColumns[i].Name))
                {
                    return false;
                }
                if (left.KeyColumns[i].IsDescending != right.KeyColumns[i].IsDescending)
                {
                    return false;
                }
            }

            if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns, names))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<DifferencePair> CompareTriggers(
        IReadOnlyList<Trigger> ax,
        IReadOnlyList<Trigger> bx,
        ObjectIdentityComparer ids)
    {
        Dictionary<ObjectIdentity, Trigger> aByIdentity = MapByIdentity(ax, m => m.Identity, ids);
        Dictionary<ObjectIdentity, Trigger> bByIdentity = MapByIdentity(bx, m => m.Identity, ids);
        HashSet<ObjectIdentity> allIdentities = new(aByIdentity.Keys, ids);
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Trigger? sideA);
            bByIdentity.TryGetValue(id, out Trigger? sideB);
            DifferenceStatus status = ClassifyTrigger(sideA, sideB, ids.Names);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static DifferenceStatus ClassifyTrigger(Trigger? a, Trigger? b, StringComparer names)
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
            || !names.Equals(a.ParentSchema, b.ParentSchema)
            || !names.Equals(a.ParentTable, b.ParentTable)
            ? DifferenceStatus.Different
            : DifferenceStatus.Identical;
    }
}

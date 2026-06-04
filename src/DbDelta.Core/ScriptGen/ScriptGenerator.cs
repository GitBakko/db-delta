using System.Text;
using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Orchestrates per-object emitters and wraps the output in a deployment-ready
/// batch. Order:
/// <list type="number">
///   <item>Prologue — Users, Roles (no inter-object dependencies the resolver
///       tracks, so they emit up front).</item>
///   <item>DROP pass — removed objects (<see cref="DifferenceStatus.OnlyInB"/>)
///       in reverse-topological order (dependent-first) so a referenced object
///       is never dropped before its dependents.</item>
///   <item>Inbound-FK drop for identity-rebuild targets (#33) — FKs pointing at
///       a table about to be rebuilt are dropped first so the rebuild succeeds.</item>
///   <item>CREATE / ALTER pass — a single topological order
///       (<see cref="DependencyResolver"/>) over Sequence,
///       UserDefinedType, TableType, Table, View, Function, Procedure, Trigger,
///       Synonym, so cross-kind dependencies (e.g. a computed column referencing
///       a function) emit in dependency order. With no edges the resolver falls
///       back to its stable kind-then-alphabetical order.</item>
///   <item>Indexes (CREATE / DROP / delta), iterated in table topo order.</item>
///   <item>Foreign keys — emitted last so referenced tables already exist; this
///       also breaks FK cycles. Includes the #33 inbound-FK re-add.</item>
///   <item>Permissions — GRANT / REVOKE, gated on
///       <see cref="ComparisonOptions.IgnorePermissions"/> (default ON).</item>
/// </list>
/// </summary>
public sealed class ScriptGenerator
{
    private readonly TableScriptEmitter _tableEmitter = new();
    private readonly IndexScriptEmitter _indexEmitter = new();
    private readonly ForeignKeyScriptEmitter _fkEmitter = new();
    private readonly ViewScriptEmitter _viewEmitter = new();
    private readonly ProcedureScriptEmitter _procEmitter = new();
    private readonly FunctionScriptEmitter _functionEmitter = new();
    private readonly TriggerScriptEmitter _triggerEmitter = new();
    private readonly SequenceScriptEmitter _sequenceEmitter = new();
    private readonly SynonymScriptEmitter _synonymEmitter = new();
    private readonly UserDefinedTypeScriptEmitter _udtEmitter = new();
    private readonly TableTypeUdtScriptEmitter _tableTypeEmitter = new();
    private readonly UserScriptEmitter _userEmitter = new();
    private readonly RoleScriptEmitter _roleEmitter = new();
    private readonly PermissionScriptEmitter _permissionEmitter = new();

    /// <summary>
    /// Generates a complete T-SQL migration script for the given comparison result.
    /// </summary>
    public string Generate(
        ComparisonResult result,
        IEnumerable<DifferencePair>? selection = null,
        ComparisonOptions options = ComparisonOptions.Default,
        IReadOnlyList<DependencyEdge>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        dependencies ??= [];
        List<DifferencePair> pairs = [.. (selection ?? result.Differences)
            .Where(p => p.Status != DifferenceStatus.Identical)];

        // M13-PARITY.6 #33 — identify identity-rebuild targets. Inbound FKs
        // pointing at any of these tables must be dropped *before* the
        // rebuild block (so DROP TABLE succeeds) and re-added *after*.
        // Looking up via `result.Differences` (unfiltered) covers FKs held
        // by Identical tables, which never appear in <c>pairs</c>.
        HashSet<(string Schema, string Name)> rebuildTargets = [];
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "Table"))
        {
            if (pair.Status == DifferenceStatus.Different
                && pair.SideA is Table src && pair.SideB is Table tgt
                && TableScriptEmitter.RequiresFullRebuild(src, tgt))
            {
                rebuildTargets.Add((src.Schema, src.Name));
            }
        }
        List<(string FromSchema, string FromTable, ForeignKey FK)> inboundFkDrops = [];
        List<(string FromSchema, string FromTable, ForeignKey FK)> inboundFkAdds = [];
        HashSet<string> rebuildOrchestratedFkNames = new(StringComparer.Ordinal);
        if (rebuildTargets.Count > 0)
        {
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))
            {
                if (p.SideB is Table tgtT)
                {
                    foreach (ForeignKey fk in tgtT.Constraints.OfType<ForeignKey>())
                    {
                        if (rebuildTargets.Contains((fk.ReferencedSchema, fk.ReferencedTable))
                            && !rebuildTargets.Contains((tgtT.Schema, tgtT.Name)))
                        {
                            inboundFkDrops.Add((tgtT.Schema, tgtT.Name, fk));
                            rebuildOrchestratedFkNames.Add(fk.Name);
                        }
                    }
                }
                if (p.SideA is Table srcT)
                {
                    foreach (ForeignKey fk in srcT.Constraints.OfType<ForeignKey>())
                    {
                        if (rebuildTargets.Contains((fk.ReferencedSchema, fk.ReferencedTable))
                            && !rebuildTargets.Contains((srcT.Schema, srcT.Name)))
                        {
                            inboundFkAdds.Add((srcT.Schema, srcT.Name, fk));
                            rebuildOrchestratedFkNames.Add(fk.Name);
                        }
                    }
                }
            }
        }

        // Build a single topological CREATE order over the nine schema-object
        // kinds. These nine are exactly the kinds with create-time dependency
        // validation (Sequence, UserDefinedType, TableType, Table, View,
        // Function, Synonym) or deferred-resolution modules ordered for
        // cleanliness (Procedure, Trigger) — so their cross-kind references must
        // be honoured at emission time. Users, Roles, Permissions, and Schemas
        // are excluded: the resolver tracks no inter-object dependencies for
        // them, so they are emitted in fixed positions (prologue / epilogue).
        HashSet<string> topoKinds = new(StringComparer.Ordinal)
        {
            "Sequence", "UserDefinedType", "TableType", "Table",
            "View", "Function", "Procedure", "Trigger", "Synonym",
        };
        List<DifferencePair> topoPairs = [.. pairs.Where(p => topoKinds.Contains(p.Identity.Kind))];
        IReadOnlyList<ObjectIdentity> createOrder = new DependencyResolver()
            .Order([.. topoPairs.Select(p => p.Identity)], dependencies);
        var pairById = topoPairs.ToDictionary(p => p.Identity);

        bool useTransaction = !options.HasFlag(ComparisonOptions.NoTransactions);
        bool includeHeader = !options.HasFlag(ComparisonOptions.DoNotOutputCommentHeader);
        StringBuilder sb = new();
        DeploymentScriptWriter writer = new(sb, useTransaction);
        writer.WritePreamble(includeHeader);

        // Prologue: Users + Roles.
        //    Sequences, UDTs, TableTypes, Tables, Views, Functions, Procedures,
        //    Triggers, and Synonyms are emitted by the topo-ordered passes below.
        EmitUsers(writer, pairs);
        EmitRoles(writer, pairs);

        // DROP pass — objects only in B (removed from source) must be dropped in
        // reverse topological order (dependent-first) so referenced objects are not
        // dropped before their dependents.
        foreach (ObjectIdentity id in createOrder.Reverse())
        {
            DifferencePair pair = pairById[id];
            if (pair.Status != DifferenceStatus.OnlyInB) { continue; }
            string? body = DispatchBuild(id.Kind, pair);
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }

        // Inbound-FK drop (identity rebuild, #33) — drop inbound FKs to tables
        //     that are about to be rebuilt (M13-PARITY.6) so DROP TABLE
        //     succeeds. The foreign-key pass below skips these names so the
        //     same FK isn't dropped twice.
        if (inboundFkDrops.Count > 0)
        {
            StringBuilder dropBody = new();
            foreach ((string fromSchema, string fromTable, ForeignKey fk) in inboundFkDrops)
            {
                dropBody.Append("ALTER TABLE [").Append(fromSchema).Append("].[").Append(fromTable)
                        .Append("] DROP CONSTRAINT [").Append(fk.Name).AppendLine("];");
            }
            writer.WriteBatch("Dropping inbound foreign keys for rebuilt tables", dropBody.ToString());
        }

        // CREATE pass — all non-drop objects in topological (referenced-first) order.
        foreach (ObjectIdentity id in createOrder)
        {
            DifferencePair pair = pairById[id];
            if (pair.Status == DifferenceStatus.OnlyInB) { continue; }
            string? body = DispatchBuild(id.Kind, pair);
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }

        // Indexes
        //    - New table (OnlyInA): emit CREATE INDEX for every index.
        //    - Existing table (Different): diff against the target side and
        //      emit DROP / CREATE for the delta (M8 polish).
        //    Iteration follows createOrder so index order tracks table order.
        foreach (ObjectIdentity id in createOrder.Where(i => i.Kind == "Table"))
        {
            DifferencePair pair = pairById[id];
            switch (pair.Status)
            {
                case DifferenceStatus.OnlyInA when pair.SideA is Table tNew && tNew.Indexes.Count > 0:
                    {
                        StringBuilder indexBody = new();
                        foreach (TableIndex ix in tNew.Indexes)
                        {
                            indexBody.AppendLine(_indexEmitter.EmitCreate(tNew.Schema, tNew.Name, ix));
                        }
                        writer.WriteBatch($"Creating indexes on [{tNew.Schema}].[{tNew.Name}]", indexBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string indexDelta = EmitIndexDelta(tSrc, tTgt);
                        if (indexDelta.Length > 0)
                        {
                            writer.WriteBatch($"Updating indexes on [{tSrc.Schema}].[{tSrc.Name}]", indexDelta);
                        }
                        break;
                    }
                case DifferenceStatus.OnlyInA:
                case DifferenceStatus.OnlyInB:
                case DifferenceStatus.Different:
                case DifferenceStatus.Identical:
                default:
                    break;
            }
        }

        // Foreign keys — emitted last so referenced tables already exist; this
        //    also breaks FK cycles. OnlyInA: add every FK. Different: diff
        //    against target — drop removed/changed FKs, add new/changed FKs
        //    (M8 polish). FKs whose names are already covered by the rebuild
        //    orchestrator (inbound-FK drop above + inbound-FK re-add below) are
        //    skipped here to avoid double drops / adds (M13-PARITY.6 #33).
        //    Iteration follows createOrder so FK order tracks table order.
        foreach (ObjectIdentity id in createOrder.Where(i => i.Kind == "Table"))
        {
            DifferencePair pair = pairById[id];
            switch (pair.Status)
            {
                case DifferenceStatus.OnlyInA when pair.SideA is Table tNew:
                    {
                        List<ForeignKey> fksNew = [.. tNew.Constraints.OfType<ForeignKey>()
                        .Where(fk => !rebuildOrchestratedFkNames.Contains(fk.Name))];
                        if (fksNew.Count == 0) { break; }
                        StringBuilder fkBody = new();
                        foreach (ForeignKey fk in fksNew)
                        {
                            fkBody.AppendLine(_fkEmitter.EmitAdd(tNew.Schema, tNew.Name, fk));
                        }
                        writer.WriteBatch($"Adding foreign keys on [{tNew.Schema}].[{tNew.Name}]", fkBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string fkDelta = EmitFkDelta(tSrc, tTgt, rebuildOrchestratedFkNames);
                        if (fkDelta.Length > 0)
                        {
                            writer.WriteBatch($"Updating foreign keys on [{tSrc.Schema}].[{tSrc.Name}]", fkDelta);
                        }
                        break;
                    }
                case DifferenceStatus.OnlyInA:
                case DifferenceStatus.OnlyInB:
                case DifferenceStatus.Different:
                case DifferenceStatus.Identical:
                default:
                    break;
            }
        }

        // Inbound-FK re-add (#33) — re-add inbound FKs to rebuilt tables
        //     (M13-PARITY.6). Source side is authoritative — these are the FKs
        //     the final shape requires. The foreign-key pass above skipped them
        //     to keep this single re-add path clean.
        if (inboundFkAdds.Count > 0)
        {
            StringBuilder addBody = new();
            foreach ((string fromSchema, string fromTable, ForeignKey fk) in inboundFkAdds)
            {
                addBody.AppendLine(_fkEmitter.EmitAdd(fromSchema, fromTable, fk));
            }
            writer.WriteBatch("Re-adding inbound foreign keys to rebuilt tables", addBody.ToString());
        }

        // Permissions — last, gated on options. Default (Redgate-parity)
        //    skips permissions entirely; consumers can clear the flag to
        //    include GRANT / REVOKE statements.
        if (!options.HasFlag(ComparisonOptions.IgnorePermissions))
        {
            EmitPermissions(writer, pairs);
        }

        writer.WriteVerdict();
        return sb.ToString();
    }

    // ── Phase-label helper ──────────────────────────────────────────────────

    private static string PhaseLabel(DifferencePair pair)
    {
        ObjectIdentity id = pair.Identity;
        bool schemaScoped = id.Kind is not ("User" or "Role" or "Permission");
        string name = schemaScoped ? $"[{id.SchemaName}].[{id.ObjectName}]" : $"[{id.ObjectName}]";
        string verb = pair.Status switch
        {
            DifferenceStatus.OnlyInA => "Creating",
            DifferenceStatus.OnlyInB => "Dropping",
            DifferenceStatus.Different => "Altering",
            DifferenceStatus.Identical => throw new ArgumentOutOfRangeException(
                nameof(pair), pair.Status,
                "PhaseLabel is only defined for OnlyInA / OnlyInB / Different pairs."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(pair), pair.Status,
                "PhaseLabel is only defined for OnlyInA / OnlyInB / Different pairs."),
        };
        return $"{verb} {id.Kind} {name}";
    }

    // ── Per-pair body builders ──────────────────────────────────────────────

    private string? BuildOneSequence(DifferencePair pair)
    {
        switch (pair.Status)
        {
            case DifferenceStatus.OnlyInA when pair.SideA is Sequence s:
                return _sequenceEmitter.EmitCreate(s);
            case DifferenceStatus.OnlyInB when pair.SideB is Sequence s:
                return _sequenceEmitter.EmitDrop(s);
            case DifferenceStatus.Different
                when pair.SideA is Sequence srcSeq && pair.SideB is Sequence tgtSeq:
                // SQL Server can ALTER every sequence property except the
                // base data type. Prefer in-place ALTER so dependent
                // `DEFAULT NEXT VALUE FOR` defaults survive (parity
                // scenario 08, 2026-05-25). Fall back to DROP + CREATE
                // only when the data type itself changed.
                string? alter = _sequenceEmitter.EmitAlter(srcSeq, tgtSeq);
                if (alter is null)
                {
                    return _sequenceEmitter.EmitDrop(tgtSeq) + Environment.NewLine
                         + _sequenceEmitter.EmitCreate(srcSeq);
                }
                return alter.Length > 0 ? alter : null;
            case DifferenceStatus.Identical:
                break;
            case DifferenceStatus.Different:
                break;
            case DifferenceStatus.OnlyInA:
                break;
            case DifferenceStatus.OnlyInB:
                break;
            default:
                break;
        }
        return null;
    }

    private string? BuildOneUserDefinedType(DifferencePair pair)
    {
        switch (pair.Status)
        {
            case DifferenceStatus.OnlyInA when pair.SideA is UserDefinedType u:
                return _udtEmitter.EmitCreate(u);
            case DifferenceStatus.OnlyInB when pair.SideB is UserDefinedType u:
                return _udtEmitter.EmitDrop(u);
            case DifferenceStatus.Different
                when pair.SideA is UserDefinedType srcU && pair.SideB is UserDefinedType tgtU:
                return _udtEmitter.EmitDrop(tgtU) + Environment.NewLine
                     + _udtEmitter.EmitCreate(srcU);
            case DifferenceStatus.Identical:
                break;
            case DifferenceStatus.Different:
                break;
            case DifferenceStatus.OnlyInA:
                break;
            case DifferenceStatus.OnlyInB:
                break;
            default:
                break;
        }
        return null;
    }

    private string? BuildOneTableTypeUdt(DifferencePair pair)
    {
        switch (pair.Status)
        {
            case DifferenceStatus.OnlyInA when pair.SideA is TableTypeUdt t:
                return _tableTypeEmitter.EmitCreate(t);
            case DifferenceStatus.OnlyInB when pair.SideB is TableTypeUdt t:
                return _tableTypeEmitter.EmitDrop(t);
            case DifferenceStatus.Different
                when pair.SideA is TableTypeUdt srcT && pair.SideB is TableTypeUdt tgtT:
                return _tableTypeEmitter.EmitDrop(tgtT) + Environment.NewLine
                     + _tableTypeEmitter.EmitCreate(srcT);
            case DifferenceStatus.Identical:
                break;
            case DifferenceStatus.Different:
                break;
            case DifferenceStatus.OnlyInA:
                break;
            case DifferenceStatus.OnlyInB:
                break;
            default:
                break;
        }
        return null;
    }

    private string? BuildOneTable(DifferencePair pair)
    {
        string ddl = _tableEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneView(DifferencePair pair)
    {
        string ddl = _viewEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneFunction(DifferencePair pair)
    {
        string ddl = _functionEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneProcedure(DifferencePair pair)
    {
        string ddl = _procEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneTrigger(DifferencePair pair)
    {
        string ddl = _triggerEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneSynonym(DifferencePair pair)
    {
        switch (pair.Status)
        {
            case DifferenceStatus.OnlyInA when pair.SideA is Synonym s:
                return _synonymEmitter.EmitCreate(s);
            case DifferenceStatus.OnlyInB when pair.SideB is Synonym s:
                return _synonymEmitter.EmitDrop(s);
            case DifferenceStatus.Different
                when pair.SideA is Synonym srcS && pair.SideB is Synonym tgtS:
                return _synonymEmitter.EmitDrop(tgtS) + Environment.NewLine
                     + _synonymEmitter.EmitCreate(srcS);
            case DifferenceStatus.Identical:
                break;
            case DifferenceStatus.Different:
                break;
            case DifferenceStatus.OnlyInA:
                break;
            case DifferenceStatus.OnlyInB:
                break;
            default:
                break;
        }
        return null;
    }

    // ── Dispatch helper ─────────────────────────────────────────────────────

    private string? DispatchBuild(string kind, DifferencePair pair) =>
        kind switch
        {
            "Sequence" => BuildOneSequence(pair),
            "UserDefinedType" => BuildOneUserDefinedType(pair),
            "TableType" => BuildOneTableTypeUdt(pair),
            "Table" => BuildOneTable(pair),
            "View" => BuildOneView(pair),
            "Function" => BuildOneFunction(pair),
            "Procedure" => BuildOneProcedure(pair),
            "Trigger" => BuildOneTrigger(pair),
            "Synonym" => BuildOneSynonym(pair),
            _ => null,
        };

    // ── Users / Roles / Permissions emitters ───────────────────────────────

    private void EmitUsers(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "User")
            .OrderBy(p => p.Identity.ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            string? body = null;
            switch (pair.Status)
            {
                case DifferenceStatus.OnlyInA when pair.SideA is DatabaseUser u:
                    body = _userEmitter.EmitCreate(u);
                    break;
                case DifferenceStatus.OnlyInB when pair.SideB is DatabaseUser u:
                    body = _userEmitter.EmitDrop(u);
                    break;
                case DifferenceStatus.Different
                    when pair.SideA is DatabaseUser srcU && pair.SideB is DatabaseUser tgtU:
                    body = DefaultSchemaIsOnlyDifference(srcU, tgtU)
                        ? _userEmitter.EmitAlterDefaultSchema(srcU)
                        : _userEmitter.EmitDrop(tgtU) + Environment.NewLine + _userEmitter.EmitCreate(srcU);
                    break;
                case DifferenceStatus.OnlyInA:
                case DifferenceStatus.OnlyInB:
                case DifferenceStatus.Different:
                case DifferenceStatus.Identical:
                default:
                    break;
            }
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }
    }

    private static bool DefaultSchemaIsOnlyDifference(DatabaseUser a, DatabaseUser b) =>
        string.Equals(a.TypeCode, b.TypeCode, StringComparison.Ordinal)
        && string.Equals(a.LoginName, b.LoginName, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(a.DefaultSchema, b.DefaultSchema, StringComparison.OrdinalIgnoreCase);

    private void EmitRoles(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Role")
            .OrderBy(p => p.Identity.ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            string? body = null;
            switch (pair.Status)
            {
                case DifferenceStatus.OnlyInA when pair.SideA is DatabaseRole r:
                    body = _roleEmitter.EmitCreate(r);
                    break;
                case DifferenceStatus.OnlyInB when pair.SideB is DatabaseRole r:
                    body = _roleEmitter.EmitDrop(r);
                    break;
                case DifferenceStatus.Different
                    when pair.SideA is DatabaseRole srcR && pair.SideB is DatabaseRole tgtR:
                    body = BuildRoleDelta(srcR, tgtR);
                    break;
                case DifferenceStatus.OnlyInA:
                case DifferenceStatus.OnlyInB:
                case DifferenceStatus.Different:
                case DifferenceStatus.Identical:
                default:
                    break;
            }
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }
    }

    private string? BuildRoleDelta(DatabaseRole src, DatabaseRole tgt)
    {
        if (!string.Equals(src.OwnerName, tgt.OwnerName, StringComparison.OrdinalIgnoreCase))
        {
            // Owner change requires DROP + CREATE — ALTER AUTHORIZATION exists
            // but ownership swaps are rare enough that DROP + CREATE is the
            // honest path: it surfaces dependent-object failures clearly.
            return _roleEmitter.EmitDrop(tgt) + Environment.NewLine + _roleEmitter.EmitCreate(src);
        }

        HashSet<string> srcMembers = new(src.Members, StringComparer.OrdinalIgnoreCase);
        HashSet<string> tgtMembers = new(tgt.Members, StringComparer.OrdinalIgnoreCase);
        StringBuilder memberBody = new();
        foreach (string drop in tgtMembers.Except(srcMembers, StringComparer.OrdinalIgnoreCase)
                                          .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            memberBody.AppendLine(_roleEmitter.EmitDropMember(src.Name, drop));
        }
        foreach (string add in srcMembers.Except(tgtMembers, StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            memberBody.AppendLine(_roleEmitter.EmitAddMember(src.Name, add));
        }
        return memberBody.Length > 0 ? memberBody.ToString() : null;
    }

    // ── Permission emitter ──────────────────────────────────────────────────

    private void EmitPermissions(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Permission")
            .OrderBy(p => p.Identity.ObjectName, StringComparer.Ordinal))
        {
            string? body = null;
            switch (pair.Status)
            {
                case DifferenceStatus.OnlyInA when pair.SideA is Permission p:
                    body = _permissionEmitter.EmitGrantOrDeny(p);
                    break;
                case DifferenceStatus.OnlyInB when pair.SideB is Permission p:
                    body = _permissionEmitter.EmitRevoke(p);
                    break;
                case DifferenceStatus.OnlyInA:
                case DifferenceStatus.OnlyInB:
                case DifferenceStatus.Different:
                case DifferenceStatus.Identical:
                default:
                    break;
            }
            if (!string.IsNullOrWhiteSpace(body))
            {
                writer.WriteBatch($"Setting permission [{pair.Identity.ObjectName}]", body);
            }
        }
    }

    /// <summary>
    /// Builds DROP INDEX / CREATE INDEX statements for the delta between a
    /// pair of versions of the same table. Indexes present only on the
    /// target side are dropped; indexes present only on the source are
    /// created; indexes whose shape differs (key columns / uniqueness /
    /// clustering / filter) are dropped + recreated.
    /// </summary>
    private string EmitIndexDelta(Table src, Table tgt)
    {
        StringBuilder sb = new();
        var srcByName =
            src.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);
        var tgtByName =
            tgt.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);

        // DROPs first so a rename-shaped change frees the slot before CREATE.
        foreach (TableIndex t in tgt.Indexes)
        {
            bool stillThere = srcByName.TryGetValue(t.Name, out TableIndex? s);
            bool shapeChanged = stillThere && !IndexShapeEqual(t, s!);
            if (!stillThere || shapeChanged)
            {
                sb.AppendLine(_indexEmitter.EmitDrop(src.Schema, src.Name, t));
            }
        }
        foreach (TableIndex s in src.Indexes)
        {
            bool existsOnTarget = tgtByName.TryGetValue(s.Name, out TableIndex? t);
            bool shapeChanged = existsOnTarget && !IndexShapeEqual(s, t!);
            if (existsOnTarget && !shapeChanged) { continue; }
            sb.AppendLine(_indexEmitter.EmitCreate(src.Schema, src.Name, s));
        }
        return sb.ToString();
    }

    private static bool IndexShapeEqual(TableIndex a, TableIndex b) =>
        a.IsUnique == b.IsUnique
        && a.IsClustered == b.IsClustered
        && BodyNormalizer.ExpressionsEqual(a.FilterExpression, b.FilterExpression)
        && a.KeyColumns.Select(k => $"{k.Name}|{k.IsDescending}")
            .SequenceEqual(b.KeyColumns.Select(k => $"{k.Name}|{k.IsDescending}"), StringComparer.Ordinal)
        && a.IncludedColumns.SequenceEqual(b.IncludedColumns, StringComparer.Ordinal);

    /// <summary>
    /// Builds DROP CONSTRAINT / ADD CONSTRAINT FOREIGN KEY statements for the
    /// FK delta between source and target versions of a table. Mirrors
    /// <see cref="EmitIndexDelta"/>: drops first, then adds; changed FKs are
    /// dropped + re-added.
    /// </summary>
    private string EmitFkDelta(Table src, Table tgt, HashSet<string>? skipNames = null)
    {
        StringBuilder sb = new();
        var srcFks =
            src.Constraints.OfType<ForeignKey>().ToDictionary(fk => fk.Name, StringComparer.Ordinal);
        var tgtFks =
            tgt.Constraints.OfType<ForeignKey>().ToDictionary(fk => fk.Name, StringComparer.Ordinal);

        foreach (ForeignKey t in tgtFks.Values)
        {
            if (skipNames is not null && skipNames.Contains(t.Name)) { continue; }
            bool stillThere = srcFks.TryGetValue(t.Name, out ForeignKey? s);
            bool shapeChanged = stillThere && !ForeignKeyShapeEqual(t, s!);
            if (!stillThere || shapeChanged)
            {
                sb.AppendLine($"ALTER TABLE [{src.Schema}].[{src.Name}] DROP CONSTRAINT [{t.Name}];");
            }
        }
        foreach (ForeignKey s in srcFks.Values)
        {
            if (skipNames is not null && skipNames.Contains(s.Name)) { continue; }
            bool existsOnTarget = tgtFks.TryGetValue(s.Name, out ForeignKey? t);
            bool shapeChanged = existsOnTarget && !ForeignKeyShapeEqual(s, t!);
            if (existsOnTarget && !shapeChanged) { continue; }
            sb.AppendLine(_fkEmitter.EmitAdd(src.Schema, src.Name, s));
        }
        return sb.ToString();
    }

    private static bool ForeignKeyShapeEqual(ForeignKey a, ForeignKey b) =>
        a.Columns.SequenceEqual(b.Columns, StringComparer.Ordinal)
        && string.Equals(a.ReferencedSchema, b.ReferencedSchema, StringComparison.Ordinal)
        && string.Equals(a.ReferencedTable, b.ReferencedTable, StringComparison.Ordinal)
        && a.ReferencedColumns.SequenceEqual(b.ReferencedColumns, StringComparer.Ordinal)
        && a.OnDelete == b.OnDelete
        && a.OnUpdate == b.OnUpdate
        && a.IsDisabled == b.IsDisabled
        && a.IsNotForReplication == b.IsNotForReplication;
}

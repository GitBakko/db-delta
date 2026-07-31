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
///   <item>Prologue — Schemas (each in its own batch: CREATE SCHEMA must be
///       first in a batch), then Users and Roles. None of these have
///       inter-object dependencies the resolver tracks.</item>
///   <item>Foreign-key DROP pass — every foreign-key drop, before any object is
///       touched. Dropping an FK has no ordering prerequisite of its own, while
///       both DROP TABLE and ALTER COLUMN are blocked by one, so doing all of
///       them first is both safe and necessary. Four feeders: FKs a Different
///       table lost or reshaped; FKs held by any table — Identical ones
///       included — that point at a table about to be dropped or rebuilt; a
///       Different table's own FKs over a column it is about to drop or retype;
///       and FKs pointing AT such a column from anywhere.</item>
///   <item>DROP pass — removed objects (<see cref="DifferenceStatus.OnlyInB"/>)
///       in reverse-topological order (dependent-first) so a referenced object
///       is never dropped before its dependents.</item>
///   <item>CREATE / ALTER pass — a single topological order
///       (<see cref="DependencyResolver"/>) over Sequence,
///       UserDefinedType, TableType, Table, View, Function, Procedure, Trigger,
///       Synonym, so cross-kind dependencies (e.g. a computed column referencing
///       a function) emit in dependency order. With no edges the resolver falls
///       back to its stable kind-then-alphabetical order.</item>
///   <item>Indexes — the delta for a normal table; the FULL source-side set for
///       a rebuilt one, whose indexes went with its DROP TABLE.</item>
///   <item>Trigger re-create for rebuilt tables — including triggers that are
///       Identical on both sides, which no other pass would ever emit.</item>
///   <item>Foreign-key ADD pass — last, so referenced tables already exist; this
///       also breaks FK cycles. Includes the re-add of every FK the up-front
///       drop pass took ownership of, and the full outbound set for rebuilt
///       tables.</item>
///   <item>Schema DROP pass — after every object pass, so a removed schema is
///       already empty.</item>
///   <item>Permissions — GRANT / REVOKE, gated on
///       <see cref="ComparisonOptions.IgnorePermissions"/> (default ON).</item>
/// </list>
/// </summary>
public sealed class ScriptGenerator
{
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
                && TableScriptEmitter.RequiresFullRebuild(src, tgt, result.NameComparer))
            {
                rebuildTargets.Add((src.Schema, src.Name));
            }
        }
        // Foreign keys the up-front DROP pass takes ownership of: dropped before
        // anything else and re-added by a single late pass. Two collections that
        // always move together — the list says what to re-add, the key set makes
        // the normal FK delta stand back so nothing is added twice.
        List<(string FromSchema, string FromTable, ForeignKey FK)> orchestratedFkAdds = [];
        // Keyed on the HOLDER table, same reason as fkDropKeys below: this set
        // makes the FK pass SKIP a foreign key, so a bare name meant an unrelated
        // namesake on another table lost its legitimate ADD and the deploy left
        // that table with no foreign key at all, silently.
        HashSet<(string Schema, string Table, string Fk)> orchestratedFks = [];

        // Hands one foreign key to that pair. The source side is authoritative:
        // it carries the shape the final schema wants. An FK the source no
        // longer has is claimed but never re-added, which is the point — the
        // claim is what stops some other pass from putting it back.
        void OrchestrateFkReAdd(Table sourceHolder, string fkName)
        {
            if (!orchestratedFks.Add((sourceHolder.Schema, sourceHolder.Name, fkName))) { return; }
            ForeignKey? sourceFk = sourceHolder.Constraints.OfType<ForeignKey>()
                .FirstOrDefault(f => string.Equals(f.Name, fkName, StringComparison.Ordinal));
            if (sourceFk is not null)
            {
                orchestratedFkAdds.Add((sourceHolder.Schema, sourceHolder.Name, sourceFk));
            }
        }

        if (rebuildTargets.Count > 0)
        {
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))
            {
                if (p.SideA is Table srcT)
                {
                    foreach (ForeignKey fk in srcT.Constraints.OfType<ForeignKey>())
                    {
                        if (rebuildTargets.Contains((fk.ReferencedSchema, fk.ReferencedTable))
                            && !rebuildTargets.Contains((srcT.Schema, srcT.Name)))
                        {
                            OrchestrateFkReAdd(srcT, fk.Name);
                        }
                    }
                }
            }
        }

        // ── Every foreign-key DROP, collected up front.
        //    Dropping a foreign key has no ordering prerequisite of its own, so
        //    doing all of them before anything else is always safe — and it is
        //    what makes the later DROP TABLE possible at all. Previously the FK
        //    drops lived in the FK pass at the very END of the script, so a
        //    table removed from the source was dropped while another table still
        //    referenced it: Msg 3726, every time. Three sources feed this:
        //      (a) target-side FKs that a Different table lost or reshaped;
        //      (b) FKs held by ANY table — Identical ones included, which never
        //          appear in `pairs` — pointing at a table about to be DROPped;
        //      (c) the same, pointing at a table about to be rebuilt (#33).
        //    (c) used to be a separate pass; folding it in here removes the
        //    duplication and orders it earlier, which is strictly safer.
        HashSet<(string Schema, string Name)> droppedTables = [.. pairs
            .Where(p => p.Identity.Kind == "Table" && p.Status == DifferenceStatus.OnlyInB)
            .Select(p => p.SideB)
            .OfType<Table>()
            .Select(t => (t.Schema, t.Name))];

        List<(string FromSchema, string FromTable, ForeignKey FK)> fkDrops = [];
        HashSet<(string Schema, string Table, string Fk)> fkDropKeys = [];
        void AddFkDrop(string schema, string table, ForeignKey fk)
        {
            // The holder table is part of the dedupe key. Constraints are
            // sys.objects rows carrying their parent table's schema_id, so a
            // constraint name is unique per SCHEMA, not per database:
            // dbo.FK_Righe_Testa and sales.FK_Righe_Testa coexist legally. With
            // the name alone, two Different tables in different schemas both
            // losing an identically-named FK produced ONE DROP CONSTRAINT, and
            // nothing else re-emits it (EmitFkAdds only adds) — the FK the
            // source removed survived in production under a success verdict.
            if (fkDropKeys.Add((schema, table, fk.Name)))
            {
                fkDrops.Add((schema, table, fk));
            }
        }

        foreach (DifferencePair p in pairs.Where(x =>
            x.Identity.Kind == "Table" && x.Status == DifferenceStatus.Different))
        {
            if (p.SideA is not Table sideA || p.SideB is not Table sideB) { continue; }
            var srcFks = sideA.Constraints.OfType<ForeignKey>()
                .ToDictionary(f => f.Name, StringComparer.Ordinal);
            foreach (ForeignKey t in sideB.Constraints.OfType<ForeignKey>())
            {
                bool stillThere = srcFks.TryGetValue(t.Name, out ForeignKey? s);
                if (!stillThere || !ForeignKeyShapeEqual(t, s!))
                {
                    AddFkDrop(sideA.Schema, sideA.Name, t);
                }
            }
        }

        if (droppedTables.Count > 0 || rebuildTargets.Count > 0)
        {
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))
            {
                if (p.SideB is not Table holder) { continue; }
                // A table that is itself being dropped takes its own FKs with it.
                if (droppedTables.Contains((holder.Schema, holder.Name))) { continue; }
                foreach (ForeignKey fk in holder.Constraints.OfType<ForeignKey>())
                {
                    (string, string) referenced = (fk.ReferencedSchema, fk.ReferencedTable);
                    // Deliberately NOT excluding a holder that is itself a rebuild
                    // target. With two rebuilt tables referencing each other the
                    // exclusion dropped nothing, and whichever DROP TABLE ran first
                    // died on Msg 3726 — the failure depended on the alphabetical
                    // order of the two names. The holder's own DROP TABLE would take
                    // this FK anyway, so an extra DROP CONSTRAINT is harmless, and
                    // the rebuilt table's outbound pass re-adds the full source-side
                    // set afterwards.
                    if (droppedTables.Contains(referenced) || rebuildTargets.Contains(referenced))
                    {
                        AddFkDrop(holder.Schema, holder.Name, fk);
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

        // ── Indexes that block an ALTER / DROP COLUMN.
        //    SQL Server refuses to retype or drop a column while an index covers
        //    it (Msg 5074), so widening an indexed int to bigint — a routine
        //    migration — failed outright. Those indexes are dropped in the same
        //    up-front pass as the foreign keys and force-recreated afterwards:
        //    the normal index delta would emit nothing for an index that is
        //    identical on both sides, leaving production without it.
        //    The recreate set is keyed on (schema, table, index name), NOT on the
        //    index name alone: index names are unique per object_id, not per
        //    database, so IX_TenantId on two different tables is routine. With a
        //    name-only key the SAME global set was handed to every Different
        //    table's EmitIndexDelta, and the unrelated namesake either got a
        //    CREATE for an index that still exists (Msg 1913, whole deploy rolls
        //    back) or had its legitimate DROP skipped (index survives in
        //    production, tool reports success).
        List<(string Schema, string Table, TableIndex Index)> blockingIndexDrops = [];
        HashSet<(string Schema, string Table, string Index)> forcedIndexRecreates = [];
        // The target-side columns each Different table is about to drop or
        // retype, kept so the foreign keys over them can be found below.
        Dictionary<(string Schema, string Table), IReadOnlySet<string>> touchedByTable = [];
        foreach (DifferencePair p in pairs.Where(x =>
            x.Identity.Kind == "Table" && x.Status == DifferenceStatus.Different))
        {
            if (p.SideA is not Table sideA || p.SideB is not Table sideB) { continue; }
            // A rebuilt table drops and re-creates everything anyway.
            if (rebuildTargets.Contains((sideA.Schema, sideA.Name))) { continue; }
            IReadOnlySet<string> touched =
                TableScriptEmitter.ColumnsDroppedOrAltered(sideA, sideB, result.NameComparer);
            if (touched.Count == 0) { continue; }
            touchedByTable[(sideB.Schema, sideB.Name)] = touched;
            foreach (TableIndex ix in sideB.Indexes)
            {
                if (!TableScriptEmitter.IndexDependsOnColumn(ix, touched)) { continue; }
                blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix));
                // Keyed on the SOURCE side, which is what EmitIndexDelta looks
                // up with — the same strings, not merely an equal identity.
                forcedIndexRecreates.Add((sideA.Schema, sideA.Name, ix.Name));
            }
            // A foreign key over one of those columns blocks the ALTER COLUMN
            // (Msg 5074) exactly as an index does. The three feeders above key on
            // the FK's SHAPE or on the referenced TABLE, so a byte-identical FK
            // over a widened column matched none of them: the classic int →
            // bigint of a referenced key died on the deploy.
            foreach (ForeignKey fk in sideB.Constraints.OfType<ForeignKey>())
            {
                if (!fk.Columns.Any(touched.Contains)) { continue; }
                AddFkDrop(sideA.Schema, sideA.Name, fk);
                OrchestrateFkReAdd(sideA, fk.Name);
            }
        }

        // The other half: foreign keys pointing AT a column being retyped. The
        // child's ALTER COLUMN fails (Msg 5074) and the parent cannot drop the
        // key the FK references (Msg 3725), and the holder may be an Identical
        // table that appears in no pass at all — so this walks the full result,
        // like the dropped/rebuilt feeder above.
        if (touchedByTable.Count > 0)
        {
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))
            {
                if (p.SideB is not Table holder) { continue; }
                // A table being dropped takes its own foreign keys with it.
                if (droppedTables.Contains((holder.Schema, holder.Name))) { continue; }
                foreach (ForeignKey fk in holder.Constraints.OfType<ForeignKey>())
                {
                    if (!touchedByTable.TryGetValue(
                            (fk.ReferencedSchema, fk.ReferencedTable),
                            out IReadOnlySet<string>? referencedTouched))
                    {
                        continue;
                    }
                    if (!fk.ReferencedColumns.Any(referencedTouched.Contains)) { continue; }
                    AddFkDrop(holder.Schema, holder.Name, fk);
                    if (p.SideA is Table sourceHolder) { OrchestrateFkReAdd(sourceHolder, fk.Name); }
                }
            }
        }

        bool useTransaction = !options.HasFlag(ComparisonOptions.NoTransactions);
        bool includeHeader = !options.HasFlag(ComparisonOptions.DoNotOutputCommentHeader);
        StringBuilder sb = new();
        DeploymentScriptWriter writer = new(sb, useTransaction);
        writer.WritePreamble(includeHeader);

        // Prologue: Schemas, then Users + Roles — CREATE / ALTER only.
        //    Principal DROPs are in the epilogue: a principal that owns a schema
        //    or an object cannot be dropped before them (Msg 15138).
        //    Sequences, UDTs, TableTypes, Tables, Views, Functions, Procedures,
        //    Triggers, and Synonyms are emitted by the topo-ordered passes below.
        EmitSchemaCreates(writer, result, pairs);
        EmitUsers(writer, pairs, drops: false);
        EmitRoles(writer, pairs, drops: false);

        // Foreign-key DROP pass — before every object drop, because a table
        //     cannot be dropped while any FK still references it, and an
        //     ALTER COLUMN cannot run while an FK constrains the column.
        if (fkDrops.Count > 0)
        {
            StringBuilder fkDropBody = new();
            foreach ((string fromSchema, string fromTable, ForeignKey fk) in fkDrops)
            {
                fkDropBody.Append("ALTER TABLE [").Append(fromSchema).Append("].[").Append(fromTable)
                          .Append("] DROP CONSTRAINT [").Append(fk.Name).AppendLine("];");
            }
            writer.WriteBatch("Dropping foreign keys", fkDropBody.ToString());
        }

        // Index DROP pass for columns about to be dropped or retyped — same
        //     reasoning as the foreign keys above, same position.
        if (blockingIndexDrops.Count > 0)
        {
            StringBuilder ixDropBody = new();
            foreach ((string schema, string table, TableIndex ix) in blockingIndexDrops)
            {
                ixDropBody.AppendLine(_indexEmitter.EmitDrop(schema, table, ix));
            }
            writer.WriteBatch("Dropping indexes on columns being altered", ixDropBody.ToString());
        }

        // DROP pass — objects only in B (removed from source) must be dropped in
        // reverse topological order (dependent-first) so referenced objects are not
        // dropped before their dependents.
        foreach (ObjectIdentity id in createOrder.Reverse())
        {
            DifferencePair pair = pairById[id];
            if (pair.Status != DifferenceStatus.OnlyInB) { continue; }
            string? body = DispatchBuild(id.Kind, pair, result.NameComparer);
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }

        // CREATE pass — all non-drop objects in topological (referenced-first) order.
        foreach (ObjectIdentity id in createOrder)
        {
            DifferencePair pair = pairById[id];
            if (pair.Status == DifferenceStatus.OnlyInB) { continue; }
            // A trigger whose parent table is rebuilt is emitted in full by the
            // rebuild pass below instead. Leaving it here as well would emit it
            // twice, and for a state-only difference it would emit a bare
            // ENABLE TRIGGER against an object DROP TABLE has just destroyed
            // (Msg 4916) — the rebuild re-creates it enabled anyway.
            if (IsTriggerOnRebuiltTable(pair, rebuildTargets)) { continue; }
            string? body = DispatchBuild(id.Kind, pair, result.NameComparer);
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

                // A rebuilt table was DROPped and re-created under a new name, so
                // every one of its indexes went with it — including the ones that
                // are identical on both sides and therefore absent from the delta.
                // Re-create the full source-side set instead of diffing against a
                // target that no longer exists.
                case DifferenceStatus.Different
                    when pair.SideA is Table tReb && rebuildTargets.Contains((tReb.Schema, tReb.Name)):
                    {
                        if (tReb.Indexes.Count == 0) { break; }
                        StringBuilder rebuiltIndexBody = new();
                        foreach (TableIndex ix in tReb.Indexes)
                        {
                            rebuiltIndexBody.AppendLine(_indexEmitter.EmitCreate(tReb.Schema, tReb.Name, ix));
                        }
                        writer.WriteBatch(
                            $"Re-creating indexes on rebuilt [{tReb.Schema}].[{tReb.Name}]",
                            rebuiltIndexBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string indexDelta = EmitIndexDelta(tSrc, tTgt, forcedIndexRecreates);
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

        // Trigger re-create for rebuilt tables — DROP TABLE inside the rebuild
        //    block takes EVERY trigger on the table with it, whatever its
        //    difference status, so this pass re-emits the FULL source-side set
        //    exactly like the index and outbound-FK passes above. Filtering on
        //    Identical covered only the third of the population that no other
        //    pass reaches, and left two holes: a Different trigger the user did
        //    not tick never enters `pairs`, so nothing re-created it and the
        //    deploy reported success with the trigger gone; and a state-only
        //    Different trigger got a bare ENABLE TRIGGER from the CREATE pass
        //    against an object that no longer exists. Emitting as OnlyInA
        //    forces the full CREATE OR ALTER — the source side is authoritative
        //    here, same rationale as the outbound FK re-add.
        //    A trigger the source REMOVED has no SideA and is skipped: DROP
        //    TABLE already took it, and the DROP pass emits its
        //    DROP TRIGGER IF EXISTS beforehand anyway.
        if (rebuildTargets.Count > 0)
        {
            StringBuilder triggerBody = new();
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Trigger"))
            {
                if (p.SideA is not Trigger trg) { continue; }
                if (!rebuildTargets.Contains((trg.ParentSchema, trg.ParentTable))) { continue; }
                string ddl = _triggerEmitter.Emit(
                    new DifferencePair(trg.Identity, DifferenceStatus.OnlyInA, trg, null));
                if (string.IsNullOrWhiteSpace(ddl)) { continue; }
                triggerBody.AppendLine(ddl);
            }
            if (triggerBody.Length > 0)
            {
                writer.WriteBatch("Re-creating triggers on rebuilt tables", triggerBody.ToString());
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
                        .Where(fk => !orchestratedFks.Contains((tNew.Schema, tNew.Name, fk.Name)))];
                        if (fksNew.Count == 0) { break; }
                        StringBuilder fkBody = new();
                        foreach (ForeignKey fk in fksNew)
                        {
                            fkBody.AppendLine(_fkEmitter.EmitAdd(tNew.Schema, tNew.Name, fk));
                        }
                        writer.WriteBatch($"Adding foreign keys on [{tNew.Schema}].[{tNew.Name}]", fkBody.ToString());
                        break;
                    }

                // Same as the index pass: DROP TABLE took the rebuilt table's own
                // (outbound) foreign keys with it, so the delta against the
                // vanished target would skip every FK that was identical on both
                // sides. Re-add the full source-side set.
                case DifferenceStatus.Different
                    when pair.SideA is Table tRebFk && rebuildTargets.Contains((tRebFk.Schema, tRebFk.Name)):
                    {
                        List<ForeignKey> outbound = [.. tRebFk.Constraints.OfType<ForeignKey>()
                            .Where(fk => !orchestratedFks.Contains((tRebFk.Schema, tRebFk.Name, fk.Name)))];
                        if (outbound.Count == 0) { break; }
                        StringBuilder rebuiltFkBody = new();
                        foreach (ForeignKey fk in outbound)
                        {
                            rebuiltFkBody.AppendLine(_fkEmitter.EmitAdd(tRebFk.Schema, tRebFk.Name, fk));
                        }
                        writer.WriteBatch(
                            $"Re-adding foreign keys on rebuilt [{tRebFk.Schema}].[{tRebFk.Name}]",
                            rebuiltFkBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string fkAdds = EmitFkAdds(tSrc, tTgt, orchestratedFks);
                        if (fkAdds.Length > 0)
                        {
                            writer.WriteBatch($"Adding foreign keys on [{tSrc.Schema}].[{tSrc.Name}]", fkAdds);
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

        // Re-add of every foreign key the up-front DROP pass claimed: inbound
        //     FKs to rebuilt tables (#33, M13-PARITY.6) and the ones dropped to
        //     free a column being retyped. Source side is authoritative — these
        //     are the FKs the final shape requires — and the FK delta above
        //     skipped all of them to keep this the single re-add path.
        if (orchestratedFkAdds.Count > 0)
        {
            StringBuilder addBody = new();
            foreach ((string fromSchema, string fromTable, ForeignKey fk) in orchestratedFkAdds)
            {
                addBody.AppendLine(_fkEmitter.EmitAdd(fromSchema, fromTable, fk));
            }
            writer.WriteBatch("Re-adding foreign keys dropped up front", addBody.ToString());
        }

        // Schema drops — after every object pass, so the objects a removed
        //    schema held are already gone.
        EmitSchemaDrops(writer, pairs);

        // Permissions — gated on options. Default (Redgate-parity) skips
        //    permissions entirely; consumers can clear the flag to include
        //    GRANT / REVOKE statements. Before the principal drops, so a REVOKE
        //    still has a principal to name.
        if (!options.HasFlag(ComparisonOptions.IgnorePermissions))
        {
            EmitPermissions(writer, pairs);
        }

        // Principal drops — the very end. `DROP USER` used to sit in the
        //    prologue while `DROP SCHEMA` sat 200 lines later, so a user owning
        //    a target-only schema died on Msg 15138 ("The database principal
        //    owns a schema in the database, and cannot be dropped") — and the
        //    same for a user owning any object the DROP pass removes. Roles go
        //    first: a user that owns a role cannot be dropped either.
        EmitRoles(writer, pairs, drops: true);
        EmitUsers(writer, pairs, drops: true);

        writer.WriteVerdict();
        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="pair"/> is a trigger sitting on a table this
    /// script rebuilds, so the rebuild's own re-create pass owns it.
    /// </summary>
    private static bool IsTriggerOnRebuiltTable(
        DifferencePair pair, IReadOnlySet<(string Schema, string Name)> rebuildTargets) =>
        pair.Identity.Kind == "Trigger"
        && pair.SideA is Trigger t
        && rebuildTargets.Contains((t.ParentSchema, t.ParentTable));

    // ── Phase-label helper ──────────────────────────────────────────────────

    private static string PhaseLabel(DifferencePair pair)
    {
        ObjectIdentity id = pair.Identity;
        // A Schema carries its name in SchemaName and leaves ObjectName empty,
        // so neither of the two normal shapes applies.
        bool schemaScoped = id.Kind is not ("User" or "Role" or "Permission" or "Schema");
        string name = id.Kind is "Schema"
            ? $"[{id.SchemaName}]"
            : schemaScoped ? $"[{id.SchemaName}].[{id.ObjectName}]" : $"[{id.ObjectName}]";
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

    private static string? BuildOneTable(DifferencePair pair, StringComparer names)
    {
        string ddl = new TableScriptEmitter(names).Emit(pair);
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

    private string? DispatchBuild(string kind, DifferencePair pair, StringComparer names) =>
        kind switch
        {
            "Sequence" => BuildOneSequence(pair),
            "UserDefinedType" => BuildOneUserDefinedType(pair),
            "TableType" => BuildOneTableTypeUdt(pair),
            "Table" => BuildOneTable(pair, names),
            "View" => BuildOneView(pair),
            "Function" => BuildOneFunction(pair),
            "Procedure" => BuildOneProcedure(pair),
            "Trigger" => BuildOneTrigger(pair),
            "Synonym" => BuildOneSynonym(pair),
            _ => null,
        };

    // ── Users / Roles / Permissions emitters ───────────────────────────────

    /// <summary>
    /// Emits <c>CREATE SCHEMA</c> for the source-only schemas the selection
    /// needs, one per batch.
    /// </summary>
    /// <remarks>
    /// Driven by the FULL result, not by the selection: a partial selection
    /// almost never contains the Schema row itself. Ticking
    /// <c>vendite.Ordine</c> alone made the script open with
    /// <c>CREATE TABLE [vendite].[Ordine]</c> against a target with no
    /// <c>vendite</c> schema — Msg 2760, exactly the failure the Schema kind
    /// was added to prevent. Promotion is limited to the schemas the selected
    /// objects live in, so nothing the user did not ask for is created.
    /// <para>
    /// One batch each is required, not stylistic: <c>CREATE SCHEMA</c> must be
    /// the first statement in its batch, so concatenating several into one body
    /// would make every statement after the first a syntax error.
    /// </para>
    /// </remarks>
    private static void EmitSchemaCreates(
        DeploymentScriptWriter writer,
        ComparisonResult result,
        IReadOnlyList<DifferencePair> pairs)
    {
        HashSet<string> needed = new(StringComparer.OrdinalIgnoreCase);
        foreach (DifferencePair p in pairs)
        {
            // An object being dropped needs no schema created for it.
            if (p.Identity.Kind != "Schema" && p.Status == DifferenceStatus.OnlyInB) { continue; }
            needed.Add(p.Identity.SchemaName);
        }

        foreach (DifferencePair pair in result.Differences
            .Where(p => p.Identity.Kind == "Schema" && p.Status == DifferenceStatus.OnlyInA)
            .Where(p => needed.Contains(p.Identity.SchemaName))
            .OrderBy(p => p.Identity.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.SideA is not Schema s) { continue; }
            writer.WriteBatch(PhaseLabel(pair), SchemaScriptEmitter.EmitCreate(s));
        }
    }

    /// <summary>
    /// Emits <c>DROP SCHEMA</c> for target-only schemas, one per batch.
    /// </summary>
    /// <remarks>
    /// Driven by the SELECTION, unlike <see cref="EmitSchemaCreates"/>, and
    /// deliberately so: creating a missing schema is a prerequisite of what the
    /// user asked for, while dropping one destroys something they did not tick.
    /// Ticking a schema row without its contents still fails loudly on Msg 3729
    /// (SQL Server refuses to drop a schema that owns objects) — the tool does
    /// not guess which of the two the user meant.
    /// </remarks>
    private static void EmitSchemaDrops(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Schema" && p.Status == DifferenceStatus.OnlyInB)
            .OrderBy(p => p.Identity.SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.SideB is not Schema s) { continue; }
            writer.WriteBatch(PhaseLabel(pair), SchemaScriptEmitter.EmitDrop(s));
        }
    }

    // `drops: true` emits only the DROP USER half, which the epilogue owns;
    // `false` the CREATE / ALTER half, which the prologue owns. One method
    // rather than two so the two halves cannot drift apart.
    private void EmitUsers(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs, bool drops)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "User")
            .Where(p => IsPrincipalDrop(p) == drops)
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

    private static bool IsPrincipalDrop(DifferencePair pair) =>
        pair.Status == DifferenceStatus.OnlyInB;

    private static bool DefaultSchemaIsOnlyDifference(DatabaseUser a, DatabaseUser b) =>
        string.Equals(a.TypeCode, b.TypeCode, StringComparison.Ordinal)
        && string.Equals(a.LoginName, b.LoginName, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(a.DefaultSchema, b.DefaultSchema, StringComparison.OrdinalIgnoreCase);

    // Same split as EmitUsers.
    private void EmitRoles(DeploymentScriptWriter writer, IReadOnlyList<DifferencePair> pairs, bool drops)
    {
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Role")
            .Where(p => IsPrincipalDrop(p) == drops)
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
    /// Emits the index delta for a Different table: indexes present only on the
    /// target side are dropped; indexes present only on the source are created;
    /// indexes whose shape differs (key columns / uniqueness / clustering /
    /// filter) are dropped + recreated.
    /// </summary>
    /// <param name="src">Source-side table.</param>
    /// <param name="tgt">Target-side table.</param>
    /// <param name="alreadyDropped">
    /// The indexes the up-front pass already dropped to free a column being
    /// retyped or removed, keyed <c>(schema, table, index name)</c>. Their DROP
    /// is skipped here (it has happened) and their CREATE is FORCED even when the
    /// two sides match, because otherwise the delta emits nothing and the deploy
    /// silently ends without the index. The table has to be part of the key: the
    /// set is global to the script and index names are only unique per table.
    /// </param>
    private string EmitIndexDelta(
        Table src, Table tgt, IReadOnlySet<(string Schema, string Table, string Index)> alreadyDropped)
    {
        StringBuilder sb = new();
        var srcByName =
            src.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);
        var tgtByName =
            tgt.Indexes.ToDictionary(i => i.Name, StringComparer.Ordinal);

        // DROPs first so a rename-shaped change frees the slot before CREATE.
        foreach (TableIndex t in tgt.Indexes)
        {
            if (alreadyDropped.Contains((src.Schema, src.Name, t.Name))) { continue; }
            bool stillThere = srcByName.TryGetValue(t.Name, out TableIndex? s);
            bool shapeChanged = stillThere && !IndexShapeEqual(t, s!);
            if (!stillThere || shapeChanged)
            {
                sb.AppendLine(_indexEmitter.EmitDrop(src.Schema, src.Name, t));
            }
        }
        foreach (TableIndex s in src.Indexes)
        {
            bool mustRestore = alreadyDropped.Contains((src.Schema, src.Name, s.Name));
            bool existsOnTarget = tgtByName.TryGetValue(s.Name, out TableIndex? t);
            bool shapeChanged = existsOnTarget && !IndexShapeEqual(s, t!);
            if (existsOnTarget && !shapeChanged && !mustRestore) { continue; }
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
    /// Emits the ADD half of a table's foreign-key delta: FKs present on the
    /// source that the target lacks or whose shape changed.
    /// </summary>
    /// <param name="src">Source-side table.</param>
    /// <param name="tgt">Target-side table.</param>
    /// <param name="skipKeys">
    /// Foreign keys the rebuild orchestrator already owns (dropped up front,
    /// re-added by the inbound pass), keyed <c>(schema, table, name)</c> —
    /// constraint names are unique per schema, and this set spans the whole
    /// script, so the holder table has to be part of the key or an unrelated
    /// namesake on another table loses its ADD.
    /// </param>
    private string EmitFkAdds(
        Table src, Table tgt, IReadOnlySet<(string Schema, string Table, string Fk)> skipKeys)
    {
        StringBuilder sb = new();
        var srcFks =
            src.Constraints.OfType<ForeignKey>().ToDictionary(fk => fk.Name, StringComparer.Ordinal);
        var tgtFks =
            tgt.Constraints.OfType<ForeignKey>().ToDictionary(fk => fk.Name, StringComparer.Ordinal);

        foreach (ForeignKey s in srcFks.Values)
        {
            if (skipKeys.Contains((src.Schema, src.Name, s.Name))) { continue; }
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

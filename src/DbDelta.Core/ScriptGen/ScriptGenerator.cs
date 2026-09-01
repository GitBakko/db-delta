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
/// <para>
/// <b>Every DROP this generator emits is BARE — no <c>IF EXISTS</c>, no
/// existence probe — and that is a decision, not an omission.</b> A DROP that
/// fails says something true: the target is no longer the one the comparison
/// read, and continuing would apply a delta computed against a database that
/// has moved. Owner decision, 2026-09-01. Before it, the four module kinds
/// (view, function, procedure, trigger) carried <c>IF EXISTS</c> and the other
/// nine did not, so a second execution of the same script cleared the module
/// drops and then died on the first table with Msg 3701 — the one outcome
/// neither policy wants, because it leaves the target half-way. If you are
/// about to add a guard back, that is the case to answer first.
/// </para>
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
    /// <param name="result">The comparison to emit.</param>
    /// <param name="selection">
    /// The subset to emit DDL for. Null means every difference. The unfiltered
    /// <paramref name="result"/> is still scanned by the passes that have to see
    /// Identical objects.
    /// </param>
    /// <param name="options">Emission options.</param>
    /// <param name="dependencies">
    /// SOURCE-side dependency edges, which order the CREATE pass.
    /// </param>
    /// <param name="dropDependencies">
    /// TARGET-side dependency edges, which order the DROP pass. Null or empty
    /// falls back to reversing the create order — which orders nothing at all
    /// for removed objects, since an object that exists only on the target
    /// appears in no source-side edge, and leaves the pass on the inverted kind
    /// rank. That is enough for the common case and not enough for a
    /// schemabound view over a schemabound view, where the wrong order is
    /// Msg 3729.
    /// </param>
    /// <param name="backfillDefaults">
    /// Values, keyed <c>(schema, table, column)</c>, to seed the rows that
    /// already exist when a run adds a NOT NULL column the source declares
    /// without a default — see <see cref="BackfillPreflight"/>. Each rides in on
    /// a named throwaway constraint that the next statement drops, so the
    /// column ends up exactly as the source declares it. A column with no entry
    /// is emitted unchanged and fails on a populated table with Msg 4901, which
    /// is the honest outcome when nobody has chosen a value.
    /// </param>
    public string Generate(
        ComparisonResult result,
        IEnumerable<DifferencePair>? selection = null,
        ComparisonOptions options = ComparisonOptions.Default,
        IReadOnlyList<DependencyEdge>? dependencies = null,
        IReadOnlyList<DependencyEdge>? dropDependencies = null,
        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfillDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        dependencies ??= [];
        // Same normalisation as above, and the analyzer needs it to see the
        // guard below is total: an empty list takes the same branch as null
        // at every later probe.
        dropDependencies ??= [];
        // Every (schema, table[, name]) key below is compared the way the
        // target resolves names. Value-tuple equality is ordinal, so once the
        // engine began pairing dbo.Clienti with dbo.CLIENTI, a set filled from
        // one side and probed from the other silently missed. See NameKey.
        IEqualityComparer<(string Schema, string Name)> pairKey =
            NameKey.Pair(result.NameComparer);
        IEqualityComparer<(string Schema, string Table, string Name)> tripleKey =
            NameKey.Triple(result.NameComparer);
        List<DifferencePair> pairs = [.. (selection ?? result.Differences)
            .Where(p => p.Status != DifferenceStatus.Identical)];

        // M13-PARITY.6 #33 — identify identity-rebuild targets. Inbound FKs
        // pointing at any of these tables must be dropped *before* the
        // rebuild block (so DROP TABLE succeeds) and re-added *after*.
        // Looking up via `result.Differences` (unfiltered) covers FKs held
        // by Identical tables, which never appear in <c>pairs</c>.
        HashSet<(string Schema, string Name)> rebuildTargets = new(pairKey);
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "Table"))
        {
            if (pair.Status == DifferenceStatus.Different
                && pair.SideA is Table src && pair.SideB is Table tgt
                && TableScriptEmitter.RequiresFullRebuild(src, tgt, result.NameComparer))
            {
                rebuildTargets.Add((src.Schema, src.Name));
            }
        }

        RefuseRebuildsBlockedBySchemabinding(rebuildTargets, pairs, dropDependencies, pairKey);
        RefuseTypeDropsBlockedByABinder(result, pairs, dropDependencies, pairKey);

        // The tables this run actually reshapes. A table outside the selection
        // keeps whatever the target already holds, which is what decides whose
        // foreign-key shape the re-add pass may restore — see ClaimFkReAdd.
        HashSet<(string Schema, string Name)> selectedTables = new(
            pairs.Where(p => p.Identity.Kind == "Table")
                 .Select(p => (p.Identity.SchemaName, p.Identity.ObjectName)),
            pairKey);

        // Foreign keys the up-front DROP pass takes ownership of: dropped before
        // anything else and re-added by a single late pass. Two collections that
        // always move together — the list says what to re-add, the key set makes
        // the normal FK delta stand back so nothing is added twice.
        List<(string FromSchema, string FromTable, ForeignKey FK)> orchestratedFkAdds = [];
        // Keyed on the HOLDER table, same reason as fkDropKeys below: this set
        // makes the FK pass SKIP a foreign key, so a bare name meant an unrelated
        // namesake on another table lost its legitimate ADD and the deploy left
        // that table with no foreign key at all, silently.
        HashSet<(string Schema, string Table, string Fk)> orchestratedFks = new(tripleKey);

        // Hands one foreign key to that pair. The source side is authoritative:
        // it carries the shape the final schema wants. An FK the source no
        // longer has is claimed but never re-added, which is the point — the
        // claim is what stops some other pass from putting it back.
        void OrchestrateFkReAdd(Table sourceHolder, ForeignKey fk)
        {
            // Found by PAIRING, not by name: for a key SQL Server named itself
            // the two sides carry different hashes, and looking the source's up
            // under the target's name finds nothing — the claim would then be
            // keyed on a name the skip check never asks about, and the key
            // would be added twice.
            ForeignKey? sourceFk = MatchFk(fk, sourceHolder, result.NameComparer);
            // The key set is read with the SOURCE's name (see EmitFkAdds), so
            // that is what has to go in when there is one.
            if (!orchestratedFks.Add((sourceHolder.Schema, sourceHolder.Name, (sourceFk ?? fk).Name)))
            {
                return;
            }
            if (sourceFk is not null)
            {
                orchestratedFkAdds.Add((sourceHolder.Schema, sourceHolder.Name, sourceFk));
            }
        }

        // Same pair, but restoring the TARGET's own foreign key verbatim. Used
        // when the holder has no source side at all, where "the source is
        // authoritative" has nothing to say and dropping without restoring
        // would leave a surviving table without its constraint.
        void ClaimTargetSideFkReAdd(Table targetHolder, ForeignKey fk)
        {
            if (orchestratedFks.Add((targetHolder.Schema, targetHolder.Name, fk.Name)))
            {
                orchestratedFkAdds.Add((targetHolder.Schema, targetHolder.Name, fk));
            }
        }

        // Picks between the two for one holder. The source side is authoritative
        // only for a table the run actually reshapes — that is the table whose
        // final shape the user asked for. For a holder OUTSIDE the selection the
        // script changes nothing else about it, so restoring the source's column
        // list or ON DELETE would apply a change nobody ticked, under a success
        // verdict; that constraint has to come back exactly as it was found. A
        // holder with no source side has no other shape to restore anyway.
        void ClaimFkReAdd(DifferencePair holderPair, Table targetHolder, ForeignKey targetFk)
        {
            if (holderPair.SideA is Table sourceHolder
                && selectedTables.Contains((targetHolder.Schema, targetHolder.Name)))
            {
                OrchestrateFkReAdd(sourceHolder, targetFk);
                return;
            }
            ClaimTargetSideFkReAdd(targetHolder, targetFk);
        }

        // ── Every foreign-key DROP, collected up front.
        //    Dropping a foreign key has no ordering prerequisite of its own, so
        //    doing all of them before anything else is always safe — and it is
        //    what makes the later DROP TABLE possible at all. Previously the FK
        //    drops lived in the FK pass at the very END of the script, so a
        //    table removed from the source was dropped while another table still
        //    referenced it: Msg 3726, every time. Four feeders, matching the
        //    class doc:
        //      (a) target-side FKs that a Different table lost or reshaped;
        //      (b) FKs held by ANY table — Identical ones included, which never
        //          appear in `pairs` — pointing at a table about to be DROPped
        //          or rebuilt (#33); (b) used to be two passes, and folding them
        //          together orders the rebuild half earlier, which is safer;
        //      (c) a Different table's own FKs over a column it is about to drop
        //          or retype;
        //      (d) FKs pointing AT such a column, again from anywhere.
        HashSet<(string Schema, string Name)> droppedTables = new(
            pairs
                .Where(p => p.Identity.Kind == "Table" && p.Status == DifferenceStatus.OnlyInB)
                .Select(p => p.SideB)
                .OfType<Table>()
                .Select(t => (t.Schema, t.Name)),
            pairKey);

        // Inbound foreign keys to a rebuilt table are claimed for the late
        // re-add pass here, before the drop feeders below take them. Walks the
        // FULL result because the holder may be an Identical table that appears
        // in no other pass, but only ever over the TARGET side: a source-only
        // table left unticked is never created, and an ALTER TABLE … ADD
        // CONSTRAINT against it is Msg 4902 halfway through the deploy. A holder
        // the script DROPs is skipped for the same reason — there is no table
        // left to carry the key.
        if (rebuildTargets.Count > 0)
        {
            foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))
            {
                if (p.SideB is not Table holder) { continue; }
                if (droppedTables.Contains((holder.Schema, holder.Name))) { continue; }
                // A rebuilt table's own outbound FKs are re-added in full by the
                // rebuild branch of the FK pass, not from here.
                if (rebuildTargets.Contains((holder.Schema, holder.Name))) { continue; }
                foreach (ForeignKey fk in holder.Constraints.OfType<ForeignKey>())
                {
                    if (rebuildTargets.Contains((fk.ReferencedSchema, fk.ReferencedTable)))
                    {
                        OrchestrateFkReAdd(holder, fk);
                    }
                }
            }
        }

        List<(string FromSchema, string FromTable, ForeignKey FK)> fkDrops = [];
        HashSet<(string Schema, string Table, string Fk)> fkDropKeys = new(tripleKey);
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
            foreach (ForeignKey t in sideB.Constraints.OfType<ForeignKey>())
            {
                ForeignKey? s = MatchFk(t, sideA, result.NameComparer);
                if (s is null || !ForeignKeyShapeEqual(t, s, result.NameComparer))
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
                foreach (ForeignKey fk in holder.Constraints.OfType<ForeignKey>())
                {
                    (string, string) referenced = (fk.ReferencedSchema, fk.ReferencedTable);
                    // Deliberately NOT excluding a holder that is itself being
                    // dropped or rebuilt. "Its own DROP TABLE takes the FK with
                    // it" is true only when the holder is dropped FIRST, which
                    // is not something this feeder can assume: S3 gave the drop
                    // pass a real order, but only when the caller hands over
                    // target-side edges — without them it still falls back to
                    // reversed kind rank then reverse-alphabetically, which held
                    // for Currency/Invoice and failed on Msg 3726 for
                    // Zone/Alpha, making survival a function of the two names.
                    // Two rebuilt tables referencing each other are worse still:
                    // no edge list orders a cycle. An extra DROP CONSTRAINT for
                    // a table that is about to disappear costs one statement;
                    // getting it wrong costs the deploy. The rebuilt table's
                    // outbound pass re-adds the full source-side set afterwards.
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
        ObjectIdentity[] topoIdentities = [.. topoPairs.Select(p => p.Identity)];
        IReadOnlyList<ObjectIdentity> createOrder = new DependencyResolver()
            .Order(topoIdentities, dependencies);

        // The DROP pass needs its OWN order, resolved from the target's edges.
        // Reversing createOrder cannot work: every object being dropped was
        // removed from the source, so it appears in no source-side edge, gets
        // in-degree zero, and lands wherever the inverted kind rank puts it.
        // With no target edges the fallback is that same reversal — no worse
        // than before, and correct for everything except a schemabound chain.
        IReadOnlyList<ObjectIdentity> dropOrder = ResolveDropOrder(
            topoIdentities, dropDependencies, createOrder);
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
        HashSet<(string Schema, string Table, string Index)> forcedIndexRecreates = new(tripleKey);
        // The target-side columns each Different table is about to drop or
        // retype, kept so the foreign keys over them can be found below.
        Dictionary<(string Schema, string Table), IReadOnlySet<string>> touchedByTable = new(pairKey);
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
                ClaimFkReAdd(p, sideB, fk);
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
                // A table the SCRIPT drops takes its own foreign keys with it —
                // and the DROP pass runs before every ALTER COLUMN this feeder
                // protects, so unlike the sibling feeder above no ordering
                // assumption is involved. `droppedTables` is built from the
                // selection, so a target-only table the user did not tick is
                // deliberately not in it: that table survives the deploy and its
                // key has to come back.
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
                    // Without a re-add the constraint is dropped, never put
                    // back, and the script reports success on a table that has
                    // quietly lost its referential integrity.
                    ClaimFkReAdd(p, holder, fk);
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
                fkDropBody.Append("ALTER TABLE ").Append(Sql.Q(fromSchema, fromTable))
                          .Append(" DROP CONSTRAINT ").Append(Sql.Q(fk.Name)).AppendLine(";");
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
        foreach (ObjectIdentity id in dropOrder)
        {
            DifferencePair pair = pairById[id];
            if (pair.Status != DifferenceStatus.OnlyInB) { continue; }
            string? body = DispatchBuild(id.Kind, pair, result.NameComparer, backfillDefaults);
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
            string? body = DispatchBuild(id.Kind, pair, result.NameComparer, backfillDefaults);
            if (!string.IsNullOrWhiteSpace(body)) { writer.WriteBatch(PhaseLabel(pair), body); }
        }

        // Indexes on VIEWS, in a batch of their own. Not appended to the view's
        // DDL: CREATE VIEW has to be the first statement of its batch, so a
        // CREATE INDEX after it in the same one is "Incorrect syntax near the
        // keyword 'CREATE'" — the server reads it as part of the view.
        foreach (ObjectIdentity id in createOrder.Where(i => i.Kind == "View"))
        {
            DifferencePair pair = pairById[id];
            if (pair.Status == DifferenceStatus.OnlyInB) { continue; }
            string viewIndexes = EmitViewIndexDelta(pair, result.NameComparer);
            if (!string.IsNullOrWhiteSpace(viewIndexes))
            {
                writer.WriteBatch($"Indexes on {Sql.Q(id.SchemaName, id.ObjectName)}", viewIndexes);
            }
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
                        writer.WriteBatch($"Creating indexes on {Sql.Q(tNew.Schema, tNew.Name)}", indexBody.ToString());
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
                            $"Re-creating indexes on rebuilt {Sql.Q(tReb.Schema, tReb.Name)}",
                            rebuiltIndexBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string indexDelta = EmitIndexDelta(
                            tSrc, tTgt, forcedIndexRecreates, result.NameComparer);
                        if (indexDelta.Length > 0)
                        {
                            writer.WriteBatch($"Updating indexes on {Sql.Q(tSrc.Schema, tSrc.Name)}", indexDelta);
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

            // A rebuilt table leaves every non-schemabound view and TVF that
            // reads it holding the column list it cached at CREATE time — and
            // still answering SELECTs, so nothing looks wrong. See
            // ModuleRefresh: the only SILENT failure on this path, unlike the
            // schemabound case, which refuses the DROP TABLE loudly.
            if (ModuleRefresh.Emit(rebuildTargets, dependencies, result.NameComparer) is { Length: > 0 } refreshBody)
            {
                writer.WriteBatch("Refreshing modules over rebuilt tables", refreshBody);
            }
        }

        // Foreign keys — emitted last so referenced tables already exist; this
        //    also breaks FK cycles. OnlyInA: add every FK. Different: diff
        //    against target — drop removed/changed FKs, add new/changed FKs
        //    (M8 polish). Foreign keys already claimed by the up-front drop pass
        //    are skipped here so the late re-add pass stays their single owner
        //    and nothing is dropped or added twice. That claim started as the
        //    rebuild orchestrator's inbound set (M13-PARITY.6 #33) and S2 widened
        //    it to every FK dropped to free a column being retyped, so it is no
        //    longer about rebuilds at all.
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
                        writer.WriteBatch($"Adding foreign keys on {Sql.Q(tNew.Schema, tNew.Name)}", fkBody.ToString());
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
                            $"Re-adding foreign keys on rebuilt {Sql.Q(tRebFk.Schema, tRebFk.Name)}",
                            rebuiltFkBody.ToString());
                        break;
                    }

                case DifferenceStatus.Different when pair.SideA is Table tSrc && pair.SideB is Table tTgt:
                    {
                        string fkAdds = EmitFkAdds(
                            tSrc, tTgt, orchestratedFks, result.NameComparer);
                        if (fkAdds.Length > 0)
                        {
                            writer.WriteBatch($"Adding foreign keys on {Sql.Q(tSrc.Schema, tSrc.Name)}", fkAdds);
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
    /// The order the DROP pass walks: the target's own topological order,
    /// reversed, falling back to the reversed CREATE order.
    /// </summary>
    /// <remarks>
    /// The target's edges can hold a cycle the source's cannot: a function F
    /// reads table T while T has a computed column calling F — legal on a live
    /// server, since the two were created in an order that no longer shows in
    /// the catalog. Both being removed, they appear in no source-side edge, so
    /// the CREATE resolver never sees the cycle and the script used to be
    /// emitted fine. Letting the throw escape would turn ordering the drop pass
    /// (a strict improvement) into a hard failure on input that worked before.
    /// The fallback is exactly what a caller passing no target edges gets.
    /// </remarks>
    private static IReadOnlyList<ObjectIdentity> ResolveDropOrder(
        ObjectIdentity[] topoIdentities,
        IReadOnlyList<DependencyEdge>? dropDependencies,
        IReadOnlyList<ObjectIdentity> createOrder)
    {
        if (dropDependencies is { Count: > 0 })
        {
            try
            {
                return [.. new DependencyResolver().Order(topoIdentities, dropDependencies).Reverse()];
            }
            catch (DependencyCycleException)
            {
                // Fall through to the reversal below.
            }
        }
        return [.. createOrder.Reverse()];
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

    /// <summary>
    /// Refuses, before a line of SQL is written, a rebuild the server would
    /// refuse halfway through — and names the module responsible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs HERE and not in <c>TableScriptEmitter.EmitRebuild</c> for a
    /// reason that is not style: <c>EmitRebuild</c> is private, receives
    /// <c>(newT, oldT, names, backfillDefaults)</c> and has no way to see either
    /// the dependency edges or what this run is about to drop. This method is
    /// the one place holding all three.
    /// </para>
    /// <para>
    /// The edges are the TARGET's (<c>dropDependencies</c>), never the source's:
    /// the object the DROP TABLE runs into is the one the target has today.
    /// </para>
    /// <para>
    /// <b>Two exclusions, and without either this refuses tables that deploy
    /// perfectly well today.</b> Both were measured on
    /// <c>mssql/server:2022-latest</c>, and the parity fixture cannot see the
    /// difference — no scenario in it puts a schemabound module over a table
    /// that gets rebuilt, so a wrong predicate would leave it green.
    /// </para>
    /// <para>
    /// (1) A SELF reference is not a binder. A plain <c>CHECK (Amt &gt; 0)</c>
    /// and a PERSISTED computed column each produce a row with
    /// <c>is_schema_bound_reference = 1</c> whose referencing entity is the
    /// table itself — <c>DependencyReader</c> manufactures it, by attributing a
    /// C/D constraint's references to its parent — and both tables drop without
    /// complaint.
    /// </para>
    /// <para>
    /// (2) A binder this very script DROPS first is not a binder either.
    /// Dropping the schemabound modules and then the table succeeds, measured;
    /// the DROP pass runs before the CREATE pass that carries the rebuild, so an
    /// <c>OnlyInB</c> module is already gone by then. Note the shape this does
    /// NOT cover, because it is the failing one and not an exclusion: a module
    /// present on BOTH sides. <c>Identical</c> is filtered out of
    /// <c>pairs</c> and never enters the script; <c>Different</c> is emitted as
    /// <c>CREATE OR ALTER</c> AFTER the table, since <c>KindRank</c> puts Table
    /// before View. Neither is dropped, and that is exactly why the rebuild
    /// dies.
    /// </para>
    /// </remarks>
    private static void RefuseRebuildsBlockedBySchemabinding(
        HashSet<(string Schema, string Name)> rebuildTargets,
        List<DifferencePair> pairs,
        IReadOnlyList<DependencyEdge> dropDependencies,
        IEqualityComparer<(string Schema, string Name)> pairKey)
    {
        if (rebuildTargets.Count == 0 || dropDependencies.Count == 0) { return; }

        // Names are unique per schema in sys.objects, so a (schema, name) key
        // cannot confuse a view with the table it binds.
        HashSet<(string Schema, string Name)> droppedFirst = new(
            pairs.Where(p => p.Status == DifferenceStatus.OnlyInB)
                 .Select(p => (p.Identity.SchemaName, p.Identity.ObjectName)),
            pairKey);

        foreach (DependencyEdge edge in dropDependencies)
        {
            if (!edge.IsSchemaBound) { continue; }

            (string, string) binder = (edge.Dependent.SchemaName, edge.Dependent.ObjectName);
            (string, string) bound = (edge.Referenced.SchemaName, edge.Referenced.ObjectName);

            if (pairKey.Equals(binder, bound)) { continue; }        // exclusion (1)
            if (droppedFirst.Contains(binder)) { continue; }        // exclusion (2)
            if (!rebuildTargets.Contains(bound)) { continue; }

            throw new SchemaboundRebuildException(edge.Referenced, edge.Dependent);
        }
    }

    /// <summary>
    /// Refuses, naming the object responsible, a <c>DROP TYPE</c> the server
    /// would refuse with Msg 3732.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No ordering can save this one, and that is why it is a refusal rather
    /// than a sort.</b> A changed alias type is emitted as
    /// <c>EmitDrop(tgtU) + EmitCreate(srcU)</c> in ONE indivisible body at the
    /// type's topological slot, and <c>UserDefinedType</c> ranks before every
    /// kind that can bind it — so even a binder this script does emit is emitted
    /// after the DROP that already failed. A binder that is <c>Identical</c> is
    /// not emitted at all: it is filtered out of <c>pairs</c> before generation.
    /// </para>
    /// <para>
    /// <b>Six binder forms, and they need TWO sources</b> — measured on
    /// <c>mssql/server:2022-latest</c>, one type per form so each DROP was
    /// isolated. A table column, a sequence and a table type's column are
    /// DECLARATIONS: they appear in no dependency view, and are read off the
    /// model here. A procedure parameter, a function parameter and a function's
    /// RETURN type appear only in <c>sys.sql_expression_dependencies</c>, whose
    /// <c>referenced_class = 6</c> rows <c>DependencyReader</c> used to read and
    /// throw away. Covering one source and not the other would leave three of
    /// the six silently unguarded.
    /// </para>
    /// <para>
    /// The model side is scanned over <c>result.Differences</c> UNFILTERED, not
    /// over <c>pairs</c>: a binder that compares <c>Identical</c> is precisely
    /// the case this exists for, and <c>pairs</c> has already dropped it.
    /// </para>
    /// <para>
    /// One exclusion, and it is the same as the schemabound guard's: a binder
    /// this very script DROPS first is not a binder, because the DROP pass runs
    /// before the CREATE pass that carries the type's drop-and-recreate.
    /// </para>
    /// </remarks>
    private static void RefuseTypeDropsBlockedByABinder(
        ComparisonResult result,
        List<DifferencePair> pairs,
        IReadOnlyList<DependencyEdge> dropDependencies,
        IEqualityComparer<(string Schema, string Name)> pairKey)
    {
        // Types this run drops: changed (drop + re-create) or removed outright.
        HashSet<(string Schema, string Name)> droppedTypes = new(pairKey);
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "UserDefinedType"
                                                      && p.Status != DifferenceStatus.OnlyInA))
        {
            droppedTypes.Add((pair.Identity.SchemaName, pair.Identity.ObjectName));
        }
        if (droppedTypes.Count == 0) { return; }

        HashSet<(string Schema, string Name)> droppedFirst = new(
            pairs.Where(p => p.Status == DifferenceStatus.OnlyInB)
                 .Select(p => (p.Identity.SchemaName, p.Identity.ObjectName)),
            pairKey);

        foreach ((ObjectIdentity binder, string typeSchema, string typeName) in
                 TargetSideTypeUsers(result))
        {
            if (droppedFirst.Contains((binder.SchemaName, binder.ObjectName))) { continue; }
            if (!droppedTypes.Contains((typeSchema, typeName))) { continue; }
            throw new BoundTypeDropException(
                new ObjectIdentity(typeSchema, typeName, "UserDefinedType"), binder);
        }

        foreach (DependencyEdge edge in dropDependencies)
        {
            if (edge.Referenced.Kind != "UserDefinedType") { continue; }
            if (droppedFirst.Contains((edge.Dependent.SchemaName, edge.Dependent.ObjectName))) { continue; }
            if (!droppedTypes.Contains((edge.Referenced.SchemaName, edge.Referenced.ObjectName))) { continue; }
            throw new BoundTypeDropException(edge.Referenced, edge.Dependent);
        }
    }

    /// <summary>
    /// Every object the TARGET declares with an alias type, and the type it
    /// names — the three binder forms no dependency view records.
    /// </summary>
    /// <remarks>
    /// <c>TypeSchema</c> is required, not the owning object's schema: an alias
    /// lives where it was created, which need not be where the thing using it
    /// lives. A model that never said carries null, and then nothing is claimed
    /// about it rather than the wrong thing.
    /// </remarks>
    private static IEnumerable<(ObjectIdentity Binder, string TypeSchema, string TypeName)>
        TargetSideTypeUsers(ComparisonResult result)
    {
        foreach (DifferencePair pair in result.Differences)
        {
            switch (pair.SideB)
            {
                case Table t:
                    foreach (Column c in t.Columns.Where(c => c.IsUserDefinedType && c.TypeSchema is not null))
                    {
                        yield return (t.Identity, c.TypeSchema!, c.DataType);
                    }
                    break;
                case TableTypeUdt tt:
                    foreach (Column c in tt.Columns.Where(c => c.IsUserDefinedType && c.TypeSchema is not null))
                    {
                        yield return (tt.Identity, c.TypeSchema!, c.DataType);
                    }
                    break;
                case Sequence s when s.TypeSchema is not null:
                    yield return (s.Identity, s.TypeSchema, s.DataType);
                    break;
                default:
                    break;
            }
        }
    }

    // ── Phase-label helper ──────────────────────────────────────────────────

    private static string PhaseLabel(DifferencePair pair)
    {
        ObjectIdentity id = pair.Identity;
        // A Schema carries its name in SchemaName and leaves ObjectName empty,
        // so neither of the two normal shapes applies.
        bool schemaScoped = id.Kind is not ("User" or "Role" or "Permission" or "Schema");
        string name = id.Kind is "Schema"
            ? $"{Sql.Q(id.SchemaName)}"
            : schemaScoped ? $"{Sql.Q(id.SchemaName, id.ObjectName)}" : $"{Sql.Q(id.ObjectName)}";
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

    private static string? BuildOneTable(
        DifferencePair pair,
        StringComparer names,
        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfillDefaults)
    {
        string ddl = new TableScriptEmitter(names, backfillDefaults).Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    private string? BuildOneView(DifferencePair pair)
    {
        string ddl = _viewEmitter.Emit(pair);
        return string.IsNullOrWhiteSpace(ddl) ? null : ddl;
    }

    /// <summary>
    /// The CREATE / DROP INDEX statements an indexed view needs, after its own
    /// DDL. An index on a view is what makes it a stored result set rather than
    /// a query, so it travels with the view or the view arrives as something
    /// else entirely.
    /// </summary>
    /// <remarks>
    /// A view the script DROPs gets nothing: <c>DROP VIEW</c> takes its indexes
    /// with it, and a DROP INDEX against an object about to disappear is an
    /// error at worst and noise at best. Same order as the table delta — drops
    /// before creates, so a reshaped index frees its name before it is written
    /// again.
    /// </remarks>
    private string EmitViewIndexDelta(DifferencePair pair, StringComparer names)
    {
        if (pair.SideA is not View src) { return string.Empty; }

        IReadOnlyList<TableIndex> before = pair.SideB is View tgt ? tgt.Indexes : [];
        StringBuilder sb = new();
        foreach (TableIndex t in before)
        {
            TableIndex? s = src.Indexes.FirstOrDefault(i => names.Equals(i.Name, t.Name));
            if (s is null || !IndexShapeEqual(t, s, names))
            {
                sb.AppendLine(_indexEmitter.EmitDrop(src.Schema, src.Name, t));
            }
        }
        foreach (TableIndex s in src.Indexes)
        {
            TableIndex? t = before.FirstOrDefault(i => names.Equals(i.Name, s.Name));
            if (t is not null && IndexShapeEqual(s, t, names)) { continue; }
            sb.AppendLine(_indexEmitter.EmitCreate(src.Schema, src.Name, s));
        }
        return sb.ToString();
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

    private string? DispatchBuild(
        string kind,
        DifferencePair pair,
        StringComparer names,
        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfillDefaults) =>
        kind switch
        {
            "Sequence" => BuildOneSequence(pair),
            "UserDefinedType" => BuildOneUserDefinedType(pair),
            "TableType" => BuildOneTableTypeUdt(pair),
            "Table" => BuildOneTable(pair, names, backfillDefaults),
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
        && a.LoginMatches(b)
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
                writer.WriteBatch($"Setting permission {Sql.Q(pair.Identity.ObjectName)}", body);
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
    /// <param name="names">
    /// How the target resolves identifier case. Pairing the two sides ordinally
    /// on a case-insensitive target made <c>IX_Data</c> and <c>IX_DATA</c> look
    /// like one index removed and another added, so a byte-identical index was
    /// dropped and rebuilt — minutes of table lock inside a batch with a 60 s
    /// cap, for no change at all.
    /// </param>
    private string EmitIndexDelta(
        Table src,
        Table tgt,
        IReadOnlySet<(string Schema, string Table, string Index)> alreadyDropped,
        StringComparer names)
    {
        StringBuilder sb = new();
        var srcByName = src.Indexes.ToDictionary(i => i.Name, names);
        var tgtByName = tgt.Indexes.ToDictionary(i => i.Name, names);

        // DROPs first so a rename-shaped change frees the slot before CREATE.
        foreach (TableIndex t in tgt.Indexes)
        {
            if (alreadyDropped.Contains((src.Schema, src.Name, t.Name))) { continue; }
            bool stillThere = srcByName.TryGetValue(t.Name, out TableIndex? s);
            bool shapeChanged = stillThere && !IndexShapeEqual(t, s!, names);
            if (!stillThere || shapeChanged)
            {
                sb.AppendLine(_indexEmitter.EmitDrop(src.Schema, src.Name, t));
            }
        }
        foreach (TableIndex s in src.Indexes)
        {
            bool mustRestore = alreadyDropped.Contains((src.Schema, src.Name, s.Name));
            bool existsOnTarget = tgtByName.TryGetValue(s.Name, out TableIndex? t);
            bool shapeChanged = existsOnTarget && !IndexShapeEqual(s, t!, names);
            if (existsOnTarget && !shapeChanged && !mustRestore)
            {
                // Same index, different compression: a REBUILD carries the
                // setting across without dropping an index the table is using.
                if (!Compression.Equal(s.DataCompression, t!.DataCompression))
                {
                    sb.AppendLine(_indexEmitter.EmitRebuildForCompression(src.Schema, src.Name, s));
                }
                continue;
            }
            sb.AppendLine(_indexEmitter.EmitCreate(src.Schema, src.Name, s));
        }
        return sb.ToString();
    }

    // Column names are compared pairwise rather than through one "name|desc"
    // string: a composite key would push the direction flag through the name's
    // comparer, and a case-insensitive one would then equate nothing useful.
    private static bool IndexShapeEqual(TableIndex a, TableIndex b, StringComparer names) =>
        a.IsUnique == b.IsUnique
        && a.IsClustered == b.IsClustered
        && BodyNormalizer.ExpressionsEqual(a.FilterExpression, b.FilterExpression)
        && a.KeyColumns.Count == b.KeyColumns.Count
        && a.KeyColumns.Zip(b.KeyColumns).All(p =>
            names.Equals(p.First.Name, p.Second.Name)
            && p.First.IsDescending == p.Second.IsDescending)
        && a.IncludedColumns.SequenceEqual(b.IncludedColumns, names);

    /// <summary>
    /// Emits the ADD half of a table's foreign-key delta: FKs present on the
    /// source that the target lacks or whose shape changed.
    /// </summary>
    /// <param name="src">Source-side table.</param>
    /// <param name="tgt">Target-side table.</param>
    /// <param name="skipKeys">
    /// Foreign keys the up-front drop pass already claimed — inbound keys to a
    /// rebuilt table, and (since S2) any key dropped to free a column being
    /// retyped — re-added by the late orchestrated pass and therefore not this
    /// one's to emit. Keyed <c>(schema, table, name)</c> —
    /// constraint names are unique per schema, and this set spans the whole
    /// script, so the holder table has to be part of the key or an unrelated
    /// namesake on another table loses its ADD.
    /// </param>
    /// <param name="names">
    /// How the target resolves identifier case, for the same reason as
    /// <see cref="EmitIndexDelta"/>: pairing ordinally on a case-insensitive
    /// target makes one spelling of a constraint look removed and the other
    /// added, and the up-front DROP pass has already taken the only copy.
    /// </param>
    private string EmitFkAdds(
        Table src,
        Table tgt,
        IReadOnlySet<(string Schema, string Table, string Fk)> skipKeys,
        StringComparer names)
    {
        StringBuilder sb = new();
        foreach (ForeignKey s in src.Constraints.OfType<ForeignKey>())
        {
            if (skipKeys.Contains((src.Schema, src.Name, s.Name))) { continue; }
            ForeignKey? t = MatchFk(s, tgt, names);
            if (t is not null && ForeignKeyShapeEqual(s, t, names)) { continue; }
            sb.AppendLine(_fkEmitter.EmitAdd(src.Schema, src.Name, s));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The foreign key on <paramref name="other"/> that IS
    /// <paramref name="fk"/>, or <see langword="null"/> when that side has
    /// none. The single answer all five name-keyed structures ask, so they
    /// cannot drift apart: a key SQL Server named itself pairs on what it
    /// constrains, never on its hash. See <c>ConstraintPairing</c>.
    /// </summary>
    private static ForeignKey? MatchFk(ForeignKey fk, Table other, StringComparer names) =>
        ConstraintPairing.Match(fk, [.. other.Constraints.OfType<ForeignKey>()], names) as ForeignKey;

    private static bool ForeignKeyShapeEqual(ForeignKey a, ForeignKey b, StringComparer names) =>
        a.Columns.SequenceEqual(b.Columns, names)
        && names.Equals(a.ReferencedSchema, b.ReferencedSchema)
        && names.Equals(a.ReferencedTable, b.ReferencedTable)
        && a.ReferencedColumns.SequenceEqual(b.ReferencedColumns, names)
        && a.OnDelete == b.OnDelete
        && a.OnUpdate == b.OnUpdate
        && a.IsDisabled == b.IsDisabled
        && a.IsNotForReplication == b.IsNotForReplication;
}

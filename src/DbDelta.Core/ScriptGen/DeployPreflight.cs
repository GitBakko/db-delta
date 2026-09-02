using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// The two refusals a deploy has to make BEFORE it writes a line of SQL, and
/// the target-side scan one of them needs.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <see cref="ScriptGenerator"/> verbatim: the file had grown past
/// the 500-line guideline and the rule is to move code rather than let it grow in
/// silence. Nothing here changed in the move, which is the point — both guards
/// are covered by tests that only ever call <c>ScriptGenerator.Generate</c>, so a
/// behavioural change would have had to be deliberate.
/// </para>
/// <para>
/// <b>The signature is the whole constraint.</b> These lived in
/// <c>ScriptGenerator</c> because <c>Generate</c> is the one place that holds the
/// rebuild decision, the TARGET's dependency edges and the pairs the dropped set
/// is computed from, all at once — and each guard needs a different subset of
/// exactly those. So <see cref="Refuse"/> takes all five values as they already
/// exist at the call site and recomputes none of them. It deliberately does NOT
/// copy <see cref="BackfillPreflight"/>'s <c>(result, selection)</c> shape, even
/// though the backlog entry proposed it: rebuilding <c>rebuildTargets</c> inside
/// would leave two rebuild sets that must never disagree, which is the
/// "recompute it from fewer inputs" mistake these guards were written to close.
/// </para>
/// </remarks>
internal static class DeployPreflight
{
    /// <summary>
    /// Runs both refusals. Throws <see cref="SchemaboundRebuildException"/> or
    /// <see cref="BoundTypeDropException"/> — unwrapped, and never a shared base:
    /// the CLI and the app both catch the concrete types.
    /// </summary>
    public static void Refuse(
        ComparisonResult result,
        List<DifferencePair> pairs,
        HashSet<(string Schema, string Name)> rebuildTargets,
        IReadOnlyList<DependencyEdge> dropDependencies,
        IEqualityComparer<(string Schema, string Name)> pairKey)
    {
        RefuseRebuildsBlockedBySchemabinding(rebuildTargets, pairs, dropDependencies, pairKey);
        RefuseTypeDropsBlockedByABinder(result, pairs, dropDependencies, pairKey);
    }

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
}

using System.Text;
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// After a table rebuild, refreshes the cached column metadata of every view
/// and table-valued function that reads it.
/// </summary>
/// <remarks>
/// <para>
/// The rebuild replaces the table (<c>DROP TABLE</c> + <c>sp_rename</c> of the
/// <c>_tmp</c>), and a non-schemabound module that reads it keeps the column
/// list it cached when it was created. Measured on
/// <c>mssql/server:2022-latest</c>, widening an identity from <c>int</c> to
/// <c>bigint</c>: the base column comes out <c>bigint</c> and
/// <c>sys.columns</c> for the view still says <c>int</c> — <b>and the view goes
/// on SELECTing without an error</b>, so nothing looks wrong. That silence is
/// the whole reason this exists; the schemabound case, by contrast, refuses the
/// <c>DROP TABLE</c> outright with Msg 3729 and is loud.
/// </para>
/// <para>
/// Three measurements shape what is emitted, and each of them removes an
/// obvious guess:
/// </para>
/// <list type="bullet">
/// <item>An inline table-valued <b>function</b> goes stale exactly like a view
/// (its <c>sys.columns</c> row said <c>int</c> too), so this is not a
/// view-only problem and <c>sp_refreshview</c> — which takes views only — is
/// not the right verb. <c>sys.sp_refreshsqlmodule</c> covers both.</item>
/// <item>A <b>procedure</b> does not go stale: asked through
/// <c>sys.dm_exec_describe_first_result_set</c> it already reported
/// <c>bigint</c>. Procedures are therefore deliberately NOT refreshed — a
/// statement that changes nothing is noise in a script a human has to approve.
/// </item>
/// <item>The staleness is <b>transitive and does not cascade on its own</b>: a
/// view over a stale view stayed <c>int</c> after the inner one was refreshed.
/// So the walk follows the dependency graph to its end, and emits inner before
/// outer — breadth-first from the table — because refreshing the outer one
/// first would only re-cache the inner one's stale answer.</item>
/// </list>
/// <para>
/// The set comes from the SOURCE edges, which is what makes it safe: a module
/// the deploy drops is not in them, and a module the deploy re-creates is
/// already fresh (refreshing it again is harmless — <c>sp_refreshsqlmodule</c>
/// is idempotent, measured). A module left referencing a column the rebuild
/// removed makes the refresh fail with Msg 207 — but that module is broken
/// either way, and answers Msg 4413 to a plain SELECT, so failing inside the
/// deploy is the loud version of a break that already happened.
/// </para>
/// </remarks>
internal static class ModuleRefresh
{
    /// <summary>Kinds whose cached column list a table rebuild invalidates.</summary>
    private static readonly string[] StaleKinds = ["View", "Function"];

    /// <summary>
    /// The <c>EXEC sys.sp_refreshsqlmodule</c> statements for every module that
    /// reads a rebuilt table, directly or through another module, innermost
    /// first. Empty when nothing was rebuilt or nothing reads it.
    /// </summary>
    public static string Emit(
        IReadOnlyCollection<(string Schema, string Name)> rebuildTargets,
        IReadOnlyList<DependencyEdge> dependencies,
        StringComparer names)
    {
        if (rebuildTargets.Count == 0 || dependencies.Count == 0)
        {
            return string.Empty;
        }

        // referenced -> the things that read it.
        Dictionary<ObjectIdentity, List<ObjectIdentity>> readers = new(new IdentityComparer(names));
        foreach (DependencyEdge e in dependencies)
        {
            if (!readers.TryGetValue(e.Referenced, out List<ObjectIdentity>? list))
            {
                list = [];
                readers[e.Referenced] = list;
            }
            list.Add(e.Dependent);
        }

        HashSet<ObjectIdentity> seen = new(new IdentityComparer(names));
        Queue<ObjectIdentity> queue = new();
        foreach ((string schema, string name) in rebuildTargets)
        {
            queue.Enqueue(new ObjectIdentity(schema, name, "Table"));
        }

        // Breadth-first, so a module is emitted before anything that reads it.
        List<ObjectIdentity> ordered = [];
        while (queue.Count > 0)
        {
            ObjectIdentity current = queue.Dequeue();
            if (!readers.TryGetValue(current, out List<ObjectIdentity>? next)) { continue; }
            foreach (ObjectIdentity reader in next)
            {
                if (!seen.Add(reader)) { continue; }
                if (StaleKinds.Contains(reader.Kind, StringComparer.Ordinal))
                {
                    ordered.Add(reader);
                }
                // A module that is not itself refreshed can still carry the
                // staleness onward to one that is, so the walk does not stop
                // at it.
                queue.Enqueue(reader);
            }
        }

        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        foreach (ObjectIdentity id in ordered)
        {
            // The argument is a string LITERAL, not an identifier: the name is
            // bracket-quoted first and the whole thing then quoted as a value,
            // the same pairing sp_rename needs.
            sb.Append("EXEC sys.sp_refreshsqlmodule ")
              .Append('N').Append(Sql.L(Sql.Q(id.SchemaName, id.ObjectName)))
              .AppendLine(";");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Identity equality that folds names the way the comparison folded them,
    /// so the walk matches on a case-insensitive target the way the rest of the
    /// engine does. Kind is always ordinal — it is ours, not the server's.
    /// </summary>
    private sealed class IdentityComparer(StringComparer names) : IEqualityComparer<ObjectIdentity>
    {
        public bool Equals(ObjectIdentity x, ObjectIdentity y) =>
            names.Equals(x.SchemaName, y.SchemaName)
            && names.Equals(x.ObjectName, y.ObjectName)
            && string.Equals(x.Kind, y.Kind, StringComparison.Ordinal);

        public int GetHashCode(ObjectIdentity obj) =>
            HashCode.Combine(
                names.GetHashCode(obj.SchemaName),
                names.GetHashCode(obj.ObjectName),
                obj.Kind);
    }
}

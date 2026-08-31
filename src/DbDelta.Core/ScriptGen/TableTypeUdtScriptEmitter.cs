using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE TYPE … AS TABLE / DROP TYPE statements for table-type UDTs.
/// SQL Server does not support ALTER on table types — column changes
/// require DROP + CREATE, handled by <see cref="ScriptGenerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Because the only edit is DROP + CREATE, anything this emitter leaves out is
/// dropped by the deploy and never put back. So the keys, their per-column sort
/// direction, the checks, the defaults, the identity, the computed columns and
/// the inline indexes with their INCLUDE lists are all written here — see
/// <c>docs/parity/redgate-2026-08-31.md</c> R1.
/// </para>
/// <para>
/// What is deliberately absent, measured rather than assumed on
/// <c>mssql/server:2022-latest</c>: a <b>named</b> constraint (rejected —
/// "Incorrect syntax near the keyword 'CONSTRAINT'"), and a <b>filtered</b>
/// inline index (rejected).
/// </para>
/// <para>
/// A memory-optimized table type is a separate shape this emitter does not
/// write, and it is <b>refused</b> rather than quietly rewritten — see
/// <see cref="UnscriptableTableTypeException"/>. <c>EmitDrop</c> is exempt on
/// purpose, exactly as it is for a non-rowstore index.
/// </para>
/// </remarks>
public sealed class TableTypeUdtScriptEmitter
{
    /// <exception cref="UnscriptableTableTypeException">
    /// The type is memory-optimized, so the text below would declare a
    /// disk-based type of the same name.
    /// </exception>
    public string EmitCreate(TableTypeUdt udt)
    {
        ArgumentNullException.ThrowIfNull(udt);
        UnscriptableTableTypeException.ThrowIfMemoryOptimized(udt);
        StringBuilder sb = new();
        sb.Append("CREATE TYPE ").Append(Sql.Q(udt.Schema, udt.Name)).AppendLine(" AS TABLE (");

        List<string> members = [.. udt.Columns.Select(FormatColumn)];
        members.AddRange(udt.Keys.Select(FormatKey));
        members.AddRange(udt.CheckConstraints.Select(ck => $"CHECK {ck.Expression}"));

        for (int i = 0; i < members.Count; i++)
        {
            sb.Append("    ").Append(members[i]);
            if (i < members.Count - 1) { sb.Append(','); }
            sb.AppendLine();
        }

        sb.Append(");");
        return sb.ToString();
    }

    public string EmitDrop(TableTypeUdt udt)
    {
        ArgumentNullException.ThrowIfNull(udt);
        return $"DROP TYPE {Sql.Q(udt.Schema, udt.Name)};";
    }

    /// <summary>
    /// One column line. Deliberately the same order of clauses as
    /// <c>TableScriptEmitter.FormatColumn</c> — computed, else type, IDENTITY,
    /// COLLATE, nullability, DEFAULT.
    /// </summary>
    /// <remarks>
    /// The DEFAULT is written without a <c>CONSTRAINT</c> clause because a
    /// table type refuses one, so <see cref="Column.DefaultExpression"/> is the
    /// only place it can live: there is no <c>DefaultConstraint</c> to name.
    /// </remarks>
    private static string FormatColumn(Column c)
    {
        StringBuilder sb = new();
        sb.Append(Sql.Q(c.Name)).Append(' ');

        if (c.ComputedExpression is not null)
        {
            // PERSISTED is not part of a table type's grammar, so a computed
            // column is written as the expression alone.
            return sb.Append("AS ").Append(c.ComputedExpression).ToString();
        }

        sb.Append(SqlTypeFormatter.FormatColumnType(c.DataType, c.TypeSchema));
        if (c.IsIdentity)
        {
            sb.Append(c.IdentitySeed is long seed && c.IdentityIncrement is long inc
                ? $" IDENTITY({seed},{inc})"
                : " IDENTITY");
        }
        AppendCollation(sb, c);
        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (!string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A PRIMARY KEY, a UNIQUE constraint or an inline INDEX — one row of
    /// <c>sys.indexes</c> on the type table in each case.
    /// </summary>
    /// <remarks>
    /// The name is written for an inline INDEX and only for that: it is the one
    /// member of a table type whose name is the user's rather than the server's.
    /// A PK or UNIQUE gets no <c>CONSTRAINT</c> clause because SQL Server
    /// refuses one there.
    /// </remarks>
    private static string FormatKey(TableIndex ix)
    {
        string keys = string.Join(", ", ix.KeyColumns.Select(k =>
            $"{Sql.Q(k.Name)} {(k.IsDescending ? "DESC" : "ASC")}"));
        string clustered = ix.IsClustered ? "CLUSTERED" : "NONCLUSTERED";

        if (ix.IsPrimaryKey) { return $"PRIMARY KEY {clustered} ({keys})"; }
        if (ix.IsUniqueConstraint) { return $"UNIQUE {clustered} ({keys})"; }

        string unique = ix.IsUnique ? "UNIQUE " : string.Empty;
        string include = ix.IncludedColumns.Count == 0
            ? string.Empty
            : $" INCLUDE ({string.Join(", ", ix.IncludedColumns.Select(Sql.Q))})";
        return $"INDEX {Sql.Q(ix.Name)} {unique}{clustered} ({keys}){include}";
    }

    /// <summary>
    /// Appends <c>COLLATE &lt;name&gt;</c> to <paramref name="sb"/> whenever the
    /// column carries a collation (string type). Mirrors the always-explicit
    /// rule in <see cref="TableScriptEmitter"/> — including its one exception,
    /// a column of a user-defined alias type, which SQL Server refuses to let
    /// carry the clause at all.
    /// </summary>
    private static void AppendCollation(StringBuilder sb, Column c)
    {
        if (string.IsNullOrEmpty(c.Collation) || c.IsUserDefinedType) { return; }
        sb.Append(" COLLATE ").Append(c.Collation);
    }
}

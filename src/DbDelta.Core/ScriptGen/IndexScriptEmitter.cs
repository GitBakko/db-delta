using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits standalone CREATE INDEX / DROP INDEX statements. Called by
/// <see cref="ScriptGenerator"/> after all tables have been created.
/// </summary>
public sealed class IndexScriptEmitter
{
    public string EmitCreate(string schema, string table, TableIndex ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        StringBuilder sb = new();
        sb.Append("CREATE ");
        if (ix.IsUnique)
        {
            sb.Append("UNIQUE ");
        }
        sb.Append(ix.IsClustered ? "CLUSTERED " : "NONCLUSTERED ");
        sb.Append("INDEX ").Append(Sql.Q(ix.Name)).Append(" ON ")
          .Append(Sql.Q(schema, table)).Append(" (");
        sb.Append(string.Join(", ", ix.KeyColumns.Select(k =>
            $"{Sql.Q(k.Name)} {(k.IsDescending ? "DESC" : "ASC")}")));
        sb.Append(')');

        if (ix.IncludedColumns.Count > 0)
        {
            sb.Append(" INCLUDE (");
            sb.Append(string.Join(", ", ix.IncludedColumns.Select(c => $"{Sql.Q(c)}")));
            sb.Append(')');
        }

        if (!string.IsNullOrEmpty(ix.FilterExpression))
        {
            sb.Append(" WHERE ").Append(ix.FilterExpression);
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitDrop(string schema, string table, TableIndex ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        return $"DROP INDEX {Sql.Q(ix.Name)} ON {Sql.Q(schema, table)};";
    }
}

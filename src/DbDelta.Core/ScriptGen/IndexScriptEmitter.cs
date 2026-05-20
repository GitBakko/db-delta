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
        sb.Append("INDEX [").Append(ix.Name).Append("] ON [")
          .Append(schema).Append("].[").Append(table).Append("] (");
        sb.Append(string.Join(", ", ix.KeyColumns.Select(k =>
            $"[{k.Name}] {(k.IsDescending ? "DESC" : "ASC")}")));
        sb.Append(')');

        if (ix.IncludedColumns.Count > 0)
        {
            sb.Append(" INCLUDE (");
            sb.Append(string.Join(", ", ix.IncludedColumns.Select(c => $"[{c}]")));
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
        return $"DROP INDEX [{ix.Name}] ON [{schema}].[{table}];";
    }
}

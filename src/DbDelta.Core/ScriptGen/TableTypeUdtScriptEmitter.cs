using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE TYPE … AS TABLE / DROP TYPE statements for table-type UDTs.
/// SQL Server does not support ALTER on table types — column changes
/// require DROP + CREATE, handled by <see cref="ScriptGenerator"/>.
/// </summary>
public sealed class TableTypeUdtScriptEmitter
{
    public string EmitCreate(TableTypeUdt udt)
    {
        ArgumentNullException.ThrowIfNull(udt);
        StringBuilder sb = new();
        sb.Append("CREATE TYPE ").Append(Sql.Q(udt.Schema, udt.Name)).AppendLine(" AS TABLE (");
        for (int i = 0; i < udt.Columns.Count; i++)
        {
            Column col = udt.Columns[i];
            sb.Append("    ").Append(Sql.Q(col.Name)).Append(' ').Append(SqlTypeFormatter.FormatColumnType(col.DataType));
            AppendCollation(sb, col);
            sb.Append(col.IsNullable ? " NULL" : " NOT NULL");
            if (i < udt.Columns.Count - 1)
            {
                sb.Append(',');
            }
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
    /// Appends <c>COLLATE &lt;name&gt;</c> to <paramref name="sb"/> whenever the
    /// column carries a collation (string type). Mirrors the always-explicit
    /// rule in <see cref="TableScriptEmitter"/>.
    /// </summary>
    private static void AppendCollation(StringBuilder sb, Column c)
    {
        if (string.IsNullOrEmpty(c.Collation)) { return; }
        sb.Append(" COLLATE ").Append(c.Collation);
    }
}

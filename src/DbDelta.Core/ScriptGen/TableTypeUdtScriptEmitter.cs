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
    public string EmitCreate(TableTypeUdt udt) => EmitCreate(udt, targetDefaultCollation: null);

    /// <summary>
    /// Overload that accepts the target DB default collation so string
    /// columns whose collation matches the default render without a
    /// redundant <c>COLLATE</c> clause (Redgate parity, M13-PARITY.5 #32).
    /// </summary>
    public string EmitCreate(TableTypeUdt udt, string? targetDefaultCollation)
    {
        ArgumentNullException.ThrowIfNull(udt);
        StringBuilder sb = new();
        sb.Append("CREATE TYPE [").Append(udt.Schema).Append("].[").Append(udt.Name).AppendLine("] AS TABLE (");
        for (int i = 0; i < udt.Columns.Count; i++)
        {
            Column col = udt.Columns[i];
            sb.Append("    [").Append(col.Name).Append("] ").Append(col.DataType);
            AppendCollation(sb, col, targetDefaultCollation);
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
        return $"DROP TYPE [{udt.Schema}].[{udt.Name}];";
    }

    /// <summary>
    /// Appends <c>COLLATE &lt;name&gt;</c> to <paramref name="sb"/> when the
    /// column's collation diverges from the target DB default. Mirrors the
    /// rule in <see cref="TableScriptEmitter"/>.
    /// </summary>
    private static void AppendCollation(StringBuilder sb, Column c, string? targetDefaultCollation)
    {
        if (string.IsNullOrEmpty(c.Collation)) { return; }
        if (targetDefaultCollation is not null
            && string.Equals(c.Collation, targetDefaultCollation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        sb.Append(" COLLATE ").Append(c.Collation);
    }
}

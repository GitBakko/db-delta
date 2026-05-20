using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP / ALTER (add-column only in M1) for tables.
/// </summary>
public sealed class TableScriptEmitter : IScriptEmitter
{
    /// <inheritdoc />
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA => EmitCreate((Table)pair.SideA!),
            DifferenceStatus.OnlyInB => EmitDrop((Table)pair.SideB!),
            DifferenceStatus.Different => EmitAlter((Table)pair.SideA!, (Table)pair.SideB!),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreate(Table table)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name).AppendLine("] (");
        for (int i = 0; i < table.Columns.Count; i++)
        {
            Column c = table.Columns[i];
            sb.Append("    ").Append(FormatColumn(c));
            if (i < table.Columns.Count - 1)
            {
                sb.Append(',');
            }

            sb.AppendLine();
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string EmitDrop(Table table) =>
        $"DROP TABLE [{table.Schema}].[{table.Name}];";

    private static string EmitAlter(Table newT, Table oldT)
    {
        var sb = new StringBuilder();
        var existingByName = oldT.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (Column newCol in newT.Columns)
        {
            if (!existingByName.ContainsKey(newCol.Name))
            {
                sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
                  .Append("] ADD ").Append(FormatColumn(newCol)).AppendLine(";");
            }
        }

        // Note: removed columns + altered types are deferred to M2.
        return sb.ToString();
    }

    private static string FormatColumn(Column c)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(c.Name).Append("] ").Append(c.DataType);
        if (c.IsIdentity)
        {
            sb.Append(" IDENTITY");
        }

        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (!string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }

        return sb.ToString();
    }
}

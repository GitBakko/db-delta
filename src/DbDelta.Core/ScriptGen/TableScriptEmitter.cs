using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP / ALTER (add-column + add-constraint) for tables.
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
        StringBuilder sb = new();
        sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name).AppendLine("] (");

        HashSet<string> colsWithNamedDefault =
            [.. table.Constraints.OfType<DefaultConstraint>().Select(d => d.ColumnName)];

        bool firstLine = true;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            Column col = table.Columns[i];
            AppendLineSeparator(sb, ref firstLine);
            sb.Append("    ").Append(FormatColumn(col, colsWithNamedDefault.Contains(col.Name)));
        }

        foreach (Constraint c in table.Constraints)
        {
            switch (c)
            {
                case PrimaryKey pk:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(pk.Name).Append("] PRIMARY KEY ")
                      .Append(pk.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                      .Append('(').Append(string.Join(", ", pk.Columns.Select(Bracket))).Append(')');
                    break;
                case UniqueConstraint uq:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(uq.Name).Append("] UNIQUE ")
                      .Append(uq.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                      .Append('(').Append(string.Join(", ", uq.Columns.Select(Bracket))).Append(')');
                    break;
                case CheckConstraint ck:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(ck.Name).Append("] CHECK ")
                      .Append(ck.Expression);
                    break;
                case DefaultConstraint df:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(df.Name).Append("] DEFAULT ")
                      .Append(df.Expression).Append(" FOR [").Append(df.ColumnName).Append(']');
                    break;
                case ForeignKey:
                    // FK is emitted standalone by ForeignKeyScriptEmitter.
                    break;
                default:
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static void AppendLineSeparator(StringBuilder sb, ref bool firstLine)
    {
        if (firstLine)
        {
            firstLine = false;
            return;
        }
        sb.AppendLine(",");
    }

    private static string Bracket(string identifier) => $"[{identifier}]";

    /// <summary>
    /// Generates a standalone CREATE TABLE script for a single table, suitable for
    /// body diffing in the diff viewer. Does not include the transaction wrapper.
    /// </summary>
    public static string GenerateCreateTable(Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return EmitCreate(table);
    }

    private static string EmitDrop(Table table) =>
        $"DROP TABLE [{table.Schema}].[{table.Name}];";

    private static string EmitAlter(Table newT, Table oldT)
    {
        StringBuilder sb = new();
        HashSet<string> colsWithNamedDefault =
            [.. newT.Constraints.OfType<DefaultConstraint>().Select(d => d.ColumnName)];

        var existingColsByName =
            oldT.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (Column newCol in newT.Columns)
        {
            if (!existingColsByName.ContainsKey(newCol.Name))
            {
                sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
                  .Append("] ADD ")
                  .Append(FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name)))
                  .AppendLine(";");
            }
        }

        HashSet<string> existingConstraintNames =
            [.. oldT.Constraints.Select(c => c.Name)];
        foreach (Constraint c in newT.Constraints)
        {
            if (existingConstraintNames.Contains(c.Name))
            {
                continue;
            }
            string body = FormatStandaloneConstraintBody(c);
            if (body.Length == 0)
            {
                continue;
            }
            sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
              .Append("] ADD CONSTRAINT [").Append(c.Name).Append("] ")
              .Append(body).AppendLine(";");
        }

        return sb.ToString();
    }

    private static string FormatStandaloneConstraintBody(Constraint c) => c switch
    {
        PrimaryKey pk => $"PRIMARY KEY {(pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", pk.Columns.Select(Bracket))})",
        UniqueConstraint uq => $"UNIQUE {(uq.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", uq.Columns.Select(Bracket))})",
        CheckConstraint ck => $"CHECK {ck.Expression}",
        DefaultConstraint df => $"DEFAULT {df.Expression} FOR [{df.ColumnName}]",
        ForeignKey => string.Empty,
        _ => string.Empty,
    };

    private static string FormatColumn(Column c, bool hasNamedDefault)
    {
        StringBuilder sb = new();
        sb.Append('[').Append(c.Name).Append("] ");

        if (c.ComputedExpression is not null)
        {
            sb.Append("AS ").Append(c.ComputedExpression);
            if (c.IsPersistedComputed)
            {
                sb.Append(" PERSISTED");
                if (!c.IsNullable)
                {
                    sb.Append(" NOT NULL");
                }
            }
            return sb.ToString();
        }

        sb.Append(c.DataType);
        if (c.IsIdentity)
        {
            if (c.IdentitySeed is long seed && c.IdentityIncrement is long inc)
            {
                sb.Append(" IDENTITY(").Append(seed).Append(',').Append(inc).Append(')');
            }
            else
            {
                sb.Append(" IDENTITY");
            }
        }
        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (!hasNamedDefault && !string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }
        return sb.ToString();
    }
}

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

    /// <summary>
    /// Full table body for the diff viewer — CREATE TABLE plus standalone
    /// FOREIGN KEY ALTER statements plus CREATE INDEX statements. The
    /// ComparisonEngine flags a table as <c>Different</c> when columns OR
    /// constraints (incl. FKs) OR indexes differ, so the body shown to the
    /// user MUST include all three or "Different" rows render as empty
    /// diffs (the bug fixed in round-16). Statements are emitted in a
    /// deterministic order (FKs by name, indexes by name) so both source
    /// and target sides line up cleanly in the line-diff.
    /// </summary>
    public static string GenerateFullTableBody(Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        StringBuilder sb = new();
        sb.Append(EmitCreate(table));

        // Foreign-key constraints — appended as standalone ALTER TABLE statements.
        List<ForeignKey> fks =
        [
            .. table.Constraints
                .OfType<ForeignKey>()
                .OrderBy(fk => fk.Name, StringComparer.Ordinal)
        ];
        if (fks.Count > 0)
        {
            sb.AppendLine();
            ForeignKeyScriptEmitter fkEmitter = new();
            foreach (ForeignKey fk in fks)
            {
                sb.AppendLine(fkEmitter.EmitAdd(table.Schema, table.Name, fk));
            }
        }

        // Indexes — appended as standalone CREATE INDEX statements.
        List<TableIndex> indexes =
        [
            .. table.Indexes.OrderBy(ix => ix.Name, StringComparer.Ordinal)
        ];
        if (indexes.Count > 0)
        {
            sb.AppendLine();
            IndexScriptEmitter ixEmitter = new();
            foreach (TableIndex ix in indexes)
            {
                sb.AppendLine(ixEmitter.EmitCreate(table.Schema, table.Name, ix));
            }
        }

        return sb.ToString();
    }

    private static string EmitDrop(Table table) =>
        $"DROP TABLE [{table.Schema}].[{table.Name}];";

    private static string EmitAlter(Table newT, Table oldT)
    {
        StringBuilder sb = new();
        string qualifiedName = $"[{newT.Schema}].[{newT.Name}]";

        Dictionary<string, Constraint> newConstraintsByName =
            newT.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
        Dictionary<string, Constraint> oldConstraintsByName =
            oldT.Constraints.ToDictionary(c => c.Name, StringComparer.Ordinal);
        HashSet<string> colsWithNamedDefault =
            [.. newT.Constraints.OfType<DefaultConstraint>().Select(d => d.ColumnName)];

        Dictionary<string, Column> existingColsByName =
            oldT.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        Dictionary<string, Column> newColsByName =
            newT.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        // ── 1) DROP non-FK constraints first (FKs handled by ForeignKeyScriptEmitter).
        //       Dropping a constraint may also be a prerequisite for the column /
        //       constraint shape changes below.
        foreach (Constraint oldC in oldT.Constraints)
        {
            if (oldC is ForeignKey) { continue; }
            // Constraint disappeared from source → drop on target.
            // Constraint shape changed → drop now and re-create below.
            bool stillPresent = newConstraintsByName.TryGetValue(oldC.Name, out Constraint? newSame);
            bool shapeChanged = stillPresent && !ConstraintShapeEqual(oldC, newSame!);
            if (!stillPresent || shapeChanged)
            {
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" DROP CONSTRAINT [").Append(oldC.Name).AppendLine("];");
            }
        }

        // ── 2) DROP columns present only on target. Any default constraint on
        //       the column was already dropped above (system-named defaults
        //       are part of the constraints list).
        foreach (Column oldCol in oldT.Columns)
        {
            if (!newColsByName.ContainsKey(oldCol.Name))
            {
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" DROP COLUMN [").Append(oldCol.Name).AppendLine("];");
            }
        }

        // ── 3) ALTER columns whose shape changed.
        foreach (Column newCol in newT.Columns)
        {
            if (!existingColsByName.TryGetValue(newCol.Name, out Column? oldCol)) { continue; }
            if (ColumnShapeEqual(oldCol, newCol)) { continue; }
            // Computed columns require drop + add; same for identity changes.
            if (newCol.ComputedExpression is not null || oldCol.ComputedExpression is not null
                || newCol.IsIdentity != oldCol.IsIdentity)
            {
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" DROP COLUMN [").Append(newCol.Name).AppendLine("];");
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" ADD ").Append(FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name)))
                  .AppendLine(";");
                continue;
            }
            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ALTER COLUMN [").Append(newCol.Name).Append("] ")
              .Append(newCol.DataType)
              .Append(newCol.IsNullable ? " NULL" : " NOT NULL")
              .AppendLine(";");
        }

        // ── 4) ADD new columns (present in source but not target).
        foreach (Column newCol in newT.Columns)
        {
            if (existingColsByName.ContainsKey(newCol.Name)) { continue; }
            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ADD ")
              .Append(FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name)))
              .AppendLine(";");
        }

        // ── 5) ADD constraints — new ones AND the shape-changed ones we
        //       dropped above. FKs are emitted standalone by
        //       ForeignKeyScriptEmitter (see ScriptGenerator section 7).
        foreach (Constraint c in newT.Constraints)
        {
            if (c is ForeignKey) { continue; }
            bool existsOnTarget = oldConstraintsByName.TryGetValue(c.Name, out Constraint? oldSame);
            bool shapeChanged = existsOnTarget && !ConstraintShapeEqual(oldSame!, c);
            if (existsOnTarget && !shapeChanged) { continue; }
            string body = FormatStandaloneConstraintBody(c);
            if (body.Length == 0) { continue; }
            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ADD CONSTRAINT [").Append(c.Name).Append("] ")
              .Append(body).AppendLine(";");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Shape-equality for columns: same data type, same nullability, same
    /// identity flag (+ seed/increment when identity), same default
    /// expression text, same computed expression text. Used by EmitAlter to
    /// decide between ALTER COLUMN and DROP+ADD.
    /// </summary>
    private static bool ColumnShapeEqual(Column a, Column b)
    {
        if (!string.Equals(a.DataType, b.DataType, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (a.IsNullable != b.IsNullable) { return false; }
        if (a.IsIdentity != b.IsIdentity) { return false; }
        if (a.IsIdentity && b.IsIdentity)
        {
            if (a.IdentitySeed != b.IdentitySeed) { return false; }
            if (a.IdentityIncrement != b.IdentityIncrement) { return false; }
        }
        return string.Equals(a.DefaultExpression, b.DefaultExpression, StringComparison.Ordinal)
            && string.Equals(a.ComputedExpression, b.ComputedExpression, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shape-equality for non-FK constraints (PK/UQ/CK/Default). Mirrors the
    /// rules used by <c>ComparisonEngine.ConstraintShapeEqual</c> so EmitAlter
    /// only emits DROP+ADD when ComparisonEngine would consider the constraint
    /// to have changed.
    /// </summary>
    private static bool ConstraintShapeEqual(Constraint a, Constraint b) => (a, b) switch
    {
        (PrimaryKey pa, PrimaryKey pb) =>
            pa.IsClustered == pb.IsClustered && pa.Columns.SequenceEqual(pb.Columns, StringComparer.Ordinal),
        (UniqueConstraint ua, UniqueConstraint ub) =>
            ua.IsClustered == ub.IsClustered && ua.Columns.SequenceEqual(ub.Columns, StringComparer.Ordinal),
        (CheckConstraint ca, CheckConstraint cb) =>
            string.Equals(ca.Expression, cb.Expression, StringComparison.Ordinal),
        (DefaultConstraint da, DefaultConstraint db) =>
            string.Equals(da.ColumnName, db.ColumnName, StringComparison.Ordinal)
            && string.Equals(da.Expression, db.Expression, StringComparison.Ordinal),
        _ => false,
    };

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

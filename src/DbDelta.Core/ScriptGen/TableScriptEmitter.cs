using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP / ALTER (add-column + add-constraint) for tables.
/// </summary>
/// <param name="names">
/// How the target server resolves identifier case — normally
/// <c>ComparisonResult.NameComparer</c>. Columns and constraints have to be
/// paired by the same rule the engine matched their table with: on a
/// case-insensitive target, matching <c>Nome</c> against <c>NOME</c> ordinally
/// makes one look dropped and the other added, and DROP COLUMN takes the data
/// with it. Defaults to ordinal so direct callers keep the old behaviour.
/// </param>
/// <param name="backfillDefaults">
/// Values, keyed <c>(schema, table, column)</c>, that seed the existing rows
/// when this ALTER adds a NOT NULL column the source declares without a
/// default. See <see cref="ColumnsNeedingABackfillDefault"/> for why one is
/// needed at all.
/// </param>
public sealed class TableScriptEmitter(
    StringComparer? names = null,
    IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfillDefaults = null)
    : IScriptEmitter
{
    private readonly StringComparer _names = names ?? StringComparer.Ordinal;

    /// <summary>
    /// Prefix for the throwaway constraint that carries a backfill value onto
    /// the rows already in the table. Named, so the script can drop exactly the
    /// one it added and leave the column matching the source.
    /// </summary>
    private const string BackfillPrefix = "DF__dbdelta_backfill__";

    /// <summary>
    /// The columns an ALTER of <paramref name="oldT"/> into <paramref name="newT"/>
    /// would add as <c>NOT NULL</c> with nothing to put in the rows that already
    /// exist.
    /// </summary>
    /// <remarks>
    /// <c>ALTER TABLE … ADD</c> of a NOT NULL column is legal only on an empty
    /// table unless a DEFAULT travels with it (Msg 4901). The source schema
    /// simply may not have one, and then no tool can deploy the change without
    /// inventing a value — Redgate's rebuild leaves the column out of its
    /// INSERT list and dies on the same data. Surfacing the list BEFORE the
    /// script runs is what lets a human supply the value instead of finding out
    /// halfway through a deploy.
    /// </remarks>
    public static IReadOnlyList<string> ColumnsNeedingABackfillDefault(
        Table newT, Table oldT, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(newT);
        ArgumentNullException.ThrowIfNull(oldT);
        ArgumentNullException.ThrowIfNull(names);
        if (RequiresFullRebuild(newT, oldT, names)) { return []; }

        HashSet<string> existing = new(oldT.Columns.Select(c => c.Name), names);
        Dictionary<string, DefaultConstraint> namedDefaults = NamedDefaultsByColumn(newT, names);
        return
        [
            .. newT.Columns
                .Where(c => !existing.Contains(c.Name))
                .Where(c => !c.IsNullable
                            && !c.IsIdentity
                            && c.ComputedExpression is null
                            && string.IsNullOrEmpty(c.DefaultExpression)
                            && !namedDefaults.ContainsKey(c.Name))
                .Select(c => c.Name)
        ];
    }

    /// <inheritdoc />
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA => EmitCreate((Table)pair.SideA!),
            DifferenceStatus.OnlyInB => EmitDrop((Table)pair.SideB!),
            DifferenceStatus.Different => EmitAlter((Table)pair.SideA!, (Table)pair.SideB!, _names, backfillDefaults),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreate(
        Table table,
        bool includeNamedConstraints = true)
    {
        StringBuilder sb = new();
        sb.Append("CREATE TABLE ").Append(Sql.Q(table.Schema, table.Name)).AppendLine(" (");

        // Named DEFAULT constraints are resolved up-front and always passed to
        // FormatColumn, because DEFAULT is the one constraint kind that CREATE
        // TABLE only accepts *inline on the column* — it is absent from the
        // table_constraint grammar, so a table-level
        // "CONSTRAINT [x] DEFAULT (e) FOR [c]" is a syntax error (Msg 102).
        // With includeNamedConstraints the named form is emitted inline;
        // without it the default is suppressed entirely, because the only
        // caller in that mode (EmitRebuild) re-adds it by name after
        // sp_rename and an inline copy would collide ("Column already has a
        // DEFAULT bound to it").
        // Ordinal: both the keys and the lookups come from `table` itself, so
        // there is no cross-side pairing here for a collation to disagree with.
        Dictionary<string, DefaultConstraint> namedDefaults =
            NamedDefaultsByColumn(table, StringComparer.Ordinal);

        bool firstLine = true;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            Column col = table.Columns[i];
            AppendLineSeparator(sb, ref firstLine);
            sb.Append("    ").Append(FormatColumn(
                col,
                namedDefaults.GetValueOrDefault(col.Name),
                inlineNamedDefault: includeNamedConstraints));
        }

        if (includeNamedConstraints)
        {
            foreach (Constraint c in table.Constraints)
            {
                switch (c)
                {
                    case PrimaryKey:
                        // PK is emitted as a trailing ALTER TABLE ADD CONSTRAINT
                        // statement (Redgate parity), not inline.
                        break;
                    case UniqueConstraint uq:
                        AppendLineSeparator(sb, ref firstLine);
                        sb.Append("    CONSTRAINT ").Append(Sql.Q(uq.Name)).Append(" UNIQUE ")
                          .Append(uq.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                          .Append('(').Append(string.Join(", ", uq.Columns.Select(Sql.Q))).Append(')');
                        break;
                    case CheckConstraint ck:
                        AppendLineSeparator(sb, ref firstLine);
                        sb.Append("    CONSTRAINT ").Append(Sql.Q(ck.Name)).Append(" CHECK ")
                          .Append(ck.Expression);
                        break;
                    case DefaultConstraint:
                        // DEFAULT is not a table_constraint in T-SQL; it was
                        // already emitted inline on its column above, which is
                        // the only valid CREATE TABLE form and preserves the
                        // constraint name.
                        break;
                    case ForeignKey:
                        // FK is emitted standalone by ForeignKeyScriptEmitter.
                        break;
                    default:
                        break;
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(");");

        // Primary key as a separate ALTER TABLE ADD CONSTRAINT (Redgate parity).
        if (includeNamedConstraints)
        {
            foreach (PrimaryKey pk in table.Constraints.OfType<PrimaryKey>())
            {
                sb.Append("ALTER TABLE ").Append(Sql.Q(table.Schema, table.Name))
                  .Append(" ADD CONSTRAINT ").Append(Sql.Q(pk.Name)).Append(' ')
                  .Append(FormatStandaloneConstraintBody(pk)).AppendLine(";");
            }
        }

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
        $"DROP TABLE {Sql.Q(table.Schema, table.Name)};";

    private static string EmitAlter(
        Table newT,
        Table oldT,
        StringComparer names,
        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfillDefaults = null)
    {
        // Spec §3.4: identity column changes on an EXISTING column require a
        // full temp-table rebuild — you cannot ALTER an IDENTITY flag or its
        // seed/increment in place, and DROP COLUMN + ADD COLUMN would erase
        // the data we are trying to migrate.
        if (RequiresFullRebuild(newT, oldT, names))
        {
            return EmitRebuild(newT, oldT, names);
        }

        StringBuilder sb = new();
        string qualifiedName = $"{Sql.Q(newT.Schema, newT.Name)}";

        var newConstraintsByName = newT.Constraints.ToDictionary(c => c.Name, names);
        var oldConstraintsByName = oldT.Constraints.ToDictionary(c => c.Name, names);
        Dictionary<string, DefaultConstraint> namedDefaults = NamedDefaultsByColumn(newT, names);
        // Columns whose named DEFAULT we emit inline on an ADD below. Section 5
        // must then NOT re-add the same constraint standalone, or SQL Server
        // rejects it with "Column already has a DEFAULT bound to it".
        HashSet<string> inlinedNamedDefaults = new(names);

        var existingColsByName = oldT.Columns.ToDictionary(c => c.Name, names);
        var newColsByName = newT.Columns.ToDictionary(c => c.Name, names);

        // Columns this ALTER will drop or retype. A key or CHECK constraint that
        // depends on one of them blocks the column DDL with Msg 5074, so it has
        // to be dropped in section 1 and put back in section 5 — even when the
        // constraint itself is completely unchanged.
        IReadOnlySet<string> touchedColumns = ColumnsDroppedOrAltered(newT, oldT, names);
        HashSet<string> droppedForColumnDependency = new(names);
        // Set by sections 3 and 4. A constraint added in the same batch as the
        // column it covers cannot compile — see the separator before section 5.
        bool addedColumns = false;

        // ── 1) DROP non-FK constraints first (FKs handled by ForeignKeyScriptEmitter).
        //       Dropping a constraint may also be a prerequisite for the column /
        //       constraint shape changes below.
        foreach (Constraint oldC in oldT.Constraints)
        {
            if (oldC is ForeignKey) { continue; }
            // Constraint disappeared from source → drop on target.
            // Constraint shape changed → drop now and re-create below.
            bool stillPresent = newConstraintsByName.TryGetValue(oldC.Name, out Constraint? newSame);
            bool shapeChanged = stillPresent && !ConstraintShapeEqual(oldC, newSame!, names);
            bool blocksColumnDdl = stillPresent && !shapeChanged
                && DependsOnColumn(oldC, touchedColumns);
            if (blocksColumnDdl) { droppedForColumnDependency.Add(oldC.Name); }
            if (!stillPresent || shapeChanged || blocksColumnDdl)
            {
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" DROP CONSTRAINT ").Append(Sql.Q(oldC.Name)).AppendLine(";");
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
                  .Append(" DROP COLUMN ").Append(Sql.Q(oldCol.Name)).AppendLine(";");
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
                  .Append(" DROP COLUMN ").Append(Sql.Q(newCol.Name)).AppendLine(";");
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" ADD ").Append(FormatColumn(
                      newCol,
                      namedDefaults.GetValueOrDefault(newCol.Name),
                      inlineNamedDefault: true))
                  .AppendLine(";");
                if (namedDefaults.ContainsKey(newCol.Name)) { inlinedNamedDefaults.Add(newCol.Name); }
                addedColumns = true;
                continue;
            }
            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ALTER COLUMN ").Append(Sql.Q(newCol.Name)).Append(' ')
              .Append(SqlTypeFormatter.FormatColumnType(newCol.DataType));
            AppendCollation(sb, newCol);
            sb.Append(newCol.IsNullable ? " NULL" : " NOT NULL")
              .AppendLine(";");
        }

        // ── 4) ADD new columns (present in source but not target). The DEFAULT
        //       MUST travel inline on the ADD: a NOT NULL column added to a
        //       populated table without one fails with Msg 4901 ("ALTER TABLE
        //       only allows columns to be added that can contain nulls, or have
        //       a DEFAULT definition specified").
        foreach (Column newCol in newT.Columns)
        {
            if (existingColsByName.ContainsKey(newCol.Name)) { continue; }
            // A NOT NULL column with nothing to seed the existing rows cannot be
            // added at all (Msg 4901). When the operator supplied a backfill
            // value it rides in on a NAMED throwaway constraint, which the next
            // statement drops — the rows get a value, and the column is left
            // exactly as the source declares it, with no default of its own.
            string? backfill = null;
            if (backfillDefaults is not null
                && namedDefaults.GetValueOrDefault(newCol.Name) is null
                && string.IsNullOrEmpty(newCol.DefaultExpression)
                && !newCol.IsNullable
                && !newCol.IsIdentity
                && newCol.ComputedExpression is null)
            {
                backfillDefaults.TryGetValue((newT.Schema, newT.Name, newCol.Name), out backfill);
            }

            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ADD ")
              .Append(FormatColumn(
                  newCol,
                  namedDefaults.GetValueOrDefault(newCol.Name),
                  inlineNamedDefault: true));
            if (backfill is not null)
            {
                string dfName = BackfillPrefix + newCol.Name;
                sb.Append(" CONSTRAINT ").Append(Sql.Q(dfName)).Append(" DEFAULT ").Append(backfill)
                  .AppendLine(";");
                sb.Append("ALTER TABLE ").Append(qualifiedName)
                  .Append(" DROP CONSTRAINT ").Append(Sql.Q(dfName)).AppendLine(";");
            }
            else
            {
                sb.AppendLine(";");
            }
            if (namedDefaults.ContainsKey(newCol.Name)) { inlinedNamedDefaults.Add(newCol.Name); }
            addedColumns = true;
        }

        // ── 5) ADD constraints — new ones AND the shape-changed ones we
        //       dropped above. FKs are emitted standalone by
        //       ForeignKeyScriptEmitter (see ScriptGenerator section 7).
        StringBuilder constraints = new();
        foreach (Constraint c in newT.Constraints)
        {
            if (c is ForeignKey) { continue; }
            // Already emitted inline on the column's ADD in section 3 / 4.
            if (c is DefaultConstraint dc && inlinedNamedDefaults.Contains(dc.ColumnName)) { continue; }
            bool existsOnTarget = oldConstraintsByName.TryGetValue(c.Name, out Constraint? oldSame);
            bool shapeChanged = existsOnTarget && !ConstraintShapeEqual(oldSame!, c, names);
            // Unchanged, but section 1 had to drop it to free an altered column.
            bool mustRestore = droppedForColumnDependency.Contains(c.Name);
            if (existsOnTarget && !shapeChanged && !mustRestore) { continue; }
            string body = FormatStandaloneConstraintBody(c);
            if (body.Length == 0) { continue; }
            constraints.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" ADD CONSTRAINT ").Append(Sql.Q(c.Name)).Append(' ')
              .Append(body).AppendLine(";");
        }

        if (constraints.Length > 0)
        {
            // A batch is COMPILED IN FULL before any of it runs, so a constraint
            // over a column the same batch adds cannot resolve that column and
            // dies at compile time with Msg 207 — which is every deploy that
            // adds a column and a CHECK over it in one go. The separator is
            // emitted only when a column was actually added, so a table whose
            // constraints alone changed keeps its single-batch shape.
            if (addedColumns) { sb.AppendLine("GO"); }
            sb.Append(constraints);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The column names that an ALTER of <paramref name="oldT"/> into
    /// <paramref name="newT"/> will DROP or ALTER.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set mixes spellings on purpose, and callers must not assume one side:
    /// a column the source removed can only be named by the TARGET, while a
    /// retyped one is added under the SOURCE's spelling, so on a
    /// case-insensitive server half the set carries each. That is safe only
    /// because the set is built with <paramref name="names"/> and every consumer
    /// probes it with <c>Contains</c>, letting its own comparer decide — never
    /// by comparing the strings it yields against something else.
    /// </para>
    /// SQL Server refuses to drop or retype a column while an index, key or
    /// CHECK constraint depends on it (Msg 5074, "The object '…' is dependent on
    /// column '…'"). Everything that depends on one of these columns therefore
    /// has to be dropped first and re-created afterwards. The set is computed
    /// once here and consumed twice: by <see cref="EmitAlter"/> for the
    /// constraints it owns, and by <see cref="ScriptGenerator"/> for the indexes
    /// it owns.
    /// <para>
    /// Membership is decided by <see cref="ColumnRequiresAlterColumn"/>, NOT by
    /// <see cref="ColumnShapeEqual"/>: the latter also compares the DEFAULT
    /// expression, so <c>((0)) → ((1))</c> put the column in here and dropped the
    /// primary key and every index covering it — for a change that needs nothing
    /// but a DROP/ADD of the DF_ constraint.
    /// </para>
    /// </remarks>
    internal static IReadOnlySet<string> ColumnsDroppedOrAltered(Table newT, Table oldT, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(newT);
        ArgumentNullException.ThrowIfNull(oldT);
        ArgumentNullException.ThrowIfNull(names);

        HashSet<string> touched = new(names);
        // A rebuild re-creates the whole table, so nothing is "altered in place".
        if (RequiresFullRebuild(newT, oldT, names)) { return touched; }

        var newColsByName = newT.Columns.ToDictionary(c => c.Name, names);
        var oldColsByName = oldT.Columns.ToDictionary(c => c.Name, names);

        foreach (Column oldCol in oldT.Columns)
        {
            if (!newColsByName.ContainsKey(oldCol.Name))
            {
                touched.Add(oldCol.Name);   // section 2 — DROP COLUMN
            }
        }
        foreach (Column newCol in newT.Columns)
        {
            if (!oldColsByName.TryGetValue(newCol.Name, out Column? oldCol)) { continue; }
            if (!ColumnRequiresAlterColumn(oldCol, newCol)) { continue; }
            touched.Add(newCol.Name);       // section 3 — ALTER COLUMN, or DROP+ADD
        }
        return touched;
    }

    /// <summary>
    /// True when going from <paramref name="oldCol"/> to <paramref name="newCol"/>
    /// needs DDL against the COLUMN itself — the thing a dependent index, key or
    /// CHECK constraint blocks.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="ColumnShapeEqual"/>, which also
    /// compares the DEFAULT expression. A DEFAULT change is carried entirely by
    /// dropping and re-adding the DF_ constraint (sections 1 and 5 of
    /// <see cref="EmitAlter"/>) and touches no index and no key, so feeding it
    /// into the touched-column set was pure damage: a <c>((0)) → ((1))</c> default
    /// change dropped the primary key — Msg 3725 when an FK references it —
    /// and rebuilt the clustered index inside a batch with a 60 s cap, while
    /// section 3 emitted nothing but a no-op ALTER COLUMN.
    /// </remarks>
    private static bool ColumnRequiresAlterColumn(Column oldCol, Column newCol) =>
        !string.Equals(oldCol.DataType, newCol.DataType, StringComparison.OrdinalIgnoreCase)
        || oldCol.IsNullable != newCol.IsNullable
        || !string.Equals(oldCol.Collation, newCol.Collation, StringComparison.OrdinalIgnoreCase)
        || !BodyNormalizer.ExpressionsEqual(oldCol.ComputedExpression, newCol.ComputedExpression)
        || oldCol.IsIdentity != newCol.IsIdentity
        || (oldCol.IsIdentity && newCol.IsIdentity
            && (oldCol.IdentitySeed != newCol.IdentitySeed
                || oldCol.IdentityIncrement != newCol.IdentityIncrement));

    /// <summary>
    /// True when <paramref name="c"/> depends on one of
    /// <paramref name="touchedColumns"/> and therefore blocks the column DDL.
    /// </summary>
    /// <remarks>
    /// DEFAULT constraints are deliberately excluded: ALTER COLUMN tolerates a
    /// bound default, and a column being DROPped takes its default with it (the
    /// default disappears from the source side too, so the normal
    /// disappeared-constraint rule already drops it). Dropping and re-adding
    /// defaults here would be churn plus a chance of colliding with the inline
    /// default emitted on an ADD.
    /// </remarks>
    private static bool DependsOnColumn(Constraint c, IReadOnlySet<string> touchedColumns) => c switch
    {
        PrimaryKey pk => pk.Columns.Any(touchedColumns.Contains),
        UniqueConstraint uq => uq.Columns.Any(touchedColumns.Contains),
        // Catalog CHECK definitions bracket their column references, so the
        // bracketed tokens ARE the column list. Testing them against the set
        // lets the set's own comparer decide, which keeps the case rule in one
        // place instead of re-deriving a StringComparison here.
        CheckConstraint ck => ck.Expression is { } expr
            && BracketedNames(expr).Any(touchedColumns.Contains),
        _ => false,
    };

    /// <summary>
    /// The identifiers a catalog expression brackets, e.g. <c>([Qty]&gt;(0))</c>
    /// yields <c>Qty</c>.
    /// </summary>
    /// <remarks>
    /// A <c>]</c> inside a name is written doubled, by the catalog and by
    /// <see cref="Sql.Q(string)"/> alike. Stopping at the first <c>]</c> cut a
    /// column named <c>a]b</c> down to <c>a</c>, which matched nothing in the
    /// touched-column set, so the CHECK constraint over it was never dropped and
    /// the ALTER COLUMN it blocks died on Msg 5074.
    /// </remarks>
    private static IEnumerable<string> BracketedNames(string expression)
    {
        int i = 0;
        while (true)
        {
            int open = expression.IndexOf('[', i);
            if (open < 0) { yield break; }
            int close = open + 1;
            while (true)
            {
                close = expression.IndexOf(']', close);
                if (close < 0) { yield break; }
                // A doubled ']' is content, not the end of the identifier.
                if (close + 1 < expression.Length && expression[close + 1] == ']')
                {
                    close += 2;
                    continue;
                }
                break;
            }
            yield return expression[(open + 1)..close].Replace("]]", "]", StringComparison.Ordinal);
            i = close + 1;
        }
    }

    /// <summary>
    /// True when <paramref name="index"/> covers one of
    /// <paramref name="touchedColumns"/>, as a key or an included column.
    /// </summary>
    internal static bool IndexDependsOnColumn(TableIndex index, IReadOnlySet<string> touchedColumns)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(touchedColumns);
        return index.KeyColumns.Any(k => touchedColumns.Contains(k.Name))
            || index.IncludedColumns.Any(touchedColumns.Contains);
    }

    /// <summary>
    /// Detects whether the alter from <paramref name="oldT"/> to
    /// <paramref name="newT"/> needs a full temp-table rebuild rather than
    /// per-column ALTER statements. Triggered when an existing column's
    /// IDENTITY flag flips (on or off) or when its seed/increment changes —
    /// none of those are expressible via ALTER COLUMN, and DROP COLUMN +
    /// ADD COLUMN would silently lose row data.
    /// </summary>
    internal static bool RequiresFullRebuild(Table newT, Table oldT, StringComparer names)
    {
        var oldByName = oldT.Columns.ToDictionary(c => c.Name, names);
        foreach (Column newCol in newT.Columns)
        {
            if (!oldByName.TryGetValue(newCol.Name, out Column? oldCol)) { continue; }
            if (oldCol.IsIdentity != newCol.IsIdentity) { return true; }
            if (oldCol.IsIdentity && newCol.IsIdentity)
            {
                if (oldCol.IdentitySeed != newCol.IdentitySeed) { return true; }
                if (oldCol.IdentityIncrement != newCol.IdentityIncrement) { return true; }
            }
        }
        return false;
    }

    /// <summary>
    /// Emits the spec §3.4 temp-table dance: CREATE a parallel <c>_tmp</c>
    /// table with the new shape, optionally <c>SET IDENTITY_INSERT … ON</c>,
    /// copy the rows over (excluding computed columns, which re-derive),
    /// DROP the original, then <c>sp_rename</c> the temp table into place.
    /// </summary>
    private static string EmitRebuild(Table newT, Table oldT, StringComparer names)
    {
        string qualifiedOld = $"{Sql.Q(newT.Schema, newT.Name)}";
        string tmpName = $"{newT.Name}_tmp";
        string qualifiedTmp = $"{Sql.Q(newT.Schema, tmpName)}";

        HashSet<string> oldColNames = new(oldT.Columns.Select(c => c.Name), names);
        List<string> commonInsertable =
        [
            .. newT.Columns
                .Where(c => oldColNames.Contains(c.Name) && c.ComputedExpression is null)
                .OrderBy(c => c.Ordinal)
                .Select(c => $"{Sql.Q(c.Name)}")
        ];

        StringBuilder sb = new();

        // M13-PARITY.6 #33 — PK-around-swap pattern. A constraint name is
        // unique per SCHEMA (constraints are sys.objects rows carrying their
        // parent table's schema_id) — NOT per database — and `[X_tmp]` is
        // created in the same schema as `[X]`, so it cannot carry the same PK
        // name while the original still exists. Drop the existing non-FK named
        // constraints (PK / UQ / CK / named DEFAULT) before the rebuild and
        // re-create them after `sp_rename`. This also mirrors Redgate SQL Compare's
        // shape for scenario 03 — the safer pattern when other tables hold
        // FKs pointing AT this PK (inbound-FK lifecycle is orchestrated by
        // <see cref="ScriptGenerator"/>).
        List<Constraint> namedNonFkConstraintsOnOld =
            [.. oldT.Constraints.Where(IsNamedNonFkConstraint)];
        foreach (Constraint oldC in namedNonFkConstraintsOnOld)
        {
            sb.Append("ALTER TABLE ").Append(qualifiedOld)
              .Append(" DROP CONSTRAINT ").Append(Sql.Q(oldC.Name)).AppendLine(";");
        }

        // _tmp is created *without* named constraints; we add them back
        // after the rename so their names are restored on the final table.
        sb.Append(EmitCreate(
            newT with { Name = tmpName },
            includeNamedConstraints: false));

        bool newHasIdentity = newT.Columns.Any(c => c.IsIdentity);
        if (newHasIdentity)
        {
            sb.Append("SET IDENTITY_INSERT ").Append(qualifiedTmp).AppendLine(" ON;");
        }

        if (commonInsertable.Count > 0)
        {
            string colList = string.Join(", ", commonInsertable);
            sb.Append("INSERT INTO ").Append(qualifiedTmp)
              .Append(" (").Append(colList).Append(") SELECT ")
              .Append(colList).Append(" FROM ").Append(qualifiedOld).AppendLine(";");
        }

        if (newHasIdentity)
        {
            sb.Append("SET IDENTITY_INSERT ").Append(qualifiedTmp).AppendLine(" OFF;");
        }

        sb.Append("DROP TABLE ").Append(qualifiedOld).AppendLine(";");
        sb.Append("EXEC sp_rename ").Append(Sql.L(qualifiedTmp)).Append(", ")
          .Append(Sql.L(newT.Name)).AppendLine(";");

        // Re-add the named non-FK constraints from the source-side table
        // (newT) — these are the constraints the final shape requires.
        foreach (Constraint c in newT.Constraints.Where(IsNamedNonFkConstraint))
        {
            string body = FormatStandaloneConstraintBody(c);
            if (body.Length == 0) { continue; }
            sb.Append("ALTER TABLE ").Append(qualifiedOld)
              .Append(" ADD CONSTRAINT ").Append(Sql.Q(c.Name)).Append(' ')
              .Append(body).AppendLine(";");
        }
        return sb.ToString();
    }

    private static bool IsNamedNonFkConstraint(Constraint c) => c switch
    {
        PrimaryKey => true,
        UniqueConstraint => true,
        CheckConstraint => true,
        DefaultConstraint => true,
        _ => false,
    };

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
        return string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase)
            && BodyNormalizer.ExpressionsEqual(a.DefaultExpression, b.DefaultExpression)
            && BodyNormalizer.ExpressionsEqual(a.ComputedExpression, b.ComputedExpression);
    }

    /// <summary>
    /// Shape-equality for non-FK constraints (PK/UQ/CK/Default). Mirrors the
    /// rules used by <c>ComparisonEngine.ConstraintShapeEqual</c> so EmitAlter
    /// only emits DROP+ADD when ComparisonEngine would consider the constraint
    /// to have changed.
    /// </summary>
    private static bool ConstraintShapeEqual(Constraint a, Constraint b, StringComparer names) => (a, b) switch
    {
        (PrimaryKey pa, PrimaryKey pb) =>
            pa.IsClustered == pb.IsClustered && pa.Columns.SequenceEqual(pb.Columns, names),
        (UniqueConstraint ua, UniqueConstraint ub) =>
            ua.IsClustered == ub.IsClustered && ua.Columns.SequenceEqual(ub.Columns, names),
        (CheckConstraint ca, CheckConstraint cb) =>
            BodyNormalizer.ExpressionsEqual(ca.Expression, cb.Expression),
        (DefaultConstraint da, DefaultConstraint db) =>
            names.Equals(da.ColumnName, db.ColumnName)
            && BodyNormalizer.ExpressionsEqual(da.Expression, db.Expression),
        _ => false,
    };

    private static string FormatStandaloneConstraintBody(Constraint c) => c switch
    {
        PrimaryKey pk => $"PRIMARY KEY {(pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", pk.Columns.Select(Sql.Q))})",
        UniqueConstraint uq => $"UNIQUE {(uq.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", uq.Columns.Select(Sql.Q))})",
        CheckConstraint ck => $"CHECK {ck.Expression}",
        DefaultConstraint df => $"DEFAULT {df.Expression} FOR {Sql.Q(df.ColumnName)}",
        ForeignKey => string.Empty,
        _ => string.Empty,
    };

    private static Dictionary<string, DefaultConstraint> NamedDefaultsByColumn(Table table, StringComparer names)
    {
        Dictionary<string, DefaultConstraint> map = new(names);
        foreach (DefaultConstraint df in table.Constraints.OfType<DefaultConstraint>())
        {
            map[df.ColumnName] = df;
        }
        return map;
    }

    /// <summary>
    /// Formats one column definition, valid both inside CREATE TABLE and after
    /// <c>ALTER TABLE … ADD</c>.
    /// </summary>
    /// <param name="c">The column to format.</param>
    /// <param name="namedDefault">
    /// The named DEFAULT constraint bound to this column, when the table has
    /// one; <see langword="null"/> when it has none.
    /// </param>
    /// <param name="inlineNamedDefault">
    /// <see langword="true"/> to emit <paramref name="namedDefault"/> inline as
    /// <c>CONSTRAINT [name] DEFAULT (expr)</c> — the only form CREATE TABLE
    /// accepts, and the form that keeps ALTER TABLE ADD legal on a populated
    /// table. <see langword="false"/> to omit it because the caller re-adds it
    /// standalone afterwards (the rebuild path).
    /// </param>
    private static string FormatColumn(Column c, DefaultConstraint? namedDefault, bool inlineNamedDefault)
    {
        StringBuilder sb = new();
        sb.Append(Sql.Q(c.Name)).Append(' ');

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

        sb.Append(SqlTypeFormatter.FormatColumnType(c.DataType));
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
        AppendCollation(sb, c);
        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (namedDefault is not null && inlineNamedDefault)
        {
            sb.Append(" CONSTRAINT ").Append(Sql.Q(namedDefault.Name)).Append(" DEFAULT ")
              .Append(namedDefault.Expression);
        }
        else if (namedDefault is null && !string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends an explicit <c>COLLATE &lt;name&gt;</c> clause whenever the
    /// column carries a collation (i.e. is a string type). Mirrors Redgate SQL
    /// Compare, which always emits the explicit collation on character columns.
    /// Non-string columns (null collation) are skipped silently.
    /// </summary>
    private static void AppendCollation(StringBuilder sb, Column c)
    {
        if (string.IsNullOrEmpty(c.Collation)) { return; }
        sb.Append(" COLLATE ").Append(c.Collation);
    }
}

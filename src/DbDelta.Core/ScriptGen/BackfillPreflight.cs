using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// One column the deploy cannot add without a value for the rows already in the
/// table.
/// </summary>
/// <param name="Schema">Schema of the table gaining the column.</param>
/// <param name="Table">Table gaining the column.</param>
/// <param name="Column">The new column's name.</param>
/// <param name="DataType">
/// Its declared type as the operator sees it in the dialog — for an ALIAS
/// column that is the bare alias name, deliberately: showing <c>bigint</c> for
/// a column declared <c>app.MioTipo</c> would hide which column is being seeded.
/// </param>
/// <param name="BaseType">
/// The system type an ALIAS in <paramref name="DataType"/> resolves to, null
/// for a built-in type and null when the alias could not be found. It exists
/// only so <see cref="SuggestedValue"/> can reason about the shape the server
/// will actually store.
/// </param>
public sealed record BackfillRequirement(
    string Schema,
    string Table,
    string Column,
    string DataType,
    string? BaseType = null)
{
    /// <summary>The key <c>ScriptGenerator</c> looks the supplied value up by.</summary>
    public (string Schema, string Table, string Column) Key => (Schema, Table, Column);

    /// <summary>
    /// A value of the right shape for the column, offered as a starting point.
    /// Deliberately dull — it exists to make the column addable, not to mean
    /// anything. The operator is expected to replace it wherever the column
    /// carries meaning. Empty when no dull value of the right shape exists;
    /// the dialog already refuses to confirm a blank row, so an honest blank
    /// asks the operator instead of inventing something.
    /// </summary>
    /// <remarks>
    /// Every arm below was measured on <c>mssql/server:2022-latest</c> by
    /// adding the column NOT NULL to a table with a row in it, not reasoned
    /// about. The old single <c>_ => "('')"</c> fallback is what made that
    /// necessary, and it was wrong in two different ways at once. Loudly, for
    /// <c>binary</c>, <c>varbinary</c>, <c>hierarchyid</c>, <c>geometry</c> and
    /// <c>geography</c>: the server refuses the DEFAULT and the deploy stops.
    /// SILENTLY, which is worse and is why this is not a cosmetic fix, for an
    /// ALIAS type over a non-string base — <c>('')</c> on an alias over
    /// <c>bigint</c> does not fail, it stores <c>0</c>; over <c>datetime2</c> it
    /// stores <c>1900-01-01</c>. A valid statement quietly meaning something
    /// else is the exact shape the <c>Unscriptable*</c> family exists to refuse.
    /// <para>
    /// The string family keeps <c>('')</c> because for it that answer was always
    /// right; it is now listed rather than reached by falling off the end, so a
    /// type nobody thought of gets a blank instead of a string's answer.
    /// </para>
    /// </remarks>
    public string SuggestedValue
    {
        get
        {
            // An alias resolves to the type the server will actually store, and
            // it is the ONLY reason BaseType exists. DataType stays the alias
            // name for the operator to read.
            string t = (BaseType ?? DataType).Split('(')[0].Trim().ToUpperInvariant();
            return t switch
            {
                "BIT" => "((0))",
                "TINYINT" or "SMALLINT" or "INT" or "BIGINT" => "((0))",
                "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" or "FLOAT" or "REAL" => "((0))",
                "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET" => "(SYSUTCDATETIME())",
                "TIME" => "(CONVERT(time, '00:00:00'))",
                "UNIQUEIDENTIFIER" => "(NEWID())",
                "CHAR" or "VARCHAR" or "NCHAR" or "NVARCHAR" or "TEXT" or "NTEXT" or "SYSNAME"
                    or "XML" or "SQL_VARIANT" => "('')",
                "BINARY" or "VARBINARY" or "IMAGE" => "(0x)",
                "HIERARCHYID" => "(hierarchyid::GetRoot())",
                "GEOMETRY" => "(geometry::Parse('POINT EMPTY'))",
                "GEOGRAPHY" => "(geography::Parse('POINT EMPTY'))",
                _ => "",
            };
        }
    }
}

/// <summary>
/// Finds, before a line of SQL runs, every column the script would try to add
/// as <c>NOT NULL</c> with nothing to put in the rows that already exist.
/// </summary>
/// <remarks>
/// <c>ALTER TABLE … ADD</c> of a NOT NULL column is legal on a populated table
/// only when a DEFAULT travels with it (Msg 4901). If the source schema has no
/// default, the change is not deployable by any tool without inventing a value:
/// Redgate rebuilds the table but leaves the column out of its INSERT list, so
/// its script dies on the same data, later and with a DROP TABLE already
/// queued. Answering this up front is the difference between a question asked
/// of a human and a deploy that fails halfway.
/// </remarks>
public static class BackfillPreflight
{
    /// <summary>
    /// Scans the tables a run would alter and returns what it cannot add
    /// unaided, in a stable order.
    /// </summary>
    /// <param name="result">The comparison, for its name comparer.</param>
    /// <param name="selection">
    /// The subset being deployed. Null means every difference — a table outside
    /// the selection is not altered, so it cannot raise the problem.
    /// </param>
    public static IReadOnlyList<BackfillRequirement> Scan(
        ComparisonResult result,
        IEnumerable<DifferencePair>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The source-side alias types, and no new parameter was needed to get
        // them: CompareUserDefinedTypes yields a pair for EVERY identity at
        // EVERY status — Identical included — with SideA the source type. That
        // matters, because an alias type the deploy does not touch is the
        // COMMON case: a map built only from Different pairs would miss exactly
        // the columns this is here for.
        Dictionary<(string Schema, string Name), UserDefinedType> aliasTypes =
            new(NameKey.Pair(result.NameComparer));
        foreach (DifferencePair udtPair in result.Differences
                     .Where(p => p.Identity.Kind == "UserDefinedType"))
        {
            if (udtPair.SideA is UserDefinedType udt) { aliasTypes[(udt.Schema, udt.Name)] = udt; }
        }

        List<BackfillRequirement> found = [];
        foreach (DifferencePair pair in (selection ?? result.Differences)
                     .Where(p => p.Identity.Kind == "Table" && p.Status == DifferenceStatus.Different))
        {
            if (pair.SideA is not Table src || pair.SideB is not Table tgt) { continue; }
            foreach (string column in
                     TableScriptEmitter.ColumnsNeedingABackfillDefault(src, tgt, result.NameComparer))
            {
                Column col = src.Columns.First(c => result.NameComparer.Equals(c.Name, column));

                // TypeSchema, never the table's schema: an alias lives where it
                // was created, which is not necessarily where the table using
                // it lives. Null means a hand-built model that never said, and
                // then no lookup happens and the suggestion falls back to the
                // alias name — which yields a blank, not a wrong value.
                string? baseType =
                    col.IsUserDefinedType
                    && col.TypeSchema is string typeSchema
                    && aliasTypes.TryGetValue((typeSchema, col.DataType), out UserDefinedType? alias)
                        ? alias.BaseTypeName
                        : null;

                found.Add(new BackfillRequirement(src.Schema, src.Name, column, col.DataType, baseType));
            }
        }
        return found;
    }
}

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
/// <param name="DataType">Its declared type, so a caller can suggest a value.</param>
public sealed record BackfillRequirement(string Schema, string Table, string Column, string DataType)
{
    /// <summary>The key <c>ScriptGenerator</c> looks the supplied value up by.</summary>
    public (string Schema, string Table, string Column) Key => (Schema, Table, Column);

    /// <summary>
    /// A value of the right shape for <see cref="DataType"/>, offered as a
    /// starting point. Deliberately dull — it exists to make the column
    /// addable, not to mean anything. The operator is expected to replace it
    /// wherever the column carries meaning.
    /// </summary>
    public string SuggestedValue
    {
        get
        {
            string t = DataType.Split('(')[0].Trim().ToUpperInvariant();
            return t switch
            {
                "BIT" => "((0))",
                "TINYINT" or "SMALLINT" or "INT" or "BIGINT" => "((0))",
                "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" or "FLOAT" or "REAL" => "((0))",
                "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET" => "(SYSUTCDATETIME())",
                "TIME" => "(CONVERT(time, '00:00:00'))",
                "UNIQUEIDENTIFIER" => "(NEWID())",
                _ => "('')",
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
        List<BackfillRequirement> found = [];
        foreach (DifferencePair pair in (selection ?? result.Differences)
                     .Where(p => p.Identity.Kind == "Table" && p.Status == DifferenceStatus.Different))
        {
            if (pair.SideA is not Table src || pair.SideB is not Table tgt) { continue; }
            foreach (string column in
                     TableScriptEmitter.ColumnsNeedingABackfillDefault(src, tgt, result.NameComparer))
            {
                string dataType = src.Columns
                    .First(c => result.NameComparer.Equals(c.Name, column)).DataType;
                found.Add(new BackfillRequirement(src.Schema, src.Name, column, dataType));
            }
        }
        return found;
    }
}

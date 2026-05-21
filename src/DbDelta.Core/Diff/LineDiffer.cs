namespace DbDelta.Core.Diff;

public static class LineDiffer
{
    /// <summary>
    /// Computes per-line diff. Null/empty input is treated as zero lines.
    /// Line endings (\r\n, \r, \n) are normalised to \n before splitting,
    /// and the final empty line after a trailing newline is dropped.
    /// </summary>
    public static IReadOnlyList<LineDiff> Compute(string? sourceSql, string? targetSql)
    {
        string[] src = SplitLines(sourceSql);
        string[] tgt = SplitLines(targetSql);

        // Build LCS via O(N*M) DP — well within budget for SQL bodies.
        int[,] lcs = BuildLcsTable(src, tgt);

        // Walk back through the LCS table to produce an edit script.
        // editScript[i] = (srcLine, tgtLine) for Unchanged; one null for
        // Added/Removed. We collect raw edit ops then pair up delete+insert runs.
        var editOps = new List<(string? Src, string? Tgt)>();
        CollectEdits(lcs, src, tgt, src.Length, tgt.Length, editOps);

        return BuildAlignedRows(editOps);
    }

    /// <summary>
    /// Groups runs of non-Unchanged rows into <see cref="DiffSection"/>s
    /// for minimap rendering.
    /// </summary>
    public static IReadOnlyList<DiffSection> SectionsFrom(IReadOnlyList<LineDiff> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<DiffSection> sections = [];
        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Status == LineStatus.Unchanged)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < rows.Count && rows[i].Status != LineStatus.Unchanged)
            {
                i++;
            }

            sections.Add(new DiffSection(start, i - start));
        }

        return sections;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static string[] SplitLines(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return [];
        }

        // Normalise line endings to \n then split.
        string normalised = sql.Replace("\r\n", "\n", StringComparison.Ordinal)
                               .Replace("\r", "\n", StringComparison.Ordinal);

        string[] lines = normalised.Split('\n');

        // Drop a single trailing empty line produced by a trailing newline.
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    /// Builds the standard LCS DP table: lcs[i,j] = LCS length of src[0..i-1]
    /// vs tgt[0..j-1].
    /// </summary>
    private static int[,] BuildLcsTable(string[] src, string[] tgt)
    {
        int m = src.Length;
        int n = tgt.Length;
        int[,] table = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                table[i, j] = string.Equals(src[i - 1], tgt[j - 1], StringComparison.Ordinal)
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table;
    }

    /// <summary>
    /// Recursively walks the LCS back-pointer table and appends raw edit ops
    /// to <paramref name="ops"/>. Each op is (srcLine, tgtLine):
    ///   (non-null, non-null) = Unchanged
    ///   (non-null, null)     = Removed (delete)
    ///   (null, non-null)     = Added (insert)
    /// </summary>
    private static void CollectEdits(
        int[,] lcs,
        string[] src,
        string[] tgt,
        int i,
        int j,
        List<(string? Src, string? Tgt)> ops)
    {
        // Use an iterative approach to avoid stack overflow on large inputs.
        var stack = new Stack<(int I, int J)>();
        stack.Push((i, j));

        // We build the list in forward order by collecting in reverse then reversing.
        var reversed = new List<(string? Src, string? Tgt)>();

        while (stack.Count > 0)
        {
            (int ci, int cj) = stack.Pop();

            if (ci == 0 && cj == 0)
            {
                continue;
            }

            if (ci > 0 && cj > 0
                && string.Equals(src[ci - 1], tgt[cj - 1], StringComparison.Ordinal))
            {
                // Unchanged — part of LCS
                reversed.Add((src[ci - 1], tgt[cj - 1]));
                stack.Push((ci - 1, cj - 1));
            }
            else if (cj > 0 && (ci == 0 || lcs[ci, cj - 1] >= lcs[ci - 1, cj]))
            {
                // Insert (target-only line)
                reversed.Add((null, tgt[cj - 1]));
                stack.Push((ci, cj - 1));
            }
            else
            {
                // Delete (source-only line)
                reversed.Add((src[ci - 1], null));
                stack.Push((ci - 1, cj));
            }
        }

        // reversed was built right-to-left; reverse to get forward order.
        reversed.Reverse();
        ops.AddRange(reversed);
    }

    /// <summary>
    /// Converts raw edit ops (Unchanged, delete, insert) into aligned dual-pane rows.
    /// Contiguous runs of deletes + inserts are paired: paired → Modified, excess
    /// deletes → Removed, excess inserts → Added.
    /// </summary>
    private static IReadOnlyList<LineDiff> BuildAlignedRows(List<(string? Src, string? Tgt)> ops)
    {
        var result = new List<LineDiff>();
        int rowIndex = 0;
        int srcLineNo = 0;
        int tgtLineNo = 0;

        int i = 0;
        while (i < ops.Count)
        {
            (string? src, string? tgt) = ops[i];

            if (src is not null && tgt is not null)
            {
                // Unchanged aligned pair
                srcLineNo++;
                tgtLineNo++;
                result.Add(new LineDiff(rowIndex++, srcLineNo, tgtLineNo, src, tgt, LineStatus.Unchanged));
                i++;
                continue;
            }

            // Collect the current run of non-Unchanged ops (deletes then inserts, or
            // any interleaving — pair them up in order of appearance).
            var deletes = new List<string>();
            var inserts = new List<string>();

            while (i < ops.Count && !(ops[i].Src is not null && ops[i].Tgt is not null))
            {
                if (ops[i].Src is not null)
                {
                    deletes.Add(ops[i].Src!);
                }
                else
                {
                    inserts.Add(ops[i].Tgt!);
                }

                i++;
            }

            // Pair deletes with inserts → Modified
            int pairs = Math.Min(deletes.Count, inserts.Count);
            for (int p = 0; p < pairs; p++)
            {
                srcLineNo++;
                tgtLineNo++;
                result.Add(new LineDiff(
                    rowIndex++,
                    srcLineNo,
                    tgtLineNo,
                    deletes[p],
                    inserts[p],
                    LineStatus.Modified));
            }

            // Excess deletes → Removed
            for (int d = pairs; d < deletes.Count; d++)
            {
                srcLineNo++;
                result.Add(new LineDiff(
                    rowIndex++,
                    srcLineNo,
                    null,
                    deletes[d],
                    null,
                    LineStatus.Removed));
            }

            // Excess inserts → Added
            for (int a = pairs; a < inserts.Count; a++)
            {
                tgtLineNo++;
                result.Add(new LineDiff(
                    rowIndex++,
                    null,
                    tgtLineNo,
                    null,
                    inserts[a],
                    LineStatus.Added));
            }
        }

        return result;
    }
}

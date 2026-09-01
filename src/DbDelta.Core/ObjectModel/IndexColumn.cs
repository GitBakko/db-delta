namespace DbDelta.Core.ObjectModel;

/// <summary>
/// One column participating in a key list — an index's, or a PRIMARY KEY's or
/// UNIQUE constraint's.
/// </summary>
public sealed record IndexColumn(string Name, bool IsDescending)
{
    /// <summary>
    /// A bare column name is an ASCENDING key column, which is what T-SQL means
    /// by <c>(A, B)</c>.
    /// </summary>
    /// <remarks>
    /// It exists so <c>PrimaryKey</c> and <c>UniqueConstraint</c> could start
    /// carrying a direction without splitting the truth in two. They held
    /// <c>IReadOnlyList&lt;string&gt;</c>, and a <c>PRIMARY KEY (A ASC, B
    /// DESC)</c> was read back as all-ascending, compared Identical and
    /// flattened by the next rebuild — measured on
    /// <c>mssql/server:2022-latest</c>: <c>is_descending_key</c> 1 before,
    /// 0 after. Adding a second list beside the names would have left two
    /// things to disagree; widening the existing one costs an implicit
    /// conversion, and a collection expression applies it per element, so the
    /// sixty call sites that only ever meant "these columns, in this order"
    /// still say exactly that.
    /// </remarks>
    public static implicit operator IndexColumn(string name)
    {
        return FromName(name);
    }

    /// <summary>The named alternative to the implicit conversion, for callers that prefer it.</summary>
    public static IndexColumn FromName(string name) => new(name, IsDescending: false);

    /// <summary>
    /// True when two key lists are the same key: same columns, same order, same
    /// direction.
    /// </summary>
    /// <param name="a">The first key list.</param>
    /// <param name="b">The second key list.</param>
    /// <param name="names">
    /// The case-folding rule for the column NAMES, and it is required rather
    /// than defaulted: the engine pairs the objects around these calls with the
    /// target's collation, and a key comparison that folded case on its own
    /// would be the one comparison inside a matched table disagreeing with the
    /// table itself. Direction is a bool and never folds.
    /// </param>
    /// <remarks>
    /// The single seam all THREE key comparisons pass through — ComparisonEngine's
    /// constraint equality, ConstraintPairing's shape match, and
    /// TableScriptEmitter's ALTER decision. All three took a comparer and all
    /// three compared names only; a direction reaching two of them and not the
    /// third would leave a key that reads Identical on one path and Different on
    /// another.
    /// </remarks>
    public static bool KeysMatch(
        IReadOnlyList<IndexColumn> a,
        IReadOnlyList<IndexColumn> b,
        StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(names);
        if (a.Count != b.Count) { return false; }
        for (int i = 0; i < a.Count; i++)
        {
            if (!names.Equals(a[i].Name, b[i].Name)) { return false; }
            if (a[i].IsDescending != b[i].IsDescending) { return false; }
        }
        return true;
    }
}

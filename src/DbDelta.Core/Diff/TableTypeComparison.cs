using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Diff;

/// <summary>
/// Equality for a table-type UDT: its columns, and the keys, checks and inline
/// indexes SQL Server lets one declare.
/// </summary>
/// <remarks>
/// Its own file because it is its own question. A table type has no ALTER, so
/// every difference here means DROP + CREATE and every attribute the comparison
/// cannot see is one the deploy drops in silence — the shape of the R1 defect
/// in docs/parity/redgate-2026-08-31.md. Keeping it out of ComparisonEngine
/// also keeps that file from growing past the size the project caps it at.
/// </remarks>
internal static class TableTypeComparison
{
    internal static bool Equal(TableTypeUdt a, TableTypeUdt b, StringComparer names)
    {
        if (a.Columns.Count != b.Columns.Count) { return false; }
        var bByName = b.Columns.ToDictionary(c => c.Name, names);
        foreach (Column ac in a.Columns)
        {
            if (!bByName.TryGetValue(ac.Name, out Column? bc)) { return false; }
            if (!string.Equals(ac.DataType, bc.DataType, StringComparison.OrdinalIgnoreCase)) { return false; }
            if (ac.IsNullable != bc.IsNullable) { return false; }
            if (ac.Ordinal != bc.Ordinal) { return false; }
            // M13-PARITY.5 #32 — UDTT column collation participates in equality
            // for the same reason it does on plain tables.
            if (!string.Equals(ac.Collation, bc.Collation, StringComparison.OrdinalIgnoreCase)) { return false; }
            // A table type has no ALTER, so a DEFAULT, an IDENTITY or a computed
            // expression the two sides disagree on is a rebuild — and one the
            // model did not carry was dropped by that rebuild unreported.
            // Through BodyNormalizer, exactly as the plain-table path compares
            // the same two expressions: two servers spell one predicate with
            // different whitespace, and comparing them raw turns that into a
            // Different that emits a DROP TYPE for nothing. Case still counts —
            // Normalize does not fold it — so DEFAULT 'A' is not DEFAULT 'a'.
            if (!BodyNormalizer.ExpressionsEqual(ac.DefaultExpression, bc.DefaultExpression)) { return false; }
            if (!BodyNormalizer.ExpressionsEqual(ac.ComputedExpression, bc.ComputedExpression)) { return false; }
            if (ac.IsIdentity != bc.IsIdentity) { return false; }
            if (ac.IdentitySeed != bc.IdentitySeed) { return false; }
            if (ac.IdentityIncrement != bc.IdentityIncrement) { return false; }
        }
        return KeysEqual(a, b, names);
    }

    /// <summary>
    /// Compares a table type's PRIMARY KEY, UNIQUE constraints, inline indexes
    /// and CHECK constraints.
    /// </summary>
    /// <remarks>
    /// <b>The keys pair by shape, not by name, and that is forced rather than
    /// chosen.</b> <c>CREATE TYPE … AS TABLE</c> rejects a <c>CONSTRAINT</c>
    /// clause, so a PK's and a UNIQUE's name was minted by the server from an
    /// <c>object_id</c>; two servers carrying the identical type disagree on
    /// them by construction. An inline INDEX is the exception — that name is
    /// the user's, so it is part of the shape.
    /// <para>
    /// CHECK definitions are expressions, not identifiers, so they go through
    /// <see cref="BodyNormalizer"/> and then compare ordinally — the same route
    /// every other CHECK in the engine takes (<c>ConstraintPairing</c>,
    /// <c>ComparisonEngine.ConstraintsEqual</c>). Comparing them raw would make
    /// two servers that merely space one predicate differently look Different,
    /// and for a table type Different means an unnecessary DROP TYPE.
    /// </para>
    /// </remarks>
    private static bool KeysEqual(TableTypeUdt a, TableTypeUdt b, StringComparer names) =>
        SameMultiset(a.Keys.Select(Shape), b.Keys.Select(Shape), names)
        && SameMultiset(
            a.CheckConstraints.Select(c => BodyNormalizer.Normalize(c.Expression) ?? string.Empty),
            b.CheckConstraints.Select(c => BodyNormalizer.Normalize(c.Expression) ?? string.Empty),
            StringComparer.Ordinal);

    /// <summary>
    /// The index reduced to what the target will actually hold. Key column
    /// ORDER is significant — <c>(A, B)</c> is not <c>(B, A)</c> — and so is
    /// each column's direction, which SQL Server accepts on a table type's key
    /// and which a comparison blind to it would let a rebuild flatten.
    /// </summary>
    private static string Shape(TableIndex ix) =>
        $"{(ix.IsPrimaryKey ? "PK" : ix.IsUniqueConstraint ? "UQ" : "IX")}|"
        + $"{(ix.IsSystemNamed ? string.Empty : ix.Name)}|{ix.IsUnique}|{ix.IsClustered}|"
        + string.Join(",", ix.KeyColumns.Select(k => $"{k.Name}:{k.IsDescending}"))
        + "|" + string.Join(",", ix.IncludedColumns);

    /// <summary>
    /// Order-insensitive equality over the shapes, under the comparer the two
    /// objects were paired with — identifiers inside a shape have to fold the
    /// way the target server folds them, and the literal separators are the
    /// same on both sides so folding them changes nothing.
    /// </summary>
    private static bool SameMultiset(IEnumerable<string> a, IEnumerable<string> b, StringComparer comparer)
    {
        List<string> left = [.. a.Order(comparer)];
        List<string> right = [.. b.Order(comparer)];
        return left.SequenceEqual(right, comparer);
    }
}

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Equality for the (schema, table[, name]) tuples the generator uses as set
/// and dictionary keys, honouring the target's collation.
/// </summary>
/// <remarks>
/// <para>
/// The generator keys a dozen sets on value tuples of strings, whose built-in
/// equality is ordinal. That was harmless while the comparison engine paired
/// objects ordinally too — two spellings of one table never met. Once the
/// engine started pairing by the target's collation, a pair could hold
/// <c>dbo.Clienti</c> on the source and <c>dbo.CLIENTI</c> on the target, and
/// every set keyed from one side and probed from the other silently missed:
/// the inbound foreign key to a rebuilt table was never dropped (Msg 3726 on
/// the DROP TABLE), and a constraint claimed under both spellings was dropped
/// twice (Msg 3728). Both abort the deploy under XACT_ABORT.
/// </para>
/// <para>
/// Normalising every call site to one side would work too, but it is a bigger
/// diff and one missed site puts the defect straight back. Making the keys
/// themselves case-aware cannot be forgotten at a call site.
/// </para>
/// </remarks>
internal static class NameKey
{
    /// <summary>Comparer for a (schema, name) key.</summary>
    public static IEqualityComparer<(string Schema, string Name)> Pair(StringComparer names) =>
        new PairComparer(names);

    /// <summary>Comparer for a (schema, table, name) key.</summary>
    public static IEqualityComparer<(string Schema, string Table, string Name)> Triple(StringComparer names) =>
        new TripleComparer(names);

    private sealed class PairComparer(StringComparer names)
        : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            names.Equals(x.Schema, y.Schema) && names.Equals(x.Name, y.Name);

        public int GetHashCode((string Schema, string Name) obj) =>
            HashCode.Combine(names.GetHashCode(obj.Schema), names.GetHashCode(obj.Name));
    }

    private sealed class TripleComparer(StringComparer names)
        : IEqualityComparer<(string Schema, string Table, string Name)>
    {
        public bool Equals(
            (string Schema, string Table, string Name) x,
            (string Schema, string Table, string Name) y) =>
            names.Equals(x.Schema, y.Schema)
            && names.Equals(x.Table, y.Table)
            && names.Equals(x.Name, y.Name);

        public int GetHashCode((string Schema, string Table, string Name) obj) => HashCode.Combine(
            names.GetHashCode(obj.Schema),
            names.GetHashCode(obj.Table),
            names.GetHashCode(obj.Name));
    }
}

namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server SEQUENCE object (sys.sequences). Number-generator separate
/// from any table. Supports integer / decimal-no-scale data types; the
/// boundary values are stored as <see cref="long"/> here — sufficient for
/// the vast majority of real-world schemas (tinyint / smallint / int /
/// bigint / decimal(p,0)). Sequences with floating-point base types are
/// rare and out of scope for v1.
/// </summary>
public sealed record Sequence(
    string Schema,
    string Name,
    string DataType,
    long StartValue,
    long Increment,
    long? MinValue,
    long? MaxValue,
    bool IsCycling,
    bool IsCached,
    int? CacheSize)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Sequence");

    /// <summary>
    /// Schema of the ALIAS type in <see cref="DataType"/>, when the sequence is
    /// declared over one. Null for a built-in base type. Measured: a sequence
    /// over an alias type is legal — <c>CREATE SEQUENCE app.S1 AS
    /// app.MioIntTipo</c> is accepted — and <c>TYPE_NAME(seq.user_type_id)</c>
    /// hands back the bare name exactly as it does for a column, so the same
    /// unqualified-name defect reaches <c>CREATE SEQUENCE</c>.
    /// </summary>
    public string? TypeSchema { get; init; }

    /// <summary>
    /// True when two sequences are declared over the same type, schema
    /// included. The sequence half of <c>Column.TypeMatches</c>, and it exists
    /// for the same reason: without it two sequences over same-named alias
    /// types in different schemas compare Identical and nothing is emitted.
    /// </summary>
    /// <param name="other">The sequence to compare this one against.</param>
    /// <param name="names">
    /// The case-folding rule, required for the reason spelled out on
    /// <c>Column.TypeMatches</c>: these are server identifiers and a
    /// case-sensitive database tells two same-spelled types apart.
    /// </param>
    public bool TypeMatches(Sequence other, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(names);
        return names.Equals(DataType, other.DataType)
            && names.Equals(TypeSchema, other.TypeSchema);
    }
}

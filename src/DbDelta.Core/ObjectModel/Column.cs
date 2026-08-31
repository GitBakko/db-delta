namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A table column (sys.columns row). Extended in M2 to carry identity seed /
/// increment (when <see cref="IsIdentity"/>) and the persisted-computed
/// expression (when <see cref="ComputedExpression"/> is non-null).
/// </summary>
public sealed record Column
{
    public string Name { get; init; }
    public string DataType { get; init; }
    public bool IsNullable { get; init; }
    public int Ordinal { get; init; }
    public string? DefaultExpression { get; init; }
    public bool IsIdentity { get; init; }
    public long? IdentitySeed { get; init; }
    public long? IdentityIncrement { get; init; }
    public string? ComputedExpression { get; init; }
    public bool IsPersistedComputed { get; init; }

    /// <summary>
    /// Column collation name (sys.columns.collation_name). Populated only for
    /// character / text base types (char, varchar, nchar, nvarchar, text,
    /// ntext, sysname). Null for non-string columns. M13-PARITY.5 #32 — used
    /// to detect divergence from the database default collation and emit an
    /// explicit COLLATE clause when needed (Redgate parity scenarios 01, 11).
    /// </summary>
    public string? Collation { get; init; }

    /// <summary>
    /// The column is typed with a user-defined ALIAS type
    /// (<c>sys.types.is_user_defined = 1</c>), not a built-in one.
    /// </summary>
    /// <remarks>
    /// It matters for exactly one thing, and it is not cosmetic: a column of an
    /// alias type may not carry a <c>COLLATE</c> clause. SQL Server refuses the
    /// statement — "COLLATE clause cannot be used on user-defined data types" —
    /// and the deploy stops there. <c>sys.columns</c> reports a collation for
    /// such a column exactly as it does for an <c>nvarchar</c> one, so nothing
    /// else in the row says so. An <c>init</c> property, so every existing
    /// construction still compiles and still means "a built-in type".
    /// </remarks>
    public bool IsUserDefinedType { get; init; }

    /// <summary>
    /// Schema of the ALIAS type in <see cref="DataType"/>, when
    /// <see cref="IsUserDefinedType"/> is true. Null for a built-in type, and
    /// null for a column built by hand rather than read from a catalog — in
    /// which case the emitters fall back to the bare name, exactly as before.
    /// </summary>
    /// <remarks>
    /// It is a field of its own and NOT baked into <see cref="DataType"/> on
    /// purpose. <c>SqlTypeFormatter</c> bracket-quotes the whole pre-paren
    /// token in one go, so a dotted <c>DataType</c> would be emitted as the
    /// single identifier <c>[app.MioTipo]</c> — the very shape S11 removed,
    /// and one <c>IdentifierEscapingTests</c> still pins
    /// (<c>"dbo.Money" -&gt; "[dbo.Money]"</c>). The schema therefore travels
    /// beside the name and is quoted at the emitter with
    /// <c>Sql.Q(schema, name)</c>.
    /// </remarks>
    public string? TypeSchema { get; init; }

    /// <summary>
    /// True when two columns carry the same data type, schema included.
    /// </summary>
    /// <remarks>
    /// The single seam every type comparison passes through, on the model of
    /// <c>DatabaseUser.LoginMatches</c>. There are six of them — two in
    /// <c>ComparisonEngine</c>, one in <c>TableTypeComparison</c>, two in
    /// <c>TableScriptEmitter</c> and one in <c>SequenceScriptEmitter</c> — and
    /// a <see cref="TypeSchema"/> that failed to reach even one of them would
    /// not be a wrong script but SILENCE: <c>app.MioTipo</c> and
    /// <c>dbo.MioTipo</c> both read <c>DataType = "MioTipo"</c>, the pair
    /// compares Identical, no difference row is produced and nothing is
    /// emitted. Nothing here relies on record equality: no caller compares two
    /// <see cref="Column"/> instances with <c>==</c>.
    /// </remarks>
    /// <param name="other">The column to compare this one against.</param>
    /// <param name="names">
    /// The rule for folding identifier case, and it is REQUIRED rather than
    /// defaulted. Both halves compared here are server identifiers, and on a
    /// case-sensitive database <c>app.Codice</c> and <c>app.codice</c> are two
    /// distinct types — <c>user_type_id</c> 257 and 258, measured. The engine
    /// pairs the objects around this call with the target's collation
    /// (<c>ObjectIdentityComparer.Names</c>); folding case here regardless
    /// would make the one comparison inside a matched table that disagrees
    /// with the collation the table itself was matched under, and it would
    /// disagree in the silent direction: Identical, no row, no script. The
    /// emitters have no collation to consult and pass
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> explicitly, which is
    /// what they already did.
    /// </param>
    public bool TypeMatches(Column other, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(names);
        return names.Equals(DataType, other.DataType)
            && names.Equals(TypeSchema, other.TypeSchema);
    }

    public Column(
        string name,
        string dataType,
        bool isNullable,
        int ordinal,
        string? defaultExpression = null,
        bool isIdentity = false,
        long? identitySeed = null,
        long? identityIncrement = null,
        string? computedExpression = null,
        bool isPersistedComputed = false,
        string? collation = null)
    {
        Name = name;
        DataType = dataType;
        IsNullable = isNullable;
        Ordinal = ordinal;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
        IdentitySeed = identitySeed;
        IdentityIncrement = identityIncrement;
        ComputedExpression = computedExpression;
        IsPersistedComputed = isPersistedComputed;
        Collation = collation;
    }
}

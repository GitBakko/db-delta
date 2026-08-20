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

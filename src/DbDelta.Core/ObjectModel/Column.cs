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
        bool isPersistedComputed = false)
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
    }
}

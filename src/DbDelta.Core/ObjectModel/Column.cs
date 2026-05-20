namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A table column (sys.columns row) — minimal v1 property surface.
/// </summary>
public sealed record Column
{
    public string Name { get; init; }
    public string DataType { get; init; }
    public bool IsNullable { get; init; }
    public int Ordinal { get; init; }
    public string? DefaultExpression { get; init; }
    public bool IsIdentity { get; init; }

    public Column(
        string name,
        string dataType,
        bool isNullable,
        int ordinal,
        string? defaultExpression = null,
        bool isIdentity = false)
    {
        Name = name;
        DataType = dataType;
        IsNullable = isNullable;
        Ordinal = ordinal;
        DefaultExpression = defaultExpression;
        IsIdentity = isIdentity;
    }
}

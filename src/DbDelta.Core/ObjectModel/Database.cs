namespace DbDelta.Core.ObjectModel;

/// <summary>
/// The root of an in-memory schema graph: a single SQL Server database snapshot.
/// </summary>
public sealed record Database(
    string Name,
    IReadOnlyList<Schema> Schemas,
    IReadOnlyList<Table> Tables)
{
    /// <summary>All views defined in the database (flattened across schemas).</summary>
    public IReadOnlyList<View> Views { get; init; } = [];

    /// <summary>All stored procedures defined in the database (flattened across schemas).</summary>
    public IReadOnlyList<StoredProcedure> Procedures { get; init; } = [];

    /// <summary>
    /// Convenience ctor accepting modules. Provided as a named ctor rather than positional
    /// parameters so existing call sites (<c>Database(name, schemas, tables)</c>) continue to compile.
    /// </summary>
    public Database(
        string Name,
        IReadOnlyList<Schema> Schemas,
        IReadOnlyList<Table> Tables,
        IReadOnlyList<View> Views,
        IReadOnlyList<StoredProcedure> Procedures)
        : this(Name, Schemas, Tables)
    {
        this.Views = Views;
        this.Procedures = Procedures;
    }
}

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

    /// <summary>All user-defined functions (scalar + inline TVF + multi-TVF).</summary>
    public IReadOnlyList<Function> Functions { get; init; } = [];

    /// <summary>All DML triggers defined in the database.</summary>
    public IReadOnlyList<Trigger> Triggers { get; init; } = [];

    /// <summary>
    /// M3 ctor — tables + views + procedures. Kept so existing call sites still compile.
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

    /// <summary>
    /// M4 ctor — tables + views + procedures + functions + triggers.
    /// </summary>
    public Database(
        string Name,
        IReadOnlyList<Schema> Schemas,
        IReadOnlyList<Table> Tables,
        IReadOnlyList<View> Views,
        IReadOnlyList<StoredProcedure> Procedures,
        IReadOnlyList<Function> Functions,
        IReadOnlyList<Trigger> Triggers)
        : this(Name, Schemas, Tables, Views, Procedures)
    {
        this.Functions = Functions;
        this.Triggers = Triggers;
    }
}

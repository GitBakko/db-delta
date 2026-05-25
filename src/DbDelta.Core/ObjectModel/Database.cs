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

    /// <summary>All sequence objects (M5).</summary>
    public IReadOnlyList<Sequence> Sequences { get; init; } = [];

    /// <summary>All synonym aliases (M5).</summary>
    public IReadOnlyList<Synonym> Synonyms { get; init; } = [];

    /// <summary>All alias user-defined types (M5).
    /// CLR UDTs are intentionally excluded — see <see cref="UserDefinedType"/>.</summary>
    public IReadOnlyList<UserDefinedType> UserDefinedTypes { get; init; } = [];

    /// <summary>All table-type user-defined types (M13-FIX.4) — the
    /// thirteenth object kind from spec §1.2, distinct from alias UDTs and
    /// most often used as TVPs.</summary>
    public IReadOnlyList<TableTypeUdt> TableTypeUdts { get; init; } = [];

    /// <summary>Database users (M6).</summary>
    public IReadOnlyList<DatabaseUser> Users { get; init; } = [];

    /// <summary>Custom database roles + their memberships (M6).</summary>
    public IReadOnlyList<DatabaseRole> Roles { get; init; } = [];

    /// <summary>Object-level GRANT/DENY permissions (M6).</summary>
    public IReadOnlyList<Permission> Permissions { get; init; } = [];

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

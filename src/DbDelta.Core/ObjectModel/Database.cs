using DbDelta.Core.Dependency;

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
    /// Object-level dependency edges (#24). Populated by the provider from
    /// catalog metadata; consumed by the script generator to topologically
    /// order CREATE emission. Empty ⇒ the generator falls back to its stable
    /// kind-then-alphabetical order (current behaviour). Foreign-key edges may
    /// be present but are ignored by the topological sort.
    /// </summary>
    public IReadOnlyList<DependencyEdge> Dependencies { get; init; } = [];

    /// <summary>
    /// Database default collation (<c>sys.databases.collation_name</c>). Null
    /// when unknown (headless / unit-test fixtures).
    /// </summary>
    /// <remarks>
    /// This is not a cosmetic hint about <c>COLLATE</c> clauses. Read from the
    /// TARGET, it is the single input deciding how the whole comparison pairs
    /// names: <see cref="Diff.NameComparison.ForCollation"/> turns it into the
    /// comparer that matches objects, columns, constraints and indexes, and
    /// every keyed set in the generator inherits it. Null means
    /// case-INSENSITIVE, deliberately — assuming case-sensitivity on a
    /// case-insensitive server generates a DROP of live data, while the
    /// converse at worst pairs two objects that should have stayed apart.
    /// Changing this value changes what the deploy does, not how it reads.
    /// </remarks>
    public string? DefaultCollation { get; init; }

    /// <summary>
    /// The families of objects this snapshot did NOT capture, counted. Empty for
    /// hand-built fixtures and for a database holding nothing outside the
    /// thirteen modelled kinds.
    /// </summary>
    /// <remarks>
    /// Carried so the verdict can disclose its own scope. Without it a database
    /// whose only drift is an unmodelled object — a missing columnstore index,
    /// a changed CLR assembly — compares Identical and says so without
    /// qualification. See <see cref="UnexaminedCensus"/>.
    /// </remarks>
    public UnexaminedCensus Unexamined { get; init; } = UnexaminedCensus.Empty;

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

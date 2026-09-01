using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// "<paramref name="Dependent"/> depends on <paramref name="Referenced"/>"
/// ⇒ Referenced must be created before Dependent.
/// </summary>
/// <param name="Dependent">The object that holds the reference.</param>
/// <param name="Referenced">The object being referenced.</param>
/// <param name="Kind">Foreign key, or an expression-level module reference.</param>
/// <param name="IsSchemaBound">
/// <c>sys.sql_expression_dependencies.is_schema_bound_reference</c>: the
/// dependency is a SCHEMABINDING one, which the server ENFORCES rather than
/// merely records.
/// </param>
/// <remarks>
/// The flag changes nothing about ordering — a schemabound edge points the same
/// way as any other — and everything about whether the referenced object can be
/// dropped at all. <c>DROP TABLE</c> under one is refused with Msg 3729 and
/// <c>sp_rename</c> with Msg 15336, so an identity rebuild of that table cannot
/// be written, and saying so before the script runs is the whole point of
/// carrying it.
/// <para>
/// Reading it as "this edge blocks a DROP" is wrong twice over, and both ways
/// were measured on <c>mssql/server:2022-latest</c>. A plain
/// <c>CHECK (Amt &gt; 0)</c>, and a PERSISTED computed column, each produce a
/// row with the flag set whose referencing entity is the table ITSELF — and
/// those tables drop perfectly well. This reader manufactures more of them, by
/// attributing a C/D constraint's references to its parent table. So the
/// question is never "is there a schemabound edge" but "does something OTHER
/// than this object hold one over it".
/// </para>
/// Defaulted so every hand-built model keeps compiling and keeps meaning
/// "an ordinary reference", which is what a model that never said should mean.
/// </remarks>
public readonly record struct DependencyEdge(
    ObjectIdentity Dependent,
    ObjectIdentity Referenced,
    EdgeKind Kind,
    bool IsSchemaBound = false);

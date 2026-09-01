using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when an alias type has to be dropped — on its own, or as the
/// <c>DROP</c> half of the drop-and-recreate a changed base type needs — while
/// something the script does not drop first still uses it.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="SchemaboundRebuildException"/>, and the same owner
/// decision of 2026-09-01: a diagnostic preflight, not a fifth member of the
/// <c>Unscriptable*</c> family. Nothing silent happens — SQL Server answers
/// Msg 3732 and <c>XACT_ABORT</c> rolls the deploy back. What is bought is the
/// name of the thing still using the type, given before a line of SQL runs.
/// </para>
/// <para>
/// The two share a root, and it is worth naming: <c>ScriptGenerator</c> filters
/// <c>Identical</c> pairs out before generating, so an object that binds the one
/// being dropped is not in the script and there is no slot to put it in. Here
/// there is a second reason no ordering can save it — the type's
/// <c>DROP</c>+<c>CREATE</c> is ONE indivisible body at the type's topological
/// slot, and <c>UserDefinedType</c> ranks before every kind that can bind it, so
/// even a binder the script does emit is emitted too late.
/// </para>
/// <para>
/// Six binders block the drop, all measured on
/// <c>mssql/server:2022-latest</c>: a table column, a sequence, a table type's
/// column, a procedure parameter, a function parameter and a function's RETURN
/// type. The first three are declarations and appear in no dependency view —
/// they are read off the model. The last three appear only in
/// <c>sys.sql_expression_dependencies</c>, as <c>referenced_class = 6</c> rows.
/// A seventh form does not exist: an alias type is illegal in a <c>CAST</c>
/// (Msg 243).
/// </para>
/// <para>
/// The server's own message is not always usable, which is the other half of
/// why this exists: for a table type's column it names the internal type-table
/// (<c>TT_UsaTT_37A5467C</c>), a name the user never wrote.
/// </para>
/// </remarks>
/// <param name="type">The alias type that has to be dropped.</param>
/// <param name="binder">The object still using it.</param>
public sealed class BoundTypeDropException(ObjectIdentity type, ObjectIdentity binder)
    : Exception(
        $"{type.SchemaName}.{type.ObjectName} has to be dropped and re-created, but "
        + $"{binder.SchemaName}.{binder.ObjectName} ({binder.Kind}) still uses it: "
        + "SQL Server refuses the DROP TYPE with Msg 3732.")
{
    /// <summary>The alias type that has to be dropped.</summary>
    public ObjectIdentity Type { get; } = type;

    /// <summary>The object still using it.</summary>
    public ObjectIdentity Binder { get; } = binder;
}

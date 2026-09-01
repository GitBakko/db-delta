using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when a table needs an identity rebuild — <c>CREATE _tmp</c>, copy,
/// <c>DROP TABLE</c>, <c>sp_rename</c> — while a <c>SCHEMABINDING</c> module
/// holds a reference to it that the server enforces.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is NOT a fifth member of the <c>Unscriptable*</c> family, and the
/// distinction is written down rather than left to be re-derived.</b> Those
/// four exist because the alternative to throwing was a VALID statement that
/// silently means something else — a disk-based table type where the source has
/// a memory-optimized one, a GRANT over the whole database instead of over one
/// object. Nothing silent happens here: the server refuses the
/// <c>DROP TABLE</c> with Msg 3729, <c>XACT_ABORT</c> rolls the deploy back and
/// the target is left as it was.
/// </para>
/// <para>
/// What this buys is diagnostic quality, and the owner decided on 2026-09-01
/// that it is worth buying: the difference between a deploy that dies halfway
/// with a server message naming a view the operator never chose to touch, and
/// an answer given before a line of SQL runs, naming the module that blocks it
/// and what to do about it. Same shape as <c>BackfillPreflight</c> — a question
/// answered up front instead of a failure discovered halfway.
/// </para>
/// <para>
/// It rides on the exit code §4.3 already reserves for a script that could not
/// be produced (30, via <c>ErrorCode.UnsupportedSchemaChange</c>). No new code
/// was minted: inventing one would have meant amending the spec for a shape the
/// existing one already describes.
/// </para>
/// <para>
/// <c>sp_rename</c> is no escape either — Msg 15336, measured — so "rename the
/// table instead of dropping it" is not an alternative the generator declined
/// to take.
/// </para>
/// </remarks>
/// <param name="table">The table that needs the rebuild.</param>
/// <param name="binder">The schemabound module that blocks it.</param>
public sealed class SchemaboundRebuildException(ObjectIdentity table, ObjectIdentity binder)
    : Exception(
        $"{table.SchemaName}.{table.ObjectName} needs a full rebuild, but "
        + $"{binder.SchemaName}.{binder.ObjectName} ({binder.Kind}) references it WITH SCHEMABINDING: "
        + "SQL Server refuses both the DROP TABLE (Msg 3729) and the sp_rename (Msg 15336).")
{
    /// <summary>The table that needs the rebuild.</summary>
    public ObjectIdentity Table { get; } = table;

    /// <summary>The schemabound module that blocks it.</summary>
    public ObjectIdentity Binder { get; } = binder;
}

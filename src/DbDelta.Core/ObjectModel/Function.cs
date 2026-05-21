namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server user-defined function. Body holds the full <c>CREATE FUNCTION …</c>
/// text as stored in <c>sys.sql_modules.definition</c>; <c>null</c> when the
/// function is encrypted (see <see cref="Module.IsEncrypted"/>).
/// </summary>
/// <remarks>
/// <see cref="Kind"/> always returns <c>"Function"</c> — the per-row kind discriminant
/// used by <see cref="ObjectIdentity"/> and the comparison engine. The SQL-level
/// shape (scalar vs inline-TVF vs multi-TVF) is carried by <see cref="FunctionKind"/>
/// so the script emitter can decide whether to use <c>CREATE OR ALTER FUNCTION</c>
/// or fall back to <c>DROP + CREATE</c> when the return-shape changed.
/// </remarks>
public sealed record Function(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    FunctionKind FunctionKind,
    DateTime? ModifyDate = null)
    : Module(Schema, Name, Body, IsEncrypted, ModifyDate)
{
    public override string Kind => "Function";
}

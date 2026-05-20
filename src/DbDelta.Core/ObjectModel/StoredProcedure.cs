namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server stored procedure. Body holds the full <c>CREATE PROCEDURE …</c> text
/// as stored in <c>sys.sql_modules.definition</c>; <c>null</c> when the procedure is
/// encrypted (see <see cref="Module.IsEncrypted"/>).
/// </summary>
/// <remarks>
/// <see cref="Kind"/> intentionally returns <c>"Procedure"</c>, not <c>"StoredProcedure"</c>:
/// the <c>Kind</c> discriminator is the vocabulary shared by <see cref="ObjectIdentity"/>,
/// the comparison engine, and the per-kind script emitters — it mirrors SQL Server's
/// own naming (e.g. <c>DROP PROCEDURE</c>) rather than the C# class name.
/// </remarks>
public sealed record StoredProcedure(string Schema, string Name, string? Body, bool IsEncrypted)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Procedure";
}

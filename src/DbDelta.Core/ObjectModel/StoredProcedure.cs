namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server stored procedure. Body holds the full <c>CREATE PROCEDURE …</c> text
/// as stored in <c>sys.sql_modules.definition</c>.
/// </summary>
public sealed record StoredProcedure(string Schema, string Name, string? Body, bool IsEncrypted)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Procedure";
}

namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server view. Body holds the full <c>CREATE VIEW …</c> text as stored in
/// <c>sys.sql_modules.definition</c>.
/// </summary>
public sealed record View(string Schema, string Name, string? Body, bool IsEncrypted)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "View";
}

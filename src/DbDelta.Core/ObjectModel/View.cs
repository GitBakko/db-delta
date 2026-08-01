namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server view. Body holds the full <c>CREATE VIEW …</c> text as stored in
/// <c>sys.sql_modules.definition</c>; <c>null</c> when the view is encrypted
/// (see <see cref="Module.IsEncrypted"/>).
/// </summary>
public sealed record View(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    DateTime? ModifyDate = null,
    bool UsesQuotedIdentifier = true,
    bool UsesAnsiNulls = true)
    : Module(Schema, Name, Body, IsEncrypted, ModifyDate, UsesQuotedIdentifier, UsesAnsiNulls)
{
    public override string Kind => "View";
}

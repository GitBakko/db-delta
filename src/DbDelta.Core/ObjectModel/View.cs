namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server view. Body holds the full <c>CREATE VIEW …</c> text as stored in
/// <c>sys.sql_modules.definition</c>; <c>null</c> when the view is encrypted
/// (see <see cref="Module.IsEncrypted"/>).
/// </summary>
public sealed record View(string Schema, string Name, string? Body, bool IsEncrypted, DateTime? ModifyDate = null)
    : Module(Schema, Name, Body, IsEncrypted, ModifyDate)
{
    public override string Kind => "View";
}

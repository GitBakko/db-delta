namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server DML trigger (INSERT / UPDATE / DELETE). DDL triggers, logon
/// triggers, and CLR triggers are out of scope for v1 per spec §1.2.
/// </summary>
/// <param name="Schema">Owning schema of the trigger (matches the parent table's schema in normal usage).</param>
/// <param name="Name">Trigger name.</param>
/// <param name="Body">
/// Full T-SQL definition as returned by <c>sys.sql_modules.definition</c>, or <c>null</c>
/// when the trigger is encrypted (<see cref="Module.IsEncrypted"/>).
/// </param>
/// <param name="IsEncrypted"><c>true</c> when the trigger was created <c>WITH ENCRYPTION</c>.</param>
/// <param name="ParentSchema">Schema of the table the trigger fires on.</param>
/// <param name="ParentTable">Name of the table the trigger fires on.</param>
/// <param name="IsDisabled">
/// <c>true</c> when the trigger is currently disabled via
/// <c>DISABLE TRIGGER … ON …</c>. Toggling this is a non-DDL operation
/// emitted as an <c>ENABLE TRIGGER</c> / <c>DISABLE TRIGGER</c> statement
/// rather than a full <c>CREATE OR ALTER</c>.
/// </param>
/// <param name="IsNotForReplication"><c>true</c> when defined with <c>NOT FOR REPLICATION</c>.</param>
public sealed record Trigger(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    string ParentSchema,
    string ParentTable,
    bool IsDisabled,
    bool IsNotForReplication)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Trigger";
}

namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Common base for code-bearing objects (views, procedures, functions, triggers).
/// </summary>
/// <param name="Schema">Owning schema (e.g. "dbo").</param>
/// <param name="Name">Object name.</param>
/// <param name="Body">
/// Full T-SQL definition as returned by <c>sys.sql_modules.definition</c>, or <c>null</c>
/// when the module is encrypted (<see cref="IsEncrypted"/> is <c>true</c>).
/// </param>
/// <param name="IsEncrypted">
/// <c>true</c> when the module was created <c>WITH ENCRYPTION</c>. Encrypted modules have
/// an opaque definition: DbDelta surfaces presence/absence diffs and emits a warning but
/// cannot diff bodies.
/// </param>
/// <param name="ModifyDate">
/// The <c>sys.objects.modify_date</c> for this module, in UTC. <c>null</c> when the value
/// was not available from the data source (e.g. older provider versions).
/// </param>
public abstract record Module(string Schema, string Name, string? Body, bool IsEncrypted, DateTime? ModifyDate = null)
{
    /// <summary>The discriminator used in <see cref="ObjectIdentity"/> for this module kind.</summary>
    public abstract string Kind { get; }

    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: Kind);
}

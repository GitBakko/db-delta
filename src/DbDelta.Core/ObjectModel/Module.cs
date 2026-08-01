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
/// The <c>sys.objects.modify_date</c> for this module — the <b>DB server's local
/// clock</b> (Kind = Unspecified), displayed verbatim, never timezone-converted.
/// <c>null</c> when the value was not available from the data source.
/// </param>
/// <param name="UsesQuotedIdentifier">
/// The <c>QUOTED_IDENTIFIER</c> setting captured when the module was created
/// (<c>sys.sql_modules.uses_quoted_identifier</c>). It is part of the module, not
/// of the session running it: with it OFF a double-quoted token is a string
/// literal rather than an identifier, so the same body means two different
/// things. Two modules whose definitions are byte-identical but whose flags
/// differ are genuinely different objects.
/// </param>
/// <param name="UsesAnsiNulls">
/// Likewise for <c>ANSI_NULLS</c>: with it OFF, <c>= NULL</c> matches NULLs
/// instead of yielding UNKNOWN, which silently changes what a WHERE clause
/// returns.
/// </param>
public abstract record Module(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    DateTime? ModifyDate = null,
    bool UsesQuotedIdentifier = true,
    bool UsesAnsiNulls = true)
{
    /// <summary>The discriminator used in <see cref="ObjectIdentity"/> for this module kind.</summary>
    public abstract string Kind { get; }

    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: Kind);
}

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

    /// <summary>
    /// Indexes materialising this view, if any. Empty for an ordinary view,
    /// which is every view that is not indexed.
    /// </summary>
    /// <remarks>
    /// An index on a view is not decoration: it is what makes the view a stored
    /// result set rather than a query. Until 2026-08-20 nothing read them, so
    /// two databases differing only by one compared Identical — worse than
    /// refusing to script it, because nobody was told. The census declared the
    /// blind spot (<c>INDEX_ON_VIEW</c>), which is why it was medium and not
    /// high. An <c>init</c> property so every existing construction still
    /// compiles and still means "no indexes".
    /// </remarks>
    public IReadOnlyList<TableIndex> Indexes { get; init; } = [];
}

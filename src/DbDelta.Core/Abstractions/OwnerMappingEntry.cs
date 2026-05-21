namespace DbDelta.Core.Abstractions;

/// <summary>
/// Maps a schema owner name in the source database to a corresponding owner
/// name in the target database.
/// </summary>
public sealed record OwnerMappingEntry(string SourceOwner, string TargetOwner);

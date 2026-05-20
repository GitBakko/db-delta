namespace DbDelta.Shared.Dtos;

/// <summary>
/// Wire representation of one paired object between the source and target
/// databases — flat strings only, safe to cross the App ↔ Core boundary.
/// </summary>
public sealed record DifferenceDto(
    string Kind,
    string SchemaName,
    string ObjectName,
    string Status);

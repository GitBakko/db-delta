namespace DbDelta.Shared.Dtos;

/// <summary>
/// Wire representation of one paired object between the source and target
/// databases — flat strings only, safe to cross the App ↔ Core boundary.
/// The LastModified values are <c>sys.objects.modify_date</c> and carry the
/// <b>DB server's local clock</b> (Kind = Unspecified) — display them verbatim,
/// never convert them to the client timezone.
/// </summary>
public sealed record DifferenceDto(
    string Kind,
    string SchemaName,
    string ObjectName,
    string Status,
    DateTime? LastModifiedSource = null,
    DateTime? LastModifiedTarget = null);

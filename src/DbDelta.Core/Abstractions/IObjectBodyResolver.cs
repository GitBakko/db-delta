namespace DbDelta.Core.Abstractions;

/// <summary>Resolves the SQL "body" used by the diff viewer for one object.
/// For modules (views, procedures, functions, triggers) this is the stored
/// CREATE definition. For tables this is the synthesised CREATE TABLE script
/// produced by the existing ScriptGenerator.</summary>
public interface IObjectBodyResolver
{
    /// <summary>Resolve the source-side body. Returns null if the object does
    /// not exist on the source.</summary>
    Task<string?> ResolveSourceBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct);

    /// <summary>Resolve the target-side body. Returns null if the object does
    /// not exist on the target.</summary>
    Task<string?> ResolveTargetBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct);
}

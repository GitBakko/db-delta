using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for view differences:
/// <list type="bullet">
///   <item><c>OnlyInA</c> (add): the side-A body rewritten as <c>CREATE OR ALTER VIEW</c>.</item>
///   <item><c>OnlyInB</c> (drop): <c>DROP VIEW IF EXISTS [schema].[name];</c></item>
///   <item><c>Different</c> (modify): the side-A body rewritten as <c>CREATE OR ALTER VIEW</c>.</item>
///   <item>Encrypted on side A: a <c>-- WARNING</c> comment, no DDL (cannot script an opaque body).</item>
/// </list>
/// </summary>
public sealed class ViewScriptEmitter
{
    /// <summary>Emit DDL for a view difference. Returns empty string when no action is required.</summary>
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is View v => EmitCreateOrAlter(v),
            DifferenceStatus.OnlyInB when pair.SideB is View v => EmitDrop(v),
            DifferenceStatus.Different when pair.SideA is View v => EmitCreateOrAlter(v),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(View v)
    {
        if (v.IsEncrypted || v.Body is null)
        {
            return $"-- WARNING: view [{v.Schema}].[{v.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }

        // Rewrite the leading CREATE VIEW (case-insensitive) to CREATE OR ALTER VIEW.
        // If the catalog returned a different shape (e.g. already CREATE OR ALTER VIEW), leave it.
        string body = v.Body.TrimStart();
        const string createView = "CREATE VIEW";
        const string createOrAlterView = "CREATE OR ALTER VIEW";
        if (body.StartsWith(createView, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterView, body.AsSpan(createView.Length));
        }
        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(View v) =>
        $"DROP VIEW IF EXISTS [{v.Schema}].[{v.Name}];";
}

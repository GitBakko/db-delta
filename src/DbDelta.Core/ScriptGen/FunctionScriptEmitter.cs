using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for user-defined function differences. Mirrors the procedure
/// emitter but rewrites the leading <c>CREATE FUNCTION</c> token instead.
/// </summary>
public sealed class FunctionScriptEmitter
{
    /// <summary>Emit DDL for a function difference. Returns empty string when no action is required.</summary>
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is Function f => EmitCreateOrAlter(f),
            DifferenceStatus.OnlyInB when pair.SideB is Function f => EmitDrop(f),
            DifferenceStatus.Different when pair.SideA is Function f => EmitCreateOrAlter(f),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(Function f)
    {
        if (f.IsEncrypted || f.Body is null)
        {
            return $"-- WARNING: function [{f.Schema}].[{f.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }

        string body = f.Body.TrimStart();
        const string createFn = "CREATE FUNCTION";
        const string createOrAlterFn = "CREATE OR ALTER FUNCTION";
        if (body.StartsWith(createFn, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterFn, body.AsSpan(createFn.Length));
        }

        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(Function f) =>
        $"DROP FUNCTION IF EXISTS [{f.Schema}].[{f.Name}];";
}

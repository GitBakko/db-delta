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
        return f.IsEncrypted || f.Body is null
            ? $"-- WARNING: function {Sql.Q(f.Schema, f.Name)} is encrypted (WITH ENCRYPTION); body cannot be scripted."
            : ModuleHeader.ToCreateOrAlterScript(
                f.Body, f.Schema, f.Name, f.UsesQuotedIdentifier, f.UsesAnsiNulls);
    }

    private static string EmitDrop(Function f) =>
        $"DROP FUNCTION IF EXISTS {Sql.Q(f.Schema, f.Name)};";
}

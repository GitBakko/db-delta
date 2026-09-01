using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for stored-procedure differences. Same shape as
/// <see cref="ViewScriptEmitter"/> but using <c>CREATE OR ALTER PROCEDURE</c>
/// and a bare <c>DROP PROCEDURE</c>.
/// </summary>
public sealed class ProcedureScriptEmitter
{
    /// <summary>Emit DDL for a stored-procedure difference. Returns empty string when no action is required.</summary>
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is StoredProcedure p => EmitCreateOrAlter(p),
            DifferenceStatus.OnlyInB when pair.SideB is StoredProcedure p => EmitDrop(p),
            DifferenceStatus.Different when pair.SideA is StoredProcedure p => EmitCreateOrAlter(p),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(StoredProcedure p)
    {
        return p.IsEncrypted || p.Body is null
            ? $"-- WARNING: procedure {Sql.Q(p.Schema, p.Name)} is encrypted (WITH ENCRYPTION); body cannot be scripted."
            : ModuleHeader.ToCreateOrAlterScript(
                p.Body, p.Schema, p.Name, p.UsesQuotedIdentifier, p.UsesAnsiNulls);
    }

    private static string EmitDrop(StoredProcedure p) =>
        $"DROP PROCEDURE {Sql.Q(p.Schema, p.Name)};";
}

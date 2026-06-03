using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for stored-procedure differences. Same shape as
/// <see cref="ViewScriptEmitter"/> but using <c>CREATE OR ALTER PROCEDURE</c>
/// and <c>DROP PROCEDURE IF EXISTS</c>.
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
        if (p.IsEncrypted || p.Body is null)
        {
            return $"-- WARNING: procedure [{p.Schema}].[{p.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }

        string body = ModuleHeader.AlignNameToCatalog(p.Body.TrimStart(), p.Schema, p.Name);
        const string createProc = "CREATE PROCEDURE";
        const string createOrAlterProc = "CREATE OR ALTER PROCEDURE";
        const string createProcShort = "CREATE PROC";
        const string createOrAlterProcShort = "CREATE OR ALTER PROC";

        // Try the long form first because "CREATE PROCEDURE" starts with "CREATE PROC".
        if (body.StartsWith(createProc, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterProc, body.AsSpan(createProc.Length));
        }
        else if (body.StartsWith(createProcShort, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterProcShort, body.AsSpan(createProcShort.Length));
        }

        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(StoredProcedure p) =>
        $"DROP PROCEDURE IF EXISTS [{p.Schema}].[{p.Name}];";
}

using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for trigger differences. Mirrors the procedure / function
/// emitters for body-bearing diffs and additionally emits
/// <c>ENABLE TRIGGER</c> / <c>DISABLE TRIGGER</c> for state-only diffs
/// (body unchanged but <see cref="Trigger.IsDisabled"/> flipped).
/// </summary>
public sealed class TriggerScriptEmitter
{
    /// <summary>Emit DDL for a trigger difference. Returns empty string when no action is required.</summary>
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is Trigger t => EmitCreateOrAlter(t),
            DifferenceStatus.OnlyInB when pair.SideB is Trigger t => EmitDrop(t),
            DifferenceStatus.Different when pair.SideA is Trigger a && pair.SideB is Trigger b
                => EmitDifferent(a, b),
            DifferenceStatus.Different when pair.SideA is Trigger t => EmitCreateOrAlter(t),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(Trigger t)
    {
        return t.IsEncrypted || t.Body is null
            ? $"-- WARNING: trigger [{t.Schema}].[{t.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted."
            : ModuleHeader.ToCreateOrAlterScript(t.Body, t.Schema, t.Name);
    }

    private static string EmitDrop(Trigger t) =>
        $"DROP TRIGGER IF EXISTS [{t.Schema}].[{t.Name}];";

    private static string EmitDifferent(Trigger sideA, Trigger sideB)
    {
        bool bodiesMatch = !sideA.IsEncrypted && !sideB.IsEncrypted
            && string.Equals(
                BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(sideA.Body, sideA.Schema, sideA.Name)),
                BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(sideB.Body, sideB.Schema, sideB.Name)),
                StringComparison.Ordinal);

        if (bodiesMatch && sideA.IsDisabled != sideB.IsDisabled)
        {
            string verb = sideA.IsDisabled ? "DISABLE" : "ENABLE";
            return $"{verb} TRIGGER [{sideA.Schema}].[{sideA.Name}] ON [{sideA.ParentSchema}].[{sideA.ParentTable}];";
        }

        return EmitCreateOrAlter(sideA);
    }
}

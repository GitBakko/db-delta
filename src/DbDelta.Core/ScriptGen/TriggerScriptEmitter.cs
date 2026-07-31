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

    /// <summary>
    /// Emits the trigger body as <c>CREATE OR ALTER</c>, followed by a
    /// <c>DISABLE TRIGGER</c> when the source side is disabled.
    /// </summary>
    /// <remarks>
    /// The DISABLE belongs here, not at the call sites. <c>CREATE OR ALTER</c>
    /// always yields an ENABLED trigger, so every path through this method
    /// re-enables a trigger somebody deliberately disabled — a body change on a
    /// disabled audit trigger silently turned it back on. The guard used to
    /// exist at exactly one call site (the rebuild rescue in
    /// <see cref="ScriptGenerator"/>), which is the shape that leaves every
    /// sibling caller broken.
    /// </remarks>
    private static string EmitCreateOrAlter(Trigger t)
    {
        if (t.IsEncrypted || t.Body is null)
        {
            return $"-- WARNING: trigger {Sql.Q(t.Schema, t.Name)} is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }

        string ddl = ModuleHeader.ToCreateOrAlterScript(t.Body, t.Schema, t.Name);
        return t.IsDisabled
            ? ddl + Environment.NewLine + EmitStateChange(t, disable: true)
            : ddl;
    }

    private static string EmitStateChange(Trigger t, bool disable) =>
        $"{(disable ? "DISABLE" : "ENABLE")} TRIGGER {Sql.Q(t.Schema, t.Name)} "
        + $"ON {Sql.Q(t.ParentSchema, t.ParentTable)};";

    private static string EmitDrop(Trigger t) =>
        $"DROP TRIGGER IF EXISTS {Sql.Q(t.Schema, t.Name)};";

    private static string EmitDifferent(Trigger sideA, Trigger sideB)
    {
        bool bodiesMatch = !sideA.IsEncrypted && !sideB.IsEncrypted
            && string.Equals(
                BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(sideA.Body, sideA.Schema, sideA.Name)),
                BodyNormalizer.Normalize(ModuleHeader.CanonicalizeObjectName(sideB.Body, sideB.Schema, sideB.Name)),
                StringComparison.Ordinal);

        return bodiesMatch && sideA.IsDisabled != sideB.IsDisabled
            ? EmitStateChange(sideA, disable: sideA.IsDisabled)
            : EmitCreateOrAlter(sideA);
    }
}

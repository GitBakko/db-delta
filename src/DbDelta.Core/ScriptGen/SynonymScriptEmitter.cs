using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP SYNONYM statements. SQL Server has no ALTER SYNONYM —
/// changing the base object means DROP + CREATE.
/// </summary>
public sealed class SynonymScriptEmitter
{
    public string EmitCreate(Synonym syn)
    {
        ArgumentNullException.ThrowIfNull(syn);
        return $"CREATE SYNONYM [{syn.Schema}].[{syn.Name}] FOR {syn.BaseObjectName};";
    }

    public string EmitDrop(Synonym syn)
    {
        ArgumentNullException.ThrowIfNull(syn);
        return $"DROP SYNONYM [{syn.Schema}].[{syn.Name}];";
    }
}

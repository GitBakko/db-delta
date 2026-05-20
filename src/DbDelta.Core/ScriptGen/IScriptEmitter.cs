using DbDelta.Core.Diff;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits the T-SQL DDL required to transform side B into side A for one paired object.
/// </summary>
public interface IScriptEmitter
{
    string Emit(DifferencePair pair);
}

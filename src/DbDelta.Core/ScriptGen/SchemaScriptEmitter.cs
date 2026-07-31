using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP for schemas.
/// </summary>
/// <remarks>
/// <para>
/// Schemas are the outermost container, so they are emitted outside the
/// topological passes: every CREATE in the prologue (before any object can
/// reference one) and every DROP in the epilogue (after the objects they held
/// are gone).
/// </para>
/// <para>
/// <c>CREATE SCHEMA</c> must be the first statement in its batch, so callers
/// must give each emitted statement its own batch rather than concatenating
/// several into one body.
/// </para>
/// <para>
/// Ownership (<c>AUTHORIZATION</c>) is deliberately not emitted: the reader
/// models a schema as its name alone, so there is no owner to reproduce and
/// SQL Server correctly defaults it to the executing principal. Emitting a
/// guessed owner would be worse than omitting it.
/// </para>
/// </remarks>
public sealed class SchemaScriptEmitter : IScriptEmitter
{
    /// <inheritdoc />
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is Schema s => EmitCreate(s),
            DifferenceStatus.OnlyInB when pair.SideB is Schema s => EmitDrop(s),
            // A schema is modelled by its name alone, so two schemas that pair
            // by identity are equal by construction — there is no Different.
            DifferenceStatus.Different => string.Empty,
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>Emits <c>CREATE SCHEMA</c>. Must be first in its own batch.</summary>
    public static string EmitCreate(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return $"CREATE SCHEMA {Sql.Q(schema.Name)};";
    }

    /// <summary>Emits <c>DROP SCHEMA</c>.</summary>
    public static string EmitDrop(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return $"DROP SCHEMA {Sql.Q(schema.Name)};";
    }
}

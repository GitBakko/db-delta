using System.Globalization;
using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP / ALTER SEQUENCE statements for the M5 sequence
/// object kind. ALTER is generated when the START/INCREMENT options of an
/// existing sequence differ — SQL Server allows changing every option
/// except the base data type via ALTER SEQUENCE.
/// </summary>
public sealed class SequenceScriptEmitter
{
    public string EmitCreate(Sequence seq)
    {
        ArgumentNullException.ThrowIfNull(seq);
        StringBuilder sb = new();
        sb.Append("CREATE SEQUENCE [").Append(seq.Schema).Append("].[").Append(seq.Name).Append("] AS ").Append(seq.DataType);
        sb.Append(" START WITH ").Append(seq.StartValue.ToString(CultureInfo.InvariantCulture));
        sb.Append(" INCREMENT BY ").Append(seq.Increment.ToString(CultureInfo.InvariantCulture));
        if (seq.MinValue.HasValue)
        {
            sb.Append(" MINVALUE ").Append(seq.MinValue.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(" NO MINVALUE");
        }
        if (seq.MaxValue.HasValue)
        {
            sb.Append(" MAXVALUE ").Append(seq.MaxValue.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(" NO MAXVALUE");
        }
        sb.Append(seq.IsCycling ? " CYCLE" : " NO CYCLE");
        if (seq.IsCached)
        {
            sb.Append(" CACHE");
            if (seq.CacheSize.HasValue)
            {
                sb.Append(' ').Append(seq.CacheSize.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            sb.Append(" NO CACHE");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string EmitDrop(Sequence seq)
    {
        ArgumentNullException.ThrowIfNull(seq);
        return $"DROP SEQUENCE [{seq.Schema}].[{seq.Name}];";
    }
}

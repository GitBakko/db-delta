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
        sb.Append("CREATE SEQUENCE ").Append(Sql.Q(seq.Schema, seq.Name)).Append(" AS ").Append(seq.DataType);
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
        return $"DROP SEQUENCE {Sql.Q(seq.Schema, seq.Name)};";
    }

    /// <summary>
    /// Builds the minimum <c>ALTER SEQUENCE</c> statement that brings the
    /// target side into shape with the source. Only emits clauses for
    /// properties that actually differ; returns an empty string when the
    /// two sides are already equal. Returns <c>null</c> when the data type
    /// has changed — SQL Server cannot ALTER a sequence's base type, so
    /// callers must fall back to DROP + CREATE in that case.
    /// </summary>
    public string? EmitAlter(Sequence source, Sequence target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (!string.Equals(source.DataType, target.DataType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        StringBuilder sb = new();
        sb.Append("ALTER SEQUENCE ").Append(Sql.Q(source.Schema, source.Name));
        int prefixLength = sb.Length;

        if (source.StartValue != target.StartValue)
        {
            sb.Append(" RESTART WITH ").Append(source.StartValue.ToString(CultureInfo.InvariantCulture));
        }
        if (source.Increment != target.Increment)
        {
            sb.Append(" INCREMENT BY ").Append(source.Increment.ToString(CultureInfo.InvariantCulture));
        }
        if (source.MinValue != target.MinValue)
        {
            sb.Append(source.MinValue.HasValue
                ? " MINVALUE " + source.MinValue.Value.ToString(CultureInfo.InvariantCulture)
                : " NO MINVALUE");
        }
        if (source.MaxValue != target.MaxValue)
        {
            sb.Append(source.MaxValue.HasValue
                ? " MAXVALUE " + source.MaxValue.Value.ToString(CultureInfo.InvariantCulture)
                : " NO MAXVALUE");
        }
        if (source.IsCycling != target.IsCycling)
        {
            sb.Append(source.IsCycling ? " CYCLE" : " NO CYCLE");
        }
        if (source.IsCached != target.IsCached || source.CacheSize != target.CacheSize)
        {
            if (source.IsCached)
            {
                sb.Append(" CACHE");
                if (source.CacheSize.HasValue)
                {
                    sb.Append(' ').Append(source.CacheSize.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                sb.Append(" NO CACHE");
            }
        }

        // Nothing changed → return empty so the caller skips emission entirely.
        if (sb.Length == prefixLength)
        {
            return string.Empty;
        }

        sb.Append(';');
        return sb.ToString();
    }
}

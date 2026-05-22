using System.Globalization;
using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE TYPE / DROP TYPE statements for alias UDTs. CLR UDTs are
/// out of scope (need ASSEMBLY deploy first). Format matches SSMS output:
/// CREATE TYPE [schema].[name] FROM &lt;baseType&gt;(&lt;size&gt;) [NOT NULL].
/// </summary>
public sealed class UserDefinedTypeScriptEmitter
{
    public string EmitCreate(UserDefinedType udt)
    {
        ArgumentNullException.ThrowIfNull(udt);
        StringBuilder sb = new();
        sb.Append("CREATE TYPE [").Append(udt.Schema).Append("].[").Append(udt.Name)
          .Append("] FROM ").Append(FormatBaseType(udt));
        if (!udt.IsNullable)
        {
            sb.Append(" NOT NULL");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string EmitDrop(UserDefinedType udt)
    {
        ArgumentNullException.ThrowIfNull(udt);
        return $"DROP TYPE [{udt.Schema}].[{udt.Name}];";
    }

    /// <summary>
    /// Renders the base SQL type with size / precision suffix, matching the
    /// convention used by <see cref="TableScriptEmitter"/> for table columns
    /// so source and target sides line up byte-for-byte in the diff viewer.
    /// </summary>
    private static string FormatBaseType(UserDefinedType udt) => udt.BaseTypeName.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" =>
            udt.MaxLength == -1 ? $"{udt.BaseTypeName}(max)"
                                : $"{udt.BaseTypeName}({(udt.MaxLength / 2).ToString(CultureInfo.InvariantCulture)})",
        "varchar" or "char" or "varbinary" or "binary" =>
            udt.MaxLength == -1 ? $"{udt.BaseTypeName}(max)"
                                : $"{udt.BaseTypeName}({udt.MaxLength.ToString(CultureInfo.InvariantCulture)})",
        "decimal" or "numeric" =>
            $"{udt.BaseTypeName}({udt.Precision.ToString(CultureInfo.InvariantCulture)},{udt.Scale.ToString(CultureInfo.InvariantCulture)})",
        "datetime2" or "time" or "datetimeoffset" =>
            $"{udt.BaseTypeName}({udt.Scale.ToString(CultureInfo.InvariantCulture)})",
        _ => udt.BaseTypeName,
    };
}

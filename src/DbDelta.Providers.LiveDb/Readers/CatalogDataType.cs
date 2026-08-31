namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Renders a <c>sys.columns</c> row's type as the text the object model carries
/// in <c>Column.DataType</c>: the bare catalog type name, with the length /
/// precision the type actually takes.
/// </summary>
/// <remarks>
/// <para>
/// This was three byte-identical private copies — <c>TableReader</c>,
/// <c>TableTypeUdtReader</c> and <c>LiveDbObjectBodyResolver</c> — which is
/// three chances to drift and no way to notice. Qualifying alias type names had
/// to touch all three, so they became one.
/// </para>
/// <para>
/// An ALIAS type falls through to the default arm and keeps its bare name with
/// no length appended, which is what <c>CREATE TABLE (c app.MioTipo)</c> wants:
/// the length belongs to the type, not to the column that uses it. The schema
/// is NOT joined on here — it travels beside this value in
/// <c>Column.TypeSchema</c>, because a dotted <c>DataType</c> would be
/// bracket-quoted as one identifier downstream.
/// </para>
/// </remarks>
internal static class CatalogDataType
{
    public static string Format(string typeName, short maxLength, byte precision, byte scale) =>
        typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName,
        };
}

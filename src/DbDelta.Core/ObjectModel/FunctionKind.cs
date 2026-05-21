namespace DbDelta.Core.ObjectModel;

/// <summary>
/// SQL Server function kinds that DbDelta supports in v1. Mirrors the
/// <c>sys.objects.type</c> codes: <c>FN</c> / <c>IF</c> / <c>TF</c>.
/// </summary>
/// <remarks>
/// CLR functions (<c>FS</c>, <c>FT</c>) are intentionally excluded — they
/// fall under the v2 parking-lot per spec §6.3.
/// </remarks>
public enum FunctionKind
{
    /// <summary>Scalar T-SQL function (<c>sys.objects.type = 'FN'</c>).</summary>
    Scalar,
    /// <summary>Inline table-valued function (<c>sys.objects.type = 'IF'</c>).</summary>
    InlineTableValued,
    /// <summary>Multi-statement table-valued function (<c>sys.objects.type = 'TF'</c>).</summary>
    MultiStatementTableValued,
}

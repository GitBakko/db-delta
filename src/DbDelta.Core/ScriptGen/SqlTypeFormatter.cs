namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Formats a column data-type token to match Redgate SQL Compare's style: the
/// type name is bracket-quoted and a single space precedes the size/precision
/// arguments, whose internal commas are spaced. For example
/// <c>nvarchar(200)</c> becomes <c>[nvarchar] (200)</c> and
/// <c>decimal(18,2)</c> becomes <c>[decimal] (18, 2)</c>.
/// </summary>
/// <remarks>
/// The name used to be passed through unquoted when it already started with
/// <c>[</c> or held a <c>.</c>, on the theory that it was an alias or a
/// schema-qualified user-defined type someone had already quoted. No producer
/// in this repo ever emits either shape: every <c>DataType</c> reaching here is
/// a bare <c>sys.types.name</c> with an optional length, from
/// <c>TableReader</c>, <c>TableTypeUdtReader</c>, or the body resolver. So the
/// branch fired for exactly one input — a catalog type name holding a bracket
/// or a dot — and handed it to the script raw, which is the one sink S11 set
/// out to close.
/// <para>
/// That invariant is now load-bearing rather than historical. An alias type's
/// schema reaches this class as its own argument and is quoted separately;
/// nothing ever writes <c>schema.Name</c> into a <c>DataType</c>. Were anything
/// to start, the single <c>Sql.Q</c> below would wrap the lot into
/// <c>[app.MioTipo]</c> — the shape
/// <c>IdentifierEscapingTests("dbo.Money", "[dbo.Money]")</c> deliberately
/// pins.
/// </para>
/// </remarks>
internal static class SqlTypeFormatter
{
    /// <summary>
    /// The schema an unqualified alias type name is allowed to stay
    /// unqualified for. This is the ONE place the qualification policy lives.
    /// </summary>
    private const string ImplicitTypeSchema = "dbo";

    /// <summary>
    /// Whether an alias type living in <paramref name="typeSchema"/> has to be
    /// written schema-qualified. Today: everything except <c>dbo</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on <c>mssql/server:2022-latest</c>, on real logins whose
    /// DEFAULT_SCHEMA differed, not under <c>EXECUTE AS</c>: SQL Server
    /// resolves an unqualified type name for a table or table-type column
    /// against the CALLER'S DEFAULT SCHEMA FIRST, then <c>dbo</c>. So a type
    /// in <c>dbo</c> always resolves for everyone — which is why the bare name
    /// is kept for it, and why no existing script changes a byte — while a
    /// type in any other schema is invisible to a <c>dbo</c>-default caller
    /// and dies with Msg 2715 ("Cannot find data type"). That is the defect
    /// this predicate closes.
    /// </para>
    /// <para>
    /// KNOWN AND ACCEPTED, measured on the same server: because the caller's
    /// default schema wins over <c>dbo</c>, a bare <c>dbo</c> type name binds
    /// to a DIFFERENT type when the target holds a same-named one in the
    /// deploying principal's default schema. Byte-identical DDL produced
    /// <c>user_type_id</c> 258 (<c>dbo.MioTipo</c>) run by a dbo-default login
    /// and 257 (<c>app.MioTipo</c>) run by an app-default one. That is silent,
    /// not loud. Closing it means qualifying <c>dbo</c> too, and the whole
    /// change is this constant: set <see cref="ImplicitTypeSchema"/> to
    /// <c>null</c> and every alias type comes out qualified. Left as it is
    /// because it makes the emitted script diverge from every one issued so
    /// far — a product decision, recorded in docs/BACKLOG.md rather than taken
    /// here.
    /// </para>
    /// <para>
    /// Note this governs COLUMNS only. A procedure or function parameter
    /// resolves against the schema of the MODULE being created, not the
    /// caller's — a second rule in the same server, also measured — and DbDelta
    /// never models those types anyway: they ride inside
    /// <c>sys.sql_modules.definition</c> as opaque text and are re-emitted
    /// verbatim, which binds identically on the target precisely because the
    /// module's own schema travels with them.
    /// </para>
    /// </remarks>
    private static bool NeedsSchemaQualification(string? typeSchema) =>
        typeSchema is not null
        && !string.Equals(typeSchema, ImplicitTypeSchema, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The base type of a <c>CREATE SEQUENCE</c>. Returns
    /// <paramref name="dataType"/> untouched unless it is an alias type needing
    /// its schema.
    /// </summary>
    /// <remarks>
    /// A sequence's BUILT-IN base type has never been bracket-quoted —
    /// <c>AS bigint</c>, not <c>AS [bigint]</c> — and bracketing it now would
    /// be cosmetic churn on every sequence ever emitted. An ALIAS type is the
    /// other case entirely and is always quoted, dbo included: that name is an
    /// arbitrary user identifier out of <c>TYPE_NAME(seq.user_type_id)</c>, and
    /// <c>SequenceScriptEmitter</c> appended it raw — no <c>Sql.Q</c> anywhere
    /// — which is the last sink left outside the rule <c>Sql</c> states as
    /// "every bracketed name in a generated script goes through here".
    /// <c>CREATE TYPE dbo.[Ordine Riga] FROM bigint</c> emitted
    /// <c>AS Ordine Riga</c> and would not parse (Msg 102, measured); a name
    /// holding <c>]</c> is the S11 injection shape. Quoting a dbo alias type
    /// moves no existing script that was valid to begin with.
    /// </remarks>
    public static string FormatSequenceBaseType(string dataType, string? typeSchema) =>
        typeSchema is null ? dataType : FormatColumnType(dataType, typeSchema);

    /// <param name="dataType">
    /// The bare catalog type name with its optional length — never
    /// schema-qualified. See the class remarks: a dotted value here is quoted
    /// as one identifier and produces <c>[app.MioTipo]</c>.
    /// </param>
    /// <param name="typeSchema">
    /// Schema of the alias type, or null for a built-in type or a column that
    /// was not read from a catalog.
    /// </param>
    public static string FormatColumnType(string dataType, string? typeSchema)
    {
        string t = dataType.Trim();
        int paren = t.IndexOf('(');
        string name = (paren < 0 ? t : t[..paren]).TrimEnd();
        string bracketedName = NeedsSchemaQualification(typeSchema)
            ? Sql.Q(typeSchema!, name)
            : Sql.Q(name);
        if (paren < 0)
        {
            return bracketedName;
        }

        string inner = t[(paren + 1)..].Trim().TrimEnd(')').Trim();
        string spacedArgs = string.Join(", ", inner.Split(',').Select(a => a.Trim()));
        return $"{bracketedName} ({spacedArgs})";
    }
}

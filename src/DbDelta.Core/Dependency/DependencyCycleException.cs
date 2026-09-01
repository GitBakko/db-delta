using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// Thrown when a dependency cycle is detected among create-validated objects.
/// </summary>
/// <remarks>
/// <para>
/// This used to say such a cycle was uncreatable in a valid source database and
/// therefore signalled a reader bug rather than user error. That was wrong, and
/// being wrong is what left the CREATE path unguarded: a <c>CHECK</c> that
/// calls a function reading its own table is legal and ordinary, and it closes
/// the loop. Measured on <c>mssql/server:2022-latest</c> — the table, the
/// function and <c>CHECK (dbo.fnRowCount() &lt; 100)</c> all create, and the
/// table then accepts rows.
/// </para>
/// <para>
/// The cycle exists because DbDelta writes the constraint INSIDE
/// <c>CREATE TABLE</c>, which forces the table after the function while the
/// function still needs the table. Its own reader query returns both arcs —
/// <c>fnRowCount [FN] → Righe [U]</c>, and <c>Righe [U] → fnRowCount [FN]</c>
/// because a CHECK's references are attributed to its parent table. A tool that
/// emits the constraint as a trailing <c>ALTER TABLE</c> is immune by
/// construction; changing that here is a product decision, so the cycle is
/// REFUSED rather than worked around.
/// </para>
/// <para>
/// It is therefore a user-facing refusal, not an internal error: CLI exit 31,
/// which <c>ExitCodes.UnresolvableDependencyCycle</c> and
/// <c>CliErrorMapper</c> had reserved and documented since spec §4.3 without
/// anything ever producing it, and a banner in the app.
/// </para>
/// </remarks>
public sealed class DependencyCycleException(IReadOnlyList<ObjectIdentity> cycle) : Exception("Dependency cycle among create-validated objects: "
               + string.Join(" → ", cycle.Select(o => $"{o.SchemaName}.{o.ObjectName}")))
{
    public IReadOnlyList<ObjectIdentity> Cycle { get; } = cycle;
}

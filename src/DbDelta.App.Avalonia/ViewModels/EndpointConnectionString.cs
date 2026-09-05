using DbDelta.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace DbDelta.App.ViewModels;

/// <summary>
/// The single place the app turns endpoint fields into a connection string.
/// </summary>
/// <remarks>
/// There were four copies of this, all interpolating raw values into a
/// <c>';'</c>-delimited, <c>'='</c>-keyed format. A password may legally contain
/// both characters, so <c>Password=a;b</c> produced a string that no longer
/// parses — and the error the user reads names the initialization string, never
/// the password. Nothing downstream required a successful connection either, so
/// the broken string was accepted and carried into <c>AppState</c>, where every
/// later comparison inherited it. <see cref="SqlConnectionStringBuilder"/> owns
/// the quoting rules: this is CLAUDE.md's "validate input at system boundaries"
/// and the app's DRY rule #3 answered by the same few lines.
/// </remarks>
internal static class EndpointConnectionString
{
    /// <param name="serverName">Goes to <c>Data Source</c>.</param>
    /// <param name="databaseName">
    /// Omitted when null or blank. The setup panel connects without a catalog
    /// first — that is how it lists the databases the user then picks from.
    /// </param>
    /// <param name="authMode">
    /// Windows auth writes <c>Integrated Security</c> and no credentials at all;
    /// every other value is treated as SQL auth.
    /// </param>
    /// <param name="userName">
    /// Ignored under <see cref="AuthenticationMode.WindowsIntegrated"/>. Trimmed,
    /// like <paramref name="databaseName"/>: the old unquoted format was re-parsed
    /// by SqlClient, which drops the padding of an unquoted value, so a pasted
    /// <c>" sa"</c> logged in as <c>sa</c>. The builder quotes a padded value and
    /// the parser then keeps the padding, so without the trim the same paste now
    /// reaches the server as a login called <c>" sa"</c>.
    /// </param>
    /// <param name="password">
    /// Ignored under <see cref="AuthenticationMode.WindowsIntegrated"/>. NOT
    /// trimmed — carrying it byte for byte is the point of this class.
    /// </param>
    /// <param name="trustServerCertificate">Always written, as all four callers did.</param>
    /// <param name="encrypt">
    /// Left unset when null, so the string carries no <c>Encrypt</c> keyword and
    /// SqlClient's own default applies — which is what the connection manager
    /// has always done, and changing it here would be a behaviour change hiding
    /// inside a de-duplication.
    /// </param>
    public static string Build(
        string serverName,
        string? databaseName,
        AuthenticationMode authMode,
        string? userName,
        string? password,
        bool trustServerCertificate,
        bool? encrypt = null)
    {
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = serverName,
            TrustServerCertificate = trustServerCertificate,
        };

        if (!string.IsNullOrWhiteSpace(databaseName)) { builder.InitialCatalog = databaseName.Trim(); }
        if (encrypt is bool e) { builder.Encrypt = e; }

        if (authMode == AuthenticationMode.WindowsIntegrated)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = (userName ?? "").Trim();
            builder.Password = password ?? "";
        }

        return builder.ConnectionString;
    }
}

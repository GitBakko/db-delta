using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when a user must be created but the login it is mapped to has no
/// name DbDelta can read, so the statement it would emit would map the user to
/// no login at all.
/// </summary>
/// <remarks>
/// <para>
/// This is the third of the same family, and it fails the same way
/// <see cref="UnscriptablePermissionException"/> does: not by producing an
/// invalid statement, but a valid one that means something else.
/// <c>CREATE USER [app] WITHOUT LOGIN</c> succeeds, and leaves an orphaned
/// principal nobody can authenticate as — under a green banner, on a target
/// where the user was already correct.
/// </para>
/// <para>
/// The name goes missing for an ordinary reason: <c>sys.server_principals</c>
/// is filtered by metadata visibility, so a least-privilege login reads NULL
/// for every login it does not own. <see cref="DatabaseUser.LoginNameIsHidden"/>
/// is what separates that from a user genuinely created WITHOUT LOGIN — the
/// reader sets it from <c>authentication_type</c>, which stays readable.
/// </para>
/// <para>
/// Generation runs to completion before a single batch is sent, so a throw here
/// stops the deploy with no SQL executed. Callers surface it as a refusal, not
/// as a crash: the CLI exits 30 and the app shows the error banner. The way out
/// is to re-read that endpoint with a login that can see the server principals,
/// which is also the only way to learn what the correct statement would be.
/// </para>
/// </remarks>
public sealed class UnscriptableUserException(DatabaseUser user)
    : Exception($"Refusing to script the user {Sql.Q(user.Name)}: it is mapped to a login "
              + "whose name this connection cannot see, so the statement would create it "
              + "WITHOUT LOGIN and leave a principal nobody can sign in as.")
{
    /// <summary>The user that stopped the run.</summary>
    public DatabaseUser User { get; } = user;

    public string UserName { get; } = user.Name;

    /// <summary>
    /// Throws when the login name was hidden from the reader. The single guard
    /// the one emission path calls — both the deploy script and the diff
    /// viewer's body go through <see cref="UserScriptEmitter.EmitCreate"/>.
    /// </summary>
    public static void ThrowIfLoginNameIsHidden(DatabaseUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.LoginNameIsHidden) { return; }
        throw new UnscriptableUserException(user);
    }
}

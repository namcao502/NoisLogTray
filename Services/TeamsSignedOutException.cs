namespace NoisLogTray;

// The saved Teams sign-in is gone or was rejected; the user has to sign in again.
internal sealed class TeamsSignedOutException : Exception
{
    internal TeamsSignedOutException() : base("Teams is signed out. Sign in again.")
    {
    }
}

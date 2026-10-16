namespace NoisLogTray;

// The reply request may have reached Teams, so it must not be sent again without the user checking the thread.
internal sealed class TeamsReplyUncertainException : Exception
{
    internal TeamsReplyUncertainException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

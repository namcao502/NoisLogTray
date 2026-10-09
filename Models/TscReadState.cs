namespace NoisLogTray;

// Why a TSC cell has (or lacks) a value: future days are deliberately not read, which is not a failure.
internal enum TscReadState
{
    Read,
    NotDue,
    NoToken,
    Failed,
}

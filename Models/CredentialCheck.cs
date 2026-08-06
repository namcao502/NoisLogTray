namespace NoisLogTray;

// Outcome of verifying an entered credential against its live service:
// Valid = accepted; Rejected = reached the service and it refused the credential
// (401/403); Unreachable = could not reach the service to decide (offline, bad host,
// timeout) so the credential is neither confirmed nor disproven.
internal enum CredentialCheck
{
    Valid,
    Rejected,
    Unreachable,
}

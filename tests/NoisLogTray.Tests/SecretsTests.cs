using NoisLogTray;

namespace NoisLogTray.Tests;

// Covers Secrets (DPAPI at rest): protect/unprotect round-trip, the "enc:" marker,
// and graceful passthrough of plaintext / empty values.
public class SecretsTests
{
    [Fact]
    public void ProtectThenUnprotectRoundTrips()
    {
        const string plain = "super-secret-token-123";
        var protectedValue = Secrets.Protect(plain);

        Assert.NotEqual(plain, protectedValue);
        Assert.True(Secrets.IsProtected(protectedValue));
        Assert.Equal(plain, Secrets.Unprotect(protectedValue));
    }

    [Fact]
    public void UnprotectPassesThroughPlaintext()
    {
        // A value without the "enc:" marker (hand-edited or not yet upgraded) is returned as-is.
        Assert.Equal("plain-value", Secrets.Unprotect("plain-value"));
    }

    [Fact]
    public void EmptyStaysEmptyAndIsNotProtected()
    {
        Assert.Equal("", Secrets.Protect(""));
        Assert.False(Secrets.IsProtected(""));
    }

    [Fact]
    public void ProtectIsIdempotentOnAlreadyProtectedValue()
    {
        var once = Secrets.Protect("token");
        var twice = Secrets.Protect(once);
        Assert.Equal(once, twice); // already-protected value is left unchanged
        Assert.Equal("token", Secrets.Unprotect(twice));
    }

    [Fact]
    public void SecretKeysAreRecognised()
    {
        Assert.True(Secrets.IsSecretKey("JIRA_API_TOKEN"));
        Assert.True(Secrets.IsSecretKey("HRM_API_KEY"));
        Assert.False(Secrets.IsSecretKey("JIRA_EMAIL"));
    }
}

using System.Security.Cryptography;
using System.Text;

namespace NoisLogTray;

// Encrypt secret config values at rest with Windows DPAPI (CurrentUser scope), so
// tokens in settings.json can only be decrypted by the same Windows user on the same
// machine. Stored values are prefixed "enc:" + base64; anything without the prefix is
// treated as plaintext (hand-edited or not yet upgraded) and returned as-is.
internal static class Secrets
{
    private const string Prefix = "enc:";

    // Config keys whose values are encrypted at rest.
    internal static readonly string[] Keys = { "JIRA_API_TOKEN", "HRM_API_KEY" };

    internal static bool IsSecretKey(string key) => Array.IndexOf(Keys, key) >= 0;

    internal static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    // Encrypt a plaintext value. Empty stays empty; on any DPAPI failure the plaintext
    // is returned unchanged so a value is never lost, just not encrypted.
    internal static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch
        {
            return plaintext;
        }
    }

    // Decrypt a stored value. A non-prefixed (plaintext) value is returned as-is; if a
    // prefixed value cannot be decrypted (e.g. copied to another machine), it is
    // returned unchanged so the caller can surface a normal "invalid credential" path.
    internal static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored)) return stored;
        try
        {
            var enc = Convert.FromBase64String(stored[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return stored;
        }
    }
}

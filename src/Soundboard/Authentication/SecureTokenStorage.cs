using System.Security.Cryptography;
using System.Text.Json;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Persists the current session's refresh token to disk, encrypted with Windows DPAPI
/// (tied to the current Windows user account — nothing readable by another user on the
/// same machine, and nothing that survives copying the file elsewhere). This is what
/// "Remember Me" and auto-login are actually built on: the access token is short-lived and
/// never persisted, only the refresh token, which is exchanged for a fresh access token at
/// startup via SupabaseAuthService.RefreshSessionAsync.
/// </summary>
public sealed class SecureTokenStorage
{
    private static string StoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Soundboard", "session.dat");

    private sealed class StoredSession
    {
        public string UserId { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
    }

    public void Save(AuthSession session)
    {
        try
        {
            var stored = new StoredSession { UserId = session.UserId, RefreshToken = session.RefreshToken };
            var json = JsonSerializer.Serialize(stored);
            var plainBytes = System.Text.Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plainBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

            var directory = Path.GetDirectoryName(StoragePath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(StoragePath, encrypted);
        }
        catch
        {
            // Remember Me is a convenience, not a critical operation — a failure here just
            // means the user has to log in again next time, not a reason to crash.
        }
    }

    public (string UserId, string RefreshToken)? TryLoad()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;

            var encrypted = File.ReadAllBytes(StoragePath);
            var plainBytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            var json = System.Text.Encoding.UTF8.GetString(plainBytes);
            var stored = JsonSerializer.Deserialize<StoredSession>(json);

            return stored is null || string.IsNullOrEmpty(stored.RefreshToken)
                ? null
                : (stored.UserId, stored.RefreshToken);
        }
        catch
        {
            // Corrupt file, DPAPI decryption failure (e.g. different Windows user), etc. —
            // treat exactly like "no remembered session" rather than propagating an error.
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(StoragePath))
            {
                File.Delete(StoragePath);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}

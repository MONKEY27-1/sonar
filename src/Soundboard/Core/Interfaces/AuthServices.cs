using Soundboard.Core.Models;

namespace Soundboard.Core.Interfaces;

/// <summary>
/// Talks to the auth backend (Supabase Auth). Every method returns an AuthResult rather than
/// throwing for expected failures — wrong password, email already taken, offline, etc. are
/// all normal outcomes a login screen needs to show inline, not exceptions to catch.
/// </summary>
public interface IAuthenticationService
{
    Task<AuthResult> RegisterAsync(string username, string email, string password, CancellationToken cancellationToken = default);
    Task<AuthResult<AuthSession>> LoginAsync(string emailOrUsername, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(AuthSession session, CancellationToken cancellationToken = default);
    Task<AuthResult<AuthSession>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<AuthResult> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task<AuthResult> ResendVerificationEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<AuthResult<UserProfile>> GetProfileAsync(AuthSession session, CancellationToken cancellationToken = default);

    /// <summary>Confirms a signup using the numeric code Supabase emailed the user, returning
    /// a live session on success (the same as logging in) so the app can go straight into the
    /// signed-in state without a second round trip.</summary>
    Task<AuthResult<AuthSession>> VerifyEmailAsync(string email, string token, CancellationToken cancellationToken = default);

    /// <summary>Confirms a password reset using the numeric code Supabase emailed the user and
    /// sets the new password in the same call, returning a live session on success.</summary>
    Task<AuthResult<AuthSession>> ConfirmPasswordResetAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    Task<AuthResult> ChangePasswordAsync(AuthSession session, string newPassword, CancellationToken cancellationToken = default);

    Task<AuthResult> UpdateProfileAsync(AuthSession session, ProfileUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Marks the account for deletion (sets a timestamp the user themself can set via
    /// RLS). Actual removal of the underlying auth.users row requires a trusted server-side
    /// process — a desktop client can never safely hold the credentials to do that directly.</summary>
    Task<AuthResult> RequestAccountDeletionAsync(AuthSession session, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the current session's lifecycle: restoring it at startup (auto-login), persisting it
/// across restarts (remember me), and clearing it on logout. The actual token storage
/// mechanism (DPAPI-encrypted local file, for now) is an implementation detail behind this.
/// </summary>
public interface ISessionService
{
    AuthSession? CurrentSession { get; }
    UserProfile? CurrentProfile { get; }
    bool IsLoggedIn { get; }

    event EventHandler? SessionChanged;

    /// <summary>Called once at startup. Returns true if a remembered session was restored
    /// (and is valid or was successfully refreshed) — false means show the login screen.</summary>
    Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default);

    Task SetSessionAsync(AuthSession session, UserProfile profile, bool rememberMe, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a valid access token for the current session, silently refreshing
    /// first if it's close to expiry. Null if there's no active session.</summary>
    Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// License/feature-gating logic. Beta testers and paid Pro users both unlock everything —
/// this is the single place that decision lives, so UI code never has to special-case it.
/// </summary>
public interface ILicenseService
{
    LicenseType CurrentLicense { get; }
    bool IsProUnlocked { get; }
    bool IsBetaTester { get; }

    /// <summary>Maximum sounds the current license allows in the library; null means unlimited.</summary>
    int? MaxSounds { get; }

    /// <summary>Maximum folders the current license allows; null means unlimited.</summary>
    int? MaxFolders { get; }

    /// <summary>Whether the AMOLED/Custom theme kinds and a custom accent color are unlocked.</summary>
    bool CanUseCustomTheme { get; }

    void UpdateFromProfile(UserProfile? profile);
}

/// <summary>
/// Cross-user account management for Administrator accounts — listing every user and changing
/// their license/beta/suspended status. Kept separate from <see cref="IAuthenticationService"/>
/// on purpose: this is a privileged, different-user operation, not the caller's own identity.
/// Every method is server-enforced (the backing RPCs re-check the caller is actually an admin),
/// not just gated by the UI.
/// </summary>
public interface IAdminService
{
    Task<AuthResult<IReadOnlyList<AdminUserSummary>>> ListUsersAsync(AuthSession session, CancellationToken cancellationToken = default);
    Task<AuthResult> UpdateUserAsync(AuthSession session, string targetUserId, LicenseType license, bool isBetaTester, bool isSuspended, CancellationToken cancellationToken = default);

    /// <summary>Marks a Plugin Marketplace catalog entry (see PluginCatalog) as verified/
    /// trustworthy or not — server-enforced via the same is_caller_admin() check as
    /// <see cref="UpdateUserAsync"/>.</summary>
    Task<AuthResult> SetPluginVerifiedAsync(AuthSession session, string pluginId, bool isVerified, CancellationToken cancellationToken = default);

    /// <summary>Marks a Community tab submission (see <see cref="ICommunityPluginService"/>) as
    /// verified/trustworthy — the admin has actually read the script source.</summary>
    Task<AuthResult> SetCommunityPluginVerifiedAsync(AuthSession session, string communityPluginId, bool isVerified, CancellationToken cancellationToken = default);

    /// <summary>Outright removes a Community tab submission — for spam or anything a plugin
    /// review decides shouldn't be listed at all, not just left unverified.</summary>
    Task<AuthResult> DeleteCommunityPluginAsync(AuthSession session, string communityPluginId, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="SetCommunityPluginVerifiedAsync"/> but for a published
    /// <see cref="ICommunityPackService"/> submission.</summary>
    Task<AuthResult> SetCommunityPackVerifiedAsync(AuthSession session, string communityPackId, bool isVerified, CancellationToken cancellationToken = default);

    Task<AuthResult> DeleteCommunityPackAsync(AuthSession session, string communityPackId, CancellationToken cancellationToken = default);

    /// <summary>Overwrites the single broadcast announcement every user sees (see
    /// <see cref="IAdminMessageService"/>) — an empty string clears it.</summary>
    Task<AuthResult> SetAdminMessageAsync(AuthSession session, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Public read of the current admin broadcast message — shown to every user, including
/// offline/not-logged-in ones, same reasoning as <see cref="IPluginTrustService"/>.
/// </summary>
public interface IAdminMessageService
{
    /// <summary>Never throws — returns null on any failure (offline, misconfigured, server
    /// error) or when the message is empty, so the caller can treat both cases identically as
    /// "nothing to show."</summary>
    Task<string?> GetMessageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Browsing/searching/submitting user-authored Community tab script plugins. Submissions are
/// plain script text run through a sandbox (see PluginScriptRunner) — this service only ever
/// stores/retrieves that text, it never executes anything.
/// </summary>
public interface ICommunityPluginService
{
    /// <summary>Never throws — returns an empty list on any failure (offline, misconfigured,
    /// server error), same never-throw contract as <see cref="IPluginTrustService"/>.
    /// <paramref name="query"/> filters by name/description (null/empty = no filter);
    /// <paramref name="verifiedOnly"/> true restricts to verified submissions only.</summary>
    Task<IReadOnlyList<CommunityPlugin>> SearchAsync(string? query, bool verifiedOnly, CancellationToken cancellationToken = default);

    Task<AuthResult> SubmitAsync(AuthSession session, string name, string? description, string scriptSource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Browsing/searching/submitting user-published "Basic Plugin" settings packs — the no-code
/// counterpart to <see cref="ICommunityPluginService"/>.
/// </summary>
public interface ICommunityPackService
{
    /// <summary>Never throws — same never-throw contract as <see cref="ICommunityPluginService.SearchAsync"/>.</summary>
    Task<IReadOnlyList<CommunityPack>> SearchAsync(string? query, bool verifiedOnly, CancellationToken cancellationToken = default);

    Task<AuthResult> SubmitAsync(AuthSession session, string name, string? description, PluginPack pack, CancellationToken cancellationToken = default);
}

/// <summary>
/// Public read of which Plugin Marketplace catalog entries are currently verified/trustworthy —
/// deliberately separate from <see cref="ICloudService"/>, which requires a logged-in session
/// via IsAvailable. Trust status has to be visible even to offline/not-logged-in users, since
/// offline use is a first-class path in this app.
/// </summary>
public interface IPluginTrustService
{
    /// <summary>Never throws — returns an empty set on any failure (offline, misconfigured,
    /// server error), so a failed fetch just means "nothing shows as verified" rather than an
    /// error surfaced to the user. Mirrors ICloudService.GetLastSyncTimeAsync's safety style.</summary>
    Task<IReadOnlySet<string>> GetVerifiedPluginIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Cloud sync — intentionally unimplemented for now. Every method throws
/// <see cref="NotSupportedException"/> until a real sync backend and conflict-resolution
/// strategy are designed; this interface exists so the rest of the app (settings, library,
/// hotkeys) can be written against "there might be a cloud" without knowing or caring
/// whether one actually exists yet.
/// </summary>
public interface ICloudService
{
    bool IsAvailable { get; }
    Task<DateTime?> GetLastSyncTimeAsync(CancellationToken cancellationToken = default);
    Task SyncSoundLibraryAsync(CancellationToken cancellationToken = default);
    Task SyncSettingsAsync(CancellationToken cancellationToken = default);
}

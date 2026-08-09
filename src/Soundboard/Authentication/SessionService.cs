using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

public sealed class SessionService : ISessionService, IDisposable
{
    private static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);

    private readonly IAuthenticationService _authService;
    private readonly SecureTokenStorage _tokenStorage;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Timer? _revalidationTimer;

    public SessionService(IAuthenticationService authService, SecureTokenStorage tokenStorage)
    {
        _authService = authService;
        _tokenStorage = tokenStorage;
    }

    public AuthSession? CurrentSession { get; private set; }
    public UserProfile? CurrentProfile { get; private set; }
    public bool IsLoggedIn => CurrentSession is not null;

    public event EventHandler? SessionChanged;

    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var remembered = _tokenStorage.TryLoad();
        if (remembered is null) return false;

        var result = await _authService.RefreshSessionAsync(remembered.Value.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Value is null)
        {
            // Refresh token is no longer valid (expired, revoked, account changed elsewhere) —
            // clear it so we don't keep retrying a dead token on every future launch.
            _tokenStorage.Clear();
            return false;
        }

        var profileResult = await _authService.GetProfileAsync(result.Value, cancellationToken).ConfigureAwait(false);

        // A suspended account's remembered session must be rejected outright, not silently
        // treated as "logged in with an unknown profile" the way other profile-fetch failures
        // (e.g. a flaky server) are tolerated below.
        if (profileResult.ErrorKind == AuthErrorKind.AccountSuspended)
        {
            _tokenStorage.Clear();
            return false;
        }

        CurrentSession = result.Value;
        CurrentProfile = profileResult.Success ? profileResult.Value : null;
        _tokenStorage.Save(result.Value); // Refresh tokens rotate on use — persist the new one.

        StartRevalidationTimer();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public Task SetSessionAsync(AuthSession session, UserProfile profile, bool rememberMe, CancellationToken cancellationToken = default)
    {
        CurrentSession = session;
        CurrentProfile = profile;

        if (rememberMe)
        {
            _tokenStorage.Save(session);
        }
        else
        {
            _tokenStorage.Clear();
        }

        StartRevalidationTimer();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentSession is not null)
        {
            await _authService.LogoutAsync(CurrentSession, cancellationToken).ConfigureAwait(false);
        }

        CurrentSession = null;
        CurrentProfile = null;
        _tokenStorage.Clear();

        StopRevalidationTimer();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var session = CurrentSession;
        if (session is null) return null;
        if (!session.NeedsRefresh) return session.AccessToken;

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — another call may have already refreshed
            // while this one was waiting.
            session = CurrentSession;
            if (session is null) return null;
            if (!session.NeedsRefresh) return session.AccessToken;

            var result = await _authService.RefreshSessionAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.Value is null)
            {
                CurrentSession = null;
                CurrentProfile = null;
                _tokenStorage.Clear();
                StopRevalidationTimer();
                SessionChanged?.Invoke(this, EventArgs.Empty);
                return null;
            }

            CurrentSession = result.Value;
            if (_tokenStorage.TryLoad() is not null)
            {
                _tokenStorage.Save(result.Value);
            }

            return result.Value.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Periodically calls <see cref="GetValidAccessTokenAsync"/> while logged in, so an
    /// expired/revoked refresh token is discovered (and the session cleared, logging the user
    /// out) even if nothing else happens to need a fresh access token for a while — this is
    /// what "auto logout after expired sessions" actually means for a mostly-offline desktop
    /// app that doesn't constantly call authenticated endpoints.</summary>
    private void StartRevalidationTimer()
    {
        _revalidationTimer ??= new Timer(_ => _ = RevalidateAsync(), null, RevalidationInterval, RevalidationInterval);
    }

    private void StopRevalidationTimer()
    {
        _revalidationTimer?.Dispose();
        _revalidationTimer = null;
    }

    private async Task RevalidateAsync()
    {
        try
        {
            var token = await GetValidAccessTokenAsync().ConfigureAwait(false);
            if (token is null) return; // already logged out by a failed token refresh above

            var session = CurrentSession;
            if (session is null) return;

            // Token refresh alone wouldn't catch an admin suspending this account mid-session
            // (the refresh token can still be perfectly valid) — re-fetch the profile too, which
            // is where suspension is actually enforced (SupabaseAuthService.GetProfileAsync).
            var profileResult = await _authService.GetProfileAsync(session).ConfigureAwait(false);
            if (profileResult.ErrorKind == AuthErrorKind.AccountSuspended)
            {
                await LogoutAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort background check — a real failure already clears the session and
            // raises SessionChanged from inside GetValidAccessTokenAsync itself.
        }
    }

    public void Dispose() => _revalidationTimer?.Dispose();
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Talks to Supabase Auth (the GoTrue REST API) for identity — signup/login/logout/refresh/
/// password reset/email verification — and to Supabase's auto-generated PostgREST API for
/// the app-level "profiles" table (username, display name, license, etc.), which is separate
/// from the identity data Supabase's own auth.users table holds.
///
/// Requires a "profiles" table to exist in the Supabase project — see
/// Authentication/supabase-schema.sql for the exact setup script.
/// </summary>
public sealed class SupabaseAuthService : IAuthenticationService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseAuthService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<AuthResult> RegisterAsync(string username, string email, string password, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        // Usernames aren't natively part of Supabase Auth (which is email-based), so
        // uniqueness has to be checked against our own profiles table before signup —
        // otherwise two people could "claim" the same username on their profile row later.
        var usernameTaken = await UsernameExistsAsync(username, cancellationToken).ConfigureAwait(false);
        if (usernameTaken)
        {
            return AuthResult.Fail("That username is already taken.", AuthErrorKind.UsernameAlreadyExists);
        }

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/signup", new
            {
                email,
                password,
                data = new { username }
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapSignupError(response.StatusCode, body);
            }

            var signup = JsonSerializer.Deserialize<SupabaseUser>(body, JsonOptions);
            if (signup?.Id is null)
            {
                return AuthResult.Fail("Unexpected response from the server.", AuthErrorKind.Unknown);
            }

            // The profile row (username, display name, license, etc.) is created server-side
            // by a database trigger on auth.users insert — see supabase-schema.sql. That runs
            // with elevated privileges regardless of Row Level Security, which matters here
            // specifically: at this point there's no authenticated session yet (email
            // confirmation is required before login), so a client-side insert using only the
            // anon key couldn't satisfy an "owner can insert their own row" RLS policy anyway.
            return AuthResult.Ok();
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult.Fail("The server took too long to respond.", AuthErrorKind.ServerUnavailable);
        }
    }

    public async Task<AuthResult<AuthSession>> LoginAsync(string emailOrUsername, string password, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<AuthSession>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            var email = emailOrUsername.Contains('@')
                ? emailOrUsername
                : await ResolveEmailForUsernameAsync(emailOrUsername, cancellationToken).ConfigureAwait(false);

            if (email is null)
            {
                return AuthResult<AuthSession>.Fail("Incorrect email/username or password.", AuthErrorKind.InvalidCredentials);
            }

            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/token?grant_type=password", new { email, password });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapTokenError<AuthSession>(body);
            }

            var token = JsonSerializer.Deserialize<SupabaseTokenResponse>(body, JsonOptions);
            if (token?.AccessToken is null || token.RefreshToken is null || token.User?.Id is null)
            {
                return AuthResult<AuthSession>.Fail("Unexpected response from the server.", AuthErrorKind.Unknown);
            }

            var session = new AuthSession
            {
                UserId = token.User.Id,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600)
            };

            return AuthResult<AuthSession>.Ok(session);
        }
        catch (HttpRequestException)
        {
            return AuthResult<AuthSession>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult<AuthSession>.Fail("The server took too long to respond.", AuthErrorKind.ServerUnavailable);
        }
    }

    public async Task<AuthResult<AuthSession>> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<AuthSession>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", new { refresh_token = refreshToken });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<AuthSession>.Fail("Your session has expired — please log in again.", AuthErrorKind.TokenExpired);
            }

            var token = JsonSerializer.Deserialize<SupabaseTokenResponse>(body, JsonOptions);
            if (token?.AccessToken is null || token.RefreshToken is null || token.User?.Id is null)
            {
                return AuthResult<AuthSession>.Fail("Unexpected response from the server.", AuthErrorKind.Unknown);
            }

            return AuthResult<AuthSession>.Ok(new AuthSession
            {
                UserId = token.User.Id,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600)
            });
        }
        catch (HttpRequestException)
        {
            return AuthResult<AuthSession>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult<AuthSession>.Fail("The server took too long to respond.", AuthErrorKind.ServerUnavailable);
        }
    }

    public async Task LogoutAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return;

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/logout", body: null);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Best-effort: the server-side session is revoked if this succeeds, but either way
            // the caller (SessionService) clears the local session immediately afterward.
        }
        catch
        {
            // Logging out locally must always succeed even if the network call doesn't —
            // the caller clears local state regardless of what happens here.
        }
    }

    public async Task<AuthResult> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/recover", new { email });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Supabase returns success here regardless of whether the email exists, by design
            // (so this endpoint can't be used to check which emails have accounts) — so we
            // report success on any 2xx and treat this as "check your email" either way.
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't request a password reset right now.", AuthErrorKind.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> ResendVerificationEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/resend", new { type = "signup", email });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't resend the verification email right now.", AuthErrorKind.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult<UserProfile>> GetProfileAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<UserProfile>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_config.ProjectUrl}/rest/v1/profiles?id=eq.{Uri.EscapeDataString(session.UserId)}&select=*");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<UserProfile>.Fail("Couldn't load your profile.", AuthErrorKind.ServerUnavailable);
            }

            var rows = JsonSerializer.Deserialize<List<ProfileRow>>(body, JsonOptions);
            var row = rows?.FirstOrDefault();
            if (row is null)
            {
                return AuthResult<UserProfile>.Fail("Profile not found.", AuthErrorKind.Unknown);
            }

            // Single choke point for account suspension: every caller of GetProfileAsync
            // (fresh login, startup auto-login, and periodic mid-session revalidation) already
            // branches on Success, so refusing here blocks all three without special-casing
            // suspension anywhere else.
            if (row.IsSuspended)
            {
                return AuthResult<UserProfile>.Fail("This account has been suspended. Contact support for details.", AuthErrorKind.AccountSuspended);
            }

            return AuthResult<UserProfile>.Ok(new UserProfile
            {
                UserId = row.Id,
                Username = row.Username ?? string.Empty,
                DisplayName = row.DisplayName,
                Email = row.Email ?? string.Empty,
                AccountCreatedAt = row.CreatedAt ?? DateTime.UtcNow,
                IsBetaTester = row.IsBetaTester,
                License = row.License.ParseOrFree(),
                CloudEnabled = row.CloudEnabled,
                Country = row.Country,
                Language = row.Language,
                EmailVerified = true // GetProfileAsync only ever runs with a valid session, which requires verification.
            });
        }
        catch (HttpRequestException)
        {
            return AuthResult<UserProfile>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public Task<AuthResult<AuthSession>> VerifyEmailAsync(string email, string token, CancellationToken cancellationToken = default)
        => VerifyTokenAsync(email, token, "signup", cancellationToken);

    public async Task<AuthResult<AuthSession>> ConfirmPasswordResetAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        var verifyResult = await VerifyTokenAsync(email, token, "recovery", cancellationToken).ConfigureAwait(false);
        if (!verifyResult.Success || verifyResult.Value is null) return verifyResult;

        // The recovery token exchange above already produced a live session — use it to set
        // the new password in the same flow rather than asking the user to log in again first.
        var passwordResult = await ChangePasswordAsync(verifyResult.Value, newPassword, cancellationToken).ConfigureAwait(false);
        return passwordResult.Success
            ? verifyResult
            : AuthResult<AuthSession>.Fail(passwordResult.ErrorMessage ?? "Couldn't set the new password.", passwordResult.ErrorKind);
    }

    public async Task<AuthResult> ChangePasswordAsync(AuthSession session, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = BuildRequest(HttpMethod.Put, "/auth/v1/user", new { password = newPassword });
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return AuthResult.Ok();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapSignupError(response.StatusCode, body); // shares the same "password too weak" detection
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> UpdateProfileAsync(AuthSession session, ProfileUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        var patch = new Dictionary<string, object?>();
        if (request.DisplayName is not null) patch["display_name"] = request.DisplayName;
        if (request.Country is not null) patch["country"] = request.Country;
        if (request.Language is not null) patch["language"] = request.Language;
        if (request.CloudEnabled.HasValue) patch["cloud_enabled"] = request.CloudEnabled.Value;
        if (patch.Count == 0) return AuthResult.Ok();

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Patch,
                $"{_config.ProjectUrl}/rest/v1/profiles?id=eq.{Uri.EscapeDataString(session.UserId)}");
            httpRequest.Headers.Add("apikey", _config.AnonKey);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            httpRequest.Content = JsonContent.Create(patch);

            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't update your profile right now.", AuthErrorKind.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> RequestAccountDeletionAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/request_self_deletion");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't request account deletion right now.", AuthErrorKind.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    /// <summary>Shared by email verification and password reset — both are, at the protocol
    /// level, "exchange a numeric code Supabase emailed for a live session" with only the
    /// <c>type</c> differing.</summary>
    private async Task<AuthResult<AuthSession>> VerifyTokenAsync(string email, string token, string type, CancellationToken cancellationToken)
    {
        if (!_config.IsConfigured) return AuthResult<AuthSession>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = BuildRequest(HttpMethod.Post, "/auth/v1/verify", new { type, email, token });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var kind = body.ToLowerInvariant().Contains("expired") ? AuthErrorKind.TokenExpired : AuthErrorKind.Unknown;
                return AuthResult<AuthSession>.Fail("That code is invalid or has expired. Request a new one and try again.", kind);
            }

            var tokenResponse = JsonSerializer.Deserialize<SupabaseTokenResponse>(body, JsonOptions);
            if (tokenResponse?.AccessToken is null || tokenResponse.RefreshToken is null || tokenResponse.User?.Id is null)
            {
                return AuthResult<AuthSession>.Fail("Unexpected response from the server.", AuthErrorKind.Unknown);
            }

            return AuthResult<AuthSession>.Ok(new AuthSession
            {
                UserId = tokenResponse.User.Id,
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600)
            });
        }
        catch (HttpRequestException)
        {
            return AuthResult<AuthSession>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthResult<AuthSession>.Fail("The server took too long to respond.", AuthErrorKind.ServerUnavailable);
        }
    }

    private async Task<string?> ResolveEmailForUsernameAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/get_email_for_username");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Content = JsonContent.Create(new { lookup_username = username });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // The function returns a bare JSON string (or null) via PostgREST's RPC endpoint.
            return JsonSerializer.Deserialize<string?>(body, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/username_exists");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Content = JsonContent.Create(new { lookup_username = username });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<bool>(body, JsonOptions);
        }
        catch
        {
            return false;
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, $"{_config.ProjectUrl}{path}");
        request.Headers.Add("apikey", _config.AnonKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static AuthResult MapSignupError(HttpStatusCode statusCode, string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("already registered") || lower.Contains("already exists"))
        {
            return AuthResult.Fail("An account with that email already exists.", AuthErrorKind.EmailAlreadyExists);
        }

        // Supabase's own password policy (Authentication -> Settings -> Password, in the
        // dashboard) can require more than just length — a digit, mixed case, a symbol — and
        // that's configured per-project. Show its actual reason instead of guessing one, so
        // "your password was rejected" doesn't always look like the same generic length error.
        var serverMessage = ExtractServerMessage(body);

        if (lower.Contains("password"))
        {
            return AuthResult.Fail(serverMessage ?? "Password is too weak — use at least 8 characters with a mix of letters and numbers.", AuthErrorKind.WeakPassword);
        }

        return AuthResult.Fail(serverMessage ?? "Couldn't create the account. Please try again.", AuthErrorKind.Unknown);
    }

    /// <summary>Supabase (GoTrue) error bodies are JSON with the human-readable reason under one
    /// of a few different keys depending on the endpoint/version — try each in turn.</summary>
    private static string? ExtractServerMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var propertyName in new[] { "msg", "message", "error_description" })
            {
                if (doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, or an unexpected shape — fall back to the generic message.
        }

        return null;
    }

    private static AuthResult<T> MapTokenError<T>(string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("email not confirmed") || lower.Contains("email_not_confirmed"))
        {
            return AuthResult<T>.Fail("Please verify your email before logging in.", AuthErrorKind.EmailNotVerified);
        }

        return AuthResult<T>.Fail("Incorrect email/username or password.", AuthErrorKind.InvalidCredentials);
    }

    private sealed class SupabaseUser
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class SupabaseTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("user")] public SupabaseUser? User { get; set; }
    }

    private sealed class ProfileRow
    {
        public string Id { get; set; } = string.Empty;
        public string? Username { get; set; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        public string? Email { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("is_beta_tester")] public bool IsBetaTester { get; set; }
        public string? License { get; set; }
        [JsonPropertyName("cloud_enabled")] public bool CloudEnabled { get; set; }
        public string? Country { get; set; }
        public string? Language { get; set; }
        [JsonPropertyName("is_suspended")] public bool IsSuspended { get; set; }
    }
}

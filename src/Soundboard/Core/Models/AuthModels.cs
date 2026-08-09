namespace Soundboard.Core.Models;

/// <summary>
/// A live authentication session — the access token used on every authenticated request,
/// and the refresh token used to silently obtain a new one once it expires.
/// </summary>
public sealed class AuthSession
{
    public required string UserId { get; init; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public required DateTime ExpiresAtUtc { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    /// <summary>Treat a session as needing refresh a little before it actually expires, so an
    /// in-flight request doesn't get cut off right at the boundary.</summary>
    public bool NeedsRefresh => DateTime.UtcNow >= ExpiresAtUtc.AddSeconds(-30);
}

/// <summary>
/// Wraps the outcome of an auth operation without throwing for expected failure cases
/// (wrong password, email taken, no internet, etc.) — callers check Success and show
/// ErrorMessage rather than catching exceptions for ordinary "this didn't work" outcomes.
/// </summary>
public sealed class AuthResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public AuthErrorKind ErrorKind { get; init; } = AuthErrorKind.None;

    public static AuthResult Ok() => new() { Success = true };
    public static AuthResult Fail(string message, AuthErrorKind kind = AuthErrorKind.Unknown)
        => new() { Success = false, ErrorMessage = message, ErrorKind = kind };
}

public sealed class AuthResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }
    public AuthErrorKind ErrorKind { get; init; } = AuthErrorKind.None;

    public static AuthResult<T> Ok(T value) => new() { Success = true, Value = value };
    public static AuthResult<T> Fail(string message, AuthErrorKind kind = AuthErrorKind.Unknown)
        => new() { Success = false, ErrorMessage = message, ErrorKind = kind };
}

/// <summary>
/// Fields a user can edit on their own profile. Null means "leave unchanged" so callers only
/// need to send the fields that actually changed.
/// </summary>
public sealed class ProfileUpdateRequest
{
    public string? DisplayName { get; init; }
    public string? Country { get; init; }
    public string? Language { get; init; }
    public bool? CloudEnabled { get; init; }
}

/// <summary>
/// Broad categories the UI can react to differently (e.g. show a "no internet" banner vs.
/// an inline field error) without parsing error message text.
/// </summary>
public enum AuthErrorKind
{
    None = 0,
    Unknown = 1,
    NoInternet = 2,
    ServerUnavailable = 3,
    InvalidCredentials = 4,
    EmailNotVerified = 5,
    EmailAlreadyExists = 6,
    UsernameAlreadyExists = 7,
    WeakPassword = 8,
    TokenExpired = 9,
    NotAuthenticated = 10,
    AccountSuspended = 11
}

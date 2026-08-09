namespace Soundboard.Core.Models;

/// <summary>
/// One row of the admin panel's user list — everything an Administrator account can see and
/// edit about another user, via the admin_list_users()/admin_update_user() Postgres RPCs.
/// Deliberately excludes anything not needed for that (no email, username, or password are
/// ever changed here — only license/beta/suspended status).
/// </summary>
public sealed class AdminUserSummary
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Email { get; init; }
    public LicenseType License { get; set; } = LicenseType.Free;
    public bool IsBetaTester { get; set; }
    public bool IsSuspended { get; set; }
    public bool EmailVerified { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

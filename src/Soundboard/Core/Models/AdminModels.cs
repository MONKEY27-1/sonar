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

/// <summary>One row of the admin panel's Reports list — a user-submitted flag on a Community
/// Plugin or Community Pack, via admin_list_reports(). ContentId is preserved so an admin
/// reviewing a report can cross-reference it against the Community Plugins/Packs tab, but the
/// name/reporter are denormalized snapshots (see supabase-schema.sql section 14) so the report
/// still reads sensibly even if that content or the reporter's profile changes later.</summary>
public sealed class ContentReportSummary
{
    public required string Id { get; init; }
    public required string ContentType { get; init; }
    public required string ContentId { get; init; }
    public required string ContentName { get; init; }
    public string? ReporterUsername { get; init; }
    public required string Reason { get; init; }
    public required string Status { get; init; }
    public DateTime CreatedAt { get; init; }
}

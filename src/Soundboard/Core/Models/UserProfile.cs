namespace Soundboard.Core.Models;

public enum LicenseType
{
    Free = 0,
    BetaTester = 1,
    Pro = 2,
    Developer = 3,
    Administrator = 4
}

public enum SubscriptionStatus
{
    None = 0,
    Active = 1,
    Expired = 2,
    Cancelled = 3
}

/// <summary>
/// A signed-in user's profile, populated from Supabase Auth (identity fields) and the
/// app's own "profiles" table (everything else — display name, license, preferences).
/// </summary>
public sealed class UserProfile
{
    public required string UserId { get; init; }
    public required string Username { get; set; }
    public string? DisplayName { get; set; }
    public required string Email { get; init; }
    public string? ProfilePicturePath { get; set; }
    public DateTime AccountCreatedAt { get; init; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsBetaTester { get; set; }
    public LicenseType License { get; set; } = LicenseType.Free;
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.None;
    public bool CloudEnabled { get; set; }
    public string? Country { get; set; }
    public string? Language { get; set; }
    public bool EmailVerified { get; set; }
}

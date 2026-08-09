using CommunityToolkit.Mvvm.ComponentModel;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One editable row in the Admin Panel's user grid — wraps an <see cref="AdminUserSummary"/>
/// with the mutable fields an admin can change (License/IsBetaTester/IsSuspended) plus per-row
/// save state, so each row can be saved independently instead of one big bulk-save.</summary>
public partial class AdminUserRowViewModel : ObservableObject
{
    public AdminUserRowViewModel(AdminUserSummary summary)
    {
        UserId = summary.UserId;
        Username = summary.Username;
        Email = summary.Email;
        EmailVerified = summary.EmailVerified;
        CreatedAt = summary.CreatedAt;
        LastLoginAt = summary.LastLoginAt;
        _license = summary.License;
        _isBetaTester = summary.IsBetaTester;
        _isSuspended = summary.IsSuspended;
    }

    public string UserId { get; }
    public string Username { get; }
    public string Email { get; }
    public bool EmailVerified { get; }
    public DateTime CreatedAt { get; }
    public DateTime? LastLoginAt { get; }

    [ObservableProperty] private LicenseType _license;
    [ObservableProperty] private bool _isBetaTester;
    [ObservableProperty] private bool _isSuspended;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
}

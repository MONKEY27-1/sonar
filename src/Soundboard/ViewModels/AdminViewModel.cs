using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Helpers;

namespace Soundboard.ViewModels;

/// <summary>
/// Backs the Admin Panel window — only ever opened by an Administrator account (gated in
/// AccountViewModel/AccountWindow), but every action here is also re-checked server-side by the
/// admin_list_users()/admin_update_user() RPCs, so the UI gate is a convenience, not the actual
/// security boundary.
/// </summary>
public partial class AdminViewModel : ObservableObject
{
    private readonly IAdminService _adminService;
    private readonly ISessionService _sessionService;
    private List<AdminUserSummary> _allUsers = [];

    public AdminViewModel(IAdminService adminService, ISessionService sessionService)
    {
        _adminService = adminService;
        _sessionService = sessionService;
    }

    public ObservableCollection<AdminUserRowViewModel> Users { get; } = [];

    public Array LicenseTypes => EnumBindingSource.GetValues<LicenseType>();

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _adminService.ListUsersAsync(session).ConfigureAwait(true);
            if (!result.Success || result.Value is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't load users.";
                return;
            }

            _allUsers = result.Value.ToList();
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveUserAsync(AdminUserRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.UpdateUserAsync(
                session, row.UserId, row.License, row.IsBetaTester, row.IsSuspended).ConfigureAwait(true);

            row.StatusMessage = result.Success ? "Saved." : result.ErrorMessage ?? "Couldn't save changes.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Users.Clear();

        var query = string.IsNullOrWhiteSpace(SearchQuery)
            ? _allUsers
            : _allUsers.Where(u =>
                u.Username.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var user in query)
        {
            Users.Add(new AdminUserRowViewModel(user));
        }
    }
}

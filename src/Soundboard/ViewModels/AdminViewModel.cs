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
    private readonly IPluginTrustService _pluginTrustService;
    private readonly ICommunityPluginService _communityPluginService;
    private readonly ICommunityPackService _communityPackService;
    private readonly IAdminMessageService _adminMessageService;
    private List<AdminUserSummary> _allUsers = [];

    public AdminViewModel(
        IAdminService adminService,
        ISessionService sessionService,
        IPluginTrustService pluginTrustService,
        ICommunityPluginService communityPluginService,
        ICommunityPackService communityPackService,
        IAdminMessageService adminMessageService)
    {
        _adminService = adminService;
        _sessionService = sessionService;
        _pluginTrustService = pluginTrustService;
        _communityPluginService = communityPluginService;
        _communityPackService = communityPackService;
        _adminMessageService = adminMessageService;
    }

    public ObservableCollection<AdminUserRowViewModel> Users { get; } = [];
    public ObservableCollection<PluginTrustRowViewModel> Plugins { get; } = [];
    public ObservableCollection<AdminCommunityPluginRowViewModel> CommunityPlugins { get; } = [];
    public ObservableCollection<AdminCommunityPackRowViewModel> CommunityPacks { get; } = [];
    public ObservableCollection<AdminContentReportRowViewModel> Reports { get; } = [];
    public ObservableCollection<AdminSupportTicketRowViewModel> SupportTickets { get; } = [];
    public ObservableCollection<SupportMessageViewModel> SupportMessages { get; } = [];

    public Array LicenseTypes => EnumBindingSource.GetValues<LicenseType>();
    public string[] SupportStatusOptions { get; } = ["open", "in_progress", "resolved"];

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty] private AdminSupportTicketRowViewModel? _selectedSupportTicket;
    [ObservableProperty] private string _supportReplyText = string.Empty;
    [ObservableProperty] private string _adminReplyStatus = "open";
    [ObservableProperty] private bool _isLoadingSupportMessages;
    [ObservableProperty] private bool _isSendingSupportReply;
    [ObservableProperty] private string _supportReplyStatusMessage = string.Empty;

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

    [RelayCommand]
    private async Task LoadPluginsAsync()
    {
        var verifiedIds = await _pluginTrustService.GetVerifiedPluginIdsAsync().ConfigureAwait(true);

        Plugins.Clear();
        foreach (var definition in PluginCatalog.All)
        {
            Plugins.Add(new PluginTrustRowViewModel(definition, verifiedIds.Contains(definition.Id)));
        }
    }

    [RelayCommand]
    private async Task SavePluginAsync(PluginTrustRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.SetPluginVerifiedAsync(session, row.Id, row.IsVerified).ConfigureAwait(true);
            row.StatusMessage = result.Success ? "Saved." : result.ErrorMessage ?? "Couldn't save changes.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadCommunityPluginsAsync()
    {
        // No verified-only filter here — admins need to see everything, including unverified
        // submissions awaiting review.
        var plugins = await _communityPluginService.SearchAsync(null, verifiedOnly: false).ConfigureAwait(true);

        CommunityPlugins.Clear();
        foreach (var plugin in plugins)
        {
            CommunityPlugins.Add(new AdminCommunityPluginRowViewModel(plugin));
        }
    }

    [RelayCommand]
    private async Task SaveCommunityPluginAsync(AdminCommunityPluginRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.SetCommunityPluginVerifiedAsync(session, row.Id, row.IsVerified).ConfigureAwait(true);
            row.StatusMessage = result.Success ? "Saved." : result.ErrorMessage ?? "Couldn't save changes.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteCommunityPluginAsync(AdminCommunityPluginRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var confirmed = System.Windows.MessageBox.Show(
            $"Permanently delete \"{row.Name}\" by {row.AuthorUsername}?",
            "Delete plugin",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmed != System.Windows.MessageBoxResult.Yes) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.DeleteCommunityPluginAsync(session, row.Id).ConfigureAwait(true);
            if (result.Success)
            {
                CommunityPlugins.Remove(row);
            }
            else
            {
                row.StatusMessage = result.ErrorMessage ?? "Couldn't delete.";
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadCommunityPacksAsync()
    {
        var packs = await _communityPackService.SearchAsync(null, verifiedOnly: false).ConfigureAwait(true);

        CommunityPacks.Clear();
        foreach (var pack in packs)
        {
            CommunityPacks.Add(new AdminCommunityPackRowViewModel(pack));
        }
    }

    [RelayCommand]
    private async Task SaveCommunityPackAsync(AdminCommunityPackRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.SetCommunityPackVerifiedAsync(session, row.Id, row.IsVerified).ConfigureAwait(true);
            row.StatusMessage = result.Success ? "Saved." : result.ErrorMessage ?? "Couldn't save changes.";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteCommunityPackAsync(AdminCommunityPackRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var confirmed = System.Windows.MessageBox.Show(
            $"Permanently delete \"{row.Name}\" by {row.AuthorUsername}?",
            "Delete plugin",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmed != System.Windows.MessageBoxResult.Yes) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.DeleteCommunityPackAsync(session, row.Id).ConfigureAwait(true);
            if (result.Success)
            {
                CommunityPacks.Remove(row);
            }
            else
            {
                row.StatusMessage = result.ErrorMessage ?? "Couldn't delete.";
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [ObservableProperty] private string _adminMessage = string.Empty;
    [ObservableProperty] private string _adminMessageStatus = string.Empty;
    [ObservableProperty] private bool _isSavingAdminMessage;

    [RelayCommand]
    private async Task LoadAdminMessageAsync()
    {
        AdminMessage = await _adminMessageService.GetMessageAsync().ConfigureAwait(true) ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveAdminMessageAsync()
    {
        if (IsSavingAdminMessage) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsSavingAdminMessage = true;
        AdminMessageStatus = string.Empty;
        try
        {
            var result = await _adminService.SetAdminMessageAsync(session, AdminMessage).ConfigureAwait(true);
            AdminMessageStatus = result.Success ? "Saved — every user will see this." : result.ErrorMessage ?? "Couldn't save.";
        }
        finally
        {
            IsSavingAdminMessage = false;
        }
    }

    [RelayCommand]
    private async Task LoadReportsAsync()
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var result = await _adminService.ListReportsAsync(session).ConfigureAwait(true);
        if (!result.Success || result.Value is null) return;

        Reports.Clear();
        foreach (var report in result.Value.Where(r => r.Status == "open"))
        {
            Reports.Add(new AdminContentReportRowViewModel(report));
        }
    }

    [RelayCommand]
    private async Task DismissReportAsync(AdminContentReportRowViewModel? row) =>
        await ApplyReportStatusAsync(row, "dismissed").ConfigureAwait(true);

    [RelayCommand]
    private async Task ResolveReportAsync(AdminContentReportRowViewModel? row) =>
        await ApplyReportStatusAsync(row, "resolved").ConfigureAwait(true);

    private async Task ApplyReportStatusAsync(AdminContentReportRowViewModel? row, string newStatus)
    {
        if (row is null || row.IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        row.IsBusy = true;
        row.StatusMessage = string.Empty;
        try
        {
            var result = await _adminService.SetReportStatusAsync(session, row.Id, newStatus).ConfigureAwait(true);
            if (result.Success)
            {
                Reports.Remove(row);
            }
            else
            {
                row.StatusMessage = result.ErrorMessage ?? "Couldn't save changes.";
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadSupportTicketsAsync()
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var result = await _adminService.ListSupportTicketsAsync(session).ConfigureAwait(true);
        if (!result.Success || result.Value is null) return;

        SupportTickets.Clear();
        foreach (var ticket in result.Value)
        {
            SupportTickets.Add(new AdminSupportTicketRowViewModel(ticket));
        }
    }

    [RelayCommand]
    private async Task SelectSupportTicketAsync(AdminSupportTicketRowViewModel? row)
    {
        SelectedSupportTicket = row;
        SupportReplyText = string.Empty;
        SupportReplyStatusMessage = string.Empty;
        AdminReplyStatus = row?.Status ?? "open";

        foreach (var ticket in SupportTickets)
        {
            ticket.IsSelected = ReferenceEquals(ticket, row);
        }

        SupportMessages.Clear();
        if (row is null) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsLoadingSupportMessages = true;
        try
        {
            var result = await _adminService.ListTicketMessagesAsync(session, row.Id).ConfigureAwait(true);
            if (!result.Success || result.Value is null) return;

            foreach (var message in result.Value)
            {
                SupportMessages.Add(new SupportMessageViewModel(message));
            }
        }
        finally
        {
            IsLoadingSupportMessages = false;
        }
    }

    [RelayCommand]
    private async Task SendSupportReplyAsync()
    {
        if (IsSendingSupportReply || SelectedSupportTicket is null) return;
        if (string.IsNullOrWhiteSpace(SupportReplyText)) return;

        var session = _sessionService.CurrentSession;
        if (session is null) return;

        IsSendingSupportReply = true;
        SupportReplyStatusMessage = string.Empty;
        try
        {
            var result = await _adminService.SendAdminTicketMessageAsync(
                session, SelectedSupportTicket.Id, SupportReplyText, AdminReplyStatus).ConfigureAwait(true);

            if (!result.Success)
            {
                SupportReplyStatusMessage = result.ErrorMessage ?? "Couldn't send.";
                return;
            }

            SelectedSupportTicket.Status = AdminReplyStatus;
            SupportReplyText = string.Empty;
            await SelectSupportTicketAsync(SelectedSupportTicket).ConfigureAwait(true);
        }
        finally
        {
            IsSendingSupportReply = false;
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

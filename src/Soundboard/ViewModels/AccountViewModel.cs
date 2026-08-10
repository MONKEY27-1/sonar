using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Soundboard.Authentication;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Views;

namespace Soundboard.ViewModels;

/// <summary>
/// Backs the "click your avatar" dashboard/profile window — combines what the spec calls
/// "Profile" and "Dashboard" into one window, the way Discord/Steam actually present this.
/// Only ever shown while logged in (<see cref="ISessionService.IsLoggedIn"/>).
/// </summary>
public partial class AccountViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;
    private readonly ILicenseService _licenseService;
    private readonly IAuthenticationService _authService;
    private readonly ICloudService _cloudService;
    private readonly ISettingsService _settingsService;
    private readonly IAppPaths _paths;
    private readonly LocalAvatarStore _avatarStore;
    private readonly IServiceProvider _services;

    public AccountViewModel(
        ISessionService sessionService,
        ILicenseService licenseService,
        IAuthenticationService authService,
        ICloudService cloudService,
        ISettingsService settingsService,
        IAppPaths paths,
        LocalAvatarStore avatarStore,
        IServiceProvider services)
    {
        _sessionService = sessionService;
        _licenseService = licenseService;
        _authService = authService;
        _cloudService = cloudService;
        _settingsService = settingsService;
        _paths = paths;
        _avatarStore = avatarStore;
        _services = services;
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private bool _emailVerified;
    [ObservableProperty] private DateTime _accountCreatedAt;
    [ObservableProperty] private DateTime? _lastLoginAt;
    [ObservableProperty] private string? _avatarPath;
    [ObservableProperty] private bool _isBetaTester;
    [ObservableProperty] private LicenseType _license;
    [ObservableProperty] private bool _isAdministrator;

    [ObservableProperty] private string _editableDisplayName = string.Empty;
    [ObservableProperty] private string _editableCountry = string.Empty;
    [ObservableProperty] private string _editableLanguage = string.Empty;

    public string VersionText => $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown"}";
    [ObservableProperty] private string _storageUsedText = "Calculating...";
    [ObservableProperty] private string _cloudStatusText = "Not available yet";
    [ObservableProperty] private string _lastSyncText = "Never";
    [ObservableProperty] private bool _isCloudSyncEnabled;
    [ObservableProperty] private bool _isSyncing;

    [RelayCommand]
    private async Task LoadAsync()
    {
        var profile = _sessionService.CurrentProfile;
        if (profile is null) return;

        Username = profile.Username;
        Email = profile.Email;
        EmailVerified = profile.EmailVerified;
        AccountCreatedAt = profile.AccountCreatedAt;
        LastLoginAt = profile.LastLoginAt;
        IsBetaTester = _licenseService.IsBetaTester;
        License = _licenseService.CurrentLicense;
        IsAdministrator = _licenseService.CurrentLicense == LicenseType.Administrator;
        EditableDisplayName = profile.DisplayName ?? string.Empty;
        EditableCountry = profile.Country ?? string.Empty;
        EditableLanguage = profile.Language ?? string.Empty;
        AvatarPath = _avatarStore.GetAvatarPath(profile.UserId);

        IsCloudSyncEnabled = profile.CloudEnabled;
        CloudStatusText = !_cloudService.IsAvailable ? "Not available yet" : profile.CloudEnabled ? "Enabled" : "Disabled";

        var lastSync = await _cloudService.GetLastSyncTimeAsync().ConfigureAwait(true);
        LastSyncText = lastSync?.ToLocalTime().ToString("g") ?? "Never";

        StorageUsedText = await Task.Run(ComputeStorageUsedText).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing) return;

        if (!IsCloudSyncEnabled)
        {
            InfoMessage = "Enable Cloud Sync in Settings → Account first.";
            return;
        }

        IsSyncing = true;
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        try
        {
            await _cloudService.SyncSettingsAsync().ConfigureAwait(true);
            await _cloudService.SyncSoundLibraryAsync().ConfigureAwait(true);

            var lastSync = await _cloudService.GetLastSyncTimeAsync().ConfigureAwait(true);
            LastSyncText = lastSync?.ToLocalTime().ToString("g") ?? "Never";
            InfoMessage = "Synced.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private void PickAvatar()
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose a profile picture",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
        };

        if (dialog.ShowDialog() != true) return;

        var savedPath = _avatarStore.SetAvatar(session.UserId, dialog.FileName);
        if (savedPath is null)
        {
            ErrorMessage = "Couldn't set that picture as your avatar.";
            return;
        }

        AvatarPath = savedPath;
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (IsBusy) return;

        var session = _sessionService.CurrentSession;
        var profile = _sessionService.CurrentProfile;
        if (session is null || profile is null) return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        try
        {
            var request = new ProfileUpdateRequest
            {
                DisplayName = EditableDisplayName,
                Country = EditableCountry,
                Language = EditableLanguage
            };

            var result = await _authService.UpdateProfileAsync(session, request).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = result.ErrorMessage ?? "Couldn't save your profile.";
                return;
            }

            // Refresh the cached profile (and any other UI bound to it, e.g. the toolbar
            // account button) with the values that actually landed server-side.
            var refreshed = await _authService.GetProfileAsync(session).ConfigureAwait(true);
            if (refreshed.Success && refreshed.Value is not null)
            {
                await _sessionService.SetSessionAsync(session, refreshed.Value, _settingsService.Settings.Account.RememberMe).ConfigureAwait(true);
            }

            InfoMessage = "Profile saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenAdminPanel()
    {
        var window = _services.GetRequiredService<AdminWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenSupport()
    {
        var window = _services.GetRequiredService<SupportWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    private string ComputeStorageUsedText()
    {
        try
        {
            if (!Directory.Exists(_paths.SoundsDirectory)) return "0 MB";

            var totalBytes = new DirectoryInfo(_paths.SoundsDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            var megabytes = totalBytes / 1024.0 / 1024.0;
            return megabytes >= 1024 ? $"{megabytes / 1024:0.0} GB" : $"{megabytes:0.0} MB";
        }
        catch
        {
            return "Unknown";
        }
    }
}

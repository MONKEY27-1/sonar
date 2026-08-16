using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Helpers;
using Soundboard.Views;

namespace Soundboard.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILibraryService _libraryService;
    private readonly IHotkeyManager _hotkeyManager;
    private readonly IThemeService _themeService;
    private readonly IAudioEngine _audioEngine;
    private readonly ICollectionExportService _collectionExport;
    private readonly IAppPaths _paths;
    private readonly ISessionService _sessionService;
    private readonly ILicenseService _licenseService;
    private readonly IAuthenticationService _authService;
    private readonly IUpdateService _updateService;
    private readonly IServiceProvider _services;

    public SettingsViewModel(
        ISettingsService settingsService,
        ILibraryService libraryService,
        IHotkeyManager hotkeyManager,
        IThemeService themeService,
        IAudioEngine audioEngine,
        ICollectionExportService collectionExport,
        IAppPaths paths,
        ISessionService sessionService,
        ILicenseService licenseService,
        IAuthenticationService authService,
        IUpdateService updateService,
        IServiceProvider services)
    {
        _services = services;
        _settingsService = settingsService;
        _libraryService = libraryService;
        _hotkeyManager = hotkeyManager;
        _themeService = themeService;
        _audioEngine = audioEngine;
        _collectionExport = collectionExport;
        _paths = paths;
        _sessionService = sessionService;
        _licenseService = licenseService;
        _authService = authService;
        _updateService = updateService;
        Settings = _settingsService.Settings;

        _sessionService.SessionChanged += (_, _) =>
        {
            RaiseAccountSummaryChanged();
            EnforceThemeLicenseLimit();
            EnforceProPluginLicenseLimits();
            _ = EnforceCloudSyncLicenseLimitAsync();
        };
        RaiseAccountSummaryChanged();
        EnforceThemeLicenseLimit();
        EnforceProPluginLicenseLimits();
        _ = EnforceCloudSyncLicenseLimitAsync();
    }

    public AppSettings Settings { get; }

    // Not persisted — always reopens on Audio, same as every other "which tab was open" state
    // in this app (Home is always the landing page too).
    [ObservableProperty] private SettingsCategory _selectedCategory = SettingsCategory.Audio;

    [RelayCommand]
    private void SelectCategory(SettingsCategory category) => SelectedCategory = category;

    /// <summary>Beta access has no self-service enrollment (see AdminViewModel — it's admin-panel
    /// only), so the License tab's Beta Tester card routes here instead of a "Join" action.</summary>
    [RelayCommand]
    private void OpenSupport()
    {
        var window = _services.GetRequiredService<SupportWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    // AMOLED/Custom are Pro-only — Free tier only ever sees Dark/Light as choices.
    public Array ThemeKinds => _licenseService.CanUseCustomTheme
        ? Enum.GetValues(typeof(ThemeKind))
        : new object[] { ThemeKind.Dark, ThemeKind.Light };

    /// <summary>If a license downgrade (or hand-edited settings.json) leaves the theme stuck on
    /// a Pro-only kind, fall back to Dark rather than silently rendering with locked-out
    /// settings the user can no longer even select in the combo box.</summary>
    private void EnforceThemeLicenseLimit()
    {
        if (_licenseService.CanUseCustomTheme) return;
        if (Settings.Theme.Kind is not (ThemeKind.Amoled or ThemeKind.Custom)) return;

        Settings.Theme.Kind = ThemeKind.Dark;
        _themeService.ApplyTheme(Settings);
        _ = _settingsService.SaveAsync();
    }

    public bool IsAdvancedSettingsInstalled => Settings.Plugins.InstalledPluginIds.Contains(PluginCatalog.AdvancedSettings);
    public bool IsPerformanceModeInstalled => Settings.Plugins.InstalledPluginIds.Contains(PluginCatalog.PerformanceMode);

    /// <summary>Same downgrade-guard shape as <see cref="EnforceThemeLicenseLimit"/> — if a
    /// license downgrade leaves any Pro-only plugin installed for a now-Free account, uninstall
    /// it rather than leaving it active for an account that can no longer buy it. Driven off
    /// PluginCatalog's RequiresPro flag, so a plugin newly marked Pro-only gets enforced here
    /// automatically without a matching code change.</summary>
    private void EnforceProPluginLicenseLimits()
    {
        if (_licenseService.IsProUnlocked) return;

        var removedAny = false;
        foreach (var plugin in PluginCatalog.All.Where(p => p.RequiresPro))
        {
            if (Settings.Plugins.InstalledPluginIds.Remove(plugin.Id))
            {
                removedAny = true;
            }
        }

        if (removedAny)
        {
            _ = _settingsService.SaveAsync();
        }
    }

    public Array OutputRoutes => Enum.GetValues(typeof(OutputRoute));
    public Array LatencyModes => Enum.GetValues(typeof(LatencyMode));
    public Array QueueModes => Enum.GetValues(typeof(QueueMode));

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = [];
    public ObservableCollection<DetectedVirtualDevice> DetectedVirtualDevices { get; } = [];

    [ObservableProperty] private string _diagnosticsStatus = string.Empty;

    public string HeadphoneDeviceSummary => OutputDevices.FirstOrDefault(d => d.Id == Settings.Audio.HeadphoneDeviceId)?.Name ?? "System default";
    public string VirtualMicDeviceSummary => OutputDevices.FirstOrDefault(d => d.Id == Settings.Audio.VirtualMicOutputDeviceId)?.Name ?? "Not configured";
    public string MicrophoneDeviceSummary => InputDevices.FirstOrDefault(d => d.Id == Settings.Audio.MicrophoneDeviceId)?.Name ?? "System default";

    private void RaiseDiagnosticsSummaryChanged()
    {
        OnPropertyChanged(nameof(HeadphoneDeviceSummary));
        OnPropertyChanged(nameof(VirtualMicDeviceSummary));
        OnPropertyChanged(nameof(MicrophoneDeviceSummary));
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        var outputs = await _audioEngine.GetOutputDevicesAsync().ConfigureAwait(true);
        var inputs = await _audioEngine.GetInputDevicesAsync().ConfigureAwait(true);
        var detected = await _audioEngine.DetectVirtualDevicesAsync().ConfigureAwait(true);

        SyncDevices(OutputDevices, outputs);
        SyncDevices(InputDevices, inputs);

        DetectedVirtualDevices.Clear();
        foreach (var device in detected)
        {
            DetectedVirtualDevices.Add(device);
        }

        RaiseDiagnosticsSummaryChanged();
    }

    [RelayCommand]
    private async Task UseDetectedDeviceAsync(DetectedVirtualDevice? device)
    {
        if (device?.PlaybackDeviceId is null) return;
        await _audioEngine.ChangeVirtualDeviceAsync(device.PlaybackDeviceId).ConfigureAwait(true);
        RaiseDiagnosticsSummaryChanged();
    }

    private static void SyncDevices(ObservableCollection<AudioDeviceInfo> target, IReadOnlyList<AudioDeviceInfo> latest)
    {
        // Never Clear() this collection while it's bound: clearing an ObservableCollection
        // resets a bound ComboBox's SelectedValue to null, which (via the TwoWay binding)
        // immediately wipes the saved device choice out from under the user. Update/replace
        // devices in place and only add/remove what actually changed instead.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var match = latest.FirstOrDefault(d => d.Id == target[i].Id);
            if (match is null)
            {
                target.RemoveAt(i);
            }
            else
            {
                target[i] = match;
            }
        }

        foreach (var device in latest)
        {
            if (target.All(d => d.Id != device.Id))
            {
                target.Add(device);
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
            _themeService.ApplyTheme(Settings);
            _hotkeyManager.RegisterGlobalHotkeys(Settings.GlobalHotkeys);
            RaiseDiagnosticsSummaryChanged();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Settings could not be saved:\n\n{ex.Message}",
                "Soundboard - Save Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task TestHeadphonesAsync() => await PlayTestToneAsync(OutputRoute.Headphones).ConfigureAwait(true);

    [RelayCommand]
    private async Task TestVirtualMicAsync() => await PlayTestToneAsync(OutputRoute.Microphone).ConfigureAwait(true);

    private async Task PlayTestToneAsync(OutputRoute route)
    {
        try
        {
            var demoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DemoSounds", "Ding.wav");
            if (!File.Exists(demoPath))
            {
                DiagnosticsStatus = "Test sound file not found — reinstall may be needed.";
                return;
            }

            var testSound = new SoundItem { Name = "Test Tone", FileName = "Ding.wav", Volume = 1.0f };
            await _audioEngine.PlayAsync(testSound, demoPath, route).ConfigureAwait(true);

            DiagnosticsStatus = route == OutputRoute.Headphones
                ? "Playing through headphones — you should hear a short chime."
                : "Playing through the virtual mic output — check Discord/your game for the chime, not your speakers.";
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Test failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => TryOpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenSettingsFolder() => TryOpenFolder(_paths.RootDirectory);

    private void TryOpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Couldn't open folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        var result = System.Windows.MessageBox.Show(
            "This resets every setting (theme, audio devices, hotkeys, playback preferences, notifications, etc.) back to its default value.\n\n" +
            "Your sound library, folders, and account stay untouched — only preferences reset. Audio devices and global hotkeys may need the app restarted to fully take effect.\n\nContinue?",
            "Reset to Defaults",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        await _settingsService.ReplaceSettingsAsync(new AppSettings()).ConfigureAwait(true);
        DiagnosticsStatus = "Settings reset to defaults.";
    }

    [RelayCommand]
    private async Task ExportCollectionAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Soundboard Collection|*.sbpack",
            FileName = "MySoundboard.sbpack"
        };

        if (dialog.ShowDialog() == true)
        {
            await _collectionExport.ExportCollectionAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ImportCollectionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Soundboard Collection|*.sbpack"
        };

        if (dialog.ShowDialog() == true)
        {
            await _collectionExport.ImportCollectionAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    // --- Account / Security / License ---

    public bool IsLoggedIn => _sessionService.IsLoggedIn;
    public string AccountEmail => _sessionService.CurrentProfile?.Email ?? string.Empty;
    public bool IsEmailVerified => _sessionService.CurrentProfile?.EmailVerified ?? false;
    public string SessionExpiryText => _sessionService.CurrentSession is { } session
        ? session.ExpiresAtUtc.ToLocalTime().ToString("g")
        : "Not logged in";
    public string LastLoginText => _sessionService.CurrentProfile?.LastLoginAt?.ToLocalTime().ToString("g") ?? "This session";

    public LicenseType CurrentLicense => _licenseService.CurrentLicense;
    public bool IsBetaTester => _licenseService.IsBetaTester;
    public bool IsProUnlocked => _licenseService.IsProUnlocked;
    public bool CanUseCloudSync => _licenseService.CanUseCloudSync;

    // Which of the three License tab tier cards to highlight as "your current plan" — Beta
    // Tester takes priority over Pro/Free since IsProUnlocked is also true for beta testers.
    public bool IsFreeTierCurrent => !IsProUnlocked;
    public bool IsProTierCurrent => IsProUnlocked && !IsBetaTester;

    // WPF has no embedded Stripe Checkout surface (and shouldn't — Stripe's own hosted page is
    // what actually takes the card details), so buying Pro just opens the website's pricing
    // section in the user's default browser, same "open a URL, nothing to recover if it fails"
    // pattern as FirstRunWizardViewModel.InstallVbCable. The desktop app never sees a token or
    // card number; the website's own already-secure login + Netlify Function handle the rest,
    // and the existing 5-minute profile poll picks up the resulting license change automatically.
    // Keep in sync with the identical constant in UpgradeToProDialog.cs.
    private const string PricingPageUrl = "https://sonars.netlify.app/index.html#tiers";

    [RelayCommand]
    private void UpgradeToPro()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PricingPageUrl) { UseShellExecute = true });
        }
        catch
        {
            // Nothing to recover — the user can navigate to the site manually.
        }
    }

    [ObservableProperty] private string _changePasswordCurrent = string.Empty;
    [ObservableProperty] private string _changePasswordNew = string.Empty;
    [ObservableProperty] private string _changePasswordConfirm = string.Empty;
    [ObservableProperty] private string _changePasswordMessage = string.Empty;
    [ObservableProperty] private bool _isChangingPassword;

    [ObservableProperty] private string _accountStatusMessage = string.Empty;
    [ObservableProperty] private bool _isAccountBusy;

    /// <summary>PasswordBox.Password can't be data-bound, so the three change-password fields
    /// are relayed in from code-behind rather than bound — which means clearing them here after
    /// a successful change only clears the view model's copy, not what's still shown on screen.
    /// The window subscribes to this to clear the actual PasswordBox controls too.</summary>
    public event EventHandler? ChangePasswordFieldsCleared;

    [ObservableProperty] private bool _cloudEnabled;
    private bool _isLoadingCloudEnabled;

    /// <summary>Pushes the toggle to the server as soon as it changes — guarded by
    /// _isLoadingCloudEnabled so re-reading the value FROM the profile (e.g. after login)
    /// doesn't immediately re-push the same value right back. Also the backstop against turning
    /// Cloud Sync ON for a Free account — the checkbox is disabled in the UI for Free users, but
    /// this guard is what actually prevents it regardless of how the property got set.</summary>
    partial void OnCloudEnabledChanged(bool value)
    {
        if (_isLoadingCloudEnabled) return;

        if (value && !_licenseService.CanUseCloudSync)
        {
            _isLoadingCloudEnabled = true;
            CloudEnabled = false;
            _isLoadingCloudEnabled = false;
            AccountStatusMessage = "Cloud Sync is a Pro feature.";
            return;
        }

        _ = UpdateCloudEnabledAsync(value);
    }

    private async Task UpdateCloudEnabledAsync(bool value)
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        var result = await _authService.UpdateProfileAsync(session, new ProfileUpdateRequest { CloudEnabled = value }).ConfigureAwait(true);
        if (!result.Success)
        {
            AccountStatusMessage = result.ErrorMessage ?? "Couldn't update cloud sync setting.";
            return;
        }

        var refreshed = await _authService.GetProfileAsync(session).ConfigureAwait(true);
        if (refreshed.Success && refreshed.Value is not null)
        {
            await _sessionService.SetSessionAsync(session, refreshed.Value, Settings.Account.RememberMe).ConfigureAwait(true);
        }
    }

    private void RaiseAccountSummaryChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(AccountEmail));
        OnPropertyChanged(nameof(IsEmailVerified));
        OnPropertyChanged(nameof(SessionExpiryText));
        OnPropertyChanged(nameof(LastLoginText));
        OnPropertyChanged(nameof(CurrentLicense));
        OnPropertyChanged(nameof(IsBetaTester));
        OnPropertyChanged(nameof(IsProUnlocked));
        OnPropertyChanged(nameof(CanUseCloudSync));
        OnPropertyChanged(nameof(IsFreeTierCurrent));
        OnPropertyChanged(nameof(IsProTierCurrent));
        OnPropertyChanged(nameof(ThemeKinds));

        _isLoadingCloudEnabled = true;
        CloudEnabled = _sessionService.CurrentProfile?.CloudEnabled ?? false;
        _isLoadingCloudEnabled = false;
    }

    /// <summary>Same downgrade-guard shape as <see cref="EnforceThemeLicenseLimit"/>/
    /// <see cref="EnforceProPluginLicenseLimits"/> — if a license downgrade leaves Cloud Sync
    /// turned on server-side for a now-Free account, push it back off rather than leaving cross-
    /// device sync active for an account that can no longer buy it.</summary>
    private async Task EnforceCloudSyncLicenseLimitAsync()
    {
        if (_licenseService.CanUseCloudSync) return;
        if (_sessionService.CurrentProfile?.CloudEnabled is not true) return;

        await UpdateCloudEnabledAsync(false).ConfigureAwait(true);
        _isLoadingCloudEnabled = true;
        CloudEnabled = false;
        _isLoadingCloudEnabled = false;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var result = System.Windows.MessageBox.Show(
            "Log out of your account? Your local sound library and settings stay right here on this device.",
            "Log Out",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        await _sessionService.LogoutAsync().ConfigureAwait(true);
        _licenseService.UpdateFromProfile(null);
        AccountStatusMessage = "Logged out.";
    }

    [RelayCommand]
    private async Task ResendVerificationAsync()
    {
        var email = AccountEmail;
        if (string.IsNullOrWhiteSpace(email) || IsAccountBusy) return;

        IsAccountBusy = true;
        try
        {
            var result = await _authService.ResendVerificationEmailAsync(email).ConfigureAwait(true);
            AccountStatusMessage = result.Success ? "Verification code resent." : result.ErrorMessage ?? "Couldn't resend the code.";
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (IsChangingPassword) return;

        var profile = _sessionService.CurrentProfile;
        if (profile is null) return;

        if (!InputValidators.IsValidPassword(ChangePasswordNew))
        {
            ChangePasswordMessage = InputValidators.PasswordRequirementsText;
            return;
        }

        if (ChangePasswordNew != ChangePasswordConfirm)
        {
            ChangePasswordMessage = "New passwords don't match.";
            return;
        }

        IsChangingPassword = true;
        ChangePasswordMessage = string.Empty;
        try
        {
            // Re-confirm the current password before changing anything — the existing session's
            // access token would technically already be enough to call ChangePasswordAsync, but
            // requiring the current password here is a deliberate extra check (e.g. against an
            // unlocked, unattended session).
            var reauth = await _authService.LoginAsync(profile.Email, ChangePasswordCurrent).ConfigureAwait(true);
            if (!reauth.Success || reauth.Value is null)
            {
                ChangePasswordMessage = "Current password is incorrect.";
                return;
            }

            var result = await _authService.ChangePasswordAsync(reauth.Value, ChangePasswordNew).ConfigureAwait(true);
            if (!result.Success)
            {
                ChangePasswordMessage = result.ErrorMessage ?? "Couldn't change your password.";
                return;
            }

            await _sessionService.SetSessionAsync(reauth.Value, profile, Settings.Account.RememberMe).ConfigureAwait(true);
            ChangePasswordCurrent = string.Empty;
            ChangePasswordNew = string.Empty;
            ChangePasswordConfirm = string.Empty;
            ChangePasswordMessage = "Password changed.";
            ChangePasswordFieldsCleared?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsChangingPassword = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        var session = _sessionService.CurrentSession;
        if (session is null || IsAccountBusy) return;

        var result = System.Windows.MessageBox.Show(
            "This flags your account for deletion and logs you out immediately. This cannot be undone from within the app.\n\nContinue?",
            "Delete Account",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        IsAccountBusy = true;
        try
        {
            var deletion = await _authService.RequestAccountDeletionAsync(session).ConfigureAwait(true);
            if (!deletion.Success)
            {
                AccountStatusMessage = deletion.ErrorMessage ?? "Couldn't request account deletion.";
                return;
            }

            await _sessionService.LogoutAsync().ConfigureAwait(true);
            _licenseService.UpdateFromProfile(null);
            AccountStatusMessage = "Account deletion requested. You've been logged out.";
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    // --- Installation config ---

    // Must match the AppId set in installer\Soundboard.iss exactly (Inno Setup registers
    // per-user installs under this key in HKCU, appending "_is1" to the AppId).
    private const string InnoSetupAppId = "{EFBCCA32-BBF4-4615-A440-E95FAF7FD5EE}_is1";

    public string AppVersionText => $"Version {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown"}";
    public string InstallLocationText => AppContext.BaseDirectory;
    public string DataLocationText => _paths.RootDirectory;

    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private string _updateCheckStatusText = string.Empty;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateCheckStatusText = "Checking...";
        try
        {
            var update = await _updateService.CheckForUpdateAsync().ConfigureAwait(true);
            UpdateCheckStatusText = update is null
                ? "You're up to date."
                : $"Version {update.Version} is available — use the banner on the main window to update.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAppAsync()
    {
        var result = System.Windows.MessageBox.Show(
            "This will remove Soundboard from your computer. Your settings, sound library, and imported files will be kept.\n\nContinue?",
            "Uninstall Soundboard",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            await LaunchUninstallerAndExitAsync(deleteUserData: false).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task UninstallEverythingAsync()
    {
        var result = System.Windows.MessageBox.Show(
            "This will remove Soundboard AND permanently delete all your settings, sounds, and folders.\n\n" +
            "This cannot be undone — consider using \"Export Collection\" above first if you want a backup.\n\nContinue?",
            "Uninstall Everything",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            await LaunchUninstallerAndExitAsync(deleteUserData: true).ConfigureAwait(true);
        }
    }

    private async Task LaunchUninstallerAndExitAsync(bool deleteUserData)
    {
        try
        {
            await _audioEngine.StopAllAsync().ConfigureAwait(true);

            if (deleteUserData)
            {
                try
                {
                    if (Directory.Exists(_paths.RootDirectory))
                    {
                        Directory.Delete(_paths.RootDirectory, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    // Still proceed to uninstall the app itself even if data deletion partly
                    // failed (e.g. a file briefly locked) — better than leaving the app installed
                    // with no way to retry either half.
                    DiagnosticsStatus = $"Couldn't fully delete the data folder: {ex.Message}";
                }
            }

            var uninstallString = FindUninstallerCommand();
            if (uninstallString is null)
            {
                System.Windows.MessageBox.Show(
                    "Couldn't find the uninstaller. This usually means Soundboard wasn't installed via the installer " +
                    "(e.g. it was run directly from an extracted folder) — you can delete the application folder manually instead.",
                    "Uninstaller Not Found",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var (fileName, arguments) = ParseUninstallCommand(uninstallString);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments) { UseShellExecute = true });

            // A hard process exit rather than a normal WPF shutdown: the latter runs MainWindow's
            // Closing handler, which saves settings back to disk — a folder that, in the "delete
            // everything" case, may no longer exist by this point. Nothing here needs a graceful
            // shutdown anyway, since the uninstaller is about to remove the app's files regardless.
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Uninstall failed: {ex.Message}";
        }
    }

    private static string? FindUninstallerCommand()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{InnoSetupAppId}");
            return key?.GetValue("UninstallString") as string;
        }
        catch
        {
            return null;
        }
    }

    private static (string FileName, string Arguments) ParseUninstallCommand(string uninstallString)
    {
        uninstallString = uninstallString.Trim();
        if (uninstallString.StartsWith('"'))
        {
            var endQuote = uninstallString.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var path = uninstallString[1..endQuote];
                var rest = uninstallString[(endQuote + 1)..].Trim();
                return (path, rest);
            }
        }

        return (uninstallString, string.Empty);
    }
}

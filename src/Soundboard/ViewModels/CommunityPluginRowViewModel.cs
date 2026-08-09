using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>One card in the Marketplace's Community section — wraps a fetched
/// <see cref="CommunityPlugin"/>. Installing runs its script once immediately (registering
/// whatever tiles/panel buttons it defines) and caches it locally so it auto-runs again on every
/// future launch — see <see cref="ICommunityPluginRuntime"/>. Installing an unverified plugin
/// prompts for confirmation first, since "runs automatically forever until uninstalled" is a real
/// step up from the authoring window's one-shot Test Run.</summary>
public sealed partial class CommunityPluginRowViewModel : ObservableObject
{
    private readonly CommunityPlugin _plugin;
    private readonly ICommunityPluginRuntime _runtime;

    public CommunityPluginRowViewModel(CommunityPlugin plugin, ICommunityPluginRuntime runtime)
    {
        _plugin = plugin;
        _runtime = runtime;
    }

    public string Name => _plugin.Name;
    public string Description => _plugin.Description ?? string.Empty;
    public string AuthorUsername => _plugin.AuthorUsername;
    public bool IsVerified => _plugin.IsVerified;

    public bool IsInstalled => _runtime.IsInstalled(_plugin.Id);
    public string ButtonText => IsInstalled ? "Uninstall" : "Install";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task ToggleInstallAsync()
    {
        if (IsBusy) return;

        if (IsInstalled)
        {
            _runtime.Uninstall(_plugin.Id);
            StatusMessage = "Uninstalled.";
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ButtonText));
            return;
        }

        if (!IsVerified)
        {
            var confirmed = System.Windows.MessageBox.Show(
                $"\"{Name}\" isn't verified. Installing means it'll run automatically every time Sonar starts. Continue?",
                "Install unverified plugin",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmed != System.Windows.MessageBoxResult.Yes) return;
        }

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var success = await _runtime.InstallAsync(_plugin).ConfigureAwait(true);
            StatusMessage = success ? "Installed." : "Couldn't install — see notification for details.";
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(ButtonText));
        }
        finally
        {
            IsBusy = false;
        }
    }
}

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Views;

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
    private readonly ISessionService _sessionService;
    private readonly IContentReportService _reportService;

    public CommunityPluginRowViewModel(CommunityPlugin plugin, ICommunityPluginRuntime runtime, ISessionService sessionService, IContentReportService reportService)
    {
        _plugin = plugin;
        _runtime = runtime;
        _sessionService = sessionService;
        _reportService = reportService;
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

    [RelayCommand]
    private async Task ReportAsync()
    {
        if (IsBusy) return;

        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            StatusMessage = "Sign in to report content.";
            return;
        }

        var dialog = new InputDialog("Report plugin", $"Why are you reporting \"{Name}\"?") { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var result = await _reportService.SubmitReportAsync(session, ContentReportKind.Plugin, _plugin.Id, Name, dialog.InputText).ConfigureAwait(true);
            StatusMessage = result.Success ? "Reported. Thanks for flagging it." : result.ErrorMessage ?? "Couldn't submit report.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Views;

namespace Soundboard.ViewModels;

/// <summary>Backs the Plugin Marketplace window — lists the fixed <see cref="PluginCatalog.All"/>
/// catalog as installable rows. Installing/uninstalling is an instant local settings toggle, not
/// a download; the only gate is Voice Changer requiring a Pro license (see
/// <see cref="PluginRowViewModel.IsLocked"/>). Trust/verified status is fetched live from the
/// cloud via <see cref="IPluginTrustService"/> (see <see cref="LoadTrustStatusCommand"/>) — never
/// blocks the window opening, since that service never throws and returns an empty set on any
/// failure (offline, etc.), so every card just starts as "Unverified" until the fetch resolves.</summary>
public partial class PluginMarketplaceViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILicenseService _licenseService;
    private readonly IPluginTrustService _pluginTrustService;
    private readonly IPluginPackService _pluginPackService;
    private readonly ICommunityPluginService _communityPluginService;
    private readonly ICommunityPackService _communityPackService;
    private readonly ICommunityPluginRuntime _pluginRuntime;
    private readonly IServiceProvider _services;

    public PluginMarketplaceViewModel(
        ISettingsService settingsService,
        ILicenseService licenseService,
        IPluginTrustService pluginTrustService,
        IPluginPackService pluginPackService,
        ICommunityPluginService communityPluginService,
        ICommunityPackService communityPackService,
        ICommunityPluginRuntime pluginRuntime,
        IServiceProvider services)
    {
        _settingsService = settingsService;
        _licenseService = licenseService;
        _pluginTrustService = pluginTrustService;
        _pluginPackService = pluginPackService;
        _communityPluginService = communityPluginService;
        _communityPackService = communityPackService;
        _pluginRuntime = pluginRuntime;
        _services = services;

        foreach (var definition in PluginCatalog.All)
        {
            Plugins.Add(new PluginRowViewModel(definition, _settingsService, _licenseService, OnPluginToggled));
        }

        OnPluginToggled();
    }

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];
    public ObservableCollection<CommunityPluginRowViewModel> CommunityPlugins { get; } = [];
    public ObservableCollection<CommunityPackRowViewModel> CommunityPacks { get; } = [];

    [ObservableProperty] private string _installedSummaryText = string.Empty;
    [ObservableProperty] private bool _isDeveloperModeInstalled;
    [ObservableProperty] private string _importStatusMessage = string.Empty;
    [ObservableProperty] private string _communitySearchQuery = string.Empty;
    [ObservableProperty] private bool _communityVerifiedOnly;
    [ObservableProperty] private bool _isSearchingCommunity;

    [RelayCommand]
    private async Task LoadTrustStatusAsync()
    {
        var verifiedIds = await _pluginTrustService.GetVerifiedPluginIdsAsync().ConfigureAwait(true);
        foreach (var plugin in Plugins)
        {
            plugin.IsVerified = verifiedIds.Contains(plugin.Id);
        }
    }

    [RelayCommand]
    private async Task OpenCreatePluginAsync()
    {
        if (!IsDeveloperModeInstalled) return;

        var chooser = new PluginTypeChooserWindow { Owner = Application.Current.MainWindow };
        if (chooser.ShowDialog() != true) return;

        switch (chooser.SelectedType)
        {
            case PluginCreationType.Basic:
            {
                var window = _services.GetRequiredService<PluginAuthoringWindow>();
                window.Owner = Application.Current.MainWindow;
                window.ShowDialog();
                break;
            }
            case PluginCreationType.Custom:
            {
                var window = _services.GetRequiredService<ScriptPluginAuthoringWindow>();
                window.Owner = Application.Current.MainWindow;
                window.ShowDialog();
                break;
            }
        }

        // Either path may have just published something — refresh so it shows up immediately.
        await SearchCommunityAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportPluginPackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sonar Plugin|*.sonarplugin"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var pack = await _pluginPackService.ImportAsync(dialog.FileName).ConfigureAwait(true);
            var included = new List<string>();
            if (pack.Hotkeys is not null) included.Add("hotkeys");
            if (pack.VoiceChangerPresets is not null) included.Add("voice changer presets");
            if (pack.Theme is not null) included.Add("theme");

            ImportStatusMessage = $"Imported \"{pack.Name}\" ({string.Join(", ", included)}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            ImportStatusMessage = "That file isn't a valid Sonar plugin.";
        }
    }

    [RelayCommand]
    private async Task SearchCommunityAsync()
    {
        IsSearchingCommunity = true;
        try
        {
            var pluginsTask = _communityPluginService.SearchAsync(CommunitySearchQuery, CommunityVerifiedOnly);
            var packsTask = _communityPackService.SearchAsync(CommunitySearchQuery, CommunityVerifiedOnly);
            await Task.WhenAll(pluginsTask, packsTask).ConfigureAwait(true);

            CommunityPlugins.Clear();
            foreach (var plugin in pluginsTask.Result)
            {
                CommunityPlugins.Add(new CommunityPluginRowViewModel(plugin, _pluginRuntime));
            }

            CommunityPacks.Clear();
            foreach (var pack in packsTask.Result)
            {
                CommunityPacks.Add(new CommunityPackRowViewModel(pack, _pluginPackService));
            }
        }
        finally
        {
            IsSearchingCommunity = false;
        }
    }

    private void OnPluginToggled()
    {
        RefreshSummary();
        IsDeveloperModeInstalled = _settingsService.Settings.Plugins.InstalledPluginIds.Contains(PluginCatalog.Developer);
    }

    private void RefreshSummary()
    {
        var installedCount = Plugins.Count(p => p.IsInstalled);
        InstalledSummaryText = $"{installedCount} of {Plugins.Count} installed";
    }
}

/// <summary>One row in the marketplace, wrapping a fixed <see cref="PluginDefinition"/> with the
/// live installed/locked state read straight off settings — same "wrap a model + services"
/// shape as <see cref="SoundButtonViewModel"/>. <see cref="IsVerified"/> is pushed in from the
/// parent VM's cloud fetch rather than owned here, since fetching it per-row would mean one
/// network call per plugin instead of one for the whole list.</summary>
public sealed partial class PluginRowViewModel : ObservableObject
{
    private readonly PluginDefinition _definition;
    private readonly ISettingsService _settingsService;
    private readonly ILicenseService _licenseService;
    private readonly Action _onToggled;

    public PluginRowViewModel(PluginDefinition definition, ISettingsService settingsService, ILicenseService licenseService, Action onToggled)
    {
        _definition = definition;
        _settingsService = settingsService;
        _licenseService = licenseService;
        _onToggled = onToggled;
    }

    public string Id => _definition.Id;
    public string Name => _definition.Name;
    public string Description => _definition.Description;
    public string Icon => _definition.Icon;
    public string Author => _definition.Author;

    [ObservableProperty] private bool _isVerified;

    public bool IsLocked => _definition.RequiresPro && !_licenseService.IsProUnlocked;
    public bool IsInstalled => _settingsService.Settings.Plugins.InstalledPluginIds.Contains(_definition.Id);
    public string ButtonText => IsLocked ? "Requires Pro" : IsInstalled ? "Uninstall" : "Install";

    [RelayCommand]
    private void Toggle()
    {
        if (IsLocked) return;

        var installedIds = _settingsService.Settings.Plugins.InstalledPluginIds;
        if (!installedIds.Remove(_definition.Id))
        {
            installedIds.Add(_definition.Id);
        }

        // notifyChanged: true (the default) — MainViewModel/SettingsViewModel react to
        // SettingsChanged to refresh the sidebar button / Settings tab visibility live.
        _ = _settingsService.SaveAsync();

        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ButtonText));
        _onToggled();
    }
}

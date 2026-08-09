using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

/// <summary>Backs the "Create a Plugin" window (unlocked by installing the Developer Tools
/// plugin) — packages selected pieces of the user's own current settings into a shareable
/// <see cref="PluginPack"/> file. Plain settings data only, no code, so there's nothing here that
/// could execute anything on another user's machine when they import it.</summary>
public partial class PluginAuthoringViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IPluginPackService _pluginPackService;
    private readonly ICommunityPackService _communityPackService;
    private readonly IProfanityFilterService _profanityFilter;
    private readonly ISessionService _sessionService;

    public PluginAuthoringViewModel(
        ISettingsService settingsService,
        IPluginPackService pluginPackService,
        ICommunityPackService communityPackService,
        IProfanityFilterService profanityFilter,
        ISessionService sessionService)
    {
        _settingsService = settingsService;
        _pluginPackService = pluginPackService;
        _communityPackService = communityPackService;
        _profanityFilter = profanityFilter;
        _sessionService = sessionService;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _includeHotkeys = true;
    [ObservableProperty] private bool _includeVoiceChangerPresets = true;
    [ObservableProperty] private bool _includeTheme;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isPublishing;

    [RelayCommand]
    private async Task ExportAsync()
    {
        var pack = BuildPack();
        if (pack is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sonar Plugin|*.sonarplugin",
            FileName = $"{Name}.sonarplugin"
        };

        if (dialog.ShowDialog() != true) return;

        await _pluginPackService.ExportAsync(dialog.FileName, pack).ConfigureAwait(true);
        StatusMessage = $"Saved to {dialog.FileName}";
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (IsPublishing) return;

        var pack = BuildPack();
        if (pack is null) return;

        if (_profanityFilter.ContainsProfanity(Name) || _profanityFilter.ContainsProfanity(Description))
        {
            StatusMessage = "That name or description isn't allowed. Please choose something else.";
            return;
        }

        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            StatusMessage = "Sign in first to publish a plugin.";
            return;
        }

        IsPublishing = true;
        try
        {
            var result = await _communityPackService
                .SubmitAsync(session, pack.Name, pack.Description, pack)
                .ConfigureAwait(true);

            StatusMessage = result.Success
                ? "Published! It'll show as unverified until an admin reviews it."
                : result.ErrorMessage ?? "Couldn't publish.";
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private PluginPack? BuildPack()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Give your plugin a name first.";
            return null;
        }

        if (!IncludeHotkeys && !IncludeVoiceChangerPresets && !IncludeTheme)
        {
            StatusMessage = "Pick at least one thing to include.";
            return null;
        }

        var settings = _settingsService.Settings;
        return new PluginPack
        {
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Hotkeys = IncludeHotkeys ? settings.GlobalHotkeys : null,
            VoiceChangerPresets = IncludeVoiceChangerPresets ? [.. settings.Audio.VoiceChangerPresets] : null,
            Theme = IncludeTheme ? settings.Theme : null
        };
    }
}

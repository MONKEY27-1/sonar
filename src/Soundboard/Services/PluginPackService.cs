using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

public sealed class PluginPackService : IPluginPackService
{
    private readonly ISettingsService _settingsService;
    private readonly IHotkeyManager _hotkeyManager;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public PluginPackService(ISettingsService settingsService, IHotkeyManager hotkeyManager)
    {
        _settingsService = settingsService;
        _hotkeyManager = hotkeyManager;
    }

    public async Task ExportAsync(string destinationPath, PluginPack pack, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(pack, JsonOptions);
        await File.WriteAllTextAsync(destinationPath, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PluginPack> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var pack = JsonSerializer.Deserialize<PluginPack>(json, JsonOptions)
            ?? throw new InvalidDataException("Not a valid Sonar plugin file.");

        await ApplyAsync(pack, cancellationToken).ConfigureAwait(false);
        return pack;
    }

    public async Task<PluginPack> ImportAsync(PluginPack pack, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(pack, cancellationToken).ConfigureAwait(false);
        return pack;
    }

    private async Task ApplyAsync(PluginPack pack, CancellationToken cancellationToken)
    {
        var settings = _settingsService.Settings;

        if (pack.Hotkeys is { } hotkeys)
        {
            settings.GlobalHotkeys = hotkeys;
            _hotkeyManager.RegisterGlobalHotkeys(hotkeys);
        }

        if (pack.VoiceChangerPresets is { } presets)
        {
            // Adds only, by name — never overwrites or removes a preset the user already has,
            // same dedup approach MainViewModel.InitializeAsync uses for seeding built-in defaults.
            var existingNames = settings.Audio.VoiceChangerPresets.Select(p => p.Name).ToHashSet();
            settings.Audio.VoiceChangerPresets.AddRange(presets.Where(p => !existingNames.Contains(p.Name)));
        }

        if (pack.Theme is { } theme)
        {
            settings.Theme = theme;
        }

        await _settingsService.SaveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

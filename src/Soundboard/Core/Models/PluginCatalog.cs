namespace Soundboard.Core.Models;

/// <summary>One entry in the Plugin Marketplace — a fixed, hardcoded catalog (see
/// <see cref="PluginCatalog.All"/>), not anything fetched remotely.</summary>
public sealed class PluginDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Icon { get; init; }
    public required string Author { get; init; }
    public bool RequiresPro { get; init; }
}

/// <summary>The optional feature groups installable from the Plugin Marketplace. Ids are plain
/// strings (not an enum) so persisted <see cref="PluginSettings.InstalledPluginIds"/> values can
/// never drift out from under an enum reorder/renumber. Whether a given id is currently
/// "verified/trustworthy" is NOT part of this static catalog — that's fetched live from the
/// cloud (see IPluginTrustService), since it can change without an app update.</summary>
public static class PluginCatalog
{
    public const string VoiceChanger = "VoiceChanger";
    public const string AdvancedSettings = "AdvancedSettings";
    public const string PerformanceMode = "PerformanceMode";
    public const string Developer = "Developer";

    public static readonly IReadOnlyList<PluginDefinition> All =
    [
        new()
        {
            Id = VoiceChanger,
            Name = "Voice Changer",
            Icon = "🎙",
            Author = "JTheGuy",
            Description = "Pitch, robot, echo, and distortion effects for your mic.",
            RequiresPro = true
        },
        new()
        {
            Id = AdvancedSettings,
            Name = "Advanced Settings",
            Icon = "🛠",
            Author = "JTheGuy",
            Description = "Diagnostics and audio normalization controls.",
            RequiresPro = false
        },
        new()
        {
            Id = PerformanceMode,
            Name = "Performance Mode",
            Icon = "⚡",
            Author = "JTheGuy",
            Description = "Tune audio latency for lower delay or more stability.",
            RequiresPro = false
        },
        new()
        {
            Id = Developer,
            Name = "Developer Tools",
            Icon = "👨‍💻",
            Author = "JTheGuy",
            Description = "Package your hotkeys, voice changer presets, and theme into a shareable plugin file.",
            RequiresPro = false
        }
    ];
}

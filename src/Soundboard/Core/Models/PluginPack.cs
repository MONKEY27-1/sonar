namespace Soundboard.Core.Models;

/// <summary>A user-authored settings bundle exported/imported by the Plugin Marketplace's
/// "Create a Plugin" tool (see PluginPackService) — deliberately much lighter than
/// ICollectionExportService's full sound-library ZIP. Each section is optional and only present
/// if the author chose to include it; this is plain settings data serialized to JSON, so it
/// can't execute anything on import, unlike a real third-party plugin would.</summary>
public sealed class PluginPack
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string Author { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public GlobalHotkeys? Hotkeys { get; init; }
    public List<VoiceChangerPreset>? VoiceChangerPresets { get; init; }
    public ThemeSettings? Theme { get; init; }
}

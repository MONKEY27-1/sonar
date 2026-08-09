namespace Soundboard.Core.Models;

/// <summary>A user-published "Basic Plugin" — a settings pack (hotkeys/voice changer presets/
/// theme, see <see cref="PluginPack"/>) shared publicly, the no-code counterpart to
/// <see cref="CommunityPlugin"/>.</summary>
public sealed class CommunityPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string AuthorUsername { get; init; }
    public required PluginPack Pack { get; init; }
    public bool IsVerified { get; init; }
    public DateTime CreatedAt { get; init; }
}

namespace Soundboard.Core.Models;

/// <summary>A user-submitted script plugin from the Community tab — plain, short script text run
/// through a sandboxed interpreter (see PluginScriptRunner), never compiled/loaded code.</summary>
public sealed class CommunityPlugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string AuthorUsername { get; init; }
    public required string ScriptSource { get; init; }
    public bool IsVerified { get; init; }
    public DateTime CreatedAt { get; init; }
}

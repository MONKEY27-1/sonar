using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

/// <summary>The ENTIRE surface a Community Plugin script can ever touch — see PluginScriptRunner/
/// CommunityPluginRuntime for how this is the only object registered into the Jint engine (no
/// CLR/.NET access is ever granted beyond these methods). Deliberately minimal: read-only sound
/// listing, play a sound by name, a log sink, and registering tiles/panel buttons. No settings
/// access, no file system, no network — anything beyond this must be added deliberately, one
/// capability at a time, not implied by this class growing organically.
///
/// AddTile/AddPanelButton's onClick parameters are typed as plain <see cref="Action"/> — Jint
/// automatically marshals a JS function argument passed at that position into a real .NET
/// delegate, which stays invokable after this script's top-level execution returns (verified
/// empirically before this was built). Nothing here invokes those callbacks; this class only
/// records them for the caller (CommunityPluginRuntime) to read once execution finishes.</summary>
internal sealed class PluginScriptHost
{
    private readonly ILibraryService _libraryService;
    private readonly IPlaybackManager _playbackManager;
    private readonly List<string> _logLines;
    private readonly List<PluginTileRegistration> _tiles;
    private readonly List<PluginButtonRegistration> _panelButtons;

    public PluginScriptHost(
        ILibraryService libraryService,
        IPlaybackManager playbackManager,
        List<string> logLines,
        List<PluginTileRegistration> tiles,
        List<PluginButtonRegistration> panelButtons)
    {
        _libraryService = libraryService;
        _playbackManager = playbackManager;
        _logLines = logLines;
        _tiles = tiles;
        _panelButtons = panelButtons;
    }

    public string[] GetSoundNames() =>
        _libraryService.Library.Sounds.Select(s => s.GetDisplayName()).ToArray();

    public void PlaySound(string soundName)
    {
        var sound = _libraryService.Library.Sounds
            .FirstOrDefault(s => string.Equals(s.GetDisplayName(), soundName, StringComparison.OrdinalIgnoreCase));

        if (sound is null)
        {
            Log($"No sound named \"{soundName}\" found.");
            return;
        }

        // Already running on a background thread (see PluginScriptRunner) — blocking here is
        // safe and keeps this method's signature synchronous, which is what Jint expects without
        // extra async plumbing on the script side.
        _playbackManager.PlaySoundAsync(sound.Id).GetAwaiter().GetResult();
    }

    public void Log(string message)
    {
        if (_logLines.Count >= 200) return; // hard cap — a runaway script can't grow this unbounded
        _logLines.Add(message);
    }

    public void AddTile(string name, string icon, Action onClick)
    {
        if (_tiles.Count >= 50) return; // hard cap — same spirit as the log-line cap above
        _tiles.Add(new PluginTileRegistration(name, icon, onClick));
    }

    public void AddPanelButton(string label, Action onClick)
    {
        if (_panelButtons.Count >= 50) return;
        _panelButtons.Add(new PluginButtonRegistration(label, onClick));
    }
}

/// <summary>Raw callback registration captured during a script's install-time run — internal
/// glue between PluginScriptHost and CommunityPluginRuntime, not a public API shape.</summary>
internal sealed record PluginTileRegistration(string Name, string Icon, Action OnClick);

internal sealed record PluginButtonRegistration(string Label, Action OnClick);

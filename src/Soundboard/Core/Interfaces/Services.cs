using Soundboard.Core.Models;

namespace Soundboard.Core.Interfaces;

/// <summary>Blocks obvious profanity/slurs from public-facing text (plugin names/descriptions
/// published to the Marketplace) — a client-side convenience filter, not a hard security
/// boundary; admin review before "Verified" status is the real backstop, same as other
/// client-gated checks in this app.</summary>
public interface IProfanityFilterService
{
    bool ContainsProfanity(string text);
}

public interface IAppPaths
{
    string RootDirectory { get; }
    string SoundsDirectory { get; }
    string IconsDirectory { get; }
    string ProfilesDirectory { get; }
    string BackupsDirectory { get; }
    string LogsDirectory { get; }
    string SettingsFile { get; }
    string LibraryFile { get; }
}

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary><paramref name="notifyChanged"/> controls whether SettingsChanged fires — pass
    /// false when the caller already handles its own targeted refresh (e.g. the Voice Changer
    /// panel updating its live effect parameters directly) so the save doesn't ALSO trigger
    /// AudioEngine's SettingsChanged subscriber, which does a full mic-capture teardown/rebuild.
    /// That double-refresh was firing on every single voice-changer parameter save regardless of
    /// which refresh path the caller intentionally chose, and was the real source of the
    /// repeated mic-capture restarts (and the audible clicking) reported during testing.</summary>
    Task SaveAsync(bool notifyChanged = true, CancellationToken cancellationToken = default);

    /// <summary>Wholesale-replaces Settings (e.g. after pulling a newer copy from cloud sync),
    /// persists it, and raises SettingsChanged — unlike SaveAsync, which persists whatever the
    /// existing Settings object already has.</summary>
    Task ReplaceSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    event EventHandler? SettingsChanged;
}

public interface ILibraryService
{
    SoundLibrary Library { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SoundItem>> ImportFilesAsync(IEnumerable<string> sourcePaths, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Backfills SoundItem.NormalizedGain for every sound already in the library — new
    /// imports get this automatically; this is for sounds that predate real normalization.</summary>
    Task NormalizeAllSoundsAsync(IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task RemoveSoundAsync(string soundId, CancellationToken cancellationToken = default);
    Task RenameSoundAsync(string soundId, string newName, CancellationToken cancellationToken = default);
    Task SetSoundFolderAsync(string soundId, string? folderId, CancellationToken cancellationToken = default);

    /// <summary>Bulk counterparts used by multi-select — same one-lock/loop/one-save shape as
    /// ImportFilesAsync/NormalizeAllSoundsAsync, so selecting hundreds of sounds doesn't do
    /// hundreds of individual file saves.</summary>
    Task RemoveSoundsAsync(IReadOnlyList<string> soundIds, CancellationToken cancellationToken = default);
    Task SetSoundsFolderAsync(IReadOnlyList<string> soundIds, string? folderId, CancellationToken cancellationToken = default);
    Task SetSoundsFavoriteAsync(IReadOnlyList<string> soundIds, bool isFavorite, CancellationToken cancellationToken = default);

    /// <summary>Adds/unions a tag into each sound's existing tags — unlike SetSoundTagsAsync,
    /// which fully replaces a single sound's tag list, this never removes any existing tag.</summary>
    Task AddTagToSoundsAsync(IReadOnlyList<string> soundIds, string tag, CancellationToken cancellationToken = default);

    /// <summary>Removes a single tag from each sound's tags, leaving the rest untouched — the
    /// counterpart to AddTagToSoundsAsync.</summary>
    Task RemoveTagFromSoundsAsync(IReadOnlyList<string> soundIds, string tag, CancellationToken cancellationToken = default);
    Task SetSoundHotkeyAsync(string soundId, HotkeyBinding? hotkey, CancellationToken cancellationToken = default);
    Task SetSoundOutputRouteOverrideAsync(string soundId, OutputRoute? route, CancellationToken cancellationToken = default);
    Task SetSoundVolumeAsync(string soundId, float volume, CancellationToken cancellationToken = default);
    Task SetSoundPlaybackModeAsync(string soundId, PlaybackMode mode, CancellationToken cancellationToken = default);
    Task SetSoundTagsAsync(string soundId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task RemoveFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task ReplaceSoundFileAsync(string soundId, string sourcePath, CancellationToken cancellationToken = default);
    Task DuplicateSoundAsync(string soundId, CancellationToken cancellationToken = default);
    Task RescanSoundsFolderAsync(CancellationToken cancellationToken = default);

    /// <summary>Reassigns SortOrder (0, 1, 2...) to exactly the sounds in <paramref name="orderedSoundIds"/>,
    /// in the order given — used after a drag-drop reorder. Sounds outside the current filtered
    /// view (not present in the list) are left untouched, so this is safe to call with just the
    /// currently-visible subset rather than the whole library. <paramref name="notifyChanged"/>
    /// mirrors ISettingsService.SaveAsync's flag of the same name — pass false when the caller
    /// already handles its own targeted refresh.</summary>
    Task ReorderSoundsAsync(IReadOnlyList<string> orderedSoundIds, bool notifyChanged = true, CancellationToken cancellationToken = default);

    IEnumerable<SoundItem> GetFilteredSounds(string? folderId, string? searchQuery, bool favoritesOnly, bool recentOnly);
    string GetSoundFilePath(SoundItem sound);
    Task MarkRecentlyUsedAsync(string soundId, CancellationToken cancellationToken = default);
    event EventHandler? LibraryChanged;
    event EventHandler<ImportProgress>? ImportProgressChanged;
    event EventHandler<ImportProgress>? NormalizeProgressChanged;
}

public sealed class ImportProgress
{
    public int Total { get; init; }
    public int Completed { get; init; }
    public string? CurrentFile { get; init; }
    public bool IsComplete => Completed >= Total;
}

public interface IAudioEngine
{
    Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DetectedVirtualDevice>> DetectVirtualDevicesAsync(CancellationToken cancellationToken = default);
    Task<double> GetDurationAsync(string filePath, CancellationToken cancellationToken = default);
    Task ChangeVirtualDeviceAsync(string? deviceId);
    Task<string> PlayAsync(SoundItem sound, string filePath, OutputRoute route, CancellationToken cancellationToken = default);
    Task StopAsync(string instanceId);
    Task StopAllAsync();
    Task PauseAsync(string instanceId);
    Task ResumeAsync(string instanceId);
    Task RestartAsync(string instanceId);
    Task SeekAsync(string instanceId, double deltaSeconds);

    /// <summary>Seeks to an absolute position, unlike <see cref="SeekAsync"/>'s relative
    /// delta — used for click-to-seek on the Now Playing progress bar, where the target is
    /// already an absolute timestamp.</summary>
    Task SeekToAsync(string instanceId, double positionSeconds);
    IReadOnlyList<PlaybackInstance> GetActiveInstances();
    void RefreshMicMonitoring();
    void RefreshSettings();

    /// <summary>Applies live voice-effect parameter changes (pitch, formant, robot/echo/
    /// distortion knobs) without restarting mic capture. Use for slider/knob-style tweaks;
    /// use <see cref="RefreshMicMonitoring"/> when the active effect TYPE changes instead.</summary>
    void UpdateVoiceEffectParameters();

    /// <summary>Turns the Voice Changer's "Test Mic" live headphone preview on/off, then
    /// immediately refreshes mic monitoring so it takes effect right away.</summary>
    void SetVoicePreviewEnabled(bool enabled);
    event EventHandler<PlaybackInstance>? PlaybackStateChanged;
    event EventHandler<(string InstanceId, double Position, double Duration)>? PlaybackProgress;
}

public interface IPlaybackManager
{
    Task PlaySoundAsync(string soundId, bool fromHotkey = false);
    Task StopSoundAsync(string soundId);
    Task StopAllAsync();
    Task PauseAllAsync();
    Task ResumeAllAsync();
    Task ToggleLoopForSoundAsync(string soundId);
    IReadOnlyList<PlaybackInstance> ActiveInstances { get; }
    event EventHandler? ActiveInstancesChanged;
}

public interface IHotkeyManager : IDisposable
{
    void RegisterSoundHotkey(SoundItem sound);
    void UnregisterSoundHotkey(string soundId);
    void RegisterGlobalHotkeys(GlobalHotkeys hotkeys);
    event EventHandler<(string SoundId, HotkeyAction Action)>? SoundHotkeyPressed;
    event EventHandler<HotkeyAction>? GlobalHotkeyPressed;
}

public enum HotkeyAction
{
    Play,
    Stop,
    PushToPlayDown,
    PushToPlayUp,
    StopAll,
    PauseAll,
    ResumeAll,
    ToggleLoop,
    ToggleVoiceChanger,
    ToggleQuickPlayOverlay
}

public interface IThemeService
{
    void ApplyTheme(AppSettings settings);
    event EventHandler? ThemeChanged;
}

public interface INotificationService
{
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    void ShowSuccess(string title, string message);
}

public interface ISoundFileWatcher : IDisposable
{
    void Start();
    event EventHandler? SoundsFolderChanged;
}

public interface ICollectionExportService
{
    Task ExportCollectionAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task ImportCollectionAsync(string sourcePath, CancellationToken cancellationToken = default);
}

/// <summary>Exports/imports a PluginPack — a small, selective settings bundle (hotkeys, voice
/// changer presets, theme), NOT a full collection backup like <see cref="ICollectionExportService"/>.
/// This is the "Create a Plugin" authoring feature's backing service; there's no code execution
/// involved anywhere in it, only plain JSON settings data.</summary>
public interface IPluginPackService
{
    Task ExportAsync(string destinationPath, PluginPack pack, CancellationToken cancellationToken = default);

    /// <summary>Merges the pack into current settings (never wholesale-destroys existing voice
    /// changer presets — only adds ones not already present by name) and persists/applies the
    /// change, returning the parsed pack so the caller can summarize what was imported.</summary>
    Task<PluginPack> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>Same merge/apply behavior as <see cref="ImportAsync(string, CancellationToken)"/>,
    /// but for a pack already in memory (e.g. fetched from the Community Packs list) rather than
    /// a local file.</summary>
    Task<PluginPack> ImportAsync(PluginPack pack, CancellationToken cancellationToken = default);
}

/// <summary>Runs a Community Plugin script inside a sandbox (Jint — no CLR/.NET access, see
/// PluginScriptRunner) with hard resource limits. Never lets a script hang the caller — always
/// returns within its own timeout, success or failure.</summary>
public interface IPluginScriptRunner
{
    Task<PluginScriptResult> RunAsync(string scriptSource, CancellationToken cancellationToken = default);
}

public sealed class PluginScriptResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> LogLines { get; init; } = [];
}

/// <summary>Owns *installed* Community Plugins — a live, sandboxed Jint engine per plugin, kept
/// alive for the app's session so the tiles/panel buttons it registered via
/// <c>sonar.addTile</c>/<c>sonar.addPanelButton</c> stay clickable. Unlike
/// <see cref="IPluginScriptRunner"/> (ephemeral, one-shot, used only by the authoring window's
/// Test Run), an installed plugin's script re-runs automatically at every startup and its effect
/// persists — see <see cref="InitializeAsync"/>.</summary>
public interface ICommunityPluginRuntime
{
    /// <summary>Called once at startup — re-runs every installed plugin's locally-cached script to
    /// rebuild this session's tiles/panel buttons. Resilient per-plugin: one script throwing never
    /// blocks the rest from loading.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<bool> InstallAsync(CommunityPlugin plugin, CancellationToken cancellationToken = default);
    void Uninstall(string pluginId);
    bool IsInstalled(string pluginId);

    IReadOnlyList<PluginTile> Tiles { get; }
    IReadOnlyList<PluginPanelButtonGroup> PanelGroups { get; }

    /// <summary>Fires after install/uninstall/initialize — subscribers rebuild their own bindable
    /// copies of Tiles/PanelGroups from current state, same reactive-refresh shape as
    /// ILibraryService.LibraryChanged.</summary>
    event EventHandler? PluginsChanged;
}

public sealed class PluginTile
{
    public required string PluginId { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }

    /// <summary>Returns the same result shape a script run reports — the caller (see
    /// PluginTileViewModel) surfaces LogLines/errors via INotificationService, since a click
    /// otherwise has nowhere in the main window to show sonar.log(...) output.</summary>
    public required Func<Task<PluginScriptResult>> InvokeAsync { get; init; }
}

public sealed class PluginPanelButton
{
    public required string Label { get; init; }
    public required Func<Task<PluginScriptResult>> InvokeAsync { get; init; }
}

public sealed class PluginPanelButtonGroup
{
    public required string PluginName { get; init; }
    public required IReadOnlyList<PluginPanelButton> Buttons { get; init; }
}

public interface IUpdateService
{
    /// <summary>Checks the GitHub release feed for a version newer than the running app.
    /// Returns null both when already up to date AND when the check itself fails (network
    /// error, rate limit, malformed release, etc.) — this always runs unattended in the
    /// background, so a failure has to look identical to "nothing to do" rather than surface
    /// as an error.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads the update's installer asset to a temp file and returns its path.</summary>
    Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required string ReleaseUrl { get; init; }
    public string? ReleaseNotes { get; init; }
}

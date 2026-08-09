using Soundboard.Core.Models;

namespace Soundboard.Core.Interfaces;

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
    Task RemoveSoundAsync(string soundId, CancellationToken cancellationToken = default);
    Task RenameSoundAsync(string soundId, string newName, CancellationToken cancellationToken = default);
    Task SetSoundFolderAsync(string soundId, string? folderId, CancellationToken cancellationToken = default);
    Task SetSoundHotkeyAsync(string soundId, HotkeyBinding? hotkey, CancellationToken cancellationToken = default);
    Task SetSoundOutputRouteOverrideAsync(string soundId, OutputRoute? route, CancellationToken cancellationToken = default);
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
    Task ChangeHeadphoneDeviceAsync(string? deviceId);
    Task ChangeVirtualDeviceAsync(string? deviceId);
    Task<string> PlayAsync(SoundItem sound, string filePath, OutputRoute route, CancellationToken cancellationToken = default);
    Task StopAsync(string instanceId);
    Task StopAllAsync();
    Task PauseAsync(string instanceId);
    Task ResumeAsync(string instanceId);
    Task RestartAsync(string instanceId);
    Task SeekAsync(string instanceId, double deltaSeconds);
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

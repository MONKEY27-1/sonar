using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using Soundboard.Helpers;
using Soundboard.Views;

namespace Soundboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly ISettingsService _settingsService;
    private readonly IPlaybackManager _playbackManager;
    private readonly IHotkeyManager _hotkeyManager;
    private readonly IThemeService _themeService;
    private readonly INotificationService _notifications;
    private readonly ISoundFileWatcher _fileWatcher;
    private readonly IAudioEngine _audioEngine;
    private readonly IServiceProvider _services;
    private readonly ISessionService _sessionService;
    private readonly ILicenseService _licenseService;
    private readonly IUpdateService _updateService;
    private readonly ICommunityPluginRuntime _pluginRuntime;
    private readonly IAdminMessageService _adminMessageService;
    private readonly ICollectionExportService _collectionExport;
    private readonly Dictionary<string, SoundButtonViewModel> _buttonCache = new();
    private UpdateInfo? _pendingUpdate;

    public MainViewModel(
        ILibraryService libraryService,
        ISettingsService settingsService,
        IPlaybackManager playbackManager,
        IHotkeyManager hotkeyManager,
        IThemeService themeService,
        INotificationService notifications,
        ISoundFileWatcher fileWatcher,
        IAudioEngine audioEngine,
        IServiceProvider services,
        ISessionService sessionService,
        ILicenseService licenseService,
        IUpdateService updateService,
        ICommunityPluginRuntime pluginRuntime,
        IAdminMessageService adminMessageService,
        ICollectionExportService collectionExport)
    {
        _libraryService = libraryService;
        _settingsService = settingsService;
        _playbackManager = playbackManager;
        _hotkeyManager = hotkeyManager;
        _themeService = themeService;
        _notifications = notifications;
        _fileWatcher = fileWatcher;
        _audioEngine = audioEngine;
        _services = services;
        _sessionService = sessionService;
        _licenseService = licenseService;
        _collectionExport = collectionExport;
        _updateService = updateService;
        _pluginRuntime = pluginRuntime;
        _adminMessageService = adminMessageService;

        _pluginRuntime.PluginsChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(RefreshPluginTilesAndPanels);
        };

        _sessionService.SessionChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _licenseService.UpdateFromProfile(_sessionService.CurrentProfile);
                RaiseAccountSummaryChanged();
            });
        };

        _libraryService.LibraryChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(RefreshSounds);
        };
        _settingsService.SettingsChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _themeService.ApplyTheme(_settingsService.Settings);
                RefreshPluginState();
            });
        };
        _playbackManager.ActiveInstancesChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.Invoke(UpdatePlayingStates);
        };
        _fileWatcher.SoundsFolderChanged += (_, _) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(async () =>
                await RescanLibraryAsync().ConfigureAwait(true));
        };
        _libraryService.ImportProgressChanged += (_, p) =>
        {
            ImportProgressText = p.IsComplete ? string.Empty : $"Importing {p.Completed}/{p.Total}: {p.CurrentFile}";
        };

        audioEngine.PlaybackProgress += (_, args) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Must match the SPECIFIC instance this tick is about, not just "any sound
                // that happens to be playing" — with more than one sound active at once, the
                // old lookup could apply one sound's progress to a different sound's tile.
                var instance = _playbackManager.ActiveInstances.FirstOrDefault(i => i.InstanceId == args.InstanceId);
                var button = instance is not null ? VisibleSounds.FirstOrDefault(b => b.Sound.Id == instance.SoundId) : null;
                button?.UpdateProgress(args.Position, args.Duration);

                if (args.InstanceId == NowPlayingInstanceId)
                {
                    NowPlayingPosition = args.Position;
                    NowPlayingDuration = args.Duration;
                }

                var activeItem = ActivePlaybackItems.FirstOrDefault(i => i.InstanceId == args.InstanceId);
                activeItem?.SetProgress(args.Position, args.Duration);
            });
        };

        _hotkeyManager.SoundHotkeyPressed += async (_, args) =>
        {
            switch (args.Action)
            {
                case HotkeyAction.Play:
                    await _playbackManager.PlaySoundAsync(args.SoundId, true).ConfigureAwait(false);
                    break;
                case HotkeyAction.PushToPlayDown:
                    await _playbackManager.PlaySoundAsync(args.SoundId, true).ConfigureAwait(false);
                    break;
                case HotkeyAction.PushToPlayUp:
                    await _playbackManager.StopSoundAsync(args.SoundId).ConfigureAwait(false);
                    break;
                case HotkeyAction.Stop:
                    await _playbackManager.StopSoundAsync(args.SoundId).ConfigureAwait(false);
                    break;
            }
        };

        _hotkeyManager.GlobalHotkeyPressed += async (_, action) =>
        {
            switch (action)
            {
                case HotkeyAction.StopAll:
                    await _playbackManager.StopAllAsync().ConfigureAwait(false);
                    break;
                case HotkeyAction.PauseAll:
                    await _playbackManager.PauseAllAsync().ConfigureAwait(false);
                    break;
                case HotkeyAction.ResumeAll:
                    await _playbackManager.ResumeAllAsync().ConfigureAwait(false);
                    break;
                case HotkeyAction.ToggleVoiceChanger:
                    if (IsVoiceChangerInstalled)
                    {
                        VoiceChangerEnabled = !VoiceChangerEnabled;
                    }
                    break;
                case HotkeyAction.ToggleLoop:
                    // No per-sound hotkey target exists for this one — it's a single global
                    // binding, so it acts on whatever the Now Playing bar is currently showing,
                    // same target as the NowPlaying* commands above.
                    if (NowPlayingInstanceId is { } toggleLoopInstanceId)
                    {
                        var instance = _playbackManager.ActiveInstances.FirstOrDefault(i => i.InstanceId == toggleLoopInstanceId);
                        if (instance is not null)
                        {
                            await _playbackManager.ToggleLoopForSoundAsync(instance.SoundId).ConfigureAwait(false);
                        }
                    }
                    break;
                case HotkeyAction.ToggleQuickPlayOverlay:
                    ToggleQuickPlayOverlay();
                    break;
            }
        };
    }

    public ObservableCollection<SoundButtonViewModel> VisibleSounds { get; } = [];
    public ObservableCollection<SoundFolder> Folders => new(_libraryService.Library.Folders);

    /// <summary>Same folder list as <see cref="Folders"/>, but with a synthetic "Unfiled" entry
    /// prepended — used by the Details panel's folder picker, which (unlike the context menu's
    /// dynamically-built submenu) needs a single flat, bindable ItemsSource including the
    /// "no folder" option.</summary>
    public IReadOnlyList<FolderOption> DetailsFolderOptions
    {
        get
        {
            var options = new List<FolderOption> { new(null, "Unfiled") };
            options.AddRange(_libraryService.Library.Folders.Select(f => new FolderOption(f.Id, f.Name)));
            return options;
        }
    }

    // --- Library toolbar: sort, view mode, tags filter ---
    public Array SortModes => EnumBindingSource.GetValues<SortMode>();

    /// <summary>Grid/List — ThemeSettings.ViewMode already existed as a persisted field before
    /// this redesign but nothing ever read it; this is what actually makes it do something.</summary>
    [ObservableProperty] private ViewMode _viewMode = ViewMode.Grid;

    partial void OnViewModeChanged(ViewMode value)
    {
        _settingsService.Settings.Theme.ViewMode = value;
        _ = _settingsService.SaveAsync();
    }

    /// <summary>Every distinct tag across the whole library, alphabetical — recomputed whenever
    /// RefreshSounds() runs (import/delete/rename can all add or orphan a tag) rather than
    /// tracked incrementally, since the source list is small and this is cheap.</summary>
    public ObservableCollection<string> AllTags { get; } = [];

    [ObservableProperty] private string? _selectedTagFilter;

    partial void OnSelectedTagFilterChanged(string? value) => RefreshSounds();

    [RelayCommand]
    private void ClearTagFilter() => SelectedTagFilter = null;

    // --- Sound Details panel option lists ---
    public Array PlaybackModes => EnumBindingSource.GetValues<PlaybackMode>();
    public static IReadOnlyList<RouteOption> DetailsRouteOptions { get; } =
    [
        new("Use Default", null),
        new("Headphones Only", OutputRoute.Headphones),
        new("Microphone Only", OutputRoute.Microphone),
        new("Both", OutputRoute.Both)
    ];

    // --- Home dashboard ---
    public ObservableCollection<ActivePlaybackItemViewModel> ActivePlaybackItems { get; } = [];
    public ObservableCollection<SoundButtonViewModel> RecentlyPlayedHome { get; } = [];
    public ObservableCollection<SoundButtonViewModel> FavoritesHome { get; } = [];

    [ObservableProperty] private bool _hasActivePlayback;

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _importProgressText = string.Empty;
    [ObservableProperty] private bool _showFavorites;
    [ObservableProperty] private bool _showRecent;
    [ObservableProperty] private string? _selectedFolderId;
    [ObservableProperty] private SortMode _sortMode = SortMode.Custom;
    [ObservableProperty] private int _activePlaybackCount;

    // --- Update banner ---
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateVersionText = string.Empty;
    [ObservableProperty] private bool _isInstallingUpdate;

    // --- Admin broadcast banner ---
    [ObservableProperty] private bool _hasAdminMessage;
    [ObservableProperty] private string _adminMessageText = string.Empty;

    // --- Plugin Marketplace ---
    [ObservableProperty] private bool _isVoiceChangerInstalled;

    // --- Community plugin tiles/panel (see ICommunityPluginRuntime) ---
    [ObservableProperty] private bool _hasPluginTiles;
    [ObservableProperty] private bool _hasPluginPanels;
    [ObservableProperty] private bool _showPluginsPanelTab;
    public ObservableCollection<PluginTileViewModel> PluginTiles { get; } = [];
    public ObservableCollection<PluginPanelGroupViewModel> PluginPanelGroups { get; } = [];

    // --- Now Playing bar ---
    [ObservableProperty] private string? _nowPlayingInstanceId;
    [ObservableProperty] private string _nowPlayingName = string.Empty;
    [ObservableProperty] private double _nowPlayingPosition;
    [ObservableProperty] private double _nowPlayingDuration;
    [ObservableProperty] private bool _nowPlayingIsPaused;
    [ObservableProperty] private bool _hasNowPlaying;

    /// <summary>Every other currently-playing sound besides the one shown in the main transport
    /// row — recomputed (and change-notified) only from UpdatePlayingStates, which itself only
    /// runs on ActiveInstancesChanged (sound starts/stops/pauses), never on the frequent position
    /// ticks — so this doesn't force the bar's ItemsControl to rebuild every tick.</summary>
    public IEnumerable<ActivePlaybackItemViewModel> OtherActivePlaybackItems =>
        ActivePlaybackItems.Where(i => i.InstanceId != NowPlayingInstanceId);

    public bool HasOtherActivePlayback => ActivePlaybackItems.Count(i => i.InstanceId != NowPlayingInstanceId) > 0;

    /// <summary>Makes a sound from the "other active sounds" strip the main transport row's
    /// focus — same effect as it naturally happening to be the most-recently-started sound,
    /// just user-triggered instead of automatic.</summary>
    [RelayCommand]
    private void PromoteToNowPlaying(ActivePlaybackItemViewModel? item)
    {
        if (item is null) return;
        NowPlayingInstanceId = item.InstanceId;
        UpdatePlayingStates();
    }

    // --- Sidebar: Home / Library / Voice Changer tab switch ---
    [ObservableProperty] private bool _showVoiceChangerTab;
    [ObservableProperty] private bool _showHomeTab;

    /// <summary>True when none of Home, the Voice Changer, or the Plugins panel is showing — the
    /// sound grid's own Visibility binding, since a plain per-tab bool can't express "hide when
    /// ANY of three other tabs is active" on its own.</summary>
    public bool ShowSoundGrid => !ShowVoiceChangerTab && !ShowPluginsPanelTab && !ShowHomeTab;

    partial void OnShowVoiceChangerTabChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSoundGrid));
        RaiseNavActiveStatesChanged();
    }

    partial void OnShowPluginsPanelTabChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSoundGrid));
        RaiseNavActiveStatesChanged();
    }

    partial void OnShowHomeTabChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSoundGrid));
        RaiseNavActiveStatesChanged();
    }

    // --- Sidebar nav active-item highlight ("obvious but subtle" per the redesign brief) —
    // each of Home/All Sounds/Favorites/Recent/Most Played/Voice Changer/Plugins is a distinct
    // combination of the underlying tab/filter/sort flags, so these are recomputed together
    // any time ANY of those flags change rather than each owning its own notification. ---
    public bool IsHomeActive => ShowHomeTab;
    public bool IsAllSoundsActive => ShowSoundGrid && !ShowFavorites && !ShowRecent && SortMode != SortMode.MostPlayed;
    public bool IsFavoritesActive => ShowSoundGrid && ShowFavorites;
    public bool IsRecentActive => ShowSoundGrid && ShowRecent;
    public bool IsMostPlayedActive => ShowSoundGrid && !ShowFavorites && !ShowRecent && SortMode == SortMode.MostPlayed;
    public bool IsVoiceChangerNavActive => ShowVoiceChangerTab;
    public bool IsPluginsNavActive => ShowPluginsPanelTab;

    private void RaiseNavActiveStatesChanged()
    {
        OnPropertyChanged(nameof(IsHomeActive));
        OnPropertyChanged(nameof(IsAllSoundsActive));
        OnPropertyChanged(nameof(IsFavoritesActive));
        OnPropertyChanged(nameof(IsRecentActive));
        OnPropertyChanged(nameof(IsMostPlayedActive));
        OnPropertyChanged(nameof(IsVoiceChangerNavActive));
        OnPropertyChanged(nameof(IsPluginsNavActive));
    }

    private void ExitHomeTab() => ShowHomeTab = false;

    [RelayCommand]
    private void ShowHome()
    {
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowHomeTab = true;
    }

    /// <summary>Icon-only sidebar mode — persisted immediately on toggle (not tied to the
    /// Settings window's manual Save), same instant-persist pattern as installing/uninstalling
    /// a Marketplace plugin.</summary>
    [ObservableProperty] private bool _isSidebarCollapsed;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        _settingsService.Settings.Layout.IsSidebarCollapsed = IsSidebarCollapsed;
        _ = _settingsService.SaveAsync();
    }

    // --- Top bar: compact audio status (master volume/mute, mic passthrough) ---
    [ObservableProperty] private float _masterVolume = 1.0f;
    [ObservableProperty] private bool _masterMuted;
    [ObservableProperty] private bool _micPassthroughEnabled;

    partial void OnMasterVolumeChanged(float value)
    {
        _settingsService.Settings.Audio.GlobalVolume = value;
        _ = _settingsService.SaveAsync();
    }

    [RelayCommand]
    private void ToggleMasterMute()
    {
        MasterMuted = !MasterMuted;
        _settingsService.Settings.Audio.MasterMuted = MasterMuted;
        _ = _settingsService.SaveAsync();
    }

    // Only ever called from an explicit click, never from the InitializeAsync load below — a
    // full mic capture teardown/rebuild on every startup (if passthrough happened to already be
    // on) would be wasteful, and InitializeAsync already calls RefreshMicMonitoring() once of
    // its own accord after every other setting is loaded.
    [RelayCommand]
    private void ToggleMicPassthrough()
    {
        MicPassthroughEnabled = !MicPassthroughEnabled;
        _settingsService.Settings.Audio.EnableMicPassthrough = MicPassthroughEnabled;
        _ = _settingsService.SaveAsync();
        _audioEngine.RefreshMicMonitoring();
    }

    [ObservableProperty] private bool _voiceChangerEnabled;

    [ObservableProperty] private bool _pitchEnabled;
    [ObservableProperty] private double _voiceChangerPitchSemitones;

    [ObservableProperty] private bool _formantEnabled;
    [ObservableProperty] private double _formantShift;

    [ObservableProperty] private bool _robotEnabled;
    [ObservableProperty] private double _robotFrequencyHz;
    [ObservableProperty] private RobotWaveform _robotWaveform;
    [ObservableProperty] private double _robotMix;

    [ObservableProperty] private bool _distortionEnabled;
    [ObservableProperty] private double _distortionDrive;
    [ObservableProperty] private double _distortionMix;

    [ObservableProperty] private bool _overdriveEnabled;
    [ObservableProperty] private double _overdriveDrive;
    [ObservableProperty] private double _overdriveMix;

    [ObservableProperty] private bool _delayEnabled;
    [ObservableProperty] private double _delayMs;
    [ObservableProperty] private double _delayMix;

    [ObservableProperty] private bool _echoEnabled;
    [ObservableProperty] private double _echoDelayMs;
    [ObservableProperty] private double _echoFeedback;
    [ObservableProperty] private double _echoMix;

    [ObservableProperty] private bool _reverbEnabled;
    [ObservableProperty] private double _reverbRoomSize;
    [ObservableProperty] private double _reverbDecay;
    [ObservableProperty] private double _reverbMix;

    [ObservableProperty] private bool _proximityEnabled;
    [ObservableProperty] private double _proximityDistance;
    [ObservableProperty] private double _proximityMix;

    [ObservableProperty] private double _effectStrength;

    [ObservableProperty] private bool _isVoicePreviewActive;
    private bool _isLoadingVoiceChangerSettings;

    // --- Voice Changer tab navigation: Voices are the primary unit (not a single always-on
    // mixer) — the tab walks through create-empty-state -> Basic/Advanced chooser -> a list of
    // saved Voices -> one Voice's own editor. Exactly one of these four is ever true; all set
    // together by RefreshVoiceChangerViewState() so they can never fall out of sync with each
    // other or with SelectedVoice/IsCreatingVoice.
    [ObservableProperty] private VoiceChangerPreset? _selectedVoice;
    [ObservableProperty] private bool _isCreatingVoice;
    [ObservableProperty] private bool _showCreateEmptyState;
    [ObservableProperty] private bool _showVoiceModeChooser;
    [ObservableProperty] private bool _showVoiceList;
    [ObservableProperty] private bool _showVoiceEditor;

    /// <summary>Which Voice is the one actually processing your mic right now (subject to
    /// <see cref="VoiceChangerEnabled"/>) — drives the white active-border on its tile in the
    /// grid. Set by both <see cref="SelectVoice"/> (opening a Voice's editor) and
    /// <see cref="ActivateVoice"/> (a plain tile click, no editor).</summary>
    [ObservableProperty] private string? _activeVoiceId;

    public ObservableCollection<VoiceChangerPreset> VoiceChangerPresets { get; } = [];
    public Array RobotWaveforms => EnumBindingSource.GetValues<RobotWaveform>();

    private void RefreshVoiceChangerViewState()
    {
        ShowVoiceEditor = SelectedVoice is not null;
        ShowVoiceModeChooser = !ShowVoiceEditor && IsCreatingVoice;
        ShowVoiceList = !ShowVoiceEditor && !ShowVoiceModeChooser && VoiceChangerPresets.Count > 0;
        ShowCreateEmptyState = !ShowVoiceEditor && !ShowVoiceModeChooser && !ShowVoiceList;
    }

    public async Task InitializeAsync()
    {
        await _settingsService.LoadAsync().ConfigureAwait(true);
        await _libraryService.LoadAsync().ConfigureAwait(true);

        SearchQuery = _libraryService.Library.SearchQuery;
        SortMode = _libraryService.Library.SortMode;
        SelectedFolderId = _libraryService.Library.SelectedFolderId;
        IsSidebarCollapsed = _settingsService.Settings.Layout.IsSidebarCollapsed;
        ViewMode = _settingsService.Settings.Theme.ViewMode;

        var audioSettings = _settingsService.Settings.Audio;
        MasterVolume = audioSettings.GlobalVolume;
        MasterMuted = audioSettings.MasterMuted;
        MicPassthroughEnabled = audioSettings.EnableMicPassthrough;
        var settingsNeedSaving = false;

        // One-time migration for anyone updating from before the Plugin Marketplace existed:
        // Advanced Settings, Performance Mode, and Voice Changer were all always-visible before
        // they became Pro-gated plugins, so Pro users get them auto-installed rather than having
        // to go hunt for a toggle just to get back something they already had. A Free user who'd
        // been using one (e.g. during a trial) loses the sidebar button same as a fresh Free
        // install would, but nothing else changes. HasMigratedLegacyPlugins guards this from ever
        // re-running, so deliberately uninstalling a plugin later is never silently undone.
        var plugins = _settingsService.Settings.Plugins;
        if (!plugins.HasMigratedLegacyPlugins)
        {
            var installed = plugins.InstalledPluginIds;
            if (_licenseService.IsProUnlocked)
            {
                foreach (var id in new[] { PluginCatalog.AdvancedSettings, PluginCatalog.PerformanceMode, PluginCatalog.VoiceChanger })
                {
                    if (!installed.Contains(id)) installed.Add(id);
                }
            }

            plugins.HasMigratedLegacyPlugins = true;
            settingsNeedSaving = true;
        }

        if (settingsNeedSaving)
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }

        RefreshPluginState();

        _isLoadingVoiceChangerSettings = true;
        VoiceChangerEnabled = audioSettings.EnableVoiceChanger;
        PitchEnabled = audioSettings.PitchEnabled;
        VoiceChangerPitchSemitones = audioSettings.VoiceChangerPitchSemitones;
        FormantEnabled = audioSettings.FormantEnabled;
        FormantShift = audioSettings.FormantShift;
        RobotEnabled = audioSettings.RobotEnabled;
        RobotFrequencyHz = audioSettings.RobotFrequencyHz;
        RobotWaveform = audioSettings.RobotWaveform;
        RobotMix = audioSettings.RobotMix;
        DistortionEnabled = audioSettings.DistortionEnabled;
        DistortionDrive = audioSettings.DistortionDrive;
        DistortionMix = audioSettings.DistortionMix;
        OverdriveEnabled = audioSettings.OverdriveEnabled;
        OverdriveDrive = audioSettings.OverdriveDrive;
        OverdriveMix = audioSettings.OverdriveMix;
        DelayEnabled = audioSettings.DelayEnabled;
        DelayMs = audioSettings.DelayMs;
        DelayMix = audioSettings.DelayMix;
        EchoEnabled = audioSettings.EchoEnabled;
        EchoDelayMs = audioSettings.EchoDelayMs;
        EchoFeedback = audioSettings.EchoFeedback;
        EchoMix = audioSettings.EchoMix;
        ReverbEnabled = audioSettings.ReverbEnabled;
        ReverbRoomSize = audioSettings.ReverbRoomSize;
        ReverbDecay = audioSettings.ReverbDecay;
        ReverbMix = audioSettings.ReverbMix;
        ProximityEnabled = audioSettings.ProximityEnabled;
        ProximityDistance = audioSettings.ProximityDistance;
        ProximityMix = audioSettings.ProximityMix;
        EffectStrength = audioSettings.EffectStrength;
        _isLoadingVoiceChangerSettings = false;

        ActiveVoiceId = audioSettings.ActiveVoicePresetId;

        VoiceChangerPresets.Clear();
        foreach (var preset in audioSettings.VoiceChangerPresets)
        {
            VoiceChangerPresets.Add(preset);
        }

        // Live mic processing continues using whatever was last active (the flat fields above
        // already carry that forward) — but the tab itself always starts at the list/empty
        // state, not auto-resuming into whichever Voice's editor happened to be open last time.
        RefreshVoiceChangerViewState();

        _themeService.ApplyTheme(_settingsService.Settings);
        RegisterAllHotkeys();
        _audioEngine.RefreshMicMonitoring();
        _fileWatcher.Start();
        RefreshSounds();

        // Re-runs every installed Community Plugin's cached script so its tiles/panel buttons
        // are back for this session — resilient per-plugin internally, never blocks the rest of
        // startup if one script fails (see CommunityPluginRuntime.InitializeAsync).
        await _pluginRuntime.InitializeAsync().ConfigureAwait(true);

        var adminMessage = await _adminMessageService.GetMessageAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(adminMessage))
        {
            AdminMessageText = adminMessage;
            HasAdminMessage = true;
        }

        StatusMessage = $"Loaded {_libraryService.Library.Sounds.Count} sounds";

        // Land on Home rather than straight into the sound grid — the redesign brief's whole
        // point for this page (context/overview before the workspace), not just a first-run
        // thing.
        ShowHomeTab = true;
    }

    /// <summary>Rebuilds the bindable tile/panel-group collections from the runtime's current
    /// state — called once at startup and again every time a plugin is installed/uninstalled (see
    /// the PluginsChanged subscription in the constructor).</summary>
    private void RefreshPluginTilesAndPanels()
    {
        PluginTiles.Clear();
        foreach (var tile in _pluginRuntime.Tiles)
        {
            PluginTiles.Add(new PluginTileViewModel(tile, _notifications));
        }
        HasPluginTiles = PluginTiles.Count > 0;

        PluginPanelGroups.Clear();
        foreach (var group in _pluginRuntime.PanelGroups)
        {
            PluginPanelGroups.Add(new PluginPanelGroupViewModel
            {
                PluginName = group.PluginName,
                Buttons = group.Buttons.Select(b => new PluginPanelButtonViewModel(b, _notifications)).ToList()
            });
        }
        HasPluginPanels = PluginPanelGroups.Count > 0;

        // If the plugin that was showing got uninstalled mid-panel-view, fall back to the sound
        // grid rather than leaving the user on a now-empty panel.
        if (ShowPluginsPanelTab && !HasPluginPanels)
        {
            ShowPluginsPanelTab = false;
        }
    }

    [RelayCommand]
    private void ShowPluginsPanel()
    {
        ExitVoiceChangerTab();
        ExitHomeTab();
        ShowPluginsPanelTab = true;
    }

    private void ExitPluginsPanelTab() => ShowPluginsPanelTab = false;

    /// <summary>Recomputes which plugin-gated features are currently unlocked from the persisted
    /// installed-plugin list — called once at startup and again every time settings are saved
    /// (see the SettingsChanged subscription above), so installing/uninstalling from the Plugin
    /// Marketplace takes effect immediately without needing a restart.</summary>
    private void RefreshPluginState()
    {
        IsVoiceChangerInstalled = _settingsService.Settings.Plugins.InstalledPluginIds.Contains(PluginCatalog.VoiceChanger);
    }

    [RelayCommand]
    private void OpenPluginMarketplace()
    {
        var window = _services.GetRequiredService<PluginMarketplaceWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
        RefreshPluginState();
    }

    /// <summary>Fire-and-forget background check called once from App startup (only when
    /// CheckForUpdatesOnLaunch is enabled) — never awaited by the caller, so a slow or failed
    /// GitHub API call has zero effect on launch time. IUpdateService already swallows its own
    /// errors, so nothing here needs a try/catch.</summary>
    public async Task CheckForUpdatesInBackgroundAsync()
    {
        var update = await _updateService.CheckForUpdateAsync().ConfigureAwait(true);
        if (update is null) return;

        _pendingUpdate = update;
        UpdateVersionText = $"Version {update.Version} is available";
        UpdateAvailable = true;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;

        IsInstallingUpdate = true;
        try
        {
            var installerPath = await _updateService.DownloadInstallerAsync(_pendingUpdate).ConfigureAwait(true);

            // UseShellExecute lets Windows show its own SmartScreen prompt naturally (the app
            // isn't code-signed) rather than this process trying to bypass or suppress it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath) { UseShellExecute = true });

            // Shutdown so the installer isn't blocked by this process's own file locks on the
            // files it's about to overwrite.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsInstallingUpdate = false;
            _notifications.ShowError("Update failed", $"Couldn't download or launch the update: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable = false;
    }

    [RelayCommand]
    private void DismissAdminMessage()
    {
        HasAdminMessage = false;
    }

    [RelayCommand]
    private void ShowVoiceChangerView()
    {
        // Defense in depth beyond hiding the sidebar button — nothing else guarantees this
        // command can't be invoked another way (e.g. a future keyboard shortcut).
        if (!IsVoiceChangerInstalled) return;
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowVoiceChangerTab = true;
    }

    /// <summary>Shared by every "leave the Voice Changer tab" nav command — also stops a live
    /// Test Mic preview rather than leaving mic monitoring silently running after the user
    /// navigates away, and resets navigation back to the list/empty state so clicking into the
    /// tab again always starts there rather than resuming mid-edit or mid-creation.</summary>
    private void ExitVoiceChangerTab()
    {
        ShowVoiceChangerTab = false;
        SelectedVoice = null;
        IsCreatingVoice = false;
        RefreshVoiceChangerViewState();

        if (IsVoicePreviewActive)
        {
            IsVoicePreviewActive = false;
            _audioEngine.SetVoicePreviewEnabled(false);
        }
    }

    [RelayCommand]
    private void ToggleVoicePreview()
    {
        IsVoicePreviewActive = !IsVoicePreviewActive;
        _audioEngine.SetVoicePreviewEnabled(IsVoicePreviewActive);
    }

    [RelayCommand]
    private void SelectVoice(VoiceChangerPreset? voice)
    {
        if (voice is null) return;

        ApplyVoiceToLiveFields(voice);
        ActiveVoiceId = voice.Id;
        SelectedVoice = voice;
        RefreshVoiceChangerViewState();

        // Can change whether Pitch itself is enabled, which changes the chain topology (whether
        // the phase vocoder is in the chain at all), so this needs the full refresh, not the
        // lightweight path — every other step's enable/params ride along for free either way
        // since RefreshMicMonitoring rebuilds the whole chain from current settings.
        _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);
    }

    /// <summary>Left-clicking a tile — a per-tile on/off toggle, not a navigation action.
    /// Clicking the tile that's already active turns the whole changer off; clicking any other
    /// tile makes IT the active one instead (only one voice ever actually processes your mic).
    /// Never opens the editor — that's "Change Settings" via right-click (<see cref="SelectVoice"/>)
    /// only.</summary>
    [RelayCommand]
    private void ActivateVoice(VoiceChangerPreset? voice)
    {
        if (voice is null) return;

        if (VoiceChangerEnabled && ActiveVoiceId == voice.Id)
        {
            VoiceChangerEnabled = false; // Its own OnVoiceChangerEnabledChanged applies/saves this.
            return;
        }

        ApplyVoiceToLiveFields(voice);
        ActiveVoiceId = voice.Id;

        _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);
    }

    /// <summary>Copies a Voice's saved fields onto the live MainViewModel properties that
    /// actually drive the mic chain — shared by <see cref="SelectVoice"/> (open its editor) and
    /// <see cref="ActivateVoice"/> (just make it the active one, no navigation). Also forces the
    /// master switch on: it used to default to off with nothing to ever turn it on, which meant
    /// every control in the editor was silently disabled (IsEnabled bound to that checkbox)
    /// right after creating a voice — looked exactly like the editor didn't work at all. Wrapped
    /// in the same loading-guard as every field here so setting it doesn't ALSO fire its own
    /// partial-changed apply on top of the explicit one the caller does afterward.</summary>
    private void ApplyVoiceToLiveFields(VoiceChangerPreset voice)
    {
        _isLoadingVoiceChangerSettings = true;
        PitchEnabled = voice.PitchEnabled;
        VoiceChangerPitchSemitones = voice.PitchSemitones;
        FormantEnabled = voice.FormantEnabled;
        FormantShift = voice.FormantShift;
        RobotEnabled = voice.RobotEnabled;
        RobotFrequencyHz = voice.RobotFrequencyHz;
        RobotWaveform = voice.RobotWaveform;
        RobotMix = voice.RobotMix;
        DistortionEnabled = voice.DistortionEnabled;
        DistortionDrive = voice.DistortionDrive;
        DistortionMix = voice.DistortionMix;
        OverdriveEnabled = voice.OverdriveEnabled;
        OverdriveDrive = voice.OverdriveDrive;
        OverdriveMix = voice.OverdriveMix;
        DelayEnabled = voice.DelayEnabled;
        DelayMs = voice.DelayMs;
        DelayMix = voice.DelayMix;
        EchoEnabled = voice.EchoEnabled;
        EchoDelayMs = voice.EchoDelayMs;
        EchoFeedback = voice.EchoFeedback;
        EchoMix = voice.EchoMix;
        ReverbEnabled = voice.ReverbEnabled;
        ReverbRoomSize = voice.ReverbRoomSize;
        ReverbDecay = voice.ReverbDecay;
        ReverbMix = voice.ReverbMix;
        ProximityEnabled = voice.ProximityEnabled;
        ProximityDistance = voice.ProximityDistance;
        ProximityMix = voice.ProximityMix;
        EffectStrength = voice.EffectStrength;
        VoiceChangerEnabled = true;
        _isLoadingVoiceChangerSettings = false;
    }

    [RelayCommand]
    private void StartCreateVoice()
    {
        IsCreatingVoice = true;
        RefreshVoiceChangerViewState();
    }

    [RelayCommand]
    private void CancelCreateVoice()
    {
        IsCreatingVoice = false;
        RefreshVoiceChangerViewState();
    }

    [RelayCommand]
    private void BackToVoiceList()
    {
        SelectedVoice = null;
        RefreshVoiceChangerViewState();
    }

    [RelayCommand]
    private async Task CreateBasicVoiceAsync() => await CreateVoiceAsync(VoiceChangerMode.Basic).ConfigureAwait(true);

    [RelayCommand]
    private async Task CreateAdvancedVoiceAsync() => await CreateVoiceAsync(VoiceChangerMode.Advanced).ConfigureAwait(true);

    private async Task CreateVoiceAsync(VoiceChangerMode mode)
    {
        var dialog = new InputDialog("Name your voice", "Name:", string.Empty);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        var voice = new VoiceChangerPreset
        {
            Name = dialog.InputText.Trim(),
            Mode = mode,
            // Basic voices don't show step checkboxes at all — Pitch and Formant are just
            // always "on" (at whatever the two sliders say, 0 = neutral) so the editor can be
            // exactly those two sliders with nothing else to toggle.
            PitchEnabled = mode == VoiceChangerMode.Basic,
            FormantEnabled = mode == VoiceChangerMode.Basic
        };
        voice.Icon = VoiceIconPalette.PickDefault(voice.Id);

        _settingsService.Settings.Audio.VoiceChangerPresets.Add(voice);
        VoiceChangerPresets.Add(voice);
        IsCreatingVoice = false;
        await _settingsService.SaveAsync().ConfigureAwait(true);

        SelectVoice(voice);
    }

    [RelayCommand]
    private async Task RenameVoiceAsync(VoiceChangerPreset? voice)
    {
        if (voice is null) return;

        var dialog = new InputDialog("Rename voice", "Name:", voice.Name);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        // VoiceChangerPreset.Name is a real [ObservableProperty], so this alone updates the
        // bound tile immediately — no collection-replace trick needed.
        voice.Name = dialog.InputText.Trim();

        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ChangeVoiceIconAsync(VoiceChangerPreset? voice)
    {
        if (voice is null) return;

        var picker = new IconPickerWindow(voice.Icon) { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() != true || picker.SelectedIcon is null) return;

        // Icon is a real [ObservableProperty] too, same as Name — updates the bound tile
        // immediately.
        voice.Icon = picker.SelectedIcon;
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteVoiceAsync(VoiceChangerPreset? voice)
    {
        if (voice is null) return;

        var confirmed = System.Windows.MessageBox.Show(
            $"Permanently delete \"{voice.Name}\"?",
            "Delete voice",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmed != System.Windows.MessageBoxResult.Yes) return;

        var wasActive = ActiveVoiceId == voice.Id;

        _settingsService.Settings.Audio.VoiceChangerPresets.Remove(voice);
        VoiceChangerPresets.Remove(voice);

        if (ReferenceEquals(SelectedVoice, voice))
        {
            SelectedVoice = null;
        }

        // Deleting the voice currently processing your mic leaves nothing for it to be
        // processing — turn the changer off rather than silently keep running its now-orphaned
        // settings under a name that no longer exists anywhere in the UI (and no tile left to
        // show as active).
        if (wasActive)
        {
            VoiceChangerEnabled = false;
            ActiveVoiceId = null;
        }

        RefreshVoiceChangerViewState();
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    // Enabling/disabling the changer, or Pitch specifically, changes the chain topology (and
    // whether mic capture is needed at all) — Pitch is the one step that's a separate structural
    // wrap (the phase vocoder) rather than a live-toggleable flag inside the always-present
    // effect stack, see VoiceEffectStackProvider's remarks. Everything else below is a value
    // tweak (including every other step's own Enabled flag) on a chain that's already running.
    partial void OnVoiceChangerEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);

    partial void OnPitchEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);

    // These are all live slider/knob/checkbox tweaks on a chain that's already running. Routing
    // every one of them through a full RefreshMicMonitoring — which tears down and restarts the
    // actual WasapiCapture — made turning any knob itself sound glitchy, independent of anything
    // in the DSP's own correctness. They go through the lightweight in-place parameter update.
    partial void OnVoiceChangerPitchSemitonesChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnFormantEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnFormantShiftChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotFrequencyHzChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotWaveformChanged(RobotWaveform value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDistortionEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDistortionDriveChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDistortionMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnOverdriveEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnOverdriveDriveChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnOverdriveMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDelayEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDelayMsChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDelayMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoDelayMsChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoFeedbackChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnReverbEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnReverbRoomSizeChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnReverbDecayChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnReverbMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnProximityEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnProximityDistanceChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnProximityMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEffectStrengthChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    /// <summary>Unlike Settings window controls (which only take effect on its explicit Save),
    /// this sidebar panel applies immediately — matching every other main-window control.
    /// <paramref name="structuralChange"/> picks between a full mic-monitoring refresh (needed
    /// when the effect chain's topology itself changes) and a lightweight in-place parameter
    /// update (for everything that's just a value tweak on the chain that's already running) —
    /// see the remarks on <see cref="IAudioEngine.UpdateVoiceEffectParameters"/> for why that
    /// distinction matters for how the audio actually sounds while you're adjusting a slider.</summary>
    private async Task ApplyVoiceChangerSettingsAsync(bool structuralChange)
    {
        if (_isLoadingVoiceChangerSettings) return;

        var audioSettings = _settingsService.Settings.Audio;
        audioSettings.EnableVoiceChanger = VoiceChangerEnabled;
        audioSettings.PitchEnabled = PitchEnabled;
        audioSettings.VoiceChangerPitchSemitones = VoiceChangerPitchSemitones;
        audioSettings.FormantEnabled = FormantEnabled;
        audioSettings.FormantShift = FormantShift;
        audioSettings.RobotEnabled = RobotEnabled;
        audioSettings.RobotFrequencyHz = RobotFrequencyHz;
        audioSettings.RobotWaveform = RobotWaveform;
        audioSettings.RobotMix = RobotMix;
        audioSettings.DistortionEnabled = DistortionEnabled;
        audioSettings.DistortionDrive = DistortionDrive;
        audioSettings.DistortionMix = DistortionMix;
        audioSettings.OverdriveEnabled = OverdriveEnabled;
        audioSettings.OverdriveDrive = OverdriveDrive;
        audioSettings.OverdriveMix = OverdriveMix;
        audioSettings.DelayEnabled = DelayEnabled;
        audioSettings.DelayMs = DelayMs;
        audioSettings.DelayMix = DelayMix;
        audioSettings.EchoEnabled = EchoEnabled;
        audioSettings.EchoDelayMs = EchoDelayMs;
        audioSettings.EchoFeedback = EchoFeedback;
        audioSettings.EchoMix = EchoMix;
        audioSettings.ReverbEnabled = ReverbEnabled;
        audioSettings.ReverbRoomSize = ReverbRoomSize;
        audioSettings.ReverbDecay = ReverbDecay;
        audioSettings.ReverbMix = ReverbMix;
        audioSettings.ProximityEnabled = ProximityEnabled;
        audioSettings.ProximityDistance = ProximityDistance;
        audioSettings.ProximityMix = ProximityMix;
        audioSettings.EffectStrength = EffectStrength;
        audioSettings.ActiveVoicePresetId = ActiveVoiceId;

        // Mirrors every live edit straight back into the Voice's own saved fields — since
        // SelectedVoice is the same object reference stored in audioSettings.VoiceChangerPresets
        // (not a copy), this keeps that Voice's persisted data in sync with whatever you're
        // hearing live as you drag its sliders, with no separate "save" step of its own.
        if (SelectedVoice is { } voice)
        {
            voice.PitchEnabled = PitchEnabled;
            voice.PitchSemitones = VoiceChangerPitchSemitones;
            voice.FormantEnabled = FormantEnabled;
            voice.FormantShift = FormantShift;
            voice.RobotEnabled = RobotEnabled;
            voice.RobotFrequencyHz = RobotFrequencyHz;
            voice.RobotWaveform = RobotWaveform;
            voice.RobotMix = RobotMix;
            voice.DistortionEnabled = DistortionEnabled;
            voice.DistortionDrive = DistortionDrive;
            voice.DistortionMix = DistortionMix;
            voice.OverdriveEnabled = OverdriveEnabled;
            voice.OverdriveDrive = OverdriveDrive;
            voice.OverdriveMix = OverdriveMix;
            voice.DelayEnabled = DelayEnabled;
            voice.DelayMs = DelayMs;
            voice.DelayMix = DelayMix;
            voice.EchoEnabled = EchoEnabled;
            voice.EchoDelayMs = EchoDelayMs;
            voice.EchoFeedback = EchoFeedback;
            voice.EchoMix = EchoMix;
            voice.ReverbEnabled = ReverbEnabled;
            voice.ReverbRoomSize = ReverbRoomSize;
            voice.ReverbDecay = ReverbDecay;
            voice.ReverbMix = ReverbMix;
            voice.ProximityEnabled = ProximityEnabled;
            voice.ProximityDistance = ProximityDistance;
            voice.ProximityMix = ProximityMix;
            voice.EffectStrength = EffectStrength;
        }

        // notifyChanged: false — this method already explicitly refreshes exactly what's
        // needed below (either path). Letting the save also broadcast SettingsChanged would
        // ADDITIONALLY trigger AudioEngine's own subscriber, which always does a full mic-capture
        // teardown/rebuild — silently overriding the lightweight path this method just chose and
        // restarting the physical mic capture on every single parameter tweak, which is what was
        // actually causing the reported clicking.
        await _settingsService.SaveAsync(notifyChanged: false).ConfigureAwait(true);

        if (structuralChange)
        {
            _audioEngine.RefreshMicMonitoring();
        }
        else
        {
            _audioEngine.UpdateVoiceEffectParameters();
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _libraryService.Library.SearchQuery = value;
        RefreshSounds();
        _ = _libraryService.SaveAsync();
    }

    partial void OnSortModeChanged(SortMode value)
    {
        _libraryService.Library.SortMode = value;
        RefreshSounds();
        _ = _libraryService.SaveAsync();
        RaiseNavActiveStatesChanged();
    }

    partial void OnShowFavoritesChanged(bool value) => RaiseNavActiveStatesChanged();
    partial void OnShowRecentChanged(bool value) => RaiseNavActiveStatesChanged();

    partial void OnSelectedFolderIdChanged(string? value)
    {
        _libraryService.Library.SelectedFolderId = value;
        ShowFavorites = false;
        ShowRecent = false;
        RefreshSounds();
        _ = _libraryService.SaveAsync();
    }

    [RelayCommand]
    private async Task ImportSoundsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Audio Files|*.mp3;*.wav;*.ogg;*.flac",
            Multiselect = true,
            Title = "Import Sounds"
        };

        if (dialog.ShowDialog() != true) return;
        await ImportPathsAsync(dialog.FileNames).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportDroppedFilesAsync(string[]? paths)
    {
        if (paths is null || paths.Length == 0) return;
        await ImportPathsAsync(paths).ConfigureAwait(true);
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var pathList = paths as IReadOnlyList<string> ?? paths.ToList();

        var maxSounds = _licenseService.MaxSounds;
        if (maxSounds.HasValue)
        {
            var remainingSlots = maxSounds.Value - _libraryService.Library.Sounds.Count;
            if (remainingSlots <= 0)
            {
                // A hard block, not a routine notification — shown regardless of the
                // notifications toggle, since suppressing this would leave the click looking
                // like it silently did nothing.
                var dialog = new UpgradeToProDialog("Sound library full", $"Free is limited to {maxSounds.Value} sounds.") { Owner = Application.Current.MainWindow };
                dialog.ShowDialog();
                return;
            }

            if (pathList.Count > remainingSlots)
            {
                if (_settingsService.Settings.Notifications.OnImport)
                {
                    _notifications.ShowInfo("Import limited", $"Free tier is limited to {maxSounds.Value} sounds — importing the first {remainingSlots} file(s) only.");
                }

                pathList = pathList.Take(remainingSlots).ToList();
            }
        }

        var imported = await _libraryService.ImportFilesAsync(pathList).ConfigureAwait(true);
        RefreshSounds();
        if (_settingsService.Settings.Notifications.OnImport)
        {
            _notifications.ShowSuccess("Import complete", $"{imported.Count} sound(s) added.");
        }
        StatusMessage = $"{_libraryService.Library.Sounds.Count} sounds in library";
        RegisterAllHotkeys();
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _playbackManager.StopAllAsync().ConfigureAwait(true);
        StatusMessage = "Stopped all sounds";
    }

    private static readonly Random RandomSoundPicker = new();

    [RelayCommand]
    private async Task PlayRandomSoundAsync()
    {
        var sounds = _libraryService.Library.Sounds;
        if (sounds.Count == 0)
        {
            StatusMessage = "No sounds to pick from";
            return;
        }

        var sound = sounds[RandomSoundPicker.Next(sounds.Count)];
        await _playbackManager.PlaySoundAsync(sound.Id).ConfigureAwait(true);
        StatusMessage = $"Random: {sound.GetDisplayName()}";
    }

    [RelayCommand]
    private async Task PlaySoundPartyAsync()
    {
        var sounds = _libraryService.Library.Sounds;
        if (sounds.Count == 0)
        {
            StatusMessage = "No sounds to pick from";
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            var sound = sounds[RandomSoundPicker.Next(sounds.Count)];
            await _playbackManager.PlaySoundAsync(sound.Id).ConfigureAwait(true);
        }
        StatusMessage = "🎉 Sound Party!";
    }

    [RelayCommand]
    private async Task PlayFirstSoundAsync()
    {
        var sounds = _libraryService.Library.Sounds;
        if (sounds.Count == 0)
        {
            StatusMessage = "No sounds to pick from";
            return;
        }

        var first = sounds.OrderBy(s => s.GetDisplayName(), StringComparer.OrdinalIgnoreCase).First();
        await _playbackManager.PlaySoundAsync(first.Id).ConfigureAwait(true);
        StatusMessage = $"First (A-Z): {first.GetDisplayName()}";
    }

    [RelayCommand]
    private void ShowSoundCount()
    {
        StatusMessage = $"You have {_libraryService.Library.Sounds.Count} sounds in your library.";
    }

    /// <summary>The overlay window is a DI singleton shown/hidden rather than recreated per
    /// toggle (see QuickPlayOverlayWindow's own remarks for why) — resolving it from DI here just
    /// hands back the same instance every time.</summary>
    private void ToggleQuickPlayOverlay()
    {
        var overlay = _services.GetRequiredService<QuickPlayOverlayWindow>();
        if (overlay.IsVisible)
        {
            overlay.Hide();
        }
        else
        {
            overlay.ShowNearCursor();
        }
    }

    [RelayCommand]
    private void ShowSettings()
    {
        var window = _services.GetRequiredService<SettingsWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
        RegisterAllHotkeys();
        RefreshSounds();
    }

    public bool IsLoggedIn => _sessionService.IsLoggedIn;
    public string AccountButtonText => _sessionService.CurrentProfile?.Username ?? "Log In";

    /// <summary>Shown in the sidebar footer — same source as AccountViewModel's own VersionText.</summary>
    public string AppVersionText => $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown"}";

    private void RaiseAccountSummaryChanged()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(AccountButtonText));
    }

    [RelayCommand]
    private void OpenAccount()
    {
        if (_sessionService.IsLoggedIn)
        {
            var accountWindow = _services.GetRequiredService<AccountWindow>();
            accountWindow.Owner = Application.Current.MainWindow;
            accountWindow.ShowDialog();
        }
        else
        {
            var authWindow = _services.GetRequiredService<Views.Auth.AuthWindow>();
            authWindow.Owner = Application.Current.MainWindow;
            authWindow.ShowDialog();
        }
    }

    // Library toolbar's favorites-filter toggle: flips between the favorites-only view and the
    // full library, reusing the exact same state transitions as the sidebar's Home/Favorites
    // buttons rather than a bespoke on/off path, so both entry points stay in sync.
    [RelayCommand]
    private void ToggleFavoritesFilter()
    {
        if (ShowFavorites) ShowAllSounds();
        else ShowFavoritesView();
    }

    [RelayCommand]
    private void ShowFavoritesView()
    {
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowFavorites = true;
        ShowRecent = false;
        SelectedFolderId = null;
        RefreshSounds();
    }

    [RelayCommand]
    private void ShowRecentView()
    {
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowRecent = true;
        ShowFavorites = false;
        SelectedFolderId = null;
        RefreshSounds();
    }

    [RelayCommand]
    private void ShowMostPlayedView()
    {
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowFavorites = false;
        ShowRecent = false;
        SelectedFolderId = null;
        SortMode = SortMode.MostPlayed; // OnSortModeChanged persists + calls RefreshSounds()
    }

    [RelayCommand]
    private void ShowAllSounds()
    {
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowFavorites = false;
        ShowRecent = false;
        SelectedFolderId = null;
        RefreshSounds();
    }

    [RelayCommand]
    private void ShowFolder(SoundFolder? folder)
    {
        if (folder is null) return;
        ExitVoiceChangerTab();
        ExitPluginsPanelTab();
        ExitHomeTab();
        ShowFavorites = false;
        ShowRecent = false;
        SelectedFolderId = folder.Id;
        RefreshSounds();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var maxFolders = _licenseService.MaxFolders;
        if (maxFolders.HasValue && _libraryService.Library.Folders.Count >= maxFolders.Value)
        {
            var upgradeDialog = new UpgradeToProDialog("Folder limit reached", $"Free is limited to {maxFolders.Value} folder{(maxFolders.Value == 1 ? "" : "s")}.") { Owner = Application.Current.MainWindow };
            upgradeDialog.ShowDialog();
            return;
        }

        var dialog = new InputDialog("New Folder", "Folder name:", string.Empty);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        _libraryService.Library.Folders.Add(new SoundFolder
        {
            Name = dialog.InputText.Trim(),
            SortOrder = _libraryService.Library.Folders.Count
        });

        await _libraryService.SaveAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(Folders));
        StatusMessage = $"Created folder \"{dialog.InputText.Trim()}\"";
    }

    /// <summary>Home dashboard quick action — same .sbpack import SettingsViewModel's own Import
    /// Collection button uses. Doesn't need to manually refresh anything afterward: importing
    /// mutates the library, which raises ILibraryService.LibraryChanged, which this VM already
    /// subscribes to (see constructor) to call RefreshSounds().</summary>
    [RelayCommand]
    private async Task ImportCollectionAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Soundboard Collection|*.sbpack"
        };

        if (dialog.ShowDialog() != true) return;

        await _collectionExport.ImportCollectionAsync(dialog.FileName).ConfigureAwait(true);
        StatusMessage = "Collection imported";
    }

    /// <summary>
    /// Deletes a folder. Sounds that were in it move to Unfiled rather than being deleted —
    /// invoked from a code-behind Click handler on the folder's context menu, same as
    /// MoveSoundToFolderAsync, so this is a plain method rather than a [RelayCommand].
    /// </summary>
    public async Task DeleteFolderAsync(SoundFolder folder)
    {
        await _libraryService.RemoveFolderAsync(folder.Id).ConfigureAwait(true);
        if (SelectedFolderId == folder.Id)
        {
            SelectedFolderId = null;
        }

        OnPropertyChanged(nameof(Folders));
        RefreshSounds();
        StatusMessage = $"Deleted folder \"{folder.Name}\"";
    }

    [RelayCommand]
    private async Task RescanLibraryAsync()
    {
        await _libraryService.RescanSoundsFolderAsync().ConfigureAwait(true);
        RefreshSounds();
        StatusMessage = "Library rescanned";
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(SoundButtonViewModel? button)
    {
        if (button is null) return;
        button.Sound.IsFavorite = !button.Sound.IsFavorite;
        await _libraryService.SaveAsync().ConfigureAwait(true);
        RefreshSounds();
    }

    [RelayCommand]
    private async Task DeleteSoundAsync(SoundButtonViewModel? button)
    {
        if (button is null) return;
        await _playbackManager.StopSoundAsync(button.Sound.Id).ConfigureAwait(true);
        _hotkeyManager.UnregisterSoundHotkey(button.Sound.Id);
        await _libraryService.RemoveSoundAsync(button.Sound.Id).ConfigureAwait(true);
        RefreshSounds();
    }

    [RelayCommand]
    private async Task RenameSoundAsync(SoundButtonViewModel? button)
    {
        if (button is null) return;
        var dialog = new InputDialog("Rename Sound", "Enter a new name:", button.DisplayName);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            await _libraryService.RenameSoundAsync(button.Sound.Id, dialog.InputText).ConfigureAwait(true);
            button.NotifyDisplayNameChanged();
            RefreshSounds();
        }
    }

    /// <summary>
    /// Moves a sound into a folder (or back to Unfiled if folderId is null). Called directly
    /// from MainWindow's dynamically-built "Move to Folder" submenu — a plain method rather
    /// than a [RelayCommand] since it's invoked from a code-behind Click handler, not a binding.
    /// </summary>
    public async Task MoveSoundToFolderAsync(SoundButtonViewModel button, string? folderId)
    {
        await _libraryService.SetSoundFolderAsync(button.Sound.Id, folderId).ConfigureAwait(true);
        RefreshSounds();
    }

    /// <summary>
    /// Sets (or clears, if hotkey is null) a sound's individual hotkey. Called directly from
    /// MainWindow's context menu code-behind, same as MoveSoundToFolderAsync — a plain method
    /// rather than a [RelayCommand] since it's invoked from a Click handler, not a binding.
    /// </summary>
    public async Task SetSoundHotkeyAsync(SoundButtonViewModel button, HotkeyBinding? hotkey)
    {
        await _libraryService.SetSoundHotkeyAsync(button.Sound.Id, hotkey).ConfigureAwait(true);

        if (hotkey is null)
        {
            _hotkeyManager.UnregisterSoundHotkey(button.Sound.Id);
        }
        else
        {
            _hotkeyManager.RegisterSoundHotkey(button.Sound);
        }

        button.NotifyHotkeyChanged();
    }

    /// <summary>
    /// Sets (or clears, if null) a sound's output-route override. Same plain-method pattern as
    /// MoveSoundToFolderAsync/SetSoundHotkeyAsync — invoked from MainWindow's context menu
    /// code-behind, not a binding.
    /// </summary>
    public async Task SetSoundOutputRouteOverrideAsync(SoundButtonViewModel button, OutputRoute? route)
    {
        await _libraryService.SetSoundOutputRouteOverrideAsync(button.Sound.Id, route).ConfigureAwait(true);
        button.NotifyRouteChanged();
    }

    /// <summary>Same plain-method pattern as the other per-sound setters above — invoked from
    /// the Sound Details panel's volume slider on drag-end (not on every tick), so this doesn't
    /// hit disk on every pixel of slider movement.</summary>
    public async Task SetSoundVolumeAsync(SoundButtonViewModel button, float volume)
    {
        await _libraryService.SetSoundVolumeAsync(button.Sound.Id, volume).ConfigureAwait(true);
    }

    public async Task SetSoundPlaybackModeAsync(SoundButtonViewModel button, PlaybackMode mode)
    {
        await _libraryService.SetSoundPlaybackModeAsync(button.Sound.Id, mode).ConfigureAwait(true);
    }

    /// <summary>Parses the Details panel's comma-separated tags text box. Saving even an
    /// unchanged value is harmless (SetSoundTagsAsync is idempotent) — simpler than tracking
    /// dirty state for a field this low-stakes.</summary>
    public async Task SetSoundTagsAsync(SoundButtonViewModel button, string tagsText)
    {
        var tags = tagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await _libraryService.SetSoundTagsAsync(button.Sound.Id, tags).ConfigureAwait(true);
    }

    // --- Sound Details panel ---

    [ObservableProperty] private SoundButtonViewModel? _selectedDetailsSound;

    public bool ShowDetailsPanel => SelectedDetailsSound is not null;

    [ObservableProperty] private float[] _detailsWaveformPeaks = [];
    [ObservableProperty] private bool _isLoadingWaveform;

    private CancellationTokenSource? _waveformCts;

    partial void OnSelectedDetailsSoundChanged(SoundButtonViewModel? value)
    {
        OnPropertyChanged(nameof(ShowDetailsPanel));
        _ = LoadDetailsWaveformAsync(value);
    }

    [RelayCommand]
    private void ShowSoundDetails(SoundButtonViewModel? button) => SelectedDetailsSound = button;

    [RelayCommand]
    private void CloseSoundDetails() => SelectedDetailsSound = null;

    private async Task LoadDetailsWaveformAsync(SoundButtonViewModel? button)
    {
        _waveformCts?.Cancel();
        DetailsWaveformPeaks = [];

        if (button is null) return;

        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        IsLoadingWaveform = true;
        try
        {
            var path = _libraryService.GetSoundFilePath(button.Sound);
            var peaks = await WaveformExtractor.ExtractPeaksAsync(path, 120, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                DetailsWaveformPeaks = peaks;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoadingWaveform = false;
        }
    }

    [RelayCommand]
    private async Task PlaySoundAsync(SoundButtonViewModel? button)
    {
        if (button is null) return;
        await _playbackManager.PlaySoundAsync(button.Sound.Id).ConfigureAwait(true);
        ActivePlaybackCount = _playbackManager.ActiveInstances.Count;
        StatusMessage = $"Playing: {button.DisplayName}";
    }

    /// <summary>Called from MainWindow's drag-drop handlers after a tile is dropped onto (or
    /// past the end of) another tile. Moves it within the currently-visible list immediately for
    /// instant visual feedback, then persists the new order.</summary>
    public void ReorderSound(SoundButtonViewModel dragged, SoundButtonViewModel? target)
    {
        if (ReferenceEquals(dragged, target)) return;

        var oldIndex = VisibleSounds.IndexOf(dragged);
        if (oldIndex < 0) return;

        var newIndex = target is null ? VisibleSounds.Count - 1 : VisibleSounds.IndexOf(target);
        if (newIndex < 0) return;

        VisibleSounds.Move(oldIndex, newIndex);

        _ = PersistReorderAsync();
    }

    /// <summary>notifyChanged: false on the save below — this method already handles its own
    /// refresh explicitly (via the SortMode switch, or the direct RefreshSounds() call at the
    /// end), so letting the save ALSO broadcast LibraryChanged would run MainViewModel's own
    /// LibraryChanged subscriber first, before SortMode had been switched to Custom, rebuilding
    /// VisibleSounds from the OLD sort mode and reverting the drop that was just made.</summary>
    private async Task PersistReorderAsync()
    {
        await _libraryService.ReorderSoundsAsync(VisibleSounds.Select(b => b.Sound.Id).ToList(), notifyChanged: false).ConfigureAwait(true);

        if (SortMode != SortMode.Custom)
        {
            SortMode = SortMode.Custom;
        }
    }

    public void RefreshSounds()
    {
        // Computed first and deliberately: if the selected tag filter no longer exists anywhere
        // (its last sound was retagged/deleted), this resets SelectedTagFilter to null — which
        // re-enters this whole method via OnSelectedTagFilterChanged. Doing that BEFORE querying/
        // populating VisibleSounds means the one-level re-entrant call does all the real work
        // once, correctly, and this outer call's own query below just runs on the now-current
        // filter instead of also doing (and then discarding) a pass with the stale one.
        RefreshAllTags();

        var sounds = _libraryService.GetFilteredSounds(SelectedFolderId, SearchQuery, ShowFavorites, ShowRecent);

        // Tag filter layers on top of whatever the folder/search/favorites/recent query already
        // narrowed down to, rather than replacing it — "favorite sounds tagged 'rage'" makes
        // sense; the library service's own filter stays folder/search/favorites/recent-only so
        // this doesn't need a signature change there.
        if (!string.IsNullOrEmpty(SelectedTagFilter))
        {
            sounds = sounds.Where(s => s.Tags.Contains(SelectedTagFilter, StringComparer.OrdinalIgnoreCase));
        }

        VisibleSounds.Clear();

        foreach (var sound in sounds)
        {
            VisibleSounds.Add(GetOrCreateSoundButton(sound));
        }

        ActivePlaybackCount = _playbackManager.ActiveInstances.Count;

        // Folders is a computed property (recreates its ObservableCollection from
        // _libraryService.Library.Folders on every access) rather than an [ObservableProperty],
        // so WPF only re-reads it when explicitly told to. RefreshSounds() already runs at
        // startup right after the library loads, and after every mutation that could change
        // the folder list — so this is the one place that needs to raise it.
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(DetailsFolderOptions));

        RefreshHomeLists();
    }

    private void RefreshAllTags()
    {
        var tags = _libraryService.Library.Sounds
            .SelectMany(s => s.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);

        AllTags.Clear();
        foreach (var tag in tags)
        {
            AllTags.Add(tag);
        }

        // The filter dropdown's selection can outlive the tag it pointed to (e.g. the last sound
        // with that tag got retagged/deleted) — clear it rather than silently keep filtering by
        // a tag that no longer exists anywhere, which would just look like the library went empty.
        if (SelectedTagFilter is not null && !AllTags.Contains(SelectedTagFilter))
        {
            SelectedTagFilter = null;
        }
    }

    /// <summary>Shared with RefreshSounds() so the Home dashboard's Recently Played/Favorites
    /// tiles are the SAME SoundButtonViewModel instances shown in the main grid (via
    /// _buttonCache) — play/favorite state stays in sync automatically instead of needing its
    /// own separate refresh path.</summary>
    private SoundButtonViewModel GetOrCreateSoundButton(SoundItem sound)
    {
        if (!_buttonCache.TryGetValue(sound.Id, out var vm))
        {
            vm = new SoundButtonViewModel(sound, _playbackManager, _libraryService);
            _buttonCache[sound.Id] = vm;
        }

        return vm;
    }

    /// <summary>Top 8 of each — a dashboard widget, not a full browsing view (those already
    /// exist as their own sidebar destinations). Called from RefreshSounds() so every library
    /// mutation (import, favorite toggle, delete, playback bumping recency) keeps these in sync
    /// without a separate event subscription.</summary>
    private void RefreshHomeLists()
    {
        RecentlyPlayedHome.Clear();
        foreach (var sound in _libraryService.GetFilteredSounds(null, string.Empty, favoritesOnly: false, recentOnly: true).Take(8))
        {
            RecentlyPlayedHome.Add(GetOrCreateSoundButton(sound));
        }

        FavoritesHome.Clear();
        foreach (var sound in _libraryService.GetFilteredSounds(null, string.Empty, favoritesOnly: true, recentOnly: false).Take(8))
        {
            FavoritesHome.Add(GetOrCreateSoundButton(sound));
        }
    }

    /// <summary>Adds/removes ActivePlaybackItems entries to match ActiveInstances, preserving
    /// existing item VMs for still-playing sounds (rebuilding them every tick would flicker and
    /// lose nothing-to-lose-but-still-wasteful UI state) — paused state and duration are cheap
    /// to just re-set on every call; position/duration ticks arrive separately via
    /// PlaybackProgress.</summary>
    private void RefreshActivePlaybackItems()
    {
        var active = _playbackManager.ActiveInstances;
        var activeIds = active.Select(i => i.InstanceId).ToHashSet();

        for (var i = ActivePlaybackItems.Count - 1; i >= 0; i--)
        {
            if (!activeIds.Contains(ActivePlaybackItems[i].InstanceId))
            {
                ActivePlaybackItems.RemoveAt(i);
            }
        }

        foreach (var instance in active)
        {
            if (ActivePlaybackItems.Any(item => item.InstanceId == instance.InstanceId)) continue;

            var sound = _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == instance.SoundId);
            ActivePlaybackItems.Add(new ActivePlaybackItemViewModel(instance.InstanceId, sound?.GetDisplayName() ?? "Unknown", _audioEngine));
        }

        foreach (var item in ActivePlaybackItems)
        {
            var instance = active.FirstOrDefault(i => i.InstanceId == item.InstanceId);
            if (instance is not null)
            {
                item.IsPaused = instance.State == PlaybackState.Paused;
                item.SetProgress(item.Position, instance.DurationSeconds);
            }
        }

        HasActivePlayback = ActivePlaybackItems.Count > 0;
    }

    private void UpdatePlayingStates()
    {
        RefreshActivePlaybackItems();
        ActivePlaybackCount = _playbackManager.ActiveInstances.Count;
        foreach (var button in VisibleSounds)
        {
            var active = _playbackManager.ActiveInstances.FirstOrDefault(i => i.SoundId == button.Sound.Id);
            button.IsPlaying = active is not null;

            // Progress is only ever pushed forward by the PlaybackProgress tick handler above,
            // which stops firing for a sound the moment it's no longer active — nothing else
            // ever clears it back to 0, so without this the bar just stays wherever it was
            // (often full) instead of resetting once playback actually stops.
            if (active is null)
            {
                button.Progress = 0;
            }
        }

        // Show whichever instance is still active from what was previously shown, so the bar
        // doesn't jump to a different sound just because a second one started playing — falls
        // back to the most recent active instance if what was showing has stopped.
        var stillActive = _playbackManager.ActiveInstances.FirstOrDefault(i => i.InstanceId == NowPlayingInstanceId);
        var current = stillActive ?? _playbackManager.ActiveInstances.LastOrDefault();

        if (current is null)
        {
            HasNowPlaying = false;
            NowPlayingInstanceId = null;
            NowPlayingName = string.Empty;
            RaiseOtherActivePlaybackChanged();
            return;
        }

        HasNowPlaying = true;
        NowPlayingInstanceId = current.InstanceId;
        NowPlayingIsPaused = current.State == PlaybackState.Paused;
        NowPlayingDuration = current.DurationSeconds;

        var sound = VisibleSounds.FirstOrDefault(b => b.Sound.Id == current.SoundId)?.Sound
                    ?? _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == current.SoundId);
        NowPlayingName = sound?.Name ?? "Unknown";
        RaiseOtherActivePlaybackChanged();
    }

    private void RaiseOtherActivePlaybackChanged()
    {
        OnPropertyChanged(nameof(OtherActivePlaybackItems));
        OnPropertyChanged(nameof(HasOtherActivePlayback));
    }

    [RelayCommand]
    private async Task NowPlayingTogglePauseAsync()
    {
        if (NowPlayingInstanceId is not { } instanceId) return;

        // No manual NowPlayingIsPaused flip here — AudioEngine fires PlaybackStateChanged
        // synchronously before PauseAsync/ResumeAsync return, which reaches UpdatePlayingStates
        // (via PlaybackManager's ActiveInstancesChanged) and already sets NowPlayingIsPaused to
        // the real engine state by the time this await completes. Toggling it again here used to
        // stomp that correct value back to the opposite of reality — audio would actually resume,
        // but the UI kept showing "paused", so the next click paused it right back.
        if (NowPlayingIsPaused)
        {
            await _audioEngine.ResumeAsync(instanceId).ConfigureAwait(true);
        }
        else
        {
            await _audioEngine.PauseAsync(instanceId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NowPlayingRestartAsync()
    {
        if (NowPlayingInstanceId is { } instanceId)
        {
            await _audioEngine.RestartAsync(instanceId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NowPlayingSeekForwardAsync()
    {
        if (NowPlayingInstanceId is { } instanceId)
        {
            await _audioEngine.SeekAsync(instanceId, 10).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NowPlayingSeekBackAsync()
    {
        if (NowPlayingInstanceId is { } instanceId)
        {
            await _audioEngine.SeekAsync(instanceId, -10).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NowPlayingSeekToAsync(double positionSeconds)
    {
        if (NowPlayingInstanceId is { } instanceId)
        {
            await _audioEngine.SeekToAsync(instanceId, positionSeconds).ConfigureAwait(true);

            // Ticks are pushed by the audio engine's own progress callback, which only fires on
            // its regular interval — without this the bar would visibly snap back to the old
            // position for a moment after a click, before the next tick catches up.
            NowPlayingPosition = positionSeconds;
        }
    }

    [RelayCommand]
    private async Task NowPlayingStopAsync()
    {
        if (NowPlayingInstanceId is { } instanceId)
        {
            await _audioEngine.StopAsync(instanceId).ConfigureAwait(true);
        }
    }

    private void RegisterAllHotkeys()
    {
        _hotkeyManager.RegisterGlobalHotkeys(_settingsService.Settings.GlobalHotkeys);
        foreach (var sound in _libraryService.Library.Sounds)
        {
            if (sound.Hotkey is not null)
            {
                _hotkeyManager.RegisterSoundHotkey(sound);
            }
        }
    }

    public async Task SaveLayoutAsync(Window window)
    {
        var layout = _settingsService.Settings.Layout;
        if (window.WindowState == WindowState.Maximized)
        {
            layout.IsMaximized = true;
        }
        else
        {
            layout.IsMaximized = false;
            layout.WindowLeft = window.Left;
            layout.WindowTop = window.Top;
            layout.WindowWidth = window.Width;
            layout.WindowHeight = window.Height;
        }

        await _settingsService.SaveAsync().ConfigureAwait(false);
    }

    public void RestoreLayout(Window window)
    {
        var layout = _settingsService.Settings.Layout;
        window.Width = layout.WindowWidth;
        window.Height = layout.WindowHeight;

        if (layout.WindowLeft is { } left && layout.WindowTop is { } top)
        {
            window.Left = left;
            window.Top = top;
        }

        if (layout.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}

/// <summary>Display label paired with the OutputRoute value it sets — used to drive the Sound
/// Details panel's route ComboBox via DisplayMemberPath/SelectedValuePath, since a nullable enum
/// (null = "use the default") doesn't work cleanly with EnumBindingSource/EnumToBoolConverter.</summary>
public sealed record RouteOption(string Label, OutputRoute? Route);

/// <summary>Display label paired with a folder id (null = Unfiled) — same reasoning as
/// RouteOption, for the Sound Details panel's folder ComboBox.</summary>
public sealed record FolderOption(string? Id, string Name);

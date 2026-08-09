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
        IUpdateService updateService)
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
        _updateService = updateService;

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
            Application.Current?.Dispatcher.Invoke(() => _themeService.ApplyTheme(_settingsService.Settings));
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
                    VoiceChangerEnabled = !VoiceChangerEnabled;
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

    // --- Now Playing bar ---
    [ObservableProperty] private string? _nowPlayingInstanceId;
    [ObservableProperty] private string _nowPlayingName = string.Empty;
    [ObservableProperty] private double _nowPlayingPosition;
    [ObservableProperty] private double _nowPlayingDuration;
    [ObservableProperty] private bool _nowPlayingIsPaused;
    [ObservableProperty] private bool _hasNowPlaying;

    // --- Sidebar: Library / Voice Changer tab switch ---
    [ObservableProperty] private bool _showVoiceChangerTab;
    [ObservableProperty] private bool _voiceChangerEnabled;
    [ObservableProperty] private VoiceEffectType _voiceEffectType;
    [ObservableProperty] private double _voiceChangerPitchSemitones;
    [ObservableProperty] private double _robotFrequencyHz;
    [ObservableProperty] private RobotWaveform _robotWaveform;
    [ObservableProperty] private double _robotMix;
    [ObservableProperty] private double _echoDelayMs;
    [ObservableProperty] private double _echoFeedback;
    [ObservableProperty] private double _echoMix;
    [ObservableProperty] private double _distortionDrive;
    [ObservableProperty] private double _distortionMix;
    [ObservableProperty] private double _formantShift;
    [ObservableProperty] private bool _isVoicePreviewActive;
    private bool _isLoadingVoiceChangerSettings;

    public ObservableCollection<VoiceChangerPreset> VoiceChangerPresets { get; } = [];
    public Array VoiceEffectTypes => EnumBindingSource.GetValues<VoiceEffectType>();
    public Array RobotWaveforms => EnumBindingSource.GetValues<RobotWaveform>();

    public async Task InitializeAsync()
    {
        await _settingsService.LoadAsync().ConfigureAwait(true);
        await _libraryService.LoadAsync().ConfigureAwait(true);

        SearchQuery = _libraryService.Library.SearchQuery;
        SortMode = _libraryService.Library.SortMode;
        SelectedFolderId = _libraryService.Library.SelectedFolderId;

        var audioSettings = _settingsService.Settings.Audio;

        // Matched by name rather than "list is empty" so adding a new built-in preset later
        // (e.g. Girl Voice) still reaches accounts that already have presets saved, without
        // touching or duplicating anything the user already has.
        var existingPresetNames = audioSettings.VoiceChangerPresets.Select(p => p.Name).ToHashSet();
        var missingDefaults = CreateDefaultVoiceChangerPresets().Where(p => !existingPresetNames.Contains(p.Name)).ToList();
        var settingsNeedSaving = false;
        if (missingDefaults.Count > 0)
        {
            audioSettings.VoiceChangerPresets.AddRange(missingDefaults);
            settingsNeedSaving = true;
        }

        // One-time retune: "Girl Voice"/"Deep Voice" already existed for anyone who used this
        // before Formant Shift was added, so the name-based seeding above won't touch them —
        // they'd otherwise keep the old pitch-only tuning forever. Safe to overwrite
        // unconditionally here since FormantShift didn't exist before this exact change, so no
        // prior save could have set it to anything other than its 0 default.
        var defaultsByName = CreateDefaultVoiceChangerPresets().ToDictionary(p => p.Name);
        foreach (var name in new[] { "Deep Voice", "Girl Voice" })
        {
            var existing = audioSettings.VoiceChangerPresets.FirstOrDefault(p => p.Name == name);
            if (existing is null || !defaultsByName.TryGetValue(name, out var retuned)) continue;
            if (existing.PitchSemitones == retuned.PitchSemitones && existing.FormantShift == retuned.FormantShift) continue;

            existing.PitchSemitones = retuned.PitchSemitones;
            existing.FormantShift = retuned.FormantShift;
            settingsNeedSaving = true;
        }

        if (settingsNeedSaving)
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }

        _isLoadingVoiceChangerSettings = true;
        VoiceChangerEnabled = audioSettings.EnableVoiceChanger;
        VoiceEffectType = audioSettings.VoiceEffectType;
        VoiceChangerPitchSemitones = audioSettings.VoiceChangerPitchSemitones;
        RobotFrequencyHz = audioSettings.RobotFrequencyHz;
        RobotWaveform = audioSettings.RobotWaveform;
        RobotMix = audioSettings.RobotMix;
        EchoDelayMs = audioSettings.EchoDelayMs;
        EchoFeedback = audioSettings.EchoFeedback;
        EchoMix = audioSettings.EchoMix;
        DistortionDrive = audioSettings.DistortionDrive;
        DistortionMix = audioSettings.DistortionMix;
        FormantShift = audioSettings.FormantShift;
        _isLoadingVoiceChangerSettings = false;

        VoiceChangerPresets.Clear();
        foreach (var preset in audioSettings.VoiceChangerPresets)
        {
            VoiceChangerPresets.Add(preset);
        }

        _themeService.ApplyTheme(_settingsService.Settings);
        RegisterAllHotkeys();
        _audioEngine.RefreshMicMonitoring();
        _fileWatcher.Start();
        RefreshSounds();

        StatusMessage = $"Loaded {_libraryService.Library.Sounds.Count} sounds";
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

    private static List<VoiceChangerPreset> CreateDefaultVoiceChangerPresets() =>
    [
        new() { Name = "Normal", EffectType = VoiceEffectType.Pitch, PitchSemitones = 0 },
        // Pitch shift alone always has a "sped up/slowed down" quality (it moves formants right
        // along with pitch) — combining a smaller pitch shift with a Formant tilt in the same
        // direction reads as a genuinely different voice rather than just higher/lower-pitched.
        new() { Name = "Deep Voice", EffectType = VoiceEffectType.Pitch, PitchSemitones = -6, FormantShift = -4 },
        new() { Name = "Girl Voice", EffectType = VoiceEffectType.Pitch, PitchSemitones = 3, FormantShift = 6 },
        new() { Name = "Chipmunk", EffectType = VoiceEffectType.Pitch, PitchSemitones = 7 },
        new() { Name = "Robot", EffectType = VoiceEffectType.Robot, RobotFrequencyHz = 30 },
        new() { Name = "Distortion", EffectType = VoiceEffectType.Distortion, DistortionDrive = 5 }
    ];

    [RelayCommand]
    private void ShowVoiceChangerView() => ShowVoiceChangerTab = true;

    /// <summary>Shared by every "leave the Voice Changer tab" nav command — also stops a live
    /// Test Mic preview rather than leaving mic monitoring silently running after the user
    /// navigates away.</summary>
    private void ExitVoiceChangerTab()
    {
        ShowVoiceChangerTab = false;

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
    private void ApplyVoiceChangerPreset(VoiceChangerPreset? preset)
    {
        if (preset is null) return;

        _isLoadingVoiceChangerSettings = true;
        VoiceEffectType = preset.EffectType;
        VoiceChangerPitchSemitones = preset.PitchSemitones;
        RobotFrequencyHz = preset.RobotFrequencyHz;
        RobotWaveform = preset.RobotWaveform;
        RobotMix = preset.RobotMix;
        EchoDelayMs = preset.EchoDelayMs;
        EchoFeedback = preset.EchoFeedback;
        EchoMix = preset.EchoMix;
        DistortionDrive = preset.DistortionDrive;
        DistortionMix = preset.DistortionMix;
        FormantShift = preset.FormantShift;
        _isLoadingVoiceChangerSettings = false;

        // A preset can change the active effect type itself (e.g. switching from a Pitch preset
        // to a Robot preset), so this needs the full topology refresh, not the lightweight path.
        _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);
    }

    [RelayCommand]
    private async Task SaveVoiceChangerPresetAsync()
    {
        var dialog = new InputDialog("Save Preset", "Preset name:", string.Empty);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        var preset = new VoiceChangerPreset
        {
            Name = dialog.InputText.Trim(),
            EffectType = VoiceEffectType,
            PitchSemitones = VoiceChangerPitchSemitones,
            RobotFrequencyHz = RobotFrequencyHz,
            RobotWaveform = RobotWaveform,
            RobotMix = RobotMix,
            EchoDelayMs = EchoDelayMs,
            EchoFeedback = EchoFeedback,
            EchoMix = EchoMix,
            DistortionDrive = DistortionDrive,
            DistortionMix = DistortionMix,
            FormantShift = FormantShift
        };

        _settingsService.Settings.Audio.VoiceChangerPresets.Add(preset);
        VoiceChangerPresets.Add(preset);
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteVoiceChangerPresetAsync(VoiceChangerPreset? preset)
    {
        if (preset is null) return;

        _settingsService.Settings.Audio.VoiceChangerPresets.Remove(preset);
        VoiceChangerPresets.Remove(preset);
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    // Enabling/disabling the changer or switching which effect is active changes the chain
    // topology (and whether mic capture is needed at all), so these two go through a full
    // RefreshMicMonitoring — everything else below is a value tweak on an already-built chain.
    partial void OnVoiceChangerEnabledChanged(bool value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);

    partial void OnVoiceEffectTypeChanged(VoiceEffectType value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: true);

    // These are all live slider/knob tweaks on an effect that's already running. Routing every
    // one of them through a full RefreshMicMonitoring — which tears down and restarts the actual
    // WasapiCapture — meant dragging the Pitch slider itself caused audible stutter/glitching
    // on every tick, regardless of anything in the phase vocoder's own DSP correctness. They now
    // go through the lightweight in-place parameter update instead.
    partial void OnVoiceChangerPitchSemitonesChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotFrequencyHzChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotWaveformChanged(RobotWaveform value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnRobotMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoDelayMsChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoFeedbackChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnEchoMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDistortionDriveChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnDistortionMixChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

    partial void OnFormantShiftChanged(double value) => _ = ApplyVoiceChangerSettingsAsync(structuralChange: false);

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
        audioSettings.VoiceEffectType = VoiceEffectType;
        audioSettings.VoiceChangerPitchSemitones = VoiceChangerPitchSemitones;
        audioSettings.RobotFrequencyHz = RobotFrequencyHz;
        audioSettings.RobotWaveform = RobotWaveform;
        audioSettings.RobotMix = RobotMix;
        audioSettings.EchoDelayMs = EchoDelayMs;
        audioSettings.EchoFeedback = EchoFeedback;
        audioSettings.EchoMix = EchoMix;
        audioSettings.DistortionDrive = DistortionDrive;
        audioSettings.DistortionMix = DistortionMix;
        audioSettings.FormantShift = FormantShift;

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
    }

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
                if (_settingsService.Settings.Notifications.OnError)
                {
                    _notifications.ShowError("Sound library full", $"Free tier is limited to {maxSounds.Value} sounds. Upgrade to Pro for unlimited sounds.");
                }

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

    [RelayCommand]
    private void ShowFavoritesView()
    {
        ExitVoiceChangerTab();
        ShowFavorites = true;
        ShowRecent = false;
        SelectedFolderId = null;
        RefreshSounds();
    }

    [RelayCommand]
    private void ShowRecentView()
    {
        ExitVoiceChangerTab();
        ShowRecent = true;
        ShowFavorites = false;
        SelectedFolderId = null;
        RefreshSounds();
    }

    [RelayCommand]
    private void ShowMostPlayedView()
    {
        ExitVoiceChangerTab();
        ShowFavorites = false;
        ShowRecent = false;
        SelectedFolderId = null;
        SortMode = SortMode.MostPlayed; // OnSortModeChanged persists + calls RefreshSounds()
    }

    [RelayCommand]
    private void ShowAllSounds()
    {
        ExitVoiceChangerTab();
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
            if (_settingsService.Settings.Notifications.OnError)
            {
                _notifications.ShowError("Folder limit reached", $"Free tier is limited to {maxFolders.Value} folders. Upgrade to Pro for unlimited folders.");
            }

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
        var sounds = _libraryService.GetFilteredSounds(SelectedFolderId, SearchQuery, ShowFavorites, ShowRecent);
        VisibleSounds.Clear();

        foreach (var sound in sounds)
        {
            if (!_buttonCache.TryGetValue(sound.Id, out var vm))
            {
                vm = new SoundButtonViewModel(sound, _playbackManager, _libraryService);
                _buttonCache[sound.Id] = vm;
            }

            VisibleSounds.Add(vm);
        }

        ActivePlaybackCount = _playbackManager.ActiveInstances.Count;

        // Folders is a computed property (recreates its ObservableCollection from
        // _libraryService.Library.Folders on every access) rather than an [ObservableProperty],
        // so WPF only re-reads it when explicitly told to. RefreshSounds() already runs at
        // startup right after the library loads, and after every mutation that could change
        // the folder list — so this is the one place that needs to raise it.
        OnPropertyChanged(nameof(Folders));
    }

    private void UpdatePlayingStates()
    {
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
            return;
        }

        HasNowPlaying = true;
        NowPlayingInstanceId = current.InstanceId;
        NowPlayingIsPaused = current.State == PlaybackState.Paused;
        NowPlayingDuration = current.DurationSeconds;

        var sound = VisibleSounds.FirstOrDefault(b => b.Sound.Id == current.SoundId)?.Sound
                    ?? _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == current.SoundId);
        NowPlayingName = sound?.Name ?? "Unknown";
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

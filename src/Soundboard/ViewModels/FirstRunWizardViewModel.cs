using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

public partial class FirstRunWizardViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILibraryService _libraryService;
    private readonly IAudioEngine _audioEngine;

    private DetectedVirtualDevice? _vbCableDevice;
    private AudioDeviceInfo? _defaultPlaybackDevice;

    public FirstRunWizardViewModel(ISettingsService settingsService, ILibraryService libraryService, IAudioEngine audioEngine)
    {
        _settingsService = settingsService;
        _libraryService = libraryService;
        _audioEngine = audioEngine;
    }

    // --- Step visibility ---
    [ObservableProperty] private bool _showProgressStep = true;
    [ObservableProperty] private bool _showVbCablePromptStep;
    [ObservableProperty] private bool _showCompleteStep;

    // --- Progress checklist ---
    [ObservableProperty] private bool _foldersDone;
    [ObservableProperty] private bool _devicesDone;
    [ObservableProperty] private bool _vbCableCheckDone;
    [ObservableProperty] private bool _settingsDone;
    [ObservableProperty] private bool _demoSoundsDone;
    [ObservableProperty] private bool _nextEnabled;

    // --- VB-Cable prompt ---
    [ObservableProperty] private string _vbCableRecheckMessage = string.Empty;

    // --- Complete summary ---
    [ObservableProperty] private string _defaultOutputSummary = string.Empty;
    [ObservableProperty] private string _monitoringSummary = string.Empty;
    [ObservableProperty] private string _microphoneSummary = string.Empty;

    public event Action? CloseRequested;

    public async Task RunSetupAsync()
    {
        // Directories are already guaranteed to exist by the time IAppPaths is constructed
        // (its constructor creates them), so this step is a quick confirmation rather than
        // real work — still worth showing so the checklist reads as a complete, honest account
        // of what setup actually does.
        await Task.Delay(250).ConfigureAwait(true);
        FoldersDone = true;

        var outputs = await _audioEngine.GetOutputDevicesAsync().ConfigureAwait(true);
        _defaultPlaybackDevice = outputs.FirstOrDefault(d => d.IsDefault) ?? outputs.FirstOrDefault();
        await Task.Delay(250).ConfigureAwait(true);
        DevicesDone = true;

        var detected = await _audioEngine.DetectVirtualDevicesAsync().ConfigureAwait(true);
        _vbCableDevice = detected.FirstOrDefault(d => d.Product == "VB-Audio Virtual Cable");
        await Task.Delay(250).ConfigureAwait(true);
        VbCableCheckDone = true;

        await _settingsService.SaveAsync().ConfigureAwait(true);
        await Task.Delay(250).ConfigureAwait(true);
        SettingsDone = true;

        await ImportDemoSoundsAsync().ConfigureAwait(true);
        await Task.Delay(250).ConfigureAwait(true);
        DemoSoundsDone = true;

        NextEnabled = true;
    }

    private async Task ImportDemoSoundsAsync()
    {
        try
        {
            var demoSoundsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "DemoSounds");
            if (!Directory.Exists(demoSoundsDir)) return;

            var files = Directory.GetFiles(demoSoundsDir, "*.wav");
            if (files.Length == 0) return;

            await _libraryService.ImportFilesAsync(files).ConfigureAwait(true);
        }
        catch
        {
            // Demo sounds are a nice-to-have — a failure here shouldn't block first-run setup.
        }
    }

    [RelayCommand]
    private void Next()
    {
        ShowProgressStep = false;

        if (_vbCableDevice is null)
        {
            ShowVbCablePromptStep = true;
        }
        else
        {
            ShowCompleteStep = true;
            PrepareCompleteSummary();
        }
    }

    [RelayCommand]
    private void InstallVbCable()
    {
        try
        {
            // Opens the official download page in the user's browser rather than silently
            // fetching and running a third-party installer — they see the real vendor page,
            // download it themselves, and run the standard (visible) Windows installer. Same
            // outcome, but nothing here can look like software installing itself unasked.
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch
        {
            // If the browser can't be launched for some reason, the user can still install
            // it manually and use "I've installed it" below — nothing to recover here.
        }
    }

    [RelayCommand]
    private async Task RecheckVbCableAsync()
    {
        var detected = await _audioEngine.DetectVirtualDevicesAsync().ConfigureAwait(true);
        _vbCableDevice = detected.FirstOrDefault(d => d.Product == "VB-Audio Virtual Cable");

        if (_vbCableDevice is not null)
        {
            ShowVbCablePromptStep = false;
            ShowCompleteStep = true;
            PrepareCompleteSummary();
        }
        else
        {
            VbCableRecheckMessage = "Still not found — make sure the installer finished, then try again.";
        }
    }

    [RelayCommand]
    private void SkipVbCable()
    {
        ShowVbCablePromptStep = false;
        ShowCompleteStep = true;
        PrepareCompleteSummary();
    }

    private void PrepareCompleteSummary()
    {
        var settings = _settingsService.Settings;

        settings.Audio.HeadphoneDeviceId = _defaultPlaybackDevice?.Id;
        MonitoringSummary = _defaultPlaybackDevice?.Name ?? "System default";

        if (_vbCableDevice is not null)
        {
            settings.Audio.VirtualMicOutputDeviceId = _vbCableDevice.PlaybackDeviceId;
            settings.Audio.DefaultOutputRoute = OutputRoute.Both;
            DefaultOutputSummary = _vbCableDevice.PlaybackDeviceName ?? "VB-Cable Input";
            MicrophoneSummary = _vbCableDevice.RecordingDeviceName is { Length: > 0 } recording
                ? $"Set Discord/your game's mic to \"{recording}\""
                : "Set Discord/your game's mic input to VB-Cable's recording device";
        }
        else
        {
            settings.Audio.DefaultOutputRoute = OutputRoute.Headphones;
            DefaultOutputSummary = MonitoringSummary;
            MicrophoneSummary = "Not set up — install a virtual cable later from Settings to route sounds into Discord/games";
        }
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        await _settingsService.SaveAsync().ConfigureAwait(true);
        CloseRequested?.Invoke();
    }
}

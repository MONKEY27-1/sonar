using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;

namespace Soundboard.ViewModels;

/// <summary>One currently-playing sound — used by the Home dashboard's Active Playback section.
/// Per-instance pause/resume/stop go straight through <see cref="IAudioEngine"/> (the same calls
/// the single-track Now Playing bar already uses), since <see cref="IPlaybackManager"/> itself
/// only exposes global PauseAll/ResumeAll, not a per-instance pause.</summary>
public sealed partial class ActivePlaybackItemViewModel : ObservableObject
{
    private readonly IAudioEngine _audioEngine;

    public ActivePlaybackItemViewModel(string instanceId, string soundName, IAudioEngine audioEngine)
    {
        InstanceId = instanceId;
        SoundName = soundName;
        _audioEngine = audioEngine;
    }

    public string InstanceId { get; }
    public string SoundName { get; }

    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private bool _isPaused;

    /// <summary>Pushed by MainViewModel's existing PlaybackProgress tick handler — kept as a
    /// plain method rather than public setters so progress updates read as "driven by the audio
    /// engine's own callback," not something arbitrary code can poke.</summary>
    public void SetProgress(double position, double duration)
    {
        Position = position;
        Duration = duration;
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (IsPaused)
        {
            await _audioEngine.ResumeAsync(InstanceId).ConfigureAwait(true);
        }
        else
        {
            await _audioEngine.PauseAsync(InstanceId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _audioEngine.StopAsync(InstanceId).ConfigureAwait(true);
    }
}

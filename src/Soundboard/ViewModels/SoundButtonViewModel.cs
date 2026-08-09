using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.ViewModels;

public partial class SoundButtonViewModel : ObservableObject
{
    private readonly IPlaybackManager _playbackManager;
    private readonly ILibraryService _libraryService;

    public SoundButtonViewModel(SoundItem sound, IPlaybackManager playbackManager, ILibraryService libraryService)
    {
        Sound = sound;
        _playbackManager = playbackManager;
        _libraryService = libraryService;
    }

    public SoundItem Sound { get; }

    public string DisplayName => Sound.GetDisplayName();

    public string DurationText => TimeSpan.FromSeconds(Sound.DurationSeconds).ToString(@"m\:ss");

    public string? HotkeyDisplay => Sound.Hotkey?.DisplayName;

    /// <summary>
    /// Sound buttons are cached and reused across RefreshSounds() calls, so mutating
    /// Sound.Hotkey directly doesn't raise any change notification on its own — call this
    /// after changing it so the bound hotkey badge actually updates.
    /// </summary>
    public void NotifyHotkeyChanged() => OnPropertyChanged(nameof(HotkeyDisplay));

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isPlaying;

    [RelayCommand]
    private async Task PlayAsync() => await _playbackManager.PlaySoundAsync(Sound.Id).ConfigureAwait(false);

    [RelayCommand]
    private async Task StopAsync() => await _playbackManager.StopSoundAsync(Sound.Id).ConfigureAwait(false);

    public void UpdateProgress(double position, double duration)
    {
        Progress = duration > 0 ? position / duration : 0;
        IsPlaying = position > 0 && position < duration;
    }
}

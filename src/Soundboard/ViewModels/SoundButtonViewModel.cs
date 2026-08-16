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

    // TimeSpan's "m" custom specifier is the minutes-of-the-hour component (0-59), not the
    // total elapsed minutes — for anything an hour or longer that silently dropped the hours
    // entirely (e.g. 51:12 shown for a sound actually over an hour long). Same fix as
    // SecondsToTimeConverter, duplicated here because tiles read this directly rather than
    // through that converter.
    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromSeconds(Sound.DurationSeconds);
            return span.Hours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    public string? HotkeyDisplay => Sound.Hotkey?.DisplayName;

    /// <summary>
    /// Sound buttons are cached and reused across RefreshSounds() calls, so mutating
    /// Sound.Hotkey directly doesn't raise any change notification on its own — call this
    /// after changing it so the bound hotkey badge actually updates.
    /// </summary>
    public void NotifyHotkeyChanged() => OnPropertyChanged(nameof(HotkeyDisplay));

    // Only shown when the sound overrides the library's default route — an unset override
    // already plays through the default, so badging every tile with it would just be noise.
    public string? RouteGlyph => Sound.OutputRouteOverride switch
    {
        OutputRoute.Headphones => "🎧",
        OutputRoute.Microphone => "🎙",
        OutputRoute.Both => "🎧🎙",
        _ => null
    };

    /// <summary>Same cached-VM reasoning as NotifyHotkeyChanged — call after changing
    /// Sound.OutputRouteOverride so the bound route badge actually updates.</summary>
    public void NotifyRouteChanged() => OnPropertyChanged(nameof(RouteGlyph));

    // Details panel's read-only "format" field — the file extension is the only thing this app
    // actually knows about a sound's format, so that's all this shows.
    public string FormatText => Path.GetExtension(Sound.FileName).TrimStart('.').ToUpperInvariant();

    public string TagsText => string.Join(", ", Sound.Tags);

    /// <summary>Same cached-VM reasoning as NotifyHotkeyChanged/NotifyRouteChanged — the Details
    /// panel binds directly to this VM instance (not through a collection re-add), so a rename
    /// needs an explicit nudge to show up there.</summary>
    public void NotifyDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));

    public void NotifyTagsChanged() => OnPropertyChanged(nameof(TagsText));

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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundboard.Core.Interfaces;

namespace Soundboard.ViewModels;

/// <summary>Backs the quick-play overlay popup — deliberately minimal dependencies (just enough
/// to list sounds and play one) since this is a lightweight, frequently-toggled window, not a
/// second copy of the main window's functionality.</summary>
public partial class QuickPlayOverlayViewModel : ObservableObject
{
    private readonly ILibraryService _libraryService;
    private readonly IPlaybackManager _playbackManager;

    public QuickPlayOverlayViewModel(ILibraryService libraryService, IPlaybackManager playbackManager)
    {
        _libraryService = libraryService;
        _playbackManager = playbackManager;
    }

    public ObservableCollection<SoundButtonViewModel> Sounds { get; } = [];

    public bool HasNoResults => Sounds.Count == 0;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value) => Refresh();

    /// <summary>Rebuilds the list from the current library — called each time the overlay is
    /// shown (not just on search changes), since sounds may have been added/renamed/removed
    /// since it was last opened.</summary>
    public void Refresh()
    {
        Sounds.Clear();

        var filtered = _libraryService.GetFilteredSounds(null, SearchQuery, favoritesOnly: false, recentOnly: false);
        foreach (var sound in filtered)
        {
            Sounds.Add(new SoundButtonViewModel(sound, _playbackManager, _libraryService));
        }

        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Event rather than a direct Window reference, so this ViewModel doesn't need to
    /// know about WPF windows at all — the code-behind subscribes and calls Hide() itself.</summary>
    public event EventHandler? RequestClose;

    [RelayCommand]
    private async Task PlayAndDismissAsync(SoundButtonViewModel? button)
    {
        if (button is null) return;

        // ConfigureAwait(true) — RequestClose ultimately calls Window.Hide() (see the code-behind
        // subscription), so this continuation has to resume on the UI thread, not a thread-pool
        // thread. false here was exactly the "calling thread cannot access this object" crash.
        await _playbackManager.PlaySoundAsync(button.Sound.Id).ConfigureAwait(true);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}

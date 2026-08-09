using System.Collections.Concurrent;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

public sealed class PlaybackManager : IPlaybackManager
{
    private readonly IAudioEngine _audioEngine;
    private readonly ILibraryService _libraryService;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notifications;
    private readonly ConcurrentDictionary<string, string> _soundToInstance = new();

    public PlaybackManager(
        IAudioEngine audioEngine,
        ILibraryService libraryService,
        ISettingsService settingsService,
        INotificationService notifications)
    {
        _audioEngine = audioEngine;
        _libraryService = libraryService;
        _settingsService = settingsService;
        _notifications = notifications;

        _audioEngine.PlaybackStateChanged += (_, instance) =>
        {
            if (instance.State == PlaybackState.Stopped)
            {
                var match = _soundToInstance.FirstOrDefault(kvp => kvp.Value == instance.InstanceId);
                if (match.Key is not null)
                {
                    _soundToInstance.TryRemove(match.Key, out var removedInstanceId);
                }

                if (_settingsService.Settings.Notifications.OnPlaybackFinished)
                {
                    var sound = _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == instance.SoundId);
                    if (sound is not null)
                    {
                        _notifications.ShowInfo("Finished", sound.GetDisplayName());
                    }
                }
            }

            ActiveInstancesChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public IReadOnlyList<PlaybackInstance> ActiveInstances => _audioEngine.GetActiveInstances();

    public event EventHandler? ActiveInstancesChanged;

    public async Task PlaySoundAsync(string soundId, bool fromHotkey = false)
    {
        var sound = _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == soundId);
        if (sound is null) return;

        var filePath = _libraryService.GetSoundFilePath(sound);
        if (!File.Exists(filePath)) return;

        var settings = _settingsService.Settings;
        if (settings.Playback.QueueMode == QueueMode.Queue && _soundToInstance.ContainsKey(soundId))
        {
            return;
        }

        if (settings.Playback.QueueMode == QueueMode.Queue && _soundToInstance.Count > 0)
        {
            // In queue mode, only one sound at a time unless overlap is enabled per sound.
            foreach (var activeInstanceId in _soundToInstance.Values.ToList())
            {
                await _audioEngine.StopAsync(activeInstanceId).ConfigureAwait(false);
            }

            _soundToInstance.Clear();
        }

        var route = sound.OutputRouteOverride ?? settings.Audio.DefaultOutputRoute;

        try
        {
            var instanceId = await _audioEngine.PlayAsync(sound, filePath, route).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(instanceId))
            {
                _soundToInstance[soundId] = instanceId;
                await _libraryService.MarkRecentlyUsedAsync(soundId).ConfigureAwait(false);
                ActiveInstancesChanged?.Invoke(this, EventArgs.Empty);

                if (settings.Notifications.OnPlaybackStarted)
                {
                    _notifications.ShowInfo("Playing", sound.GetDisplayName());
                }
            }
        }
        catch (Exception ex)
        {
            if (settings.Notifications.OnError)
            {
                _notifications.ShowError("Playback failed", $"Couldn't play \"{sound.Name}\": {ex.Message}");
            }
        }
    }

    public async Task StopSoundAsync(string soundId)
    {
        if (_soundToInstance.TryRemove(soundId, out var instanceId))
        {
            await _audioEngine.StopAsync(instanceId).ConfigureAwait(false);
            ActiveInstancesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAllAsync()
    {
        _soundToInstance.Clear();
        await _audioEngine.StopAllAsync().ConfigureAwait(false);
        ActiveInstancesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task PauseAllAsync()
    {
        foreach (var instanceId in _soundToInstance.Values)
        {
            await _audioEngine.PauseAsync(instanceId).ConfigureAwait(false);
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var instanceId in _soundToInstance.Values)
        {
            await _audioEngine.ResumeAsync(instanceId).ConfigureAwait(false);
        }
    }

    public async Task ToggleLoopForSoundAsync(string soundId)
    {
        var sound = _libraryService.Library.Sounds.FirstOrDefault(s => s.Id == soundId);
        if (sound is null) return;

        sound.PlaybackMode = sound.PlaybackMode == PlaybackMode.Loop
            ? PlaybackMode.OneShot
            : PlaybackMode.Loop;

        await _libraryService.SaveAsync().ConfigureAwait(false);
    }
}

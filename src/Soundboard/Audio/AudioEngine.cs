using System.Collections.Concurrent;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;
using PlaybackState = Soundboard.Core.Models.PlaybackState;

namespace Soundboard.Audio;

/// <summary>
/// Orchestrates playback: builds each sound's provider chain and hands it to the right
/// <see cref="AudioMixer"/>(s), and tracks active <see cref="PlaybackHandle"/>s for stop/pause/
/// resume/progress. Device enumeration lives in <see cref="AudioDeviceManager"/>, mixing/output
/// lives in <see cref="AudioMixer"/>, and mic capture lives in <see cref="MicrophoneMonitor"/> —
/// this class just wires them together.
///
/// Two <see cref="AudioMixer"/>s are created once, during construction, and live for the
/// app's lifetime: one drives the headphone/speaker route, the other the virtual-mic-output
/// route. Playing a sound never creates a new device handle — it only adds/removes an
/// <see cref="ISampleProvider"/> node in the relevant mixer's graph.
/// </summary>
public sealed class AudioEngine : IAudioEngine, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly AudioDeviceManager _deviceManager;
    private readonly MicrophoneMonitor _micMonitor;
    private readonly AudioMixer _headphoneMixer;
    private readonly AudioMixer _virtualMicMixer;
    private readonly ConcurrentDictionary<string, PlaybackHandle> _active = new();
    private readonly Timer _progressTimer;
    private bool _disposed;
    private bool _voicePreviewRequested;

    public AudioEngine(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _deviceManager = new AudioDeviceManager();
        _micMonitor = new MicrophoneMonitor(_deviceManager);

        var settings = _settingsService.Settings;
        var latencyMs = GetLatencyMs(settings);
        _headphoneMixer = new AudioMixer(_deviceManager, settings.Audio.HeadphoneDeviceId, latencyMs);
        _virtualMicMixer = new AudioMixer(_deviceManager, settings.Audio.VirtualMicOutputDeviceId, latencyMs);
        ApplyMixerVolumes(settings);

        _progressTimer = new Timer(UpdateProgress, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
        _settingsService.SettingsChanged += (_, _) => RefreshSettings();
    }

    public event EventHandler<PlaybackInstance>? PlaybackStateChanged;
    public event EventHandler<(string InstanceId, double Position, double Duration)>? PlaybackProgress;

    public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
        => _deviceManager.GetOutputDevicesAsync(cancellationToken);

    public Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
        => _deviceManager.GetInputDevicesAsync(cancellationToken);

    public Task<IReadOnlyList<DetectedVirtualDevice>> DetectVirtualDevicesAsync(CancellationToken cancellationToken = default)
        => _deviceManager.DetectVirtualDevicesAsync(cancellationToken);

    public Task<double> GetDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var source = CreateSoundSource(filePath);
                using (source.Stream)
                {
                    return source.Stream.TotalTime.TotalSeconds;
                }
            }
            catch
            {
                return 0d;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Live device switch — hands each mixer's existing graph to a freshly created output for
    /// the new device, so anything currently playing keeps playing straight through. Also
    /// updates the in-memory setting so it stays consistent with what's actually live.
    /// </summary>
    public Task ChangeHeadphoneDeviceAsync(string? deviceId)
    {
        var settings = _settingsService.Settings;
        settings.Audio.HeadphoneDeviceId = deviceId;
        _headphoneMixer.EnsureDevice(deviceId, GetLatencyMs(settings));
        return Task.CompletedTask;
    }

    public Task ChangeVirtualDeviceAsync(string? deviceId)
    {
        var settings = _settingsService.Settings;
        settings.Audio.VirtualMicOutputDeviceId = deviceId;
        _virtualMicMixer.EnsureDevice(deviceId, GetLatencyMs(settings));
        return Task.CompletedTask;
    }

    public Task<string> PlayAsync(SoundItem sound, string filePath, OutputRoute route, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Sound file not found.", filePath);
        }

        var settings = _settingsService.Settings;
        if (settings.Audio.MasterMuted)
        {
            return Task.FromResult(string.Empty);
        }

        var instanceId = Guid.NewGuid().ToString("N");
        var handle = new PlaybackHandle
        {
            InstanceId = instanceId,
            SoundId = sound.Id,
            Instance = new PlaybackInstance
            {
                InstanceId = instanceId,
                SoundId = sound.Id,
                State = PlaybackState.Playing,
                DurationSeconds = sound.DurationSeconds
            },
            IsLooping = sound.PlaybackMode == PlaybackMode.Loop,
            FadeOut = sound.FadeOut,
            FadeOutMs = Math.Max(1, sound.EditSettings?.FadeOutMs ?? 100)
        };

        try
        {
            // route == Both gets two entirely independent provider chains (separate readers,
            // separate volume/fade nodes) so each mixer's copy can be volumed/faded on its own.
            if (route is OutputRoute.Headphones or OutputRoute.Both)
            {
                handle.Channels.Add(BuildChannel(filePath, sound, settings, _headphoneMixer));
            }

            if (route is OutputRoute.Microphone or OutputRoute.Both)
            {
                handle.Channels.Add(BuildChannel(filePath, sound, settings, _virtualMicMixer));
            }

            if (handle.Channels.Count == 0)
            {
                return Task.FromResult(string.Empty);
            }

            if (sound.FadeIn)
            {
                var fadeInMs = Math.Max(1, sound.EditSettings?.FadeInMs ?? 100);
                foreach (var channel in handle.Channels)
                {
                    channel.FadeProvider.BeginFadeIn(fadeInMs);
                }
            }

            _active[instanceId] = handle;
            PlaybackStateChanged?.Invoke(this, handle.Instance);
            return Task.FromResult(instanceId);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async Task StopAsync(string instanceId)
    {
        if (!_active.TryRemove(instanceId, out var handle)) return;

        if (handle.FadeOut)
        {
            foreach (var channel in handle.Channels)
            {
                channel.FadeProvider.BeginFadeOut(handle.FadeOutMs);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(handle.FadeOutMs)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort delay; fall through and remove regardless.
            }
        }

        RemoveFromMixers(handle);
        handle.Instance.State = PlaybackState.Stopped;
        PlaybackStateChanged?.Invoke(this, handle.Instance);
        handle.Dispose();
    }

    public Task StopAllAsync()
    {
        // A "stop all" is meant to be an instant panic button — no fades here even if the
        // individual sounds have FadeOut configured, so it always kills audio immediately.
        foreach (var key in _active.Keys.ToList())
        {
            if (_active.TryRemove(key, out var handle))
            {
                RemoveFromMixers(handle);
                handle.Instance.State = PlaybackState.Stopped;
                PlaybackStateChanged?.Invoke(this, handle.Instance);
                handle.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(string instanceId)
    {
        if (_active.TryGetValue(instanceId, out var handle))
        {
            foreach (var channel in handle.Channels)
            {
                channel.Mixer.RemoveInput(channel.MixerInput);
            }

            handle.Instance.State = PlaybackState.Paused;
            PlaybackStateChanged?.Invoke(this, handle.Instance);
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(string instanceId)
    {
        if (_active.TryGetValue(instanceId, out var handle))
        {
            foreach (var channel in handle.Channels)
            {
                channel.Mixer.AddInput(channel.MixerInput);
            }

            handle.Instance.State = PlaybackState.Playing;
            PlaybackStateChanged?.Invoke(this, handle.Instance);
        }

        return Task.CompletedTask;
    }

    public Task RestartAsync(string instanceId)
    {
        if (_active.TryGetValue(instanceId, out var handle))
        {
            foreach (var channel in handle.Channels)
            {
                channel.Stream.Position = 0;
            }
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(string instanceId, double deltaSeconds)
    {
        if (_active.TryGetValue(instanceId, out var handle))
        {
            foreach (var channel in handle.Channels)
            {
                var stream = channel.Stream;
                var bytesPerSecond = stream.WaveFormat.AverageBytesPerSecond;
                var deltaBytes = (long)(deltaSeconds * bytesPerSecond);
                var newPosition = Math.Clamp(stream.Position + deltaBytes, 0, stream.Length);

                // Align to the format's block size so we don't land mid-sample and produce noise.
                var blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
                newPosition -= newPosition % blockAlign;

                stream.Position = newPosition;
            }
        }

        return Task.CompletedTask;
    }

    public Task SeekToAsync(string instanceId, double positionSeconds)
    {
        if (_active.TryGetValue(instanceId, out var handle))
        {
            foreach (var channel in handle.Channels)
            {
                var stream = channel.Stream;
                var bytesPerSecond = stream.WaveFormat.AverageBytesPerSecond;
                var newPosition = Math.Clamp((long)(positionSeconds * bytesPerSecond), 0, stream.Length);

                // Align to the format's block size so we don't land mid-sample and produce noise.
                var blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
                newPosition -= newPosition % blockAlign;

                stream.Position = newPosition;
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<PlaybackInstance> GetActiveInstances()
        => _active.Values.Select(h => h.Instance).ToList();

    private static SoundSource CreateSoundSource(string filePath)
    {
        if (string.Equals(Path.GetExtension(filePath), ".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var vorbisReader = new VorbisWaveReader(filePath);
            return new SoundSource(vorbisReader, vorbisReader);
        }

        // Everything else (.wav, .mp3, .aiff, and anything else Windows Media Foundation can
        // decode — which includes .flac natively on Windows 10 1809+/11) goes through
        // AudioFileReader, which normalizes to 32-bit IEEE float regardless of source format.
        var reader = new AudioFileReader(filePath);
        return new SoundSource(reader, reader);
    }

    private PlaybackChannel BuildChannel(string filePath, SoundItem sound, AppSettings settings, AudioMixer mixer)
    {
        var source = CreateSoundSource(filePath);
        ISampleProvider provider = source.Provider;

        if (Math.Abs(sound.PlaybackSpeed - 1.0f) > 0.01f)
        {
            var targetRate = Math.Max(1000, (int)(provider.WaveFormat.SampleRate * sound.PlaybackSpeed));
            provider = new WdlResamplingSampleProvider(provider, targetRate);
        }

        if (sound.PlaybackMode == PlaybackMode.Loop)
        {
            provider = new LoopSampleProvider(provider, source.Stream);
        }

        // Route/global volume now lives on the mixer's own master volume node (so changing
        // those sliders applies live to already-playing sounds); this stays purely per-sound.
        var baseVolume = sound.Volume;
        if (sound.Normalize || settings.Audio.NormalizeGlobally)
        {
            baseVolume *= 1.2f;
        }

        var volumeProvider = new VolumeSampleProvider(provider) { Volume = baseVolume };

        var mixerFormatProvider = AudioMixer.ConvertToMixerFormat(volumeProvider);
        var fadeProvider = new FadeInOutSampleProvider(mixerFormatProvider, initiallySilent: sound.FadeIn);

        // Non-looping sounds get a completion sentinel as the actual outermost node added to
        // the mixer, so we detect the real Read()==0 end-of-stream signal rather than trying to
        // infer completion from raw stream byte Position vs. Length (unreliable for compressed/
        // resampled sources, which is why the count-not-clearing bug persisted after the
        // threading fix alone).
        CompletionSampleProvider? completion = null;
        ISampleProvider mixerInput = fadeProvider;
        if (sound.PlaybackMode != PlaybackMode.Loop)
        {
            completion = new CompletionSampleProvider(fadeProvider);
            mixerInput = completion;
        }

        mixer.AddInput(mixerInput);

        return new PlaybackChannel(mixer, source.Stream, fadeProvider, mixerInput, completion);
    }

    private static void RemoveFromMixers(PlaybackHandle handle)
    {
        foreach (var channel in handle.Channels)
        {
            channel.Mixer.RemoveInput(channel.MixerInput);
        }
    }

    private static int GetLatencyMs(AppSettings settings) => settings.Audio.LatencyMode switch
    {
        LatencyMode.Low => 50,
        LatencyMode.Stable => 300,
        _ => 100
    };

    private void ApplyMixerVolumes(AppSettings settings)
    {
        _headphoneMixer.MasterVolume = settings.Audio.GlobalVolume * settings.Audio.HeadphoneVolume;
        _virtualMicMixer.MasterVolume = settings.Audio.GlobalVolume * settings.Audio.VirtualMicOutputVolume;
    }

    /// <summary>
    /// Called at startup (once settings are actually loaded) and whenever settings are saved:
    /// makes sure both mixers are pointed at the configured devices, applies the current
    /// master/route volumes live, and refreshes mic passthrough.
    /// </summary>
    public void RefreshSettings()
    {
        var settings = _settingsService.Settings;
        var latencyMs = GetLatencyMs(settings);

        _headphoneMixer.EnsureDevice(settings.Audio.HeadphoneDeviceId, latencyMs);
        _virtualMicMixer.EnsureDevice(settings.Audio.VirtualMicOutputDeviceId, latencyMs);
        ApplyMixerVolumes(settings);

        _micMonitor.Refresh(settings.Audio, _virtualMicMixer, _headphoneMixer, _voicePreviewRequested);
    }

    public void RefreshMicMonitoring() => RefreshSettings();

    /// <summary>Applies live voice-effect parameter changes (pitch, formant, robot/echo/
    /// distortion knobs) without restarting mic capture — see the remarks on
    /// <see cref="MicrophoneMonitor.UpdateEffectParameters"/> for why that distinction matters.
    /// Use this for slider/knob-style tweaks; use <see cref="RefreshMicMonitoring"/> for changes
    /// that alter which effect is active or whether capture is needed at all.</summary>
    public void UpdateVoiceEffectParameters() => _micMonitor.UpdateEffectParameters(_settingsService.Settings.Audio);

    public void SetVoicePreviewEnabled(bool enabled)
    {
        _voicePreviewRequested = enabled;
        RefreshMicMonitoring();
    }

    private void UpdateProgress(object? state)
    {
        foreach (var instanceId in _active.Keys.ToList())
        {
            if (!_active.TryGetValue(instanceId, out var handle)) continue;

            var primary = handle.Channels.FirstOrDefault();
            if (primary is null) continue;

            double position;
            try
            {
                position = primary.Stream.CurrentTime.TotalSeconds;
            }
            catch
            {
                continue; // Stream may be mid-disposal from a concurrent stop; skip this tick.
            }

            handle.Instance.PositionSeconds = position;
            PlaybackProgress?.Invoke(this, (instanceId, position, handle.Instance.DurationSeconds));

            // Completion is driven by the PRIMARY channel's CompletionSampleProvider (see
            // PlaybackModels.cs — it flags finished on any short read, not just a literal
            // Read() == 0, which is what actually happens at real end-of-stream). Deliberately
            // only the primary channel (the headphone copy — see PlayAsync, it's always added
            // first): with OutputRoute.Both, a sound has a SECOND, independent channel feeding
            // the virtual-mic mixer, and requiring that one to ALSO finish let a stalled virtual
            // device (or just different completion timing) hold the sound "playing" forever
            // even after the audible copy had long since gone silent. Whatever's still on the
            // second channel gets torn down below regardless, same as before.
            var reachedEnd = !handle.IsLooping && primary.Completion?.IsFinished == true;
            if (reachedEnd && _active.TryRemove(instanceId, out var finished))
            {
                RemoveFromMixers(finished);
                finished.Instance.State = PlaybackState.Stopped;
                PlaybackStateChanged?.Invoke(this, finished.Instance);
                finished.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _progressTimer.Dispose();
        _micMonitor.Dispose();

        StopAllAsync().GetAwaiter().GetResult();

        _headphoneMixer.Dispose();
        _virtualMicMixer.Dispose();
    }

    private sealed record SoundSource(WaveStream Stream, ISampleProvider Provider);
}

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

/// <summary>
/// One output route's persistent audio graph: a single <see cref="WasapiOut"/> driving a
/// <see cref="MixingSampleProvider"/>, with a live-adjustable master volume node sitting
/// between the mix and the device. Individual sounds are added/removed as mixer inputs —
/// this class never spins up a device handle per sound, and stays alive for the app's
/// lifetime once created.
///
/// Device switching (<see cref="EnsureDevice"/>) hands the SAME mixer graph to a freshly
/// created output and tears down the old one, so anything currently playing keeps playing
/// straight through the switch — nothing needs to be stopped or re-added.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly AudioDeviceManager _deviceManager;
    private readonly object _lock = new();
    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _masterVolumeProvider;
    private IWavePlayer _output;
    private string? _deviceId;
    private bool _disposed;

    public AudioMixer(AudioDeviceManager deviceManager, string? deviceId, int latencyMs)
    {
        _deviceManager = deviceManager;
        _mixer = new MixingSampleProvider(Format) { ReadFully = true };
        _masterVolumeProvider = new VolumeSampleProvider(_mixer) { Volume = 1f };
        _deviceId = deviceId;
        _output = CreateAndStart(deviceId, latencyMs);
    }

    public float MasterVolume
    {
        get => _masterVolumeProvider.Volume;
        set => _masterVolumeProvider.Volume = value;
    }

    public string? DeviceId => _deviceId;

    private bool IsAlive => _output.PlaybackState == NAudio.Wave.PlaybackState.Playing;

    public void AddInput(ISampleProvider provider)
    {
        lock (_lock)
        {
            _mixer.AddMixerInput(provider);
        }
    }

    public void RemoveInput(ISampleProvider provider)
    {
        lock (_lock)
        {
            _mixer.RemoveMixerInput(provider);
        }
    }

    public void RemoveAllInputs()
    {
        lock (_lock)
        {
            _mixer.RemoveAllMixerInputs();
        }
    }

    /// <summary>
    /// Makes sure this mixer is targeting <paramref name="deviceId"/> and actively playing.
    /// A no-op if already there. Used both for lazy "is this still the right device"
    /// checks and for explicit live device switches — either way, currently-mixed-in
    /// sounds are unaffected since only the output device underneath is swapped.
    /// </summary>
    public void EnsureDevice(string? deviceId, int latencyMs)
    {
        lock (_lock)
        {
            if (_deviceId == deviceId && IsAlive) return;

            var oldOutput = _output;
            _output = CreateAndStart(deviceId, latencyMs);
            _deviceId = deviceId;

            try { oldOutput.Stop(); } catch { /* ignore */ }
            oldOutput.Dispose();
        }
    }

    private IWavePlayer CreateAndStart(string? deviceId, int latencyMs)
    {
        var output = _deviceManager.CreateOutput(deviceId, latencyMs);
        output.Init(_masterVolumeProvider);
        output.Play();
        return output;
    }

    /// <summary>
    /// Resamples/channel-adapts an arbitrary sample provider to this mixer's fixed format.
    /// Only inserts conversion stages where the source actually differs — a genuine 44.1kHz
    /// stereo source passes straight through untouched.
    /// </summary>
    public static ISampleProvider ConvertToMixerFormat(ISampleProvider provider)
    {
        if (provider.WaveFormat.SampleRate != Format.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, Format.SampleRate);
        }

        if (provider.WaveFormat.Channels != Format.Channels)
        {
            provider = new ChannelAdaptSampleProvider(provider, Format);
        }

        return provider;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            try { _output.Stop(); } catch { /* ignore */ }
            _output.Dispose();
        }
    }
}

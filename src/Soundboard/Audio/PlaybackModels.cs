using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Models;

namespace Soundboard.Audio;

/// <summary>
/// One route's live provider chain for a single playing sound (a sound routed to "Both"
/// has two of these — one per mixer — so their volume/fade/position stay independent).
/// </summary>
internal sealed class PlaybackChannel(
    AudioMixer mixer,
    WaveStream stream,
    FadeInOutSampleProvider fadeProvider,
    ISampleProvider mixerInput,
    CompletionSampleProvider? completion = null)
{
    public AudioMixer Mixer { get; } = mixer;
    public WaveStream Stream { get; } = stream;
    public FadeInOutSampleProvider FadeProvider { get; } = fadeProvider;

    /// <summary>The actual object added to the mixer — use this (not FadeProvider) for add/remove.</summary>
    public ISampleProvider MixerInput { get; } = mixerInput;

    public CompletionSampleProvider? Completion { get; } = completion;
}

/// <summary>
/// Engine-side bookkeeping for one playing sound — one or two <see cref="PlaybackChannel"/>s
/// (Both routes), plus the metadata needed to stop/fade/detect-completion without holding a
/// live reference to the originating <see cref="SoundItem"/>.
/// </summary>
internal sealed class PlaybackHandle : IDisposable
{
    public required string InstanceId { get; init; }
    public required string SoundId { get; init; }
    public required PlaybackInstance Instance { get; init; }
    public bool IsLooping { get; init; }
    public bool FadeOut { get; init; }
    public double FadeOutMs { get; init; }
    public List<PlaybackChannel> Channels { get; } = [];

    public void Dispose()
    {
        foreach (var channel in Channels)
        {
            try { channel.Stream.Dispose(); } catch { /* ignore */ }
        }
    }
}

/// <summary>
/// Adapts an arbitrary source channel count to a target channel count by downmixing to
/// mono and duplicating across the target channels. Only invoked when channel counts
/// actually differ — genuine stereo sources pass straight through untouched elsewhere,
/// so this never degrades normal mono/stereo content.
/// </summary>
internal sealed class ChannelAdaptSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = [];

    public ChannelAdaptSampleProvider(ISampleProvider source, WaveFormat targetFormat)
    {
        _source = source;
        _sourceChannels = Math.Max(1, source.WaveFormat.Channels);
        WaveFormat = targetFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var targetChannels = WaveFormat.Channels;
        var frames = count / targetChannels;
        var sourceNeeded = frames * _sourceChannels;

        if (_sourceBuffer.Length < sourceNeeded)
        {
            _sourceBuffer = new float[sourceNeeded];
        }

        var sourceRead = _source.Read(_sourceBuffer, 0, sourceNeeded);
        var framesRead = sourceRead / _sourceChannels;

        for (var frame = 0; frame < framesRead; frame++)
        {
            var mono = 0f;
            for (var ch = 0; ch < _sourceChannels; ch++)
            {
                mono += _sourceBuffer[frame * _sourceChannels + ch];
            }

            mono /= _sourceChannels;

            for (var ch = 0; ch < targetChannels; ch++)
            {
                buffer[offset + frame * targetChannels + ch] = mono;
            }
        }

        return framesRead * targetChannels;
    }
}

/// <summary>
/// Wraps a non-looping sound's final provider chain and flags <see cref="IsFinished"/> the
/// moment the underlying source runs out. Treats ANY short read (fewer samples than asked
/// for) as the end, not just a literal <c>Read() == 0</c> — confirmed via diagnostic logging
/// that NAudio's <c>MixingSampleProvider</c> stops calling Read() on a source entirely the
/// first time it returns fewer samples than requested, even if that count is nonzero. Waiting
/// for a strict zero-length read that would never come was exactly why playback could get
/// stuck "playing" forever: the one real end-of-stream read (some nonzero amount less than
/// asked for) was silently accepted without ever flipping this flag.
/// </summary>
internal sealed class CompletionSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public CompletionSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public bool IsFinished { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read < count)
        {
            IsFinished = true;
        }

        return read;
    }
}

/// <summary>
/// Wraps a sample provider so that reaching the end of the underlying stream seeks back to
/// the start instead of ending playback — used for <see cref="PlaybackMode.Loop"/> sounds.
/// </summary>
internal sealed class LoopSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveStream _stream;

    public LoopSampleProvider(ISampleProvider source, WaveStream stream)
    {
        _source = source;
        _stream = stream;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read < count)
        {
            // Same reasoning as CompletionSampleProvider: the real end-of-stream read can
            // come back with fewer samples than asked for rather than a literal zero, and
            // NAudio's mixer won't call Read() again to give this source another chance —
            // so treating only a strict 0 as "time to loop" could make a looping sound
            // silently go dead after one pass instead of restarting.
            _stream.Position = 0;
            var restarted = _source.Read(buffer, offset + read, count - read);
            read += restarted;
        }

        return read;
    }
}

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


/// <summary>
/// "Robot" voice effect — classic ring modulation: multiplies the signal by a low-frequency
/// carrier sine wave. Cheap, simple, and the standard technique behind the robotic-buzz voice
/// effect in countless real pedals/plugins.
/// </summary>
internal sealed class RingModulationSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private long _frameIndex;

    /// <summary>Carrier frequency in Hz — lower sounds more like a buzzy robot, higher starts
    /// to sound metallic/alien.</summary>
    public double FrequencyHz { get; set; } = 30;

    /// <summary>Carrier shape — Sine is the smooth classic buzz, Square is harsher/more
    /// robotic, Triangle is in between.</summary>
    public RobotWaveform Waveform { get; set; } = RobotWaveform.Sine;

    /// <summary>Dry/wet blend, 0 (unprocessed) to 1 (fully ring-modulated).</summary>
    public double Mix { get; set; } = 1.0;

    public RingModulationSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var frameCount = read / _channels;
        var mix = Math.Clamp(Mix, 0.0, 1.0);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var phase = FrequencyHz * _frameIndex / _sampleRate;
            phase -= Math.Floor(phase); // 0..1
            _frameIndex++;

            var carrier = Waveform switch
            {
                RobotWaveform.Square => phase < 0.5 ? 1.0 : -1.0,
                RobotWaveform.Triangle => 4.0 * Math.Abs(phase - 0.5) - 1.0,
                _ => Math.Sin(2.0 * Math.PI * phase)
            };

            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var dry = buffer[idx];
                var wet = dry * carrier;
                buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
            }
        }

        return read;
    }
}

/// <summary>
/// "Echo" voice effect — a feedback delay line: each output sample is the current input plus
/// a fraction (<see cref="Feedback"/>) of whatever was output <see cref="DelayMs"/> ago, which
/// itself already contains its own earlier echoes — producing the standard decaying repeat
/// train of a real echo, entirely from one small circular buffer.
/// </summary>
internal sealed class EchoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _delayBuffer;
    private int _delayFrames;
    private int _writePos;
    private float[] _sourceReadBuffer = [];

    public double DelayMs { get; set; } = 250;

    /// <summary>0-0.9 — capped below 1.0 since at or above that the feedback loop never decays
    /// and builds into runaway noise instead of settling into a normal echo tail.</summary>
    public double Feedback { get; set; } = 0.35;

    /// <summary>Dry/wet blend, 0 (unprocessed) to 1 (fully echoed). Deliberately doesn't affect
    /// the feedback recursion itself (see Read()) — only how much of the echoed signal actually
    /// reaches the output, so a low Mix still sounds like a genuine (just quieter) echo tail
    /// rather than a shorter/weaker one.</summary>
    public double Mix { get; set; } = 1.0;

    public EchoSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _delayFrames = Math.Max(1, (int)(WaveFormat.SampleRate * (DelayMs / 1000.0)));
        _delayBuffer = new float[_delayFrames * _channels];
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        EnsureDelayBufferSize();

        if (_sourceReadBuffer.Length < count)
        {
            _sourceReadBuffer = new float[count];
        }

        var read = _source.Read(_sourceReadBuffer, 0, count);
        var frameCount = read / _channels;
        var feedback = (float)Math.Clamp(Feedback, 0.0, 0.9);
        var mix = Math.Clamp(Mix, 0.0, 1.0);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var delaySlot = _writePos * _channels + ch;
                var dry = _sourceReadBuffer[frame * _channels + ch];
                var delayed = _delayBuffer[delaySlot];
                var wetSignal = dry + delayed * feedback;

                // The recursion always uses the full wet signal regardless of Mix — Mix only
                // affects how much of it reaches the actual output below.
                _delayBuffer[delaySlot] = wetSignal;
                buffer[offset + frame * _channels + ch] = (float)(dry * (1.0 - mix) + wetSignal * mix);
            }

            _writePos = (_writePos + 1) % _delayFrames;
        }

        return read;
    }

    /// <summary>Rebuilds the delay buffer if DelayMs changed enough to need a different size
    /// (the user moved the slider) — checked lazily here each Read() rather than reacting to
    /// the property setter directly, since resizing also needs the sample rate, which this
    /// class already has on hand via WaveFormat.</summary>
    private void EnsureDelayBufferSize()
    {
        var targetFrames = Math.Max(1, (int)(WaveFormat.SampleRate * (DelayMs / 1000.0)));
        if (targetFrames == _delayFrames) return;

        _delayFrames = targetFrames;
        _delayBuffer = new float[_delayFrames * _channels];
        _writePos = 0;
    }
}

/// <summary>
/// "Distortion" voice effect — soft-clip saturation via tanh: the signal is amplified by
/// <see cref="Drive"/> then run through tanh, which smoothly compresses anything pushed past
/// roughly ±1 rather than harshly chopping it off (a hard clamp), giving a warmer, less
/// digital-sounding "fuzz" than a literal clip would. Stateless per-sample — no buffer needed.
/// </summary>
internal sealed class DistortionSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    /// <summary>How hard the signal is driven into saturation before clipping — higher is a
    /// harsher, more aggressive distortion. 1.0 is essentially no drive (barely audible).</summary>
    public double Drive { get; set; } = 5.0;

    /// <summary>Dry/wet blend, 0 (unprocessed) to 1 (fully distorted).</summary>
    public double Mix { get; set; } = 1.0;

    public DistortionSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var drive = Math.Max(1.0, Drive);
        var mix = Math.Clamp(Mix, 0.0, 1.0);

        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var dry = buffer[idx];
            var wet = Math.Tanh(dry * drive);
            buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
        }

        return read;
    }
}

/// <summary>
/// "Formant Shift" — a spectral-tilt EQ (a high-shelf filter around ~2.5kHz, using the standard
/// RBJ Audio EQ Cookbook biquad design) applied on top of whichever effect above is active.
/// Positive brightens the voice's timbre (perceptually reads as "smaller"/more feminine),
/// negative darkens it (reads as "larger"/more masculine). This is NOT true formant shifting —
/// real formant shifting moves the actual resonance peaks via full spectral-envelope analysis
/// and resynthesis (LPC or similar), which is a much bigger, riskier undertaking than everything
/// else in the Voice Changer combined. This is the same category of technique (EQ-based timbre
/// tilt) that many real consumer voice changers actually use for the same practical effect —
/// combined with a pitch shift, it gives a noticeably more natural, distinct "different voice"
/// character than pitch alone, which by itself just sounds like the same voice sped up or
/// slowed down ("chipmunk"/"helium").
/// </summary>
internal sealed class FormantShiftSampleProvider : ISampleProvider
{
    private const double ShelfFrequencyHz = 2500;
    private const double Slope = 1.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly double[] _x1, _x2, _y1, _y2; // per-channel biquad history

    /// <summary>-12 to +12, treated like a shelf gain in dB. 0 = bypass.</summary>
    public double FormantShift { get; set; }

    public FormantShiftSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;
        _x1 = new double[_channels];
        _x2 = new double[_channels];
        _y1 = new double[_channels];
        _y2 = new double[_channels];
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        if (Math.Abs(FormantShift) < 0.01)
        {
            return read; // Bypass entirely when neutral — no filtering artifacts for free.
        }

        var frameCount = read / _channels;
        var (b0, b1, b2, a1, a2) = ComputeHighShelfCoefficients(FormantShift, _sampleRate);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var x0 = buffer[idx];

                var y0 = b0 * x0 + b1 * _x1[ch] + b2 * _x2[ch] - a1 * _y1[ch] - a2 * _y2[ch];

                _x2[ch] = _x1[ch];
                _x1[ch] = x0;
                _y2[ch] = _y1[ch];
                _y1[ch] = y0;

                buffer[idx] = (float)y0;
            }
        }

        return read;
    }

    /// <summary>Standard RBJ Audio EQ Cookbook high-shelf biquad coefficients, normalized by a0
    /// (so they can be applied directly without a division per sample).</summary>
    private static (double b0, double b1, double b2, double a1, double a2) ComputeHighShelfCoefficients(double formantShift, int sampleRate)
    {
        var dbGain = Math.Clamp(formantShift, -12.0, 12.0);
        var a = Math.Pow(10.0, dbGain / 40.0);
        var w0 = 2.0 * Math.PI * ShelfFrequencyHz / sampleRate;
        var cosW0 = Math.Cos(w0);
        var sinW0 = Math.Sin(w0);
        var alpha = sinW0 / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / Slope - 1.0) + 2.0);
        var sqrtA = Math.Sqrt(a);

        var b0 = a * ((a + 1) + (a - 1) * cosW0 + 2 * sqrtA * alpha);
        var b1 = -2 * a * ((a - 1) + (a + 1) * cosW0);
        var b2 = a * ((a + 1) + (a - 1) * cosW0 - 2 * sqrtA * alpha);
        var a0 = (a + 1) - (a - 1) * cosW0 + 2 * sqrtA * alpha;
        var a1 = 2 * ((a - 1) - (a + 1) * cosW0);
        var a2 = (a + 1) - (a - 1) * cosW0 - 2 * sqrtA * alpha;

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

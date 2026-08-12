using NAudio.Wave;
using Soundboard.Core.Models;

namespace Soundboard.Audio;

/// <summary>
/// The Voice Changer's live effect chain — a single stack of independently toggleable steps
/// (Formant, Robot, Distortion, Overdrive, Delay, Echo, Reverb, Proximity) processed in a fixed
/// pipeline order — waveshaping (Robot/Distortion/Overdrive) before time-based effects
/// (Delay/Echo/Reverb) before final distance shaping (Proximity) — rather than the old "pick
/// exactly one effect" model. Each step is just an <c>if (enabled)</c> branch inside one Read()
/// loop with its own private state, not a separately wrapped <see cref="ISampleProvider"/> —
/// that's what lets every step's enable flag AND every parameter be toggled live (see
/// MicrophoneMonitor.UpdateEffectParameters) with no capture teardown/rebuild, and lets the
/// final Strength blend compare against one dry copy captured up front regardless of which
/// steps are on.
///
/// Pitch shifting is NOT a step here — it's a separate, structural wrap
/// (<see cref="PhaseVocoderProvider"/>) applied OUTSIDE this class when enabled, both because it
/// fundamentally changes the sample timeline (FFT frame buffering) rather than processing
/// sample-for-sample like everything here, and because "partially" pitch-shifting doesn't mean
/// anything sensible the way a wet/dry blend does for the other effects — blending two different
/// pitches of the same signal together sounds like a detune/chorus, not "less shifted." Strength
/// therefore blends against whatever comes INTO this class — the raw mic if Pitch is off, the
/// already pitch-shifted voice if Pitch is on — not literally your original unshifted voice.
///
/// One instance per live chain, same rule as everything else in this file — see
/// MicrophoneMonitor's class remarks on why passthrough and preview each need their own.
/// </summary>
internal sealed class VoiceEffectStackProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _sampleRate;

    /// <summary>True when Pitch is enabled upstream and already applying formant warping via
    /// the phase vocoder's own (more accurate) cepstral envelope correction — this class's own
    /// EQ-tilt Formant step is skipped in that case so the two don't stack.</summary>
    private readonly bool _formantHandledExternally;

    private float[] _dryBuffer = [];

    // -- Formant (EQ-tilt shelf) ---------------------------------------------------------------
    public bool FormantEnabled { get; set; }
    public double FormantShift { get; set; }
    private readonly double[] _formantX1, _formantX2, _formantY1, _formantY2;

    // -- Robot (ring modulation) ----------------------------------------------------------------
    public bool RobotEnabled { get; set; }
    public double RobotFrequencyHz { get; set; } = 30;
    public RobotWaveform RobotWaveform { get; set; } = RobotWaveform.Sine;
    public double RobotMix { get; set; } = 1.0;
    private long _robotFrameIndex;

    // -- Distortion (symmetric tanh soft-clip — smooth, only odd harmonics) --------------------
    public bool DistortionEnabled { get; set; }
    public double DistortionDrive { get; set; } = 5.0;
    public double DistortionMix { get; set; } = 1.0;

    // -- Overdrive (asymmetric exponential clip — a harder knee than tanh, and a different
    // curve above/below zero, which is what actually gives it a distinct character rather than
    // being the same saturation renamed: symmetric curves like tanh only add odd harmonics,
    // asymmetric ones like this also add even harmonics) -----------------------------------------
    public bool OverdriveEnabled { get; set; }
    public double OverdriveDrive { get; set; } = 4.0;
    public double OverdriveMix { get; set; } = 1.0;

    // -- Delay (a single clean repeat — no feedback write-back, unlike Echo below) --------------
    public bool DelayEnabled { get; set; }
    public double DelayMs { get; set; } = 150;
    public double DelayMix { get; set; } = 0.5;
    private float[] _delayBuffer = [];
    private int _delayFrames;
    private int _delayWritePos;

    // -- Echo (feedback delay line — a trailing, decaying repeat) --------------------------------
    public bool EchoEnabled { get; set; }
    public double EchoDelayMs { get; set; } = 250;
    public double EchoFeedback { get; set; } = 0.35;
    public double EchoMix { get; set; } = 1.0;
    private float[] _echoBuffer = [];
    private int _echoFrames;
    private int _echoWritePos;

    // -- Reverb (simplified Schroeder-style: parallel comb filters summed) ----------------------
    public bool ReverbEnabled { get; set; }
    public double ReverbRoomSize { get; set; } = 1.0;
    public double ReverbDecay { get; set; } = 0.5;
    public double ReverbMix { get; set; } = 0.35;
    private readonly CombFilter[] _combFilters;

    // -- Proximity (simulated distance — gain + low-pass rolloff) -------------------------------
    public bool ProximityEnabled { get; set; }
    public double ProximityDistance { get; set; }
    public double ProximityMix { get; set; } = 1.0;
    private readonly double[] _proximityLowpassState;

    // -- Global Strength — blends everything above against the dry signal captured at the top
    // of Read() ------------------------------------------------------------------------------
    public double Strength { get; set; } = 1.0;

    public VoiceEffectStackProvider(ISampleProvider source, bool formantHandledExternally)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;
        _formantHandledExternally = formantHandledExternally;

        _formantX1 = new double[_channels];
        _formantX2 = new double[_channels];
        _formantY1 = new double[_channels];
        _formantY2 = new double[_channels];
        _proximityLowpassState = new double[_channels];

        _combFilters =
        [
            new CombFilter(29.7, _channels, _sampleRate),
            new CombFilter(37.1, _channels, _sampleRate),
            new CombFilter(41.1, _channels, _sampleRate),
            new CombFilter(43.7, _channels, _sampleRate)
        ];
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        if (_dryBuffer.Length < read)
        {
            _dryBuffer = new float[read];
        }
        Array.Copy(buffer, offset, _dryBuffer, 0, read);

        var frameCount = read / _channels;

        if (FormantEnabled && !_formantHandledExternally && Math.Abs(FormantShift) > 0.01)
        {
            ApplyFormant(buffer, offset, frameCount);
        }

        if (RobotEnabled)
        {
            ApplyRobot(buffer, offset, frameCount);
        }

        if (DistortionEnabled)
        {
            ApplyDistortion(buffer, offset, read);
        }

        if (OverdriveEnabled)
        {
            ApplyOverdrive(buffer, offset, read);
        }

        if (DelayEnabled)
        {
            ApplyDelay(buffer, offset, frameCount);
        }

        if (EchoEnabled)
        {
            ApplyEcho(buffer, offset, frameCount);
        }

        if (ReverbEnabled)
        {
            ApplyReverb(buffer, offset, frameCount);
        }

        if (ProximityEnabled)
        {
            ApplyProximity(buffer, offset, frameCount);
        }

        var strength = Math.Clamp(Strength, 0.0, 1.0);
        if (strength < 1.0)
        {
            for (var i = 0; i < read; i++)
            {
                var idx = offset + i;
                buffer[idx] = (float)(_dryBuffer[i] * (1.0 - strength) + buffer[idx] * strength);
            }
        }

        return read;
    }

    private void ApplyFormant(float[] buffer, int offset, int frameCount)
    {
        var (b0, b1, b2, a1, a2) = ComputeHighShelfCoefficients(FormantShift, _sampleRate);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var x0 = buffer[idx];

                var y0 = b0 * x0 + b1 * _formantX1[ch] + b2 * _formantX2[ch] - a1 * _formantY1[ch] - a2 * _formantY2[ch];

                _formantX2[ch] = _formantX1[ch];
                _formantX1[ch] = x0;
                _formantY2[ch] = _formantY1[ch];
                _formantY1[ch] = y0;

                buffer[idx] = (float)y0;
            }
        }
    }

    /// <summary>Standard RBJ Audio EQ Cookbook high-shelf biquad coefficients, normalized by a0
    /// (so they can be applied directly without a division per sample).</summary>
    private static (double b0, double b1, double b2, double a1, double a2) ComputeHighShelfCoefficients(double formantShift, int sampleRate)
    {
        const double shelfFrequencyHz = 2500;
        const double slope = 1.0;

        var dbGain = Math.Clamp(formantShift, -12.0, 12.0);
        var a = Math.Pow(10.0, dbGain / 40.0);
        var w0 = 2.0 * Math.PI * shelfFrequencyHz / sampleRate;
        var cosW0 = Math.Cos(w0);
        var sinW0 = Math.Sin(w0);
        var alpha = sinW0 / 2.0 * Math.Sqrt((a + 1.0 / a) * (1.0 / slope - 1.0) + 2.0);
        var sqrtA = Math.Sqrt(a);

        var b0 = a * ((a + 1) + (a - 1) * cosW0 + 2 * sqrtA * alpha);
        var b1 = -2 * a * ((a - 1) + (a + 1) * cosW0);
        var b2 = a * ((a + 1) + (a - 1) * cosW0 - 2 * sqrtA * alpha);
        var a0 = (a + 1) - (a - 1) * cosW0 + 2 * sqrtA * alpha;
        var a1 = 2 * ((a - 1) - (a + 1) * cosW0);
        var a2 = (a + 1) - (a - 1) * cosW0 - 2 * sqrtA * alpha;

        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private void ApplyRobot(float[] buffer, int offset, int frameCount)
    {
        var mix = Math.Clamp(RobotMix, 0.0, 1.0);

        for (var frame = 0; frame < frameCount; frame++)
        {
            var phase = RobotFrequencyHz * _robotFrameIndex / _sampleRate;
            phase -= Math.Floor(phase);
            _robotFrameIndex++;

            var carrier = RobotWaveform switch
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
    }

    private void ApplyDistortion(float[] buffer, int offset, int read)
    {
        var drive = Math.Max(1.0, DistortionDrive);
        var mix = Math.Clamp(DistortionMix, 0.0, 1.0);

        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var dry = buffer[idx];
            var wet = Math.Tanh(dry * drive);
            buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
        }
    }

    private void ApplyOverdrive(float[] buffer, int offset, int read)
    {
        var drive = Math.Max(1.0, OverdriveDrive);
        var mix = Math.Clamp(OverdriveMix, 0.0, 1.0);

        for (var i = 0; i < read; i++)
        {
            var idx = offset + i;
            var dry = buffer[idx];
            var driven = dry * drive;

            // Asymmetric exponential clip — see the class-level remarks on why this (rather
            // than another tanh) is what actually differentiates Overdrive from Distortion.
            var wet = driven >= 0
                ? 1.0 - Math.Exp(-driven)
                : -(1.0 - Math.Exp(driven)) * 0.85;

            buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
        }
    }

    private void ApplyDelay(float[] buffer, int offset, int frameCount)
    {
        var targetFrames = Math.Max(1, (int)(_sampleRate * (DelayMs / 1000.0)));
        if (targetFrames != _delayFrames)
        {
            _delayFrames = targetFrames;
            _delayBuffer = new float[_delayFrames * _channels];
            _delayWritePos = 0;
        }

        var mix = Math.Clamp(DelayMix, 0.0, 1.0);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var slot = _delayWritePos * _channels + ch;
                var dry = buffer[idx];
                var delayed = _delayBuffer[slot];

                // No feedback write-back (unlike Echo below) — just the dry signal stored for
                // later playback, so this is always exactly one clean repeat, not a trail.
                _delayBuffer[slot] = dry;
                buffer[idx] = (float)(dry * (1.0 - mix) + (dry + delayed) * mix);
            }

            _delayWritePos = (_delayWritePos + 1) % _delayFrames;
        }
    }

    private void ApplyEcho(float[] buffer, int offset, int frameCount)
    {
        var targetFrames = Math.Max(1, (int)(_sampleRate * (EchoDelayMs / 1000.0)));
        if (targetFrames != _echoFrames)
        {
            _echoFrames = targetFrames;
            _echoBuffer = new float[_echoFrames * _channels];
            _echoWritePos = 0;
        }

        var feedback = (float)Math.Clamp(EchoFeedback, 0.0, 0.9);
        var mix = Math.Clamp(EchoMix, 0.0, 1.0);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var slot = _echoWritePos * _channels + ch;
                var dry = buffer[idx];
                var delayed = _echoBuffer[slot];
                var wetSignal = dry + delayed * feedback;

                _echoBuffer[slot] = wetSignal;
                buffer[idx] = (float)(dry * (1.0 - mix) + wetSignal * mix);
            }

            _echoWritePos = (_echoWritePos + 1) % _echoFrames;
        }
    }

    private void ApplyReverb(float[] buffer, int offset, int frameCount)
    {
        var roomSize = Math.Clamp(ReverbRoomSize, 0.1, 2.0);
        var decay = (float)Math.Clamp(ReverbDecay, 0.0, 0.9);
        var mix = Math.Clamp(ReverbMix, 0.0, 1.0);

        foreach (var comb in _combFilters)
        {
            comb.EnsureSize(roomSize);
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var dry = buffer[idx];

                var combSum = 0f;
                foreach (var comb in _combFilters)
                {
                    combSum += comb.Process(ch, dry, decay);
                }
                var wet = combSum / _combFilters.Length;

                buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
            }
        }
    }

    private void ApplyProximity(float[] buffer, int offset, int frameCount)
    {
        var distance = Math.Clamp(ProximityDistance, 0.0, 1.0);
        var mix = Math.Clamp(ProximityMix, 0.0, 1.0);
        var gain = 1.0 - distance * 0.7;

        // One-pole low-pass — cutoff drops from ~9kHz (barely audible effect) toward ~1.2kHz as
        // distance increases, approximating how high frequencies attenuate faster than lows
        // over distance. Cheap (one multiply-add per sample) and stable, unlike a biquad, which
        // would be overkill for what's meant to read as "farther away," not a precise acoustic
        // model.
        var cutoffHz = 9000.0 - distance * 7800.0;
        var rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        var dt = 1.0 / _sampleRate;
        var alpha = dt / (rc + dt);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var idx = offset + frame * _channels + ch;
                var dry = buffer[idx];

                _proximityLowpassState[ch] += alpha * (dry - _proximityLowpassState[ch]);
                var wet = _proximityLowpassState[ch] * gain;

                buffer[idx] = (float)(dry * (1.0 - mix) + wet * mix);
            }
        }
    }

    /// <summary>One comb filter (a feedback delay line) — several of these summed in parallel,
    /// each a different length, is the classic cheap "Schroeder reverb" building block: the
    /// differing, non-harmonically-related lengths smear a single input into a dense, natural-
    /// sounding decay instead of a single audible repeat like Echo. This is a simplified version
    /// (no allpass diffusion stage) appropriate for a live, low-latency voice effect — not a
    /// studio-grade algorithm.</summary>
    private sealed class CombFilter(double baseMs, int channels, int sampleRate)
    {
        private float[] _buffer = [];
        private int _frames;
        private int _writePos;
        private double _lastRoomSize = -1;

        public void EnsureSize(double roomSize)
        {
            if (Math.Abs(roomSize - _lastRoomSize) < 0.001 && _buffer.Length > 0) return;

            _lastRoomSize = roomSize;
            _frames = Math.Max(1, (int)(sampleRate * (baseMs * roomSize / 1000.0)));
            _buffer = new float[_frames * channels];
            _writePos = 0;
        }

        /// <summary>Must be called once per channel per frame, in channel order (0..channels-1),
        /// before moving to the next frame — matches how <see cref="ApplyReverb"/> actually
        /// calls it, and is what lets this advance its write position only after the last
        /// channel of each frame rather than needing a separate frame-boundary call.</summary>
        public float Process(int channel, float input, float decay)
        {
            if (_buffer.Length == 0) return input;

            var slot = _writePos * channels + channel;
            var delayed = _buffer[slot];
            var output = input + delayed * decay;
            _buffer[slot] = output;

            if (channel == channels - 1)
            {
                _writePos = (_writePos + 1) % _frames;
            }

            return output;
        }
    }
}

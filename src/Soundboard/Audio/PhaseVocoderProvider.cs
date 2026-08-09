using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>
/// A proper phase-vocoder pitch shifter with independent formant control — replaces the
/// previous two-tap delay-line pitch shifter for the "Pitch" voice effect specifically.
///
/// The delay-line technique was a time-domain trick (fundamentally the same operation as
/// changing playback speed): it moved formants along with pitch, which is what gave shifted
/// voices their "chipmunk/helium" quality no matter how small the shift. This does the real
/// thing real pitch/formant tools do: an FFT-based analysis of each frame, separating the
/// spectral envelope (formants — the vocal tract's resonance shape) from the excitation
/// (the harmonic/pitch content), shifting pitch via classic phase-vocoder phase tracking,
/// warping the envelope independently by a separate ratio, and recombining before resynthesis.
///
/// Honest trade-offs versus the old technique: this adds real latency (~1 FFT frame + hop,
/// ~58ms at 44.1kHz, before the first output) since it has to buffer enough audio to analyze,
/// and phase vocoders have their own characteristic artifact (a "phasy"/smeared quality,
/// mostly on transients and sharp consonants — sustained vowels hold up well). This is
/// implemented per the standard, correct math; getting the exact frame size/cepstral cutoff
/// tuned for what actually sounds best needs real listening and iteration.
/// </summary>
internal sealed class PhaseVocoderProvider : ISampleProvider
{
    private const int FftSize = 2048;
    private const int HopAnalysis = FftSize / 4; // 512 — 75% overlap, satisfies COLA for a Hann window
    private const int Bins = FftSize / 2 + 1;
    private const int CepstrumOrder = 30; // how many low-quefrency coefficients form the smoothed envelope

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly double[] _hannWindow;
    private readonly double[] _hannWindowSquared;
    private readonly ChannelState[] _channelStates;
    private float[] _sourceReadBuffer = [];

    /// <summary>2^(semitones/12). 1.0 = no shift.</summary>
    public float PitchRatio { get; set; } = 1f;

    /// <summary>2^(formantShift/12). 1.0 = formants left exactly as recorded.</summary>
    public float FormantRatio { get; set; } = 1f;

    public PhaseVocoderProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = Math.Max(1, source.WaveFormat.Channels);

        _hannWindow = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            // Deliberately the PERIODIC Hann definition (divide by FftSize, not FftSize - 1).
            // The symmetric variant (the usual textbook/filter-design Hann) breaks the
            // constant-overlap-add identity this relies on: with the window applied twice
            // (analysis and synthesis) at 4x overlap (hop = FftSize/4), the periodic variant's
            // squared-window sum is provably exactly constant across all 4 overlapping frames
            // (the oscillating terms cancel exactly over 4 quarter-period-shifted copies) — the
            // symmetric variant doesn't cancel as cleanly, leaving a small ripple that shows up
            // as a rhythmic volume "pumping" at the hop rate (~86 Hz at 44.1kHz/512 hop).
            _hannWindow[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / FftSize));
        }

        // The window is applied once at analysis (before the forward FFT) and once again at
        // synthesis (before overlap-add), so the actual per-sample contribution to the
        // reconstruction is the SQUARED window value — tracked per-sample below so the output
        // can be normalized by exactly how much window energy really landed there, rather than
        // assuming a fixed hop. A fixed constant (2/3) only holds when the overlap-add hop is
        // exactly FftSize/4 — true when PitchRatio is 1, false for every actual pitch shift,
        // since the synthesis hop is HopAnalysis * PitchRatio. That mismatch was the real source
        // of the amplitude "wobble" reported as glitchiness, not the excitation floor or output
        // clipping — those were downstream symptoms, not the cause.
        _hannWindowSquared = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            _hannWindowSquared[i] = _hannWindow[i] * _hannWindow[i];
        }

        _channelStates = new ChannelState[_channels];
        for (var ch = 0; ch < _channels; ch++)
        {
            _channelStates[ch] = new ChannelState();
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (Math.Abs(PitchRatio - 1f) < 0.001f && Math.Abs(FormantRatio - 1f) < 0.001f)
        {
            // Bypass the whole (latency-adding) pipeline for the common "enabled but centered"
            // case — same reasoning as the old pitch shifter's own bypass.
            return _source.Read(buffer, offset, count);
        }

        if (_sourceReadBuffer.Length < count)
        {
            _sourceReadBuffer = new float[count];
        }

        var read = _source.Read(_sourceReadBuffer, 0, count);
        var frameCount = read / _channels;

        for (var ch = 0; ch < _channels; ch++)
        {
            var state = _channelStates[ch];
            for (var frame = 0; frame < frameCount; frame++)
            {
                state.InputQueue.Add(_sourceReadBuffer[frame * _channels + ch]);
            }

            ProcessChannel(state);
        }

        var outputFrameCount = count / _channels;
        for (var frame = 0; frame < outputFrameCount; frame++)
        {
            for (var ch = 0; ch < _channels; ch++)
            {
                var state = _channelStates[ch];
                var idx = offset + frame * _channels + ch;
                if (state.OutputQueue.Count > 0)
                {
                    buffer[idx] = state.OutputQueue.Dequeue();
                }
                else
                {
                    buffer[idx] = 0f; // Startup latency / temporarily starved — silence, not garbage.
                }
            }
        }

        return count;
    }

    private void ProcessChannel(ChannelState state)
    {
        while (state.InputQueue.Count >= FftSize)
        {
            RunOneFrame(state);
            state.InputQueue.RemoveRange(0, HopAnalysis);
        }

        DrainResampledOutput(state);
    }

    private void RunOneFrame(ChannelState state)
    {
        var real = state.Real;
        var imag = state.Imag;
        for (var i = 0; i < FftSize; i++)
        {
            real[i] = state.InputQueue[i] * _hannWindow[i];
            imag[i] = 0.0;
        }

        Fft.Transform(real, imag, inverse: false);

        var magnitude = state.Magnitude;
        var phase = state.Phase;
        for (var k = 0; k < Bins; k++)
        {
            magnitude[k] = Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            phase[k] = Math.Atan2(imag[k], real[k]);
        }

        var envelope = state.Envelope;
        ExtractEnvelope(state, magnitude, envelope);

        // This floor only exists to stop division by a genuinely near-zero envelope value
        // (numerical noise floor, not a real spectral valley) — normal formant nulls and
        // high-frequency rolloff routinely sit 40-80dB below a frame's peak and must NOT be
        // caught here, or excitation gets overridden across most of the spectrum on any loud
        // frame instead of just the rare pathological bin. -100dB relative (1e-5) only catches
        // the pathological case; a wider floor (e.g. 1e-2) was tried and made things worse.
        var peakMagnitude = 0.0;
        for (var k = 0; k < Bins; k++)
        {
            if (magnitude[k] > peakMagnitude) peakMagnitude = magnitude[k];
        }

        var envelopeFloor = Math.Max(peakMagnitude * 1e-5, 1e-6);

        var excitation = state.Excitation;
        for (var k = 0; k < Bins; k++)
        {
            excitation[k] = magnitude[k] / Math.Max(envelope[k], envelopeFloor);
        }

        var warpedEnvelope = state.WarpedEnvelope;
        WarpEnvelope(envelope, FormantRatio, warpedEnvelope);

        var newMagnitude = state.NewMagnitude;
        for (var k = 0; k < Bins; k++)
        {
            var expectedAdvance = 2.0 * Math.PI * k * HopAnalysis / FftSize;
            var delta = WrapPhase(phase[k] - state.LastPhase[k] - expectedAdvance);
            var trueFreqPerSample = 2.0 * Math.PI * k / FftSize + delta / HopAnalysis;

            var hopSynthesis = HopAnalysis * PitchRatio;
            state.SumPhase[k] += trueFreqPerSample * hopSynthesis;
            state.LastPhase[k] = phase[k];

            newMagnitude[k] = excitation[k] * warpedEnvelope[k];
        }

        var synReal = state.SynReal;
        var synImag = state.SynImag;
        for (var k = 0; k < Bins; k++)
        {
            synReal[k] = newMagnitude[k] * Math.Cos(state.SumPhase[k]);
            synImag[k] = newMagnitude[k] * Math.Sin(state.SumPhase[k]);
            if (k > 0 && k < FftSize - k)
            {
                synReal[FftSize - k] = synReal[k];
                synImag[FftSize - k] = -synImag[k];
            }
        }

        Fft.Transform(synReal, synImag, inverse: true);

        // localStart rounds to the nearest integer sample only at the point of use; the
        // accumulator itself (below) keeps the exact fractional position so rounding error
        // doesn't compound frame after frame. Rounding once and then advancing by the ROUNDED
        // amount each time (the previous approach) throws away the leftover fraction every
        // single frame, and since it's the same hop every time, that leftover doesn't average
        // out — it accumulates linearly and never corrects itself, which over a sustained
        // held note is enough drift to be audible as instability.
        var localStart = (int)Math.Round(state.StretchedWritePos - state.StretchedBaseOffset);

        // The position this frame starts writing at is the last one any future frame could ever
        // reach back to (frame starts only increase) — so anything from here on is the only part
        // of the buffer NOT yet guaranteed its full complement of overlapping contributions.
        // DrainResampledOutput must never read past this, or it hands out the raw, single-frame,
        // not-yet-summed tail (which tapers toward zero, since the Hann window itself tapers to
        // zero at the frame boundary) instead of waiting for it to be properly overlapped —
        // producing exactly the "decays to silence, then jumps back" artifact that was reported.
        state.SafeReadLimitAbsolute = state.StretchedWritePos;

        EnsureLength(state.StretchedBuffer, localStart + FftSize);
        EnsureLength(state.StretchedWindowSum, localStart + FftSize);
        for (var i = 0; i < FftSize; i++)
        {
            state.StretchedBuffer[localStart + i] += (float)(synReal[i] * _hannWindow[i]);
            state.StretchedWindowSum[localStart + i] += (float)_hannWindowSquared[i];
        }

        state.StretchedWritePos += HopAnalysis * PitchRatio;
    }

    /// <summary>Cepstral liftering: separates the smooth spectral envelope (formants) from a
    /// magnitude spectrum by taking the log-magnitude's own "spectrum" (the cepstrum), keeping
    /// only its low-quefrency coefficients (the smooth part), and transforming back. Writes into
    /// the caller's reused scratch buffers rather than allocating.</summary>
    private static void ExtractEnvelope(ChannelState state, double[] magnitude, double[] envelopeOut)
    {
        var logMag = state.LogMag;
        var cepImag = state.CepImag;

        for (var k = 0; k < Bins; k++)
        {
            logMag[k] = Math.Log(Math.Max(magnitude[k], 1e-6));
            if (k > 0 && k < FftSize - k)
            {
                logMag[FftSize - k] = logMag[k];
            }
        }

        // logMag above is fully overwritten every call (the direct + mirrored loop covers every
        // index), but cepImag is reused across frames and the inverse transform below treats it
        // as the imaginary part of a purely-real input — it must start at zero, not whatever the
        // previous frame left behind.
        Array.Clear(cepImag, 0, FftSize);
        Fft.Transform(logMag, cepImag, inverse: true);

        for (var i = CepstrumOrder; i < FftSize - CepstrumOrder; i++)
        {
            logMag[i] = 0;
            cepImag[i] = 0;
        }

        Fft.Transform(logMag, cepImag, inverse: false);

        for (var k = 0; k < Bins; k++)
        {
            envelopeOut[k] = Math.Exp(logMag[k]);
        }
    }

    /// <summary>Resamples the envelope along the frequency axis — formantRatio &gt; 1 moves
    /// resonances up (brighter/smaller-sounding), &lt; 1 moves them down (darker/larger). Writes
    /// into the caller's reused scratch buffer rather than allocating.</summary>
    private static void WarpEnvelope(double[] envelope, double formantRatio, double[] warpedOut)
    {
        if (Math.Abs(formantRatio - 1.0) < 0.001)
        {
            Array.Copy(envelope, warpedOut, Bins);
            return;
        }

        for (var k = 0; k < Bins; k++)
        {
            var sourceBin = k / formantRatio;
            if (sourceBin <= 0)
            {
                warpedOut[k] = envelope[0];
                continue;
            }

            if (sourceBin >= Bins - 1)
            {
                warpedOut[k] = envelope[Bins - 1];
                continue;
            }

            var lower = (int)Math.Floor(sourceBin);
            var frac = sourceBin - lower;
            warpedOut[k] = envelope[lower] * (1 - frac) + envelope[lower + 1] * frac;
        }
    }

    /// <summary>Leaves anything within normal range (below -3dBFS) completely untouched, and
    /// only smoothly saturates genuine overshoot toward ±1 instead of flat-top clipping it — a
    /// hard clamp was tried first and made things worse (flat-top clipping is a harsher,
    /// more audible artifact than whatever it was replacing).</summary>
    private static double SoftLimit(double sample)
    {
        const double threshold = 0.9;
        var abs = Math.Abs(sample);
        if (abs <= threshold) return sample;

        var excess = abs - threshold;
        var compressed = threshold + (1.0 - threshold) * Math.Tanh(excess / (1.0 - threshold));
        return Math.Sign(sample) * compressed;
    }

    private static double WrapPhase(double phase)
    {
        phase %= 2.0 * Math.PI;
        if (phase > Math.PI) phase -= 2.0 * Math.PI;
        if (phase < -Math.PI) phase += 2.0 * Math.PI;
        return phase;
    }

    private static void EnsureLength(List<float> list, int requiredLength)
    {
        while (list.Count < requiredLength)
        {
            list.Add(0f);
        }
    }

    /// <summary>Drains as much of the time-stretched buffer as is currently available into
    /// OutputQueue, resampling by PitchRatio — this final resample is what actually turns the
    /// phase vocoder's pitch-preserved time-stretch into an audible pitch shift, bringing
    /// duration back to the original real-time rate in the process.</summary>
    private void DrainResampledOutput(ChannelState state)
    {
        // Never read at or past the most recent frame's own start position — everything from
        // there onward is that frame's own raw, not-yet-overlapped tail (or plain unwritten
        // padding), not a properly summed reconstruction. See the remarks where
        // SafeReadLimitAbsolute is set in RunOneFrame for why draining past it produces a
        // decay-to-silence-then-jump artifact instead of a clean, continuous signal. A couple of
        // samples of extra margin absorb the rounding in localStart's own Math.Round without
        // risking a hairline race right at the boundary.
        var safeLocalLimit = state.SafeReadLimitAbsolute - state.StretchedBaseOffset - 2;

        while (true)
        {
            var localPos = state.ResampleReadPos - state.StretchedBaseOffset;
            var lower = (int)Math.Floor(localPos);
            if (lower + 1 >= state.StretchedBuffer.Count) break;
            if (lower + 1 >= safeLocalLimit) break;

            var frac = localPos - lower;

            // Normalize each raw sample by how much window energy actually landed there before
            // interpolating — this is what makes the reconstruction correct for ANY synthesis
            // hop (i.e. any PitchRatio), not just the one hop a fixed gain constant would cover.
            // Floored well below the ~1.5 steady-state sum so the very first frame or two at
            // stream startup (before overlap has fully built up) don't get amplified instead of
            // just fading in like a normal window taper.
            const double windowSumFloor = 0.3;
            var normLower = state.StretchedBuffer[lower] / Math.Max(state.StretchedWindowSum[lower], windowSumFloor);
            var normUpper = state.StretchedBuffer[lower + 1] / Math.Max(state.StretchedWindowSum[lower + 1], windowSumFloor);
            var sample = normLower * (1 - frac) + normUpper * frac;

            state.OutputQueue.Enqueue((float)SoftLimit(sample));
            state.ResampleReadPos += PitchRatio;
        }

        // Bound memory — trim anything more than one frame behind the read cursor, since it'll
        // never be read again.
        var consumedLocal = (int)(Math.Floor(state.ResampleReadPos) - state.StretchedBaseOffset);
        var trimAmount = consumedLocal - FftSize;
        if (trimAmount > 0 && trimAmount < state.StretchedBuffer.Count)
        {
            state.StretchedBuffer.RemoveRange(0, trimAmount);
            state.StretchedWindowSum.RemoveRange(0, trimAmount);
            state.StretchedBaseOffset += trimAmount;
        }
    }

    private sealed class ChannelState
    {
        public readonly List<float> InputQueue = [];
        public readonly List<float> StretchedBuffer = [];
        public readonly List<float> StretchedWindowSum = [];
        // Queue, not List — the Read() loop dequeues one sample at a time, and List.RemoveAt(0)
        // is O(n) (it shifts every remaining element down), which turns a per-sample loop into
        // O(n^2) work on the real-time audio thread. That's exactly the kind of thing that causes
        // periodic dropouts no amount of DSP-math correctness would fix.
        public readonly Queue<float> OutputQueue = new();
        public double StretchedBaseOffset;
        public double StretchedWritePos;
        public double ResampleReadPos;
        public double SafeReadLimitAbsolute;
        public readonly double[] LastPhase = new double[Bins];
        public readonly double[] SumPhase = new double[Bins];

        // Scratch buffers for RunOneFrame/ExtractEnvelope/WarpEnvelope, reused every frame
        // instead of allocated fresh — this is a real-time audio hot path (fires roughly every
        // 11.6ms per channel), and per-frame heap allocations there mean GC pressure sitting
        // directly on the audio thread, which reads as intermittent glitching no DSP-math
        // correctness fix would touch.
        public readonly double[] Real = new double[FftSize];
        public readonly double[] Imag = new double[FftSize];
        public readonly double[] Magnitude = new double[Bins];
        public readonly double[] Phase = new double[Bins];
        public readonly double[] LogMag = new double[FftSize];
        public readonly double[] CepImag = new double[FftSize];
        public readonly double[] Envelope = new double[Bins];
        public readonly double[] Excitation = new double[Bins];
        public readonly double[] WarpedEnvelope = new double[Bins];
        public readonly double[] NewMagnitude = new double[Bins];
        public readonly double[] SynReal = new double[FftSize];
        public readonly double[] SynImag = new double[FftSize];
    }

    /// <summary>Standard iterative in-place radix-2 Cooley-Tukey FFT (decimation-in-time).
    /// Power-of-2 sizes only.</summary>
    private static class Fft
    {
        public static void Transform(double[] real, double[] imag, bool inverse)
        {
            var n = real.Length;

            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }

                j ^= bit;

                if (i < j)
                {
                    (real[i], real[j]) = (real[j], real[i]);
                    (imag[i], imag[j]) = (imag[j], imag[i]);
                }
            }

            for (var len = 2; len <= n; len <<= 1)
            {
                var angleSign = inverse ? 1.0 : -1.0;
                var angle = angleSign * 2.0 * Math.PI / len;
                var wReal = Math.Cos(angle);
                var wImag = Math.Sin(angle);

                for (var i = 0; i < n; i += len)
                {
                    double curReal = 1.0, curImag = 0.0;
                    for (var k = 0; k < len / 2; k++)
                    {
                        var uReal = real[i + k];
                        var uImag = imag[i + k];
                        var vReal = real[i + k + len / 2] * curReal - imag[i + k + len / 2] * curImag;
                        var vImag = real[i + k + len / 2] * curImag + imag[i + k + len / 2] * curReal;

                        real[i + k] = uReal + vReal;
                        imag[i + k] = uImag + vImag;
                        real[i + k + len / 2] = uReal - vReal;
                        imag[i + k + len / 2] = uImag - vImag;

                        var nextReal = curReal * wReal - curImag * wImag;
                        var nextImag = curReal * wImag + curImag * wReal;
                        curReal = nextReal;
                        curImag = nextImag;
                    }
                }
            }

            if (inverse)
            {
                for (var i = 0; i < n; i++)
                {
                    real[i] /= n;
                    imag[i] /= n;
                }
            }
        }
    }
}

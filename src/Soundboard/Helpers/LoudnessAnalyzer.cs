using NAudio.Vorbis;
using NAudio.Wave;

namespace Soundboard.Helpers;

/// <summary>
/// Computes a per-sound gain that brings its average loudness (RMS, not full ITU-R BS.1770
/// LUFS — that needs a K-weighting+gating filter, a much bigger undertaking for a soundboard's
/// worth of loudness matching) to a consistent target, so "Normalize" actually makes quiet and
/// loud sounds come out at a similar perceived volume instead of applying the same flat boost
/// to everything. Computed once (at import, or via the "Normalize All" batch backfill) and
/// cached on <see cref="Soundboard.Core.Models.SoundItem.NormalizedGain"/> — playback just reads
/// that number, never re-analyzes the file.
/// </summary>
public static class LoudnessAnalyzer
{
    // -20 dBFS RMS is a standard target for voice/game audio — quieter than what music
    // mastering typically targets, which suits short sound-effect clips better (headroom for
    // sharp transients without clipping when several sounds overlap).
    private const double TargetDb = -20.0;

    // Bounds how far a single clip can be pushed either way — a near-silent recording (mostly
    // room tone) or a corrupt/mis-decoded file shouldn't be able to produce an absurd boost or
    // cut. Roughly ±20 dB.
    private const float MinGain = 0.1f;
    private const float MaxGain = 4.0f;

    public static Task<float?> ComputeGainAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                // Same reader-selection branch as AudioEngine.CreateSoundSource/WaveformExtractor,
                // so this measures the exact same decoded samples playback would produce.
                using WaveStream reader = string.Equals(Path.GetExtension(filePath), ".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(filePath)
                    : new AudioFileReader(filePath);

                var sampleProvider = (ISampleProvider)reader;
                var buffer = new float[4096];
                double sumOfSquares = 0;
                long sampleCount = 0;
                int read;

                while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    for (var i = 0; i < read; i++)
                    {
                        sumOfSquares += (double)buffer[i] * buffer[i];
                    }

                    sampleCount += read;
                }

                if (sampleCount == 0) return null;

                var rms = Math.Sqrt(sumOfSquares / sampleCount);
                if (rms <= 0) return null; // Pure silence — nothing to normalize toward.

                var currentDb = 20.0 * Math.Log10(rms);
                var gain = (float)Math.Pow(10.0, (TargetDb - currentDb) / 20.0);

                return (float?)Math.Clamp(gain, MinGain, MaxGain);
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }
}

using NAudio.Vorbis;
using NAudio.Wave;

namespace Soundboard.Helpers;

/// <summary>
/// Lightweight peak-per-bucket waveform extraction for the Sound Details panel — not a
/// studio-grade renderer, just enough to give a visual sense of the clip's shape. Streams the
/// file in fixed-size chunks rather than buffering whole-file sample arrays, so long clips
/// (podcasts, hour-plus recordings) don't blow up memory.
/// </summary>
public static class WaveformExtractor
{
    public static Task<float[]> ExtractPeaksAsync(string filePath, int bucketCount, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                // Same reader-selection branch as AudioEngine.CreateSoundSource, so waveform
                // extraction reads the exact same decoded samples playback would produce.
                using WaveStream reader = string.Equals(Path.GetExtension(filePath), ".ogg", StringComparison.OrdinalIgnoreCase)
                    ? new VorbisWaveReader(filePath)
                    : new AudioFileReader(filePath);

                var sampleProvider = (ISampleProvider)reader;
                var totalSamples = reader.Length / sizeof(float);
                var samplesPerBucket = Math.Max(1, totalSamples / bucketCount);

                var peaks = new float[bucketCount];
                var buffer = new float[4096];
                long samplesSeen = 0;
                var bucket = 0;
                int read;

                while (bucket < bucketCount && (read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    for (var i = 0; i < read; i++)
                    {
                        var abs = Math.Abs(buffer[i]);
                        if (abs > peaks[bucket]) peaks[bucket] = abs;

                        samplesSeen++;
                        if (samplesSeen >= samplesPerBucket * (bucket + 1) && bucket < bucketCount - 1)
                        {
                            bucket++;
                        }
                    }
                }

                return peaks;
            }
            catch
            {
                return [];
            }
        }, cancellationToken);
    }
}

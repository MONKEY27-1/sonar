using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soundboard.Core.Models;

namespace Soundboard.Audio;

/// <summary>
/// Owns everything about discovering and talking to Windows audio devices: enumerating
/// render/capture endpoints, recognizing known virtual-cable products (VB-Cable,
/// Voicemeeter, SteelSeries Sonar), and constructing <see cref="WasapiOut"/>/<see cref="WasapiCapture"/>
/// instances. Nothing in here knows about mixing, playback state, or sound files —
/// that's <see cref="AudioMixer"/> and <see cref="AudioEngine"/>'s job.
/// </summary>
public sealed class AudioDeviceManager
{
    private static readonly (string Product, string[] NamePatterns)[] KnownVirtualDeviceProducts =
    [
        ("VB-Audio Virtual Cable", ["CABLE Input", "CABLE Output", "VB-Audio Virtual Cable"]),
        ("VoiceMeeter", ["VoiceMeeter Input", "VoiceMeeter Output", "VoiceMeeter Aux", "VoiceMeeter VAIO"]),
        ("SteelSeries Sonar", ["Sonar"])
    ];

    public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<AudioDeviceInfo>();
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = device.ID == defaultId,
                    IsInput = false
                });
            }

            return (IReadOnlyList<AudioDeviceInfo>)devices;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var devices = new List<AudioDeviceInfo>();
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = device.ID == defaultId,
                    IsInput = true
                });
            }

            return (IReadOnlyList<AudioDeviceInfo>)devices;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DetectedVirtualDevice>> DetectVirtualDevicesAsync(CancellationToken cancellationToken = default)
    {
        var outputs = await GetOutputDevicesAsync(cancellationToken).ConfigureAwait(false);
        var inputs = await GetInputDevicesAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<DetectedVirtualDevice>();

        foreach (var (product, patterns) in KnownVirtualDeviceProducts)
        {
            var matchingOutputs = outputs.Where(d => patterns.Any(p => d.Name.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
            var matchingInputs = inputs.Where(d => patterns.Any(p => d.Name.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();

            if (matchingOutputs.Count == 0) continue;

            // Not attempting to precisely pair specific buses (Voicemeeter/Sonar can have
            // several) — just surfacing the first matching recording device per product as a
            // "you'll likely want this as your Discord/game mic" hint alongside each playback
            // option actually usable as a virtual mic output device.
            var suggestedRecording = matchingInputs.FirstOrDefault();

            foreach (var output in matchingOutputs)
            {
                results.Add(new DetectedVirtualDevice
                {
                    Product = product,
                    PlaybackDeviceId = output.Id,
                    PlaybackDeviceName = output.Name,
                    RecordingDeviceId = suggestedRecording?.Id,
                    RecordingDeviceName = suggestedRecording?.Name
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Creates a WasapiOut for the given device ID (or the system default if null/blank/not
    /// found). Never throws — an invalid/stale/unplugged device silently falls back to default
    /// rather than taking playback down with it.
    /// </summary>
    public IWavePlayer CreateOutput(string? deviceId, int latencyMs)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                return new WasapiOut(device, AudioClientShareMode.Shared, false, latencyMs);
            }
            catch
            {
                // Configured device no longer exists/available (unplugged, stale setting, etc.) —
                // fall through to the system default rather than failing playback entirely.
            }
        }

        return new WasapiOut(AudioClientShareMode.Shared, latencyMs);
    }

    /// <summary>
    /// Resolves a capture device by ID, or the default communications microphone if
    /// null/blank. Throws if the device genuinely can't be resolved — callers decide how to
    /// handle that (capture is inherently optional, unlike playback).
    /// </summary>
    public MMDevice ResolveCaptureDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
    }
}

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Models;

namespace Soundboard.Audio;

/// <summary>
/// Owns the microphone capture lifecycle for two independent features that both need a live
/// mic feed: voice passthrough (mixing your live voice into the virtual mic output alongside
/// sound effects — automatic whenever a virtual mic output device is configured, not a separate
/// toggle) and the Voice Changer's "Test Mic" live preview (the same processed voice, routed to
/// headphones instead, so it can be checked without Discord/OBS open). One <see cref="CaptureUnit"/>
/// (one <see cref="WasapiCapture"/>) exists per configured physical microphone — all captured
/// simultaneously and mixed together — started whenever either feature needs capture at all, and
/// torn down (per device) when neither does or that specific device is no longer configured.
///
/// Passthrough and preview each get their OWN independent effect-chain instance PER DEVICE (their
/// own <see cref="BufferedWaveProvider"/> and, when active, their own Pitch/<see cref="VoiceEffectStackProvider"/>
/// instances) even though both read from the same device's capture callback — a single effect
/// provider instance must never be added to two <see cref="AudioMixer"/>s at once, since each
/// mixer pulls via Read() on its own thread and effect state (e.g. a delay line's ring buffer)
/// isn't safe for concurrent reads. Two independent buffers both being *written* from one
/// capture callback is fine; it's concurrent *reads* of shared effect state that would corrupt
/// things. This is why N microphones means N independent effect-chain instances per feature, not
/// one instance shared across N capture callbacks.
/// </summary>
internal sealed class MicrophoneMonitor : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly object _lock = new();

    private readonly Dictionary<string, CaptureUnit> _units = new();

    private bool _disposed;

    public MicrophoneMonitor(AudioDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>
    /// Applies the current settings: reconciles the open capture set against the configured
    /// microphone list — only opening newly-wanted devices and closing dropped ones, never
    /// blindly reopening a device that's already correctly capturing, since that briefly
    /// silences audio and can pop — and (re)wires the passthrough/Test-Mic-preview mixer inputs
    /// for every device that stays open. Each device is opened/wired in its own try/catch, so one
    /// unplugged/bad microphone can't take down passthrough or preview for the others. Safe to
    /// call any time settings change.
    /// </summary>
    public void Refresh(AudioSettings audio, AudioMixer virtualMicMixer, AudioMixer headphoneMixer, bool previewRequested)
    {
        lock (_lock)
        {
            var needsPassthrough = !string.IsNullOrWhiteSpace(audio.VirtualMicOutputDeviceId);
            var needsCapture = needsPassthrough || previewRequested;
            var desiredIds = needsCapture ? ResolveMicrophoneDeviceIds(audio) : [];

            foreach (var existingId in _units.Keys.ToList())
            {
                if (desiredIds.Contains(existingId)) continue;
                TearDownUnit(_units[existingId]);
                _units.Remove(existingId);
            }

            foreach (var id in desiredIds)
            {
                try
                {
                    CaptureUnit unit;
                    if (_units.TryGetValue(id, out var existingUnit))
                    {
                        // Device stays open — only rewire the cheap, in-process passthrough/
                        // preview graph against current settings; never reopens the physical
                        // capture for a device that's already correctly running.
                        unit = existingUnit;
                        TearDownWiring(unit);
                    }
                    else
                    {
                        var device = _deviceManager.ResolveCaptureDevice(ToDeviceId(id));
                        var capture = new WasapiCapture(device);
                        unit = new CaptureUnit { Capture = capture };
                        _units[id] = unit;
                        capture.DataAvailable += (_, e) => OnDataAvailable(unit, e);
                        capture.StartRecording();
                    }

                    if (needsPassthrough)
                    {
                        SetUpPassthrough(unit, virtualMicMixer, audio);
                    }

                    if (previewRequested)
                    {
                        SetUpPreview(unit, headphoneMixer, audio);
                    }
                }
                catch
                {
                    // This microphone unavailable (or failed to wire) — passthrough/preview
                    // simply won't include it until it's reconnected or settings change; other
                    // configured mics and playback aren't affected.
                    if (_units.TryGetValue(id, out var failedUnit))
                    {
                        TearDownUnit(failedUnit);
                        _units.Remove(id);
                    }
                }
            }
        }
    }

    /// <summary>Deduped configured microphone device ids, substituting a single "system default"
    /// entry (<see cref="string.Empty"/>) when the list is empty — mirrors AudioEngine's
    /// ResolveHeadphoneDeviceIds/DedupeOrDefault (empty string rather than null so this can key a
    /// plain <c>Dictionary&lt;string, CaptureUnit&gt;</c>; see <see cref="ToDeviceId"/>).</summary>
    private static List<string> ResolveMicrophoneDeviceIds(AudioSettings audio)
    {
        var ids = audio.MicrophoneDeviceIds.Distinct().ToList();
        return ids.Count > 0 ? ids : [string.Empty];
    }

    private static string? ToDeviceId(string resolvedId) => resolvedId.Length == 0 ? null : resolvedId;

    private static void SetUpPassthrough(CaptureUnit unit, AudioMixer virtualMicMixer, AudioSettings audio)
    {
        var buffer = new BufferedWaveProvider(unit.Capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1)
        };

        var provider = BuildEffectChain(AudioMixer.ConvertToMixerFormat(buffer.ToSampleProvider()), audio, out var handles);
        var volumeProvider = new VolumeSampleProvider(provider) { Volume = audio.MicPassthroughVolume };

        virtualMicMixer.AddInput(volumeProvider);

        unit.PassthroughBuffer = buffer;
        unit.PassthroughMixerInput = volumeProvider;
        unit.PassthroughMixer = virtualMicMixer;
        unit.PassthroughEffects = handles;
    }

    /// <summary>Same idea as <see cref="SetUpPassthrough"/> but routed to headphones for
    /// on-demand "Test Mic" preview — its own buffer and effect-chain instance, per the
    /// class-level remarks on why that's required. Every open capture unit gets a preview chain
    /// when preview is requested, so what you hear previews the real N-microphone mix that
    /// passthrough actually sends, not just one of them.</summary>
    private static void SetUpPreview(CaptureUnit unit, AudioMixer headphoneMixer, AudioSettings audio)
    {
        var buffer = new BufferedWaveProvider(unit.Capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1)
        };

        var provider = BuildEffectChain(AudioMixer.ConvertToMixerFormat(buffer.ToSampleProvider()), audio, out var handles);
        var volumeProvider = new VolumeSampleProvider(provider) { Volume = audio.MicPassthroughVolume };

        headphoneMixer.AddInput(volumeProvider);

        unit.PreviewBuffer = buffer;
        unit.PreviewMixerInput = volumeProvider;
        unit.PreviewMixer = headphoneMixer;
        unit.PreviewEffects = handles;
    }

    private static ISampleProvider BuildEffectChain(ISampleProvider provider, AudioSettings audio, out EffectChainHandles handles)
    {
        handles = new EffectChainHandles();

        if (!audio.EnableVoiceChanger) return provider;

        ISampleProvider chain = provider;

        if (audio.PitchEnabled || audio.TempoEnabled)
        {
            // Tempo shares this same phase vocoder node — see PhaseVocoderProvider.TempoRatio for
            // the derivation of how that decouples from PitchRatio. Either flag alone is enough
            // to need this node; each ratio individually defaults to neutral (1f) when its own
            // flag is off, so e.g. Tempo-only doesn't inherit a stale pitch shift.
            //
            // When Formant is also enabled, it rides the vocoder's own (more accurate) cepstral
            // envelope correction here rather than the stack's EQ-tilt fallback below — see
            // PhaseVocoderProvider's own remarks for what that actually does and its trade-offs.
            var pitch = new PhaseVocoderProvider(chain)
            {
                PitchRatio = audio.PitchEnabled ? (float)Math.Pow(2.0, audio.VoiceChangerPitchSemitones / 12.0) : 1f,
                FormantRatio = audio.FormantEnabled ? (float)Math.Pow(2.0, audio.FormantShift / 12.0) : 1f,
                TempoRatio = audio.TempoEnabled ? (float)(Math.Clamp(audio.VoiceChangerTempoPercent, 75, 150) / 100.0) : 1f
            };
            handles.PhaseVocoder = pitch;
            chain = pitch;
        }

        var stack = new VoiceEffectStackProvider(chain, formantHandledExternally: audio.PitchEnabled || audio.TempoEnabled);
        ApplyStackParameters(stack, audio);
        handles.Stack = stack;

        return stack;
    }

    /// <summary>Updates live-tunable effect parameters (pitch/formant plus every step's enable
    /// flag and knobs on the stack) on whatever effect chains are currently running, in place —
    /// no capture teardown, no new <see cref="WasapiCapture"/>, no rebuilt buffers, for any
    /// device. This matters because sliders (and now checkboxes) fire their value-changed
    /// callback continuously while being dragged/toggled; routing every tick through the full
    /// <see cref="Refresh"/> (which can restart physical mic captures) made turning any knob
    /// itself sound glitchy, independent of anything in the DSP's own correctness. Every step
    /// below lives inside <see cref="VoiceEffectStackProvider"/> as a plain settable property, so
    /// toggling a step's Enabled flag is just another live parameter update — only enabling/
    /// disabling the changer, passthrough/preview, or Pitch itself (which changes whether the
    /// phase vocoder is in the chain at all) still needs a real <see cref="Refresh"/>.</summary>
    public void UpdateEffectParameters(AudioSettings audio)
    {
        lock (_lock)
        {
            foreach (var unit in _units.Values)
            {
                ApplyLiveParameters(unit.PassthroughEffects, audio);
                ApplyLiveParameters(unit.PreviewEffects, audio);

                if (unit.PassthroughMixerInput is VolumeSampleProvider passthroughVolume)
                {
                    passthroughVolume.Volume = audio.MicPassthroughVolume;
                }

                if (unit.PreviewMixerInput is VolumeSampleProvider previewVolume)
                {
                    previewVolume.Volume = audio.MicPassthroughVolume;
                }
            }
        }
    }

    private static void ApplyLiveParameters(EffectChainHandles? handles, AudioSettings audio)
    {
        if (handles is null) return;

        if (handles.PhaseVocoder is { } pitch)
        {
            // This node can now exist for Tempo alone (Pitch off) — same conditional-else-neutral
            // shape as the construction path in BuildEffectChain, for the same reason.
            pitch.PitchRatio = audio.PitchEnabled ? (float)Math.Pow(2.0, audio.VoiceChangerPitchSemitones / 12.0) : 1f;
            pitch.FormantRatio = audio.FormantEnabled ? (float)Math.Pow(2.0, audio.FormantShift / 12.0) : 1f;
            pitch.TempoRatio = audio.TempoEnabled ? (float)(Math.Clamp(audio.VoiceChangerTempoPercent, 75, 150) / 100.0) : 1f;
        }

        if (handles.Stack is { } stack)
        {
            ApplyStackParameters(stack, audio);
        }
    }

    private static void ApplyStackParameters(VoiceEffectStackProvider stack, AudioSettings audio)
    {
        stack.FormantEnabled = audio.FormantEnabled;
        stack.FormantShift = audio.FormantShift;

        stack.RobotEnabled = audio.RobotEnabled;
        stack.RobotFrequencyHz = audio.RobotFrequencyHz;
        stack.RobotWaveform = audio.RobotWaveform;
        stack.RobotMix = audio.RobotMix;

        stack.DistortionEnabled = audio.DistortionEnabled;
        stack.DistortionDrive = audio.DistortionDrive;
        stack.DistortionMix = audio.DistortionMix;

        stack.OverdriveEnabled = audio.OverdriveEnabled;
        stack.OverdriveDrive = audio.OverdriveDrive;
        stack.OverdriveMix = audio.OverdriveMix;

        stack.DelayEnabled = audio.DelayEnabled;
        stack.DelayMs = audio.DelayMs;
        stack.DelayMix = audio.DelayMix;

        stack.EchoEnabled = audio.EchoEnabled;
        stack.EchoDelayMs = audio.EchoDelayMs;
        stack.EchoFeedback = audio.EchoFeedback;
        stack.EchoMix = audio.EchoMix;

        stack.ReverbEnabled = audio.ReverbEnabled;
        stack.ReverbRoomSize = audio.ReverbRoomSize;
        stack.ReverbDecay = audio.ReverbDecay;
        stack.ReverbMix = audio.ReverbMix;

        stack.ProximityEnabled = audio.ProximityEnabled;
        stack.ProximityDistance = audio.ProximityDistance;
        stack.ProximityMix = audio.ProximityMix;

        stack.Strength = audio.EffectStrength;
    }

    /// <summary>Direct references into a built effect chain so parameter tweaks can reach the
    /// live instances without unwrapping/pattern-matching an opaque <see cref="ISampleProvider"/>
    /// chain. PhaseVocoder is only non-null when Pitch is enabled; Stack is always present once
    /// the changer itself is on, since every other step lives inside it regardless of which are
    /// individually enabled.</summary>
    private sealed class EffectChainHandles
    {
        public PhaseVocoderProvider? PhaseVocoder;
        public VoiceEffectStackProvider? Stack;
    }

    /// <summary>One physical microphone's capture device plus its independent passthrough and
    /// preview wiring — see the class-level remarks for why each device needs its own effect
    /// chain instances per feature rather than sharing one across devices.</summary>
    private sealed class CaptureUnit
    {
        public required WasapiCapture Capture { get; init; }

        public BufferedWaveProvider? PassthroughBuffer;
        public ISampleProvider? PassthroughMixerInput;
        public AudioMixer? PassthroughMixer;
        public EffectChainHandles? PassthroughEffects;

        public BufferedWaveProvider? PreviewBuffer;
        public ISampleProvider? PreviewMixerInput;
        public AudioMixer? PreviewMixer;
        public EffectChainHandles? PreviewEffects;
    }

    /// <summary>Unwires this device's passthrough/preview mixer inputs and drops its effect-chain
    /// handles — leaves the physical capture itself running. Called both to re-wire a device
    /// that's staying open (fresh graph, same hardware) and as the first half of fully closing one.</summary>
    private static void TearDownWiring(CaptureUnit unit)
    {
        if (unit.PassthroughMixerInput is not null && unit.PassthroughMixer is not null)
        {
            unit.PassthroughMixer.RemoveInput(unit.PassthroughMixerInput);
        }

        unit.PassthroughMixerInput = null;
        unit.PassthroughBuffer = null;
        unit.PassthroughMixer = null;
        unit.PassthroughEffects = null;

        if (unit.PreviewMixerInput is not null && unit.PreviewMixer is not null)
        {
            unit.PreviewMixer.RemoveInput(unit.PreviewMixerInput);
        }

        unit.PreviewMixerInput = null;
        unit.PreviewBuffer = null;
        unit.PreviewMixer = null;
        unit.PreviewEffects = null;
    }

    private static void TearDownUnit(CaptureUnit unit)
    {
        TearDownWiring(unit);

        try
        {
            unit.Capture.StopRecording();
        }
        catch
        {
            // Device may already be gone (unplugged, etc.) — nothing to clean up for that case.
        }

        unit.Capture.Dispose();
    }

    private static void OnDataAvailable(CaptureUnit unit, WaveInEventArgs e)
    {
        unit.PassthroughBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        unit.PreviewBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var unit in _units.Values)
            {
                TearDownUnit(unit);
            }

            _units.Clear();
        }
    }
}

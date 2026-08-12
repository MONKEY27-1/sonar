using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundboard.Core.Models;

namespace Soundboard.Audio;

/// <summary>
/// Owns the microphone capture lifecycle for two independent features that both need a live
/// mic feed: voice passthrough (mixing your live voice into the virtual mic output alongside
/// sound effects) and the Voice Changer's "Test Mic" live preview (the same processed voice,
/// routed to headphones instead, so it can be checked without Discord/OBS open). A single
/// <see cref="WasapiCapture"/> serves both — it's started whenever either is enabled, and torn
/// down when neither is.
///
/// Passthrough and preview each get their OWN independent effect-chain instance (their own
/// <see cref="BufferedWaveProvider"/> and, when active, their own Pitch/<see cref="VoiceEffectStackProvider"/>
/// instances) even though both are fed from the same capture callback — a single effect provider
/// instance must never be added to two <see cref="AudioMixer"/>s at once, since each mixer pulls
/// via Read() on its own thread and effect state (e.g. a delay line's ring buffer) isn't safe for
/// concurrent reads. Two independent buffers both being *written* from one capture callback is
/// fine; it's concurrent *reads* of shared effect state that would corrupt things.
/// </summary>
internal sealed class MicrophoneMonitor : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly object _lock = new();

    private WasapiCapture? _capture;

    private BufferedWaveProvider? _passthroughBuffer;
    private ISampleProvider? _passthroughMixerInput;
    private AudioMixer? _passthroughMixer;
    private EffectChainHandles? _passthroughEffects;

    private BufferedWaveProvider? _previewBuffer;
    private ISampleProvider? _previewMixerInput;
    private AudioMixer? _previewMixer;
    private EffectChainHandles? _previewEffects;

    private bool _disposed;

    public MicrophoneMonitor(AudioDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>
    /// Applies the current settings: starts/stops/restarts capture as needed, and wires (or
    /// unwires) the passthrough and Test-Mic-preview mixer inputs. Safe to call any time
    /// settings change.
    /// </summary>
    public void Refresh(AudioSettings audio, AudioMixer virtualMicMixer, AudioMixer headphoneMixer, bool previewRequested)
    {
        lock (_lock)
        {
            TearDownPassthrough();
            TearDownPreview();
            TearDownCapture();

            var needsCapture = audio.EnableMicPassthrough || previewRequested;
            if (!needsCapture) return;

            try
            {
                var device = _deviceManager.ResolveCaptureDevice(audio.MicrophoneDeviceId);
                _capture = new WasapiCapture(device);

                if (audio.EnableMicPassthrough)
                {
                    SetUpPassthrough(_capture, virtualMicMixer, audio);
                }

                if (previewRequested)
                {
                    SetUpPreview(_capture, headphoneMixer, audio);
                }

                _capture.DataAvailable += OnDataAvailable;
                _capture.StartRecording();
            }
            catch
            {
                // Selected microphone unavailable — passthrough/preview simply won't engage
                // until it's reconnected or the setting is changed; playback isn't affected.
                TearDownPassthrough();
                TearDownPreview();
                TearDownCapture();
            }
        }
    }

    private void SetUpPassthrough(WasapiCapture capture, AudioMixer virtualMicMixer, AudioSettings audio)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1)
        };

        var provider = BuildEffectChain(AudioMixer.ConvertToMixerFormat(buffer.ToSampleProvider()), audio, out var handles);
        var volumeProvider = new VolumeSampleProvider(provider) { Volume = audio.MicPassthroughVolume };

        virtualMicMixer.AddInput(volumeProvider);

        _passthroughBuffer = buffer;
        _passthroughMixerInput = volumeProvider;
        _passthroughMixer = virtualMicMixer;
        _passthroughEffects = handles;
    }

    /// <summary>Same idea as <see cref="SetUpPassthrough"/> but routed to headphones for
    /// on-demand "Test Mic" preview — its own buffer and effect-chain instance, per the
    /// class-level remarks on why that's required.</summary>
    private void SetUpPreview(WasapiCapture capture, AudioMixer headphoneMixer, AudioSettings audio)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1)
        };

        var provider = BuildEffectChain(AudioMixer.ConvertToMixerFormat(buffer.ToSampleProvider()), audio, out var handles);
        var volumeProvider = new VolumeSampleProvider(provider) { Volume = audio.MicPassthroughVolume };

        headphoneMixer.AddInput(volumeProvider);

        _previewBuffer = buffer;
        _previewMixerInput = volumeProvider;
        _previewMixer = headphoneMixer;
        _previewEffects = handles;
    }

    private static ISampleProvider BuildEffectChain(ISampleProvider provider, AudioSettings audio, out EffectChainHandles handles)
    {
        handles = new EffectChainHandles();

        if (!audio.EnableVoiceChanger) return provider;

        ISampleProvider chain = provider;

        if (audio.PitchEnabled)
        {
            // When Formant is also enabled, it rides the vocoder's own (more accurate) cepstral
            // envelope correction here rather than the stack's EQ-tilt fallback below — see
            // PhaseVocoderProvider's own remarks for what that actually does and its trade-offs.
            var pitch = new PhaseVocoderProvider(chain)
            {
                PitchRatio = (float)Math.Pow(2.0, audio.VoiceChangerPitchSemitones / 12.0),
                FormantRatio = audio.FormantEnabled ? (float)Math.Pow(2.0, audio.FormantShift / 12.0) : 1f
            };
            handles.PhaseVocoder = pitch;
            chain = pitch;
        }

        var stack = new VoiceEffectStackProvider(chain, formantHandledExternally: audio.PitchEnabled);
        ApplyStackParameters(stack, audio);
        handles.Stack = stack;

        return stack;
    }

    /// <summary>Updates live-tunable effect parameters (pitch/formant plus every step's enable
    /// flag and knobs on the stack) on whatever effect chain is currently running, in place — no
    /// capture teardown, no new <see cref="WasapiCapture"/>, no rebuilt buffers. This matters
    /// because sliders (and now checkboxes) fire their value-changed callback continuously while
    /// being dragged/toggled; routing every tick through the full <see cref="Refresh"/> (which
    /// restarts the physical mic capture) made turning any knob itself sound glitchy, independent
    /// of anything in the DSP's own correctness. Every step below lives inside
    /// <see cref="VoiceEffectStackProvider"/> as a plain settable property, so toggling a step's
    /// Enabled flag is just another live parameter update — only enabling/disabling the changer,
    /// passthrough/preview, or Pitch itself (which changes whether the phase vocoder is in the
    /// chain at all) still needs a real <see cref="Refresh"/>.</summary>
    public void UpdateEffectParameters(AudioSettings audio)
    {
        lock (_lock)
        {
            ApplyLiveParameters(_passthroughEffects, audio);
            ApplyLiveParameters(_previewEffects, audio);

            if (_passthroughMixerInput is VolumeSampleProvider passthroughVolume)
            {
                passthroughVolume.Volume = audio.MicPassthroughVolume;
            }

            if (_previewMixerInput is VolumeSampleProvider previewVolume)
            {
                previewVolume.Volume = audio.MicPassthroughVolume;
            }
        }
    }

    private static void ApplyLiveParameters(EffectChainHandles? handles, AudioSettings audio)
    {
        if (handles is null) return;

        if (handles.PhaseVocoder is { } pitch)
        {
            pitch.PitchRatio = (float)Math.Pow(2.0, audio.VoiceChangerPitchSemitones / 12.0);
            pitch.FormantRatio = audio.FormantEnabled ? (float)Math.Pow(2.0, audio.FormantShift / 12.0) : 1f;
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

    private void TearDownPassthrough()
    {
        if (_passthroughMixerInput is not null && _passthroughMixer is not null)
        {
            _passthroughMixer.RemoveInput(_passthroughMixerInput);
        }

        _passthroughMixerInput = null;
        _passthroughBuffer = null;
        _passthroughMixer = null;
        _passthroughEffects = null;
    }

    private void TearDownPreview()
    {
        if (_previewMixerInput is not null && _previewMixer is not null)
        {
            _previewMixer.RemoveInput(_previewMixerInput);
        }

        _previewMixerInput = null;
        _previewBuffer = null;
        _previewMixer = null;
        _previewEffects = null;
    }

    private void TearDownCapture()
    {
        if (_capture is null) return;

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
        }
        catch
        {
            // Device may already be gone (unplugged, etc.) — nothing to clean up for that case.
        }

        _capture.Dispose();
        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _passthroughBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _previewBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            TearDownPassthrough();
            TearDownPreview();
            TearDownCapture();
        }
    }
}

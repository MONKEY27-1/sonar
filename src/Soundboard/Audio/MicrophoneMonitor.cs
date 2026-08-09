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
/// <see cref="BufferedWaveProvider"/> and, when active, their own Pitch/Robot/Echo provider) even
/// though both are fed from the same capture callback — a single effect provider instance must
/// never be added to two <see cref="AudioMixer"/>s at once, since each mixer pulls via Read() on
/// its own thread and effect state (e.g. the pitch shifter's ring buffer) isn't safe for
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

        if (audio.VoiceEffectType == VoiceEffectType.Pitch)
        {
            // Formant handling is built into the phase vocoder itself — proper independent
            // formant warping via cepstral envelope correction, not just an EQ tilt. See
            // PhaseVocoderProvider's own remarks for what this actually does and its trade-offs.
            var pitch = new PhaseVocoderProvider(provider)
            {
                PitchRatio = (float)Math.Pow(2.0, audio.VoiceChangerPitchSemitones / 12.0),
                FormantRatio = (float)Math.Pow(2.0, audio.FormantShift / 12.0)
            };
            handles.PhaseVocoder = pitch;
            return pitch;
        }

        ISampleProvider effectProvider = audio.VoiceEffectType switch
        {
            VoiceEffectType.Robot => handles.Robot = new RingModulationSampleProvider(provider)
            {
                FrequencyHz = audio.RobotFrequencyHz,
                Waveform = audio.RobotWaveform,
                Mix = audio.RobotMix
            },
            VoiceEffectType.Echo => handles.Echo = new EchoSampleProvider(provider)
            {
                DelayMs = audio.EchoDelayMs,
                Feedback = audio.EchoFeedback,
                Mix = audio.EchoMix
            },
            _ => handles.Distortion = new DistortionSampleProvider(provider)
            {
                Drive = audio.DistortionDrive,
                Mix = audio.DistortionMix
            }
        };

        // Layered on top for these non-Pitch effects — see FormantShiftSampleProvider's own
        // remarks for what this EQ-tilt approach actually is and isn't (Pitch above uses the
        // more accurate cepstral approach instead, built into the phase vocoder).
        if (Math.Abs(audio.FormantShift) > 0.01)
        {
            effectProvider = handles.FormantShift = new FormantShiftSampleProvider(effectProvider) { FormantShift = audio.FormantShift };
        }

        return effectProvider;
    }

    /// <summary>Updates live-tunable effect parameters (pitch, formant, robot/echo/distortion
    /// knobs) on whatever effect chain is currently running, in place — no capture teardown, no
    /// new <see cref="WasapiCapture"/>, no rebuilt buffers. This matters because sliders fire
    /// their value-changed callback continuously while being dragged; routing every tick through
    /// the full <see cref="Refresh"/> (which restarts the physical mic capture) made turning the
    /// Pitch knob itself sound glitchy, independent of anything in the phase vocoder's own DSP.
    /// Only call this for parameter tweaks — anything that changes which effect TYPE is active,
    /// or whether passthrough/preview are enabled at all, still needs a real
    /// <see cref="Refresh"/> since the chain topology itself has to change.</summary>
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
            pitch.FormantRatio = (float)Math.Pow(2.0, audio.FormantShift / 12.0);
        }

        if (handles.Robot is { } robot)
        {
            robot.FrequencyHz = audio.RobotFrequencyHz;
            robot.Waveform = audio.RobotWaveform;
            robot.Mix = audio.RobotMix;
        }

        if (handles.Echo is { } echo)
        {
            echo.DelayMs = audio.EchoDelayMs;
            echo.Feedback = audio.EchoFeedback;
            echo.Mix = audio.EchoMix;
        }

        if (handles.Distortion is { } distortion)
        {
            distortion.Drive = audio.DistortionDrive;
            distortion.Mix = audio.DistortionMix;
        }

        if (handles.FormantShift is { } formantShift)
        {
            formantShift.FormantShift = audio.FormantShift;
        }
    }

    /// <summary>Direct references into a built effect chain so parameter tweaks can reach the
    /// live instances without unwrapping/pattern-matching an opaque <see cref="ISampleProvider"/>
    /// chain. Only the field matching the currently-active <see cref="VoiceEffectType"/> (plus
    /// optionally FormantShift, layered on top of the non-Pitch effects) is ever non-null.</summary>
    private sealed class EffectChainHandles
    {
        public PhaseVocoderProvider? PhaseVocoder;
        public RingModulationSampleProvider? Robot;
        public EchoSampleProvider? Echo;
        public DistortionSampleProvider? Distortion;
        public FormantShiftSampleProvider? FormantShift;
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

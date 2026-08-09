namespace Soundboard.Core.Models;

public sealed class AppSettings
{
    public ThemeSettings Theme { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public GlobalHotkeys GlobalHotkeys { get; set; } = new();
    public PlaybackPreferences Playback { get; set; } = new();
    public SoundDefaults SoundDefaults { get; set; } = new();
    public AccountPreferences Account { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
}

/// <summary>Which optional feature groups (Voice Changer, Advanced Settings, Performance Mode
/// — see <see cref="PluginCatalog"/>) the user has installed from the Plugin Marketplace. A
/// fresh install starts with none installed; <see cref="HasMigratedLegacyPlugins"/> exists so an
/// upgrade from a version before this feature can tell "never installed anything" apart from
/// "deliberately uninstalled everything" and only auto-populate once.</summary>
public sealed class PluginSettings
{
    public List<string> InstalledPluginIds { get; set; } = [];
    public bool HasMigratedLegacyPlugins { get; set; }

    /// <summary>Community (script) plugins the user has installed — cached locally (id/name/the
    /// actual script text) so they keep working fully offline and don't need a network round trip
    /// just to re-appear at startup. See CommunityPluginRuntime, which re-runs each of these
    /// scripts once at launch to rebuild that plugin's tiles/panel buttons for this session.</summary>
    public List<InstalledCommunityPlugin> InstalledCommunityPlugins { get; set; } = [];
}

public sealed class InstalledCommunityPlugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ScriptSource { get; init; }
}

public sealed class AccountPreferences
{
    /// <summary>Whether logging in persists the encrypted refresh token to disk at all.</summary>
    public bool RememberMe { get; set; } = true;

    /// <summary>Whether startup should silently try to restore a remembered session. Separate
    /// from <see cref="RememberMe"/> so a user can keep a session remembered but still choose to
    /// always land on the login screen (e.g. a shared PC).</summary>
    public bool AutoLoginEnabled { get; set; } = true;
}

public sealed class ThemeSettings
{
    public ThemeKind Kind { get; set; } = ThemeKind.Dark;
    public string AccentColor { get; set; } = "#6366F1";
    public string BackgroundColor { get; set; } = "#0F172A";
    public string SurfaceColor { get; set; } = "#1E293B";
    public string TextColor { get; set; } = "#F8FAFC";
    public double ButtonSize { get; set; } = 120;
    public double ButtonSpacing { get; set; } = 8;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;
    public ViewMode ViewMode { get; set; } = ViewMode.Grid;
    public double CornerRadius { get; set; } = 12;
    public bool AnimationsEnabled { get; set; } = true;
    public double WindowOpacity { get; set; } = 1.0;
}

public sealed class AudioSettings
{
    public string? HeadphoneDeviceId { get; set; }

    /// <summary>
    /// Playback (render) device that soundboard audio is also sent to so a virtual
    /// audio cable (e.g. VB-Cable) can present it to other apps as a "microphone".
    /// This is an output device, not your physical microphone.
    /// </summary>
    public string? VirtualMicOutputDeviceId { get; set; }

    /// <summary>
    /// Your actual physical microphone (a capture/input device) — used for voice passthrough
    /// and the Voice Changer's Test Mic preview. Never played to directly.
    /// </summary>
    public string? MicrophoneDeviceId { get; set; }

    public OutputRoute DefaultOutputRoute { get; set; } = OutputRoute.Headphones;
    public float GlobalVolume { get; set; } = 1.0f;
    public float HeadphoneVolume { get; set; } = 1.0f;
    public float VirtualMicOutputVolume { get; set; } = 1.0f;
    public bool MasterMuted { get; set; }
    public int BufferSize { get; set; } = 100;
    public LatencyMode LatencyMode { get; set; } = LatencyMode.Low;
    public bool NormalizeGlobally { get; set; }

    /// <summary>
    /// Mixes your live microphone audio into the virtual mic output channel alongside
    /// sound effects, so people hear your voice AND the soundboard through the same
    /// virtual cable/mixer app instead of just one or the other.
    /// </summary>
    public bool EnableMicPassthrough { get; set; }
    public float MicPassthroughVolume { get; set; } = 1.0f;

    /// <summary>Real-time effect applied to the passthrough voice before it's mixed into
    /// the virtual mic output. Has no effect unless <see cref="EnableMicPassthrough"/> is also
    /// on — there's nothing to process otherwise.</summary>
    public bool EnableVoiceChanger { get; set; }

    /// <summary>Which single effect is active — only one at a time.</summary>
    public VoiceEffectType VoiceEffectType { get; set; } = VoiceEffectType.Pitch;

    /// <summary>-12 (one octave down, "deep") to +7 ("chipmunk"); 0 = no shift. Used when
    /// <see cref="VoiceEffectType"/> is Pitch.</summary>
    public double VoiceChangerPitchSemitones { get; set; }

    /// <summary>Ring-modulation carrier frequency, in Hz — the classic "robot buzz." Used when
    /// <see cref="VoiceEffectType"/> is Robot.</summary>
    public double RobotFrequencyHz { get; set; } = 30;

    /// <summary>Carrier waveform shape — Sine is the smooth classic buzz, Square is harsher/more
    /// robotic, Triangle is in between. Used when <see cref="VoiceEffectType"/> is Robot.</summary>
    public RobotWaveform RobotWaveform { get; set; } = RobotWaveform.Sine;

    /// <summary>Dry/wet blend, 0 (unprocessed voice) to 1 (fully ring-modulated). Used when
    /// <see cref="VoiceEffectType"/> is Robot.</summary>
    public double RobotMix { get; set; } = 1.0;

    /// <summary>Echo delay, in milliseconds. Used when <see cref="VoiceEffectType"/> is Echo.</summary>
    public double EchoDelayMs { get; set; } = 250;

    /// <summary>Echo feedback (0-0.9) — how much of the delayed signal feeds back into itself.
    /// Deliberately capped below 1.0: at or above that the feedback loop never decays and the
    /// echo builds up into runaway noise instead of settling. Used when
    /// <see cref="VoiceEffectType"/> is Echo.</summary>
    public double EchoFeedback { get; set; } = 0.35;

    /// <summary>Dry/wet blend, 0 (unprocessed voice) to 1 (fully echoed). Doesn't affect the
    /// feedback recursion itself (which always runs at full strength) — only how much of that
    /// echoed signal makes it into what you actually hear. Used when <see cref="VoiceEffectType"/>
    /// is Echo.</summary>
    public double EchoMix { get; set; } = 1.0;

    /// <summary>How hard the signal is driven into saturation before soft-clipping — higher is
    /// a harsher, more aggressive distortion. Used when <see cref="VoiceEffectType"/> is
    /// Distortion.</summary>
    public double DistortionDrive { get; set; } = 5.0;

    /// <summary>Dry/wet blend, 0 (unprocessed) to 1 (fully distorted). Used when
    /// <see cref="VoiceEffectType"/> is Distortion.</summary>
    public double DistortionMix { get; set; } = 1.0;

    /// <summary>-12 to +12 — a spectral-tilt EQ (a high-shelf filter around ~2.5kHz), applied
    /// on top of whichever effect above is active, regardless of which one that is. Positive
    /// brightens the voice's timbre (reads as "smaller"/more feminine), negative darkens it
    /// (reads as "larger"/more masculine). This is a standard, stable EQ-based approximation of
    /// true formant shifting — real formant shifting needs full spectral-envelope analysis and
    /// resynthesis (LPC or similar), a much bigger undertaking than everything else in the
    /// Voice Changer combined. Combined with Pitch, this is what actually reads as a different
    /// voice rather than just the same voice sped up ("chipmunk"/"helium").</summary>
    public double FormantShift { get; set; }

    /// <summary>User-defined (plus a few seeded-once defaults) saved combinations of the
    /// settings above, so switching "voices" doesn't mean re-dragging every slider each time.</summary>
    public List<VoiceChangerPreset> VoiceChangerPresets { get; set; } = [];
}

/// <summary>A saved, nameable Voice Changer configuration — captures everything needed to fully
/// reproduce a "voice" in one click rather than re-dragging sliders each time.</summary>
public sealed class VoiceChangerPreset
{
    public string Name { get; set; } = string.Empty;
    public VoiceEffectType EffectType { get; set; }
    public double PitchSemitones { get; set; }
    public double RobotFrequencyHz { get; set; } = 30;
    public RobotWaveform RobotWaveform { get; set; } = RobotWaveform.Sine;
    public double RobotMix { get; set; } = 1.0;
    public double EchoDelayMs { get; set; } = 250;
    public double EchoFeedback { get; set; } = 0.35;
    public double EchoMix { get; set; } = 1.0;
    public double DistortionDrive { get; set; } = 5.0;
    public double DistortionMix { get; set; } = 1.0;
    public double FormantShift { get; set; }
}

public sealed class SoundDefaults
{
    public float Volume { get; set; } = 1.0f;
    public bool Normalize { get; set; }
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public int FadeInMs { get; set; } = 100;
    public int FadeOutMs { get; set; } = 100;
}

public sealed class GeneralSettings
{
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool LaunchOnStartup { get; set; }
    public bool AutoSave { get; set; } = true;
    public SortMode DefaultSortMode { get; set; } = SortMode.Custom;
    public bool CheckForUpdatesOnLaunch { get; set; } = true;
}

public sealed class NotificationSettings
{
    public bool OnImport { get; set; } = true;
    public bool OnPlaybackStarted { get; set; }
    public bool OnPlaybackFinished { get; set; }
    public bool OnError { get; set; } = true;
}

public sealed class LayoutSettings
{
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool IsMaximized { get; set; }
    public double SidebarWidth { get; set; } = 240;
    public bool SidebarVisible { get; set; } = true;
}

public sealed class GlobalHotkeys
{
    public HotkeyBinding? StopAll { get; set; }
    public HotkeyBinding? PauseAll { get; set; }
    public HotkeyBinding? ResumeAll { get; set; }
    public HotkeyBinding? ToggleLoop { get; set; }
    public HotkeyBinding? ToggleVoiceChanger { get; set; }
    public HotkeyBinding? ToggleQuickPlayOverlay { get; set; }
}

public sealed class PlaybackPreferences
{
    public QueueMode QueueMode { get; set; } = QueueMode.Overlap;
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }
    public PlaybackMode DefaultPlaybackMode { get; set; } = PlaybackMode.OneShot;
}

public sealed class SoundLibrary
{
    public List<SoundItem> Sounds { get; set; } = [];
    public List<SoundFolder> Folders { get; set; } = [];
    public List<string> RecentSoundIds { get; set; } = [];
    public SortMode SortMode { get; set; } = SortMode.Custom;
    public string? SelectedFolderId { get; set; }
    public string SearchQuery { get; set; } = string.Empty;
}

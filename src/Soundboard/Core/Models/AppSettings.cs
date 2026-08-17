using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>Set once the user has accepted the Developer Tools terms of use — gates
    /// installing the Developer plugin (PluginCatalog.Developer) so the terms only show once,
    /// not on every reinstall.</summary>
    public bool HasAcceptedDeveloperToolsTerms { get; set; }

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
    /// <summary>Every headphone/speaker device sound plays through simultaneously — empty means
    /// "system default" (same meaning the old single <see cref="HeadphoneDeviceId"/> null had).
    /// See <see cref="HeadphoneDeviceId"/> for the migration shim that seeds this from an
    /// existing user's single saved device on first load after upgrading.</summary>
    public List<string> HeadphoneDeviceIds { get; set; } = [];

    /// <summary>Deserialization-only shim for settings.json files saved before multi-device
    /// output existed — never read at runtime except by the one-time migration in
    /// MainViewModel.InitializeAsync(), which seeds <see cref="HeadphoneDeviceIds"/> from this
    /// and then leaves it alone. Keeping the property (rather than a straight rename) is what
    /// lets an existing user's saved device survive the upgrade instead of silently reverting to
    /// system default.</summary>
    public string? HeadphoneDeviceId { get; set; }

    /// <summary>
    /// Playback (render) device that soundboard audio is also sent to so a virtual
    /// audio cable (e.g. VB-Cable) can present it to other apps as a "microphone".
    /// This is an output device, not your physical microphone.
    /// </summary>
    public string? VirtualMicOutputDeviceId { get; set; }

    /// <summary>Every physical microphone captured and mixed together for voice passthrough
    /// and the Voice Changer's Test Mic preview — empty means "system default" (same meaning the
    /// old single <see cref="MicrophoneDeviceId"/> null had). Never played to directly. See
    /// <see cref="MicrophoneDeviceId"/> for the migration shim.</summary>
    public List<string> MicrophoneDeviceIds { get; set; } = [];

    /// <summary>Deserialization-only shim — see <see cref="HeadphoneDeviceId"/>'s remarks, same
    /// reasoning applied to the microphone list.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public OutputRoute DefaultOutputRoute { get; set; } = OutputRoute.Headphones;
    public float GlobalVolume { get; set; } = 1.0f;
    public float HeadphoneVolume { get; set; } = 1.0f;
    public float VirtualMicOutputVolume { get; set; } = 1.0f;
    public bool MasterMuted { get; set; }
    public int BufferSize { get; set; } = 100;
    public LatencyMode LatencyMode { get; set; } = LatencyMode.Low;
    public bool NormalizeGlobally { get; set; }

    public float MicPassthroughVolume { get; set; } = 1.0f;

    /// <summary>Real-time effect applied to the passthrough voice before it's mixed into
    /// the virtual mic output. Has no effect unless a virtual mic output device is configured —
    /// there's nothing to process otherwise (passthrough itself is automatic whenever
    /// <see cref="VirtualMicOutputDeviceId"/> is set, not a separate toggle).</summary>
    public bool EnableVoiceChanger { get; set; }

    // --- Voice Changer: a mixer of independently toggleable steps, not a single-select effect
    // list — any number can be enabled at once and are applied in a fixed pipeline order
    // (waveshaping, then time-based effects, then distance shaping), plus one global Strength
    // dial blending the whole processed result against the dry signal. See
    // Audio/VoiceEffectStackProvider.cs for the actual DSP and processing order.

    /// <summary>-12 (one octave down, "deep") to +7 ("chipmunk"); 0 = no shift.</summary>
    public bool PitchEnabled { get; set; }
    public double VoiceChangerPitchSemitones { get; set; }

    /// <summary>-12 to +12 — a spectral-tilt EQ (a high-shelf filter around ~2.5kHz). Positive
    /// brightens the voice's timbre (reads as "smaller"/more feminine), negative darkens it
    /// (reads as "larger"/more masculine). This is a standard, stable EQ-based approximation of
    /// true formant shifting — real formant shifting needs full spectral-envelope analysis and
    /// resynthesis (LPC or similar), a much bigger undertaking than everything else in the
    /// Voice Changer combined. Combined with Pitch, this is what actually reads as a different
    /// voice rather than just the same voice sped up ("chipmunk"/"helium"). When Pitch is also
    /// enabled, this rides the phase vocoder's own (more accurate) cepstral formant warping
    /// instead of the EQ tilt — same slider, better path when it's available.</summary>
    public bool FormantEnabled { get; set; }
    public double FormantShift { get; set; }

    /// <summary>Ring-modulation carrier frequency, in Hz — the classic "robot buzz."</summary>
    public bool RobotEnabled { get; set; }
    public double RobotFrequencyHz { get; set; } = 30;

    /// <summary>Carrier waveform shape — Sine is the smooth classic buzz, Square is harsher/more
    /// robotic, Triangle is in between.</summary>
    public RobotWaveform RobotWaveform { get; set; } = RobotWaveform.Sine;

    /// <summary>Dry/wet blend, 0 (unprocessed voice) to 1 (fully ring-modulated).</summary>
    public double RobotMix { get; set; } = 1.0;

    /// <summary>How hard the signal is driven into saturation before soft-clipping (symmetric
    /// tanh curve — smooth, only adds odd harmonics) — higher is a harsher, more aggressive
    /// distortion.</summary>
    public bool DistortionEnabled { get; set; }
    public double DistortionDrive { get; set; } = 5.0;
    public double DistortionMix { get; set; } = 1.0;

    /// <summary>A harder, asymmetric clipping curve (adds even harmonics too, unlike
    /// Distortion's symmetric tanh) — genuinely different edge/character rather than the same
    /// saturation under a different name.</summary>
    public bool OverdriveEnabled { get; set; }
    public double OverdriveDrive { get; set; } = 4.0;
    public double OverdriveMix { get; set; } = 1.0;

    /// <summary>A single clean repeat, in milliseconds — no feedback loop, unlike Echo below.</summary>
    public bool DelayEnabled { get; set; }
    public double DelayMs { get; set; } = 150;
    public double DelayMix { get; set; } = 0.5;

    /// <summary>Echo delay, in milliseconds.</summary>
    public bool EchoEnabled { get; set; }
    public double EchoDelayMs { get; set; } = 250;

    /// <summary>Echo feedback (0-0.9) — how much of the delayed signal feeds back into itself.
    /// Deliberately capped below 1.0: at or above that the feedback loop never decays and the
    /// echo builds up into runaway noise instead of settling.</summary>
    public double EchoFeedback { get; set; } = 0.35;

    /// <summary>Dry/wet blend, 0 (unprocessed voice) to 1 (fully echoed). Doesn't affect the
    /// feedback recursion itself (which always runs at full strength) — only how much of that
    /// echoed signal makes it into what you actually hear.</summary>
    public double EchoMix { get; set; } = 1.0;

    /// <summary>Simplified Schroeder-style reverb (parallel comb filters, no allpass diffusion
    /// stage) — appropriate for a live, low-latency voice effect, not a studio-grade algorithm.
    /// RoomSize scales the comb delay lengths (bigger = larger perceived space); Decay is the
    /// per-comb feedback amount.</summary>
    public bool ReverbEnabled { get; set; }
    public double ReverbRoomSize { get; set; } = 1.0;
    public double ReverbDecay { get; set; } = 0.5;
    public double ReverbMix { get; set; } = 0.35;

    /// <summary>Simulated distance from the mic — 0 (close/normal) to 1 (far) drives both an
    /// overall volume drop and a low-pass filter rolling off highs, approximating how a voice
    /// dulls and quiets with distance. Not the classic close-mic bass-boost "proximity effect";
    /// the more broadly useful meaning for a distance/space control here.</summary>
    public bool ProximityEnabled { get; set; }
    public double ProximityDistance { get; set; }
    public double ProximityMix { get; set; } = 1.0;

    /// <summary>Global intensity dial, 0 (fully dry) to 1 (fully processed) — blends the entire
    /// stack's output (every step enabled above, combined) against the dry signal, on top of
    /// each step's own Mix knob. Lets the whole "voice" be dialed back without re-tuning every
    /// individual effect. Doesn't affect Pitch (see VoiceEffectStackProvider remarks on why
    /// partial pitch-blending isn't a coherent knob the way wet/dry is for everything else).</summary>
    public double EffectStrength { get; set; } = 1.0;

    /// <summary>Every "Voice" the user has created — the primary unit of the Voice Changer tab.
    /// Each is a fully independent saved configuration; exactly one (see
    /// <see cref="ActiveVoicePresetId"/>) is the one actually processing your mic right now.</summary>
    public List<VoiceChangerPreset> VoiceChangerPresets { get; set; } = [];

    /// <summary>Which Voice's settings are the ones currently mirrored into the live fields
    /// above and actually processing your mic — matches a <see cref="VoiceChangerPreset.Id"/>,
    /// or null if none has been selected yet (a fresh install, or every Voice was deleted).</summary>
    public string? ActiveVoicePresetId { get; set; }
}

/// <summary>A saved, nameable Voice Changer configuration — captures everything needed to fully
/// reproduce a "voice" in one click rather than re-dragging sliders each time. Mirrors every
/// per-step field on <see cref="AudioSettings"/> above. Name/Icon are the two fields shown
/// directly in the Voice tile grid, so they're real observable properties (unlike everything
/// else here) — a plain auto-property never notifies the already-bound tile UI when Rename/the
/// icon picker mutate it after the fact, since WPF's binding only re-reads once for a source
/// with no change notification.</summary>
public sealed partial class VoiceChangerPreset : ObservableObject
{
    /// <summary>Stable identity independent of <see cref="Name"/> — needed once renaming is
    /// possible, since the old "match saved presets by name" scheme breaks the moment a name can
    /// change out from under it.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Chosen once at creation and fixed after that — which editor (just Pitch/Formant,
    /// or the full step mixer) this Voice shows when its settings are reopened.</summary>
    public VoiceChangerMode Mode { get; set; }

    [ObservableProperty] private string _name = string.Empty;

    /// <summary>Defaults to a deterministic pick from <see cref="VoiceIconPalette"/> (set
    /// explicitly at creation, see MainViewModel.CreateVoiceAsync) — user-changeable afterward
    /// via the tile's "Change Icon" context menu entry.</summary>
    [ObservableProperty] private string _icon = VoiceIconPalette.Icons[0];

    public bool PitchEnabled { get; set; }
    public double PitchSemitones { get; set; }

    public bool FormantEnabled { get; set; }
    public double FormantShift { get; set; }

    public bool RobotEnabled { get; set; }
    public double RobotFrequencyHz { get; set; } = 30;
    public RobotWaveform RobotWaveform { get; set; } = RobotWaveform.Sine;
    public double RobotMix { get; set; } = 1.0;

    public bool DistortionEnabled { get; set; }
    public double DistortionDrive { get; set; } = 5.0;
    public double DistortionMix { get; set; } = 1.0;

    public bool OverdriveEnabled { get; set; }
    public double OverdriveDrive { get; set; } = 4.0;
    public double OverdriveMix { get; set; } = 1.0;

    public bool DelayEnabled { get; set; }
    public double DelayMs { get; set; } = 150;
    public double DelayMix { get; set; } = 0.5;

    public bool EchoEnabled { get; set; }
    public double EchoDelayMs { get; set; } = 250;
    public double EchoFeedback { get; set; } = 0.35;
    public double EchoMix { get; set; } = 1.0;

    public bool ReverbEnabled { get; set; }
    public double ReverbRoomSize { get; set; } = 1.0;
    public double ReverbDecay { get; set; } = 0.5;
    public double ReverbMix { get; set; } = 0.35;

    public bool ProximityEnabled { get; set; }
    public double ProximityDistance { get; set; }
    public double ProximityMix { get; set; } = 1.0;

    public double EffectStrength { get; set; } = 1.0;
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

    /// <summary>Icon-only sidebar mode (nav item labels/section headers/folder list hidden,
    /// just the icons + a tooltip) — a lightweight space-saving toggle, distinct from
    /// <see cref="SidebarVisible"/> (which would hide the sidebar entirely; unused today).</summary>
    public bool IsSidebarCollapsed { get; set; }
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

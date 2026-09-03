namespace Soundboard.Core.Models;

public sealed class SoundItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? FolderId { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }
    public int PlayCount { get; set; }
    public int SortOrder { get; set; }
    public double DurationSeconds { get; set; }
    public string? IconPath { get; set; }
    public string Color { get; set; } = "#F0451C";
    public float Volume { get; set; } = 1.0f;
    public float PlaybackSpeed { get; set; } = 1.0f;
    /// <summary>Null means "use the global Default Output Routing setting" — the normal case
    /// for every sound until the user explicitly overrides one via its context menu. Named
    /// distinctly from the legacy non-nullable "outputRoute" JSON key (which every sound in an
    /// existing library.json already has, defaulted to Headphones) so old data can't get
    /// misread as an intentional per-sound override the moment this shipped.</summary>
    public OutputRoute? OutputRouteOverride { get; set; }
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.OneShot;
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public bool Normalize { get; set; }
    /// <summary>The linear gain <see cref="Helpers.LoudnessAnalyzer"/> computed to bring this
    /// sound's actual RMS loudness to a consistent target — applied on top of <see cref="Volume"/>
    /// when <see cref="Normalize"/> is on. Null means never analyzed (a sound imported before this
    /// existed, or analysis failed) — no boost is applied rather than guessing, until either a
    /// re-import or the Settings "Normalize All" backfill computes it.</summary>
    public float? NormalizedGain { get; set; }
    public HotkeyBinding? Hotkey { get; set; }
    public AudioEditSettings? EditSettings { get; set; }

    public string GetDisplayName() => string.IsNullOrWhiteSpace(Name) ? FileName : Name;
}

public sealed class AudioEditSettings
{
    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public bool FadeIn { get; set; }
    public bool FadeOut { get; set; }
    public double FadeInMs { get; set; } = 100;
    public double FadeOutMs { get; set; } = 100;
}

public sealed class SoundFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? ParentId { get; set; }
    public string Color { get; set; } = "#64748B";
}

public sealed class HotkeyBinding
{
    public int KeyCode { get; set; }
    public bool IsMouseButton { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool PushToPlay { get; set; }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(IsMouseButton ? MouseButtonName(KeyCode) : KeyCodeName(KeyCode));
            return string.Join("+", parts);
        }
    }

    private static string KeyCodeName(int keyCode) => keyCode switch
    {
        >= 0x30 and <= 0x39 => ((char)keyCode).ToString(),
        >= 0x41 and <= 0x5A => ((char)keyCode).ToString(),
        >= 0x70 and <= 0x7B => $"F{keyCode - 0x6F}",
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x09 => "Tab",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        _ => $"Key{keyCode:X}"
    };

    private static string MouseButtonName(int button) => button switch
    {
        1 => "MouseLeft",
        2 => "MouseRight",
        3 => "MouseMiddle",
        4 => "MouseX1",
        5 => "MouseX2",
        _ => $"Mouse{button}"
    };
}

public sealed class PlaybackInstance
{
    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");
    public string SoundId { get; init; } = string.Empty;
    public PlaybackState State { get; set; } = PlaybackState.Playing;
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

public enum PlaybackState
{
    Playing,
    Paused,
    Stopped
}

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsInput { get; set; }
}

/// <summary>
/// A recognized virtual audio product (VB-Cable, Voicemeeter, SteelSeries Sonar, etc.)
/// found among the system's active audio endpoints, with its matched playback/recording
/// device pair (either may be absent if only one side was found).
/// </summary>
public sealed class DetectedVirtualDevice
{
    public required string Product { get; init; }
    public string? PlaybackDeviceId { get; init; }
    public string? PlaybackDeviceName { get; init; }
    public string? RecordingDeviceId { get; init; }
    public string? RecordingDeviceName { get; init; }
}

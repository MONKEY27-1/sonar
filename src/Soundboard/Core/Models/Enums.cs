namespace Soundboard.Core.Models;

public enum OutputRoute
{
    Headphones = 0,
    Microphone = 1,
    Both = 2
}

public enum PlaybackMode
{
    OneShot = 0,
    Loop = 1,
    HoldToPlay = 2
}

public enum SortMode
{
    Custom = 0,
    Alphabetical = 1,
    DateAdded = 2,
    MostPlayed = 3
}

public enum ViewMode
{
    Grid = 0,
    List = 1
}

/// <summary>Which category page the Settings window is showing — purely a UI nav concern (not
/// persisted), so it lives alongside the other display-only enums rather than in AppSettings.</summary>
public enum SettingsCategory
{
    Audio,
    Playback,
    Hotkeys,
    Appearance,
    Library,
    Notifications,
    Performance,
    Diagnostics,
    Account,
    Security,
    License,
    Installation
}

public enum ThemeKind
{
    Dark = 0,
    Light = 1,
    Amoled = 2,
    Custom = 3
}

public enum LatencyMode
{
    Low = 0,
    Balanced = 1,
    Stable = 2
}

public enum QueueMode
{
    Overlap = 0,
    Queue = 1
}

public enum RobotWaveform
{
    Sine = 0,
    Square = 1,
    Triangle = 2
}

/// <summary>Which editor a saved Voice shows when you reopen its settings — chosen once, at
/// creation, and fixed after that. Basic exposes just Pitch + Formant (the two "identity"
/// sliders); Advanced exposes the full step mixer.</summary>
public enum VoiceChangerMode
{
    Basic = 0,
    Advanced = 1
}

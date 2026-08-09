using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

/// <summary>
/// Manages the self-contained Soundboard data directory structure.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundboard");

        SoundsDirectory = Path.Combine(RootDirectory, "Sounds");
        IconsDirectory = Path.Combine(RootDirectory, "Icons");
        ProfilesDirectory = Path.Combine(RootDirectory, "Profiles");
        BackupsDirectory = Path.Combine(RootDirectory, "Backups");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        LibraryFile = Path.Combine(RootDirectory, "library.json");

        EnsureDirectories();
    }

    public string RootDirectory { get; }
    public string SoundsDirectory { get; }
    public string IconsDirectory { get; }
    public string ProfilesDirectory { get; }
    public string BackupsDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFile { get; }
    public string LibraryFile { get; }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(SoundsDirectory);
        Directory.CreateDirectory(IconsDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

public sealed class SoundFileWatcher : ISoundFileWatcher
{
    private readonly IAppPaths _paths;
    private System.IO.FileSystemWatcher? _watcher;

    public SoundFileWatcher(IAppPaths paths) => _paths = paths;

    public event EventHandler? SoundsFolderChanged;

    public void Start()
    {
        _watcher = new System.IO.FileSystemWatcher(_paths.SoundsDirectory)
        {
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        _watcher.Created += (_, _) => SoundsFolderChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Deleted += (_, _) => SoundsFolderChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Renamed += (_, _) => SoundsFolderChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _watcher?.Dispose();
}

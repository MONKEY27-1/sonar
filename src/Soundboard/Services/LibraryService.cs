using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

public sealed class LibraryService : ILibraryService
{
    private static readonly string[] SupportedExtensions = [".mp3", ".wav", ".ogg", ".flac"];
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private readonly IAppPaths _paths;
    private readonly IAudioEngine _audioEngine;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LibraryService(IAppPaths paths, IAudioEngine audioEngine, ISettingsService settingsService)
    {
        _paths = paths;
        _audioEngine = audioEngine;
        _settingsService = settingsService;
        Library = new SoundLibrary();
    }

    public SoundLibrary Library { get; private set; }

    public event EventHandler? LibraryChanged;
    public event EventHandler<ImportProgress>? ImportProgressChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.LibraryFile) || new FileInfo(_paths.LibraryFile).Length == 0)
            {
                Library = new SoundLibrary();
                await SyncWithSoundsFolderAsync(cancellationToken).ConfigureAwait(false);
                await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_paths.LibraryFile);
                Library = await JsonSerializer.DeserializeAsync<SoundLibrary>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                          ?? new SoundLibrary();
            }
            catch (JsonException)
            {
                // library.json is corrupt or unreadable — rebuild from what's on disk rather than crashing.
                Library = new SoundLibrary();
            }

            await SyncWithSoundsFolderAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SoundItem>> ImportFilesAsync(
        IEnumerable<string> sourcePaths,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var imported = new List<SoundItem>();
        var paths = sourcePaths.Where(File.Exists).ToList();
        var total = paths.Count;
        var completed = 0;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var sourcePath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(sourcePath);
                if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    completed++;
                    ReportProgress(progress, total, completed, sourcePath);
                    continue;
                }

                try
                {
                    var sound = await ImportSingleFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                    imported.Add(sound);
                }
                catch
                {
                    // Skip corrupted/unsupported files gracefully.
                }

                completed++;
                ReportProgress(progress, total, completed, sourcePath);
            }

            if (imported.Count > 0)
            {
                await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _lock.Release();
        }

        return imported;
    }

    public async Task RemoveSoundAsync(string soundId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            var filePath = Path.Combine(_paths.SoundsDirectory, sound.FileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            Library.Sounds.Remove(sound);
            Library.RecentSoundIds.Remove(soundId);
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RenameSoundAsync(string soundId, string newName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            sound.Name = newName.Trim();
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSoundFolderAsync(string soundId, string? folderId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            sound.FolderId = folderId;
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReorderSoundsAsync(IReadOnlyList<string> orderedSoundIds, bool notifyChanged = true, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < orderedSoundIds.Count; i++)
            {
                var sound = Library.Sounds.FirstOrDefault(s => s.Id == orderedSoundIds[i]);
                if (sound is not null) sound.SortOrder = i;
            }

            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        // notifyChanged: false is used by MainViewModel's drag-drop reorder — it already
        // explicitly refreshes VisibleSounds itself (after making sure SortMode is Custom first).
        // Letting this ALSO broadcast LibraryChanged meant MainViewModel's own LibraryChanged
        // subscriber ran RefreshSounds() before SortMode had been switched, rebuilding the list
        // with the OLD sort mode and reverting the drag the user had just performed — same class
        // of bug as the earlier double-refresh-on-save issue in the voice changer.
        if (notifyChanged)
        {
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetSoundHotkeyAsync(string soundId, HotkeyBinding? hotkey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            sound.Hotkey = hotkey;
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetSoundOutputRouteOverrideAsync(string soundId, OutputRoute? route, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            sound.OutputRouteOverride = route;
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Library.Folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null) return;

            // Un-file rather than delete — removing a folder shouldn't take its sounds with it.
            foreach (var sound in Library.Sounds.Where(s => s.FolderId == folderId))
            {
                sound.FolderId = null;
            }

            Library.Folders.Remove(folder);
            if (Library.SelectedFolderId == folderId)
            {
                Library.SelectedFolderId = null;
            }

            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReplaceSoundFileAsync(string soundId, string sourcePath, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null || !File.Exists(sourcePath)) return;

            var oldPath = Path.Combine(_paths.SoundsDirectory, sound.FileName);
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            var newFileName = GetUniqueFileName(Path.GetFileName(sourcePath));
            var destPath = Path.Combine(_paths.SoundsDirectory, newFileName);
            await CopyFileAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);

            sound.FileName = newFileName;
            sound.DurationSeconds = await GetDurationAsync(destPath, cancellationToken).ConfigureAwait(false);
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DuplicateSoundAsync(string soundId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (source is null) return;

            var sourcePath = Path.Combine(_paths.SoundsDirectory, source.FileName);
            if (!File.Exists(sourcePath)) return;

            var baseName = Path.GetFileNameWithoutExtension(source.FileName);
            var extension = Path.GetExtension(source.FileName);
            var duplicateFileName = GetUniqueFileName($"{baseName} (copy){extension}");
            var destPath = Path.Combine(_paths.SoundsDirectory, duplicateFileName);
            await CopyFileAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);

            var duplicate = CloneSound(source);
            duplicate.Id = Guid.NewGuid().ToString("N");
            duplicate.FileName = duplicateFileName;
            duplicate.Name = $"{source.GetDisplayName()} (copy)";
            duplicate.DateAdded = DateTime.UtcNow;
            duplicate.SortOrder = Library.Sounds.Count;
            duplicate.Hotkey = null;

            Library.Sounds.Add(duplicate);
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RescanSoundsFolderAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncWithSoundsFolderAsync(cancellationToken).ConfigureAwait(false);
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public IEnumerable<SoundItem> GetFilteredSounds(string? folderId, string? searchQuery, bool favoritesOnly, bool recentOnly)
    {
        IEnumerable<SoundItem> query = Library.Sounds;

        if (favoritesOnly)
        {
            query = query.Where(s => s.IsFavorite);
        }
        else if (recentOnly)
        {
            query = Library.RecentSoundIds
                .Select(id => Library.Sounds.FirstOrDefault(s => s.Id == id))
                .Where(s => s is not null)
                .Cast<SoundItem>();
        }
        else if (!string.IsNullOrEmpty(folderId))
        {
            query = query.Where(s => s.FolderId == folderId);
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var term = searchQuery.Trim();
            query = query.Where(s =>
                s.GetDisplayName().Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (s.FolderId is not null && Library.Folders.FirstOrDefault(f => f.Id == s.FolderId)?.Name
                    .Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        }

        query = Library.SortMode switch
        {
            SortMode.Alphabetical => query.OrderBy(s => s.GetDisplayName(), StringComparer.OrdinalIgnoreCase),
            SortMode.DateAdded => query.OrderByDescending(s => s.DateAdded),
            SortMode.MostPlayed => query.OrderByDescending(s => s.PlayCount),
            _ => query.OrderBy(s => s.SortOrder)
        };

        return query.ToList();
    }

    public string GetSoundFilePath(SoundItem sound) => Path.Combine(_paths.SoundsDirectory, sound.FileName);

    public async Task MarkRecentlyUsedAsync(string soundId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sound = Library.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound is null) return;

            sound.LastUsed = DateTime.UtcNow;
            sound.PlayCount++;
            Library.RecentSoundIds.Remove(soundId);
            Library.RecentSoundIds.Insert(0, soundId);
            if (Library.RecentSoundIds.Count > 50)
            {
                Library.RecentSoundIds.RemoveRange(50, Library.RecentSoundIds.Count - 50);
            }

            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SoundItem> ImportSingleFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fileName = GetUniqueFileName(Path.GetFileName(sourcePath));
        var destPath = Path.Combine(_paths.SoundsDirectory, fileName);
        await CopyFileAsync(sourcePath, destPath, cancellationToken).ConfigureAwait(false);

        var defaults = _settingsService.Settings.SoundDefaults;
        var sound = new SoundItem
        {
            Name = Path.GetFileNameWithoutExtension(fileName),
            FileName = fileName,
            DateAdded = DateTime.UtcNow,
            SortOrder = Library.Sounds.Count,
            DurationSeconds = await GetDurationAsync(destPath, cancellationToken).ConfigureAwait(false),
            Volume = defaults.Volume,
            Normalize = defaults.Normalize,
            FadeIn = defaults.FadeIn,
            FadeOut = defaults.FadeOut,
            EditSettings = new AudioEditSettings
            {
                Normalize = defaults.Normalize,
                FadeIn = defaults.FadeIn,
                FadeOut = defaults.FadeOut,
                FadeInMs = defaults.FadeInMs,
                FadeOutMs = defaults.FadeOutMs
            }
        };

        Library.Sounds.Add(sound);
        return sound;
    }

    private async Task SyncWithSoundsFolderAsync(CancellationToken cancellationToken)
    {
        var filesOnDisk = Directory.Exists(_paths.SoundsDirectory)
            ? Directory.GetFiles(_paths.SoundsDirectory)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        // Remove library entries for deleted files.
        Library.Sounds.RemoveAll(s => !filesOnDisk.Contains(s.FileName));

        // Add new files found on disk.
        var knownFiles = Library.Sounds.Select(s => s.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in filesOnDisk.Where(f => !knownFiles.Contains(f)))
        {
            var path = Path.Combine(_paths.SoundsDirectory, fileName);
            Library.Sounds.Add(new SoundItem
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                DateAdded = File.GetCreationTimeUtc(path),
                SortOrder = Library.Sounds.Count,
                DurationSeconds = await GetDurationAsync(path, cancellationToken).ConfigureAwait(false)
            });
        }
    }

    private string GetUniqueFileName(string desiredName)
    {
        var directory = _paths.SoundsDirectory;
        var fileName = desiredName;
        var baseName = Path.GetFileNameWithoutExtension(desiredName);
        var extension = Path.GetExtension(desiredName);
        var counter = 1;

        while (File.Exists(Path.Combine(directory, fileName)))
        {
            fileName = $"{baseName} ({counter}){extension}";
            counter++;
        }

        return fileName;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
    }

    private Task<double> GetDurationAsync(string path, CancellationToken cancellationToken)
    {
        return _audioEngine.GetDurationAsync(path, cancellationToken);
    }

    private static SoundItem CloneSound(SoundItem source) => new()
    {
        Name = source.Name,
        FileName = source.FileName,
        FolderId = source.FolderId,
        Tags = [.. source.Tags],
        IsFavorite = source.IsFavorite,
        SortOrder = source.SortOrder,
        DurationSeconds = source.DurationSeconds,
        IconPath = source.IconPath,
        Color = source.Color,
        Volume = source.Volume,
        PlaybackSpeed = source.PlaybackSpeed,
        OutputRouteOverride = source.OutputRouteOverride,
        PlaybackMode = source.PlaybackMode,
        FadeIn = source.FadeIn,
        FadeOut = source.FadeOut,
        Normalize = source.Normalize,
        EditSettings = source.EditSettings is null ? null : new AudioEditSettings
        {
            TrimStartSeconds = source.EditSettings.TrimStartSeconds,
            TrimEndSeconds = source.EditSettings.TrimEndSeconds,
            Normalize = source.EditSettings.Normalize,
            FadeIn = source.EditSettings.FadeIn,
            FadeOut = source.EditSettings.FadeOut,
            FadeInMs = source.EditSettings.FadeInMs,
            FadeOutMs = source.EditSettings.FadeOutMs
        }
    };

    private async Task SaveInternalAsync(CancellationToken cancellationToken)
    {
        var tempFile = _paths.LibraryFile + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, Library, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempFile, _paths.LibraryFile, overwrite: true);
    }

    private void ReportProgress(IProgress<ImportProgress>? progress, int total, int completed, string? currentFile)
    {
        var report = new ImportProgress { Total = total, Completed = completed, CurrentFile = currentFile };
        progress?.Report(report);
        ImportProgressChanged?.Invoke(this, report);
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

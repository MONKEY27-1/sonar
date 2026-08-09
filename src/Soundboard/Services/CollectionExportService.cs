using System.IO.Compression;
using System.Text.Json;
using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

public sealed class CollectionExportService : ICollectionExportService
{
    private readonly IAppPaths _paths;
    private readonly ILibraryService _libraryService;
    private readonly ISettingsService _settingsService;

    public CollectionExportService(IAppPaths paths, ILibraryService libraryService, ISettingsService settingsService)
    {
        _paths = paths;
        _libraryService = libraryService;
        _settingsService = settingsService;
    }

    public async Task ExportCollectionAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await _libraryService.SaveAsync(cancellationToken).ConfigureAwait(false);
        await _settingsService.SaveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        ZipFile.CreateFromDirectory(_paths.RootDirectory, destinationPath, CompressionLevel.Optimal, false);
    }

    public async Task ImportCollectionAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"soundboard-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(sourcePath, tempDir, true);

            var settingsSource = Path.Combine(tempDir, "settings.json");
            var librarySource = Path.Combine(tempDir, "library.json");
            var soundsSource = Path.Combine(tempDir, "Sounds");

            if (Directory.Exists(soundsSource))
            {
                foreach (var file in Directory.GetFiles(soundsSource))
                {
                    var dest = Path.Combine(_paths.SoundsDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest);
                    }
                }
            }

            if (File.Exists(librarySource))
            {
                File.Copy(librarySource, _paths.LibraryFile, true);
            }

            if (File.Exists(settingsSource))
            {
                File.Copy(settingsSource, _paths.SettingsFile, true);
            }

            await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _libraryService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

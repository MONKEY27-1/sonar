using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly IAppPaths _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public SettingsService(IAppPaths paths)
    {
        _paths = paths;
        Settings = new AppSettings();
    }

    public AppSettings Settings { get; private set; }

    public event EventHandler? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.SettingsFile) || new FileInfo(_paths.SettingsFile).Length == 0)
            {
                Settings = new AppSettings();
                await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await using var stream = File.OpenRead(_paths.SettingsFile);
                Settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                           ?? new AppSettings();
            }
            catch (JsonException)
            {
                // settings.json is corrupt or unreadable — fall back to defaults rather than crashing.
                Settings = new AppSettings();
                await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(bool notifyChanged = true, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        if (notifyChanged)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ReplaceSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Settings = settings;
            await SaveInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveInternalAsync(CancellationToken cancellationToken)
    {
        var tempFile = _paths.SettingsFile + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, Settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempFile, _paths.SettingsFile, overwrite: true);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
    }
}

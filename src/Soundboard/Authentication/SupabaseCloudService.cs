using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Cloud sync for settings and sound-library METADATA only (names, tags, favorites, folder
/// organization) — never the audio files themselves, so this needs no Supabase Storage bucket,
/// just the "cloud_sync" Postgres table (see supabase-schema.sql section 9).
///
/// Sync is manual ("Sync Now"), last-write-wins per data type: whichever side (this device's
/// local file mtime, or the remote row's *_updated_at) is newer wins outright — there's no
/// field-by-field merge for settings. Library sounds are different: since audio files aren't
/// synced, a device can only ever be missing files another device has, so pulling matches
/// incoming metadata to LOCAL sounds by filename and updates only those — it never creates
/// entries for files this device doesn't have, and never deletes local sounds/folders.
/// </summary>
public sealed class SupabaseCloudService : ICloudService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;
    private readonly ISessionService _sessionService;
    private readonly ISettingsService _settingsService;
    private readonly ILibraryService _libraryService;
    private readonly IAppPaths _paths;

    private static readonly JsonSerializerOptions RowJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PayloadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SupabaseCloudService(
        SupabaseConfig config,
        ISessionService sessionService,
        ISettingsService settingsService,
        ILibraryService libraryService,
        IAppPaths paths)
    {
        _config = config;
        _sessionService = sessionService;
        _settingsService = settingsService;
        _libraryService = libraryService;
        _paths = paths;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public bool IsAvailable => _config.IsConfigured && _sessionService.IsLoggedIn;

    public async Task<DateTime?> GetLastSyncTimeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        var row = await FetchRowAsync(cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        DateTime? latest = row.SettingsUpdatedAt;
        if (row.LibraryUpdatedAt is { } libraryTime && (latest is null || libraryTime > latest))
        {
            latest = libraryTime;
        }

        return latest;
    }

    public async Task SyncSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Cloud sync isn't available — check you're logged in and the app is configured.");

        var row = await FetchRowAsync(cancellationToken).ConfigureAwait(false);
        var localModifiedAt = File.Exists(_paths.SettingsFile) ? File.GetLastWriteTimeUtc(_paths.SettingsFile) : DateTime.MinValue;

        if (row?.SettingsUpdatedAt is { } remoteTime && remoteTime > localModifiedAt && row.SettingsJson is not null)
        {
            var downloaded = JsonSerializer.Deserialize<AppSettings>(row.SettingsJson.Value.GetRawText(), PayloadJsonOptions);
            if (downloaded is not null)
            {
                await _settingsService.ReplaceSettingsAsync(downloaded, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await PushAsync(new
            {
                user_id = _sessionService.CurrentSession!.UserId,
                settings_json = _settingsService.Settings,
                settings_updated_at = DateTime.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SyncSoundLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Cloud sync isn't available — check you're logged in and the app is configured.");

        var row = await FetchRowAsync(cancellationToken).ConfigureAwait(false);
        var localModifiedAt = File.Exists(_paths.LibraryFile) ? File.GetLastWriteTimeUtc(_paths.LibraryFile) : DateTime.MinValue;

        if (row?.LibraryUpdatedAt is { } remoteTime && remoteTime > localModifiedAt && row.LibraryJson is not null)
        {
            var downloaded = JsonSerializer.Deserialize<LibraryMetadataPayload>(row.LibraryJson.Value.GetRawText(), PayloadJsonOptions);
            if (downloaded is not null)
            {
                await ApplyLibraryMetadataAsync(downloaded, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var payload = BuildLibraryMetadataPayload();
            await PushAsync(new
            {
                user_id = _sessionService.CurrentSession!.UserId,
                library_json = payload,
                library_updated_at = DateTime.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private LibraryMetadataPayload BuildLibraryMetadataPayload()
    {
        var library = _libraryService.Library;
        var folderNameById = library.Folders.ToDictionary(f => f.Id, f => f.Name);

        return new LibraryMetadataPayload
        {
            FolderNames = library.Folders.Select(f => f.Name).ToList(),
            Sounds = library.Sounds.Select(s => new SoundMetadataPayload
            {
                FileName = s.FileName,
                Name = s.Name,
                IsFavorite = s.IsFavorite,
                Tags = [.. s.Tags],
                FolderName = s.FolderId is not null && folderNameById.TryGetValue(s.FolderId, out var name) ? name : null
            }).ToList()
        };
    }

    private async Task ApplyLibraryMetadataAsync(LibraryMetadataPayload payload, CancellationToken cancellationToken)
    {
        var library = _libraryService.Library;

        // Match/create folders by name — folder IDs are locally generated GUIDs that never
        // match across devices, so name is the only sensible join key.
        var folderIdByName = library.Folders.ToDictionary(f => f.Name, f => f.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in payload.FolderNames)
        {
            if (folderIdByName.ContainsKey(folderName)) continue;

            var folder = new SoundFolder { Name = folderName, SortOrder = library.Folders.Count };
            library.Folders.Add(folder);
            folderIdByName[folderName] = folder.Id;
        }

        // Only touch sounds that already exist locally (matched by filename) — never create
        // entries for audio files this device doesn't actually have, and never delete anything.
        var soundsByFileName = library.Sounds
            .GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var remoteSound in payload.Sounds)
        {
            if (!soundsByFileName.TryGetValue(remoteSound.FileName, out var localSound)) continue;

            localSound.Name = remoteSound.Name;
            localSound.IsFavorite = remoteSound.IsFavorite;
            localSound.Tags = [.. remoteSound.Tags];
            localSound.FolderId = remoteSound.FolderName is not null && folderIdByName.TryGetValue(remoteSound.FolderName, out var folderId)
                ? folderId
                : null;
        }

        await _libraryService.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CloudSyncRow?> FetchRowAsync(CancellationToken cancellationToken)
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.ProjectUrl}/rest/v1/cloud_sync?user_id=eq.{Uri.EscapeDataString(session.UserId)}&select=*");
        request.Headers.Add("apikey", _config.AnonKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Couldn't reach cloud sync right now.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var rows = JsonSerializer.Deserialize<List<CloudSyncRow>>(body, RowJsonOptions);
        return rows?.FirstOrDefault();
    }

    private async Task PushAsync(object payload, CancellationToken cancellationToken)
    {
        var session = _sessionService.CurrentSession;
        if (session is null) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/cloud_sync?on_conflict=user_id");
        request.Headers.Add("apikey", _config.AnonKey);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("Prefer", "resolution=merge-duplicates");
        request.Content = JsonContent.Create(payload, options: PayloadJsonOptions);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Couldn't upload to cloud sync right now.");
        }
    }

    private sealed class CloudSyncRow
    {
        [JsonPropertyName("settings_json")] public JsonElement? SettingsJson { get; set; }
        [JsonPropertyName("settings_updated_at")] public DateTime? SettingsUpdatedAt { get; set; }
        [JsonPropertyName("library_json")] public JsonElement? LibraryJson { get; set; }
        [JsonPropertyName("library_updated_at")] public DateTime? LibraryUpdatedAt { get; set; }
    }

    private sealed class SoundMetadataPayload
    {
        public string FileName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsFavorite { get; set; }
        public List<string> Tags { get; set; } = [];
        public string? FolderName { get; set; }
    }

    private sealed class LibraryMetadataPayload
    {
        public List<string> FolderNames { get; set; } = [];
        public List<SoundMetadataPayload> Sounds { get; set; } = [];
    }
}

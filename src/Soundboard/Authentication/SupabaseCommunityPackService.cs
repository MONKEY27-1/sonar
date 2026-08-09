using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Reads/writes the community_packs table (see supabase-schema.sql section 12) — the "Basic
/// Plugin" (settings-pack, no code) counterpart to SupabaseCommunityPluginService. Same public-read/
/// server-trigger-owns-authorship pattern; pack_json is a jsonb column holding a serialized
/// PluginPack, deserialized directly as part of the row (case-insensitive matching handles it
/// recursively, no separate parse step needed).
/// </summary>
public sealed class SupabaseCommunityPackService : ICommunityPackService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseCommunityPackService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<CommunityPack>> SearchAsync(string? query, bool verifiedOnly, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return [];

        try
        {
            var url = $"{_config.ProjectUrl}/rest/v1/community_packs?select=id,name,description,author_username,pack_json,is_verified,created_at&order=created_at.desc";

            if (verifiedOnly)
            {
                url += "&is_verified=eq.true";
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                var escaped = Uri.EscapeDataString(query.Trim());
                url += $"&or=(name.ilike.*{escaped}*,description.ilike.*{escaped}*)";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _config.AnonKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<CommunityPackRow>>(body, JsonOptions) ?? [];

            return rows
                .Where(r => r.PackJson is not null)
                .Select(r => new CommunityPack
                {
                    Id = r.Id ?? string.Empty,
                    Name = r.Name ?? string.Empty,
                    Description = r.Description,
                    AuthorUsername = r.AuthorUsername ?? "unknown",
                    Pack = r.PackJson!,
                    IsVerified = r.IsVerified,
                    CreatedAt = r.CreatedAt ?? DateTime.UtcNow
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<AuthResult> SubmitAsync(AuthSession session, string name, string? description, PluginPack pack, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/community_packs");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                name,
                description,
                pack_json = pack
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't publish your plugin — make sure you're signed in.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    private sealed class CommunityPackRow
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("author_username")] public string? AuthorUsername { get; set; }
        [JsonPropertyName("pack_json")] public PluginPack? PackJson { get; set; }
        [JsonPropertyName("is_verified")] public bool IsVerified { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}

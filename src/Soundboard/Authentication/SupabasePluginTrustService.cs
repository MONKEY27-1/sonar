using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;

namespace Soundboard.Authentication;

/// <summary>
/// Reads the plugin_trust table (see supabase-schema.sql) directly via PostgREST — a plain GET
/// governed by a public "select using (true)" RLS policy, so this works with just the anon key
/// and needs no logged-in session, unlike SupabaseCloudService/SupabaseAdminService. Writes
/// (verifying/unverifying a plugin) are admin-only and go through IAdminService instead.
/// </summary>
public sealed class SupabasePluginTrustService : IPluginTrustService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    public SupabasePluginTrustService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlySet<string>> GetVerifiedPluginIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return new HashSet<string>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_config.ProjectUrl}/rest/v1/plugin_trust?select=plugin_id&is_verified=eq.true");
            request.Headers.Add("apikey", _config.AnonKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new HashSet<string>();

            var rows = await response.Content
                .ReadFromJsonAsync<List<PluginTrustRow>>(cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? [];

            return rows.Select(r => r.PluginId).Where(id => id is not null).ToHashSet()!;
        }
        catch
        {
            // Never surfaced as an error — offline, misconfigured, or a server hiccup should all
            // just mean "nothing shows as verified yet", the same safe-fallback contract as
            // ICloudService.GetLastSyncTimeAsync.
            return new HashSet<string>();
        }
    }

    private sealed class PluginTrustRow
    {
        [JsonPropertyName("plugin_id")]
        public string? PluginId { get; set; }
    }
}

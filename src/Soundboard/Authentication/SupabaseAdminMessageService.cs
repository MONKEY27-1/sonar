using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;

namespace Soundboard.Authentication;

/// <summary>
/// Reads the admin_message singleton row (see supabase-schema.sql section 13) — public, no
/// session needed, same style as SupabasePluginTrustService. Writes are admin-only and go
/// through IAdminService.SetAdminMessageAsync instead.
/// </summary>
public sealed class SupabaseAdminMessageService : IAdminMessageService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseAdminMessageService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<string?> GetMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return null;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_config.ProjectUrl}/rest/v1/admin_message?select=message&id=eq.1");
            request.Headers.Add("apikey", _config.AnonKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<AdminMessageRow>>(body, JsonOptions) ?? [];
            var message = rows.FirstOrDefault()?.Message;

            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch
        {
            // Never surfaced as an error — same safe-fallback contract as IPluginTrustService.
            return null;
        }
    }

    private sealed class AdminMessageRow
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

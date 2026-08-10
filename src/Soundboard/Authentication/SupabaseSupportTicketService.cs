using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Reads/writes support_tickets and support_ticket_messages (see supabase-schema.sql section
/// 17). Ticket reads are scoped to the caller's own tickets by RLS (auth.uid() = user_id), not a
/// public-read table like SupabasePluginTrustService — a real Bearer token is required for
/// GetMyTicketsAsync/GetMessagesAsync to return anything. Every write (creating a ticket,
/// sending a message) goes through a security definer RPC rather than a raw insert, so
/// sender_id/sender_username/is_admin can never be spoofed by the client — same author-
/// enforcement principle as SupabaseCommunityPluginService, just via a function instead of a
/// trigger since these writes also need atomic side effects (creating the first message,
/// reopening a resolved ticket).
/// </summary>
public sealed class SupabaseSupportTicketService : ISupportTicketService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseSupportTicketService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<SupportTicket>> GetMyTicketsAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return [];

        try
        {
            var url = $"{_config.ProjectUrl}/rest/v1/support_tickets?select=id,subject,status,created_at&user_id=eq.{session.UserId}&order=created_at.desc";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<SupportTicketRow>>(body, JsonOptions) ?? [];

            return rows.Select(ToTicket).ToList();
        }
        catch
        {
            // Never surfaced as an error — same safe-fallback contract as IPluginTrustService.
            return [];
        }
    }

    public async Task<IReadOnlyList<SupportTicketMessage>> GetMessagesAsync(AuthSession session, string ticketId, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return [];

        try
        {
            var url = $"{_config.ProjectUrl}/rest/v1/support_ticket_messages?select=id,ticket_id,sender_username,is_admin,body,created_at&ticket_id=eq.{ticketId}&order=created_at.asc";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<SupportTicketMessageRow>>(body, JsonOptions) ?? [];

            return rows.Select(ToMessage).ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<AuthResult<string>> CreateTicketAsync(AuthSession session, string subject, string message, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<string>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/create_support_ticket");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { subject_text = subject, body_text = message });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<string>.Fail("Couldn't submit your request — make sure you're signed in.", AuthErrorKind.NotAuthenticated);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var ticketId = JsonSerializer.Deserialize<string>(body, JsonOptions);

            return string.IsNullOrWhiteSpace(ticketId)
                ? AuthResult<string>.Fail("Couldn't submit your request.", AuthErrorKind.ServerUnavailable)
                : AuthResult<string>.Ok(ticketId);
        }
        catch (HttpRequestException)
        {
            return AuthResult<string>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SendMessageAsync(AuthSession session, string ticketId, string message, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/send_ticket_message");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { target_ticket_id = ticketId, body_text = message });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't send your message.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    internal static SupportTicket ToTicket(SupportTicketRow row) => new()
    {
        Id = row.Id ?? string.Empty,
        Username = row.Username,
        Subject = row.Subject ?? string.Empty,
        Status = row.Status ?? "open",
        CreatedAt = row.CreatedAt ?? DateTime.UtcNow
    };

    internal static SupportTicketMessage ToMessage(SupportTicketMessageRow row) => new()
    {
        Id = row.Id ?? string.Empty,
        TicketId = row.TicketId ?? string.Empty,
        SenderUsername = row.SenderUsername,
        IsAdmin = row.IsAdmin,
        Body = row.Body ?? string.Empty,
        CreatedAt = row.CreatedAt ?? DateTime.UtcNow
    };

    internal sealed class SupportTicketRow
    {
        public string? Id { get; set; }
        public string? Username { get; set; }
        public string? Subject { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }

    internal sealed class SupportTicketMessageRow
    {
        public string? Id { get; set; }
        [JsonPropertyName("ticket_id")] public string? TicketId { get; set; }
        [JsonPropertyName("sender_username")] public string? SenderUsername { get; set; }
        [JsonPropertyName("is_admin")] public bool IsAdmin { get; set; }
        public string? Body { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}

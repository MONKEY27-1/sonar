using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Talks to the admin_list_users()/admin_update_user() Postgres RPCs (see supabase-schema.sql)
/// for the in-app Admin Panel. Every call is still server-enforced independent of this class —
/// the RPCs re-check the caller is actually an Administrator themselves, so this is a client for
/// a privileged API, not the thing that decides who's allowed to use it.
/// </summary>
public sealed class SupabaseAdminService : IAdminService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SupabaseAdminService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<AuthResult<IReadOnlyList<AdminUserSummary>>> ListUsersAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<IReadOnlyList<AdminUserSummary>>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_list_users");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The RPC raises an exception (PostgREST maps it to a non-2xx) if the caller
                // isn't actually an admin — that's the real, server-side enforcement here, not
                // just the Admin Panel button being hidden in the UI.
                return AuthResult<IReadOnlyList<AdminUserSummary>>.Fail("Couldn't load users — you may not have admin access.", AuthErrorKind.NotAuthenticated);
            }

            var rows = JsonSerializer.Deserialize<List<AdminUserRow>>(body, JsonOptions) ?? [];
            var users = rows.Select(row => new AdminUserSummary
            {
                UserId = row.UserId,
                Username = row.Username ?? string.Empty,
                Email = row.Email ?? string.Empty,
                License = row.License.ParseOrFree(),
                IsBetaTester = row.IsBetaTester,
                IsSuspended = row.IsSuspended,
                EmailVerified = row.EmailVerified,
                CreatedAt = row.CreatedAt ?? DateTime.UtcNow,
                LastLoginAt = row.LastLoginAt
            }).ToList();

            return AuthResult<IReadOnlyList<AdminUserSummary>>.Ok(users);
        }
        catch (HttpRequestException)
        {
            return AuthResult<IReadOnlyList<AdminUserSummary>>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> UpdateUserAsync(AuthSession session, string targetUserId, LicenseType license, bool isBetaTester, bool isSuspended, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_update_user");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_user_id = targetUserId,
                new_license = license.ToString(),
                new_is_beta_tester = isBetaTester,
                new_is_suspended = isSuspended
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SetPluginVerifiedAsync(AuthSession session, string pluginId, bool isVerified, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_set_plugin_verified");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_plugin_id = pluginId,
                new_is_verified = isVerified
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SetCommunityPluginVerifiedAsync(AuthSession session, string communityPluginId, bool isVerified, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_set_community_plugin_verified");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_plugin_id = communityPluginId,
                new_is_verified = isVerified
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> DeleteCommunityPluginAsync(AuthSession session, string communityPluginId, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_delete_community_plugin");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_plugin_id = communityPluginId
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't delete — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SetCommunityPackVerifiedAsync(AuthSession session, string communityPackId, bool isVerified, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_set_community_pack_verified");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_pack_id = communityPackId,
                new_is_verified = isVerified
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> DeleteCommunityPackAsync(AuthSession session, string communityPackId, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_delete_community_pack");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_pack_id = communityPackId
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't delete — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SetAdminMessageAsync(AuthSession session, string message, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_set_message");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { new_message = message });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult<IReadOnlyList<ContentReportSummary>>> ListReportsAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<IReadOnlyList<ContentReportSummary>>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_list_reports");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<IReadOnlyList<ContentReportSummary>>.Fail("Couldn't load reports — you may not have admin access.", AuthErrorKind.NotAuthenticated);
            }

            var rows = JsonSerializer.Deserialize<List<ContentReportRow>>(body, JsonOptions) ?? [];
            var reports = rows.Select(row => new ContentReportSummary
            {
                Id = row.Id ?? string.Empty,
                ContentType = row.ContentType ?? string.Empty,
                ContentId = row.ContentId ?? string.Empty,
                ContentName = row.ContentName ?? string.Empty,
                ReporterUsername = row.ReporterUsername,
                Reason = row.Reason ?? string.Empty,
                Status = row.Status ?? "open",
                CreatedAt = row.CreatedAt ?? DateTime.UtcNow
            }).ToList();

            return AuthResult<IReadOnlyList<ContentReportSummary>>.Ok(reports);
        }
        catch (HttpRequestException)
        {
            return AuthResult<IReadOnlyList<ContentReportSummary>>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SetReportStatusAsync(AuthSession session, string reportId, string newStatus, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_set_report_status");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_report_id = reportId,
                new_status = newStatus
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't save changes — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult<IReadOnlyList<SupportTicket>>> ListSupportTicketsAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<IReadOnlyList<SupportTicket>>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_list_support_tickets");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<IReadOnlyList<SupportTicket>>.Fail("Couldn't load support tickets — you may not have admin access.", AuthErrorKind.NotAuthenticated);
            }

            var rows = JsonSerializer.Deserialize<List<SupabaseSupportTicketService.SupportTicketRow>>(body, JsonOptions) ?? [];
            var tickets = rows.Select(SupabaseSupportTicketService.ToTicket).ToList();

            return AuthResult<IReadOnlyList<SupportTicket>>.Ok(tickets);
        }
        catch (HttpRequestException)
        {
            return AuthResult<IReadOnlyList<SupportTicket>>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult<IReadOnlyList<SupportTicketMessage>>> ListTicketMessagesAsync(AuthSession session, string ticketId, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult<IReadOnlyList<SupportTicketMessage>>.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_list_ticket_messages");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new { target_ticket_id = ticketId });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult<IReadOnlyList<SupportTicketMessage>>.Fail("Couldn't load messages — you may not have admin access.", AuthErrorKind.NotAuthenticated);
            }

            var rows = JsonSerializer.Deserialize<List<SupabaseSupportTicketService.SupportTicketMessageRow>>(body, JsonOptions) ?? [];
            var messages = rows.Select(SupabaseSupportTicketService.ToMessage).ToList();

            return AuthResult<IReadOnlyList<SupportTicketMessage>>.Ok(messages);
        }
        catch (HttpRequestException)
        {
            return AuthResult<IReadOnlyList<SupportTicketMessage>>.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    public async Task<AuthResult> SendAdminTicketMessageAsync(AuthSession session, string ticketId, string message, string newStatus, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/rpc/admin_send_ticket_message");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                target_ticket_id = ticketId,
                body_text = message,
                new_status = newStatus
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't send — you may not have admin access.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }

    private sealed class AdminUserRow
    {
        [JsonPropertyName("user_id")] public string UserId { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? License { get; set; }
        [JsonPropertyName("is_beta_tester")] public bool IsBetaTester { get; set; }
        [JsonPropertyName("is_suspended")] public bool IsSuspended { get; set; }
        [JsonPropertyName("email_verified")] public bool EmailVerified { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("last_login_at")] public DateTime? LastLoginAt { get; set; }
    }

    private sealed class ContentReportRow
    {
        public string? Id { get; set; }
        [JsonPropertyName("content_type")] public string? ContentType { get; set; }
        [JsonPropertyName("content_id")] public string? ContentId { get; set; }
        [JsonPropertyName("content_name")] public string? ContentName { get; set; }
        [JsonPropertyName("reporter_username")] public string? ReporterUsername { get; set; }
        public string? Reason { get; set; }
        public string? Status { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}

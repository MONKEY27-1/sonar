using System.Net.Http;
using System.Net.Http.Json;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Authentication;

/// <summary>
/// Writes to the content_reports table (see supabase-schema.sql section 14). Submitting is the
/// only operation exposed here — reading reports back is admin-only, via
/// IAdminService.ListReportsAsync, not this service; reporter_id/reporter_username are always
/// overwritten server-side by a trigger, same author-enforcement pattern as
/// SupabaseCommunityPluginService.
/// </summary>
public sealed class SupabaseContentReportService : IContentReportService
{
    private readonly HttpClient _http;
    private readonly SupabaseConfig _config;

    public SupabaseContentReportService(SupabaseConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<AuthResult> SubmitReportAsync(AuthSession session, ContentReportKind kind, string contentId, string contentName, string reason, CancellationToken cancellationToken = default)
    {
        if (!_config.IsConfigured) return AuthResult.Fail("Cloud features aren't configured yet.", AuthErrorKind.ServerUnavailable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ProjectUrl}/rest/v1/content_reports");
            request.Headers.Add("apikey", _config.AnonKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = JsonContent.Create(new
            {
                content_type = kind.ToWireValue(),
                content_id = contentId,
                content_name = contentName,
                reason
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? AuthResult.Ok()
                : AuthResult.Fail("Couldn't submit your report — make sure you're signed in.", AuthErrorKind.NotAuthenticated);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("Couldn't reach the server. Check your internet connection.", AuthErrorKind.NoInternet);
        }
    }
}

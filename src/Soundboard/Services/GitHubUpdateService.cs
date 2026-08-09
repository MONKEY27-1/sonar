using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

/// <summary>Checks GitHub Releases for a newer build than the running app. Uses the public,
/// unauthenticated "latest release" endpoint — fine for a small app's update-check volume,
/// no token/auth needed since the repo is public.</summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private const string RepoOwner = "MONKEY27-1";
    private const string RepoName = "sonar";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await HttpClient
                .GetFromJsonAsync<GitHubRelease>(
                    $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
                    cancellationToken)
                .ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease) return null;

            var tag = release.TagName?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var releaseVersion)) return null;

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion is null || releaseVersion <= currentVersion) return null;

            var installerAsset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
            if (installerAsset?.BrowserDownloadUrl is null) return null;

            return new UpdateInfo
            {
                Version = releaseVersion,
                DownloadUrl = installerAsset.BrowserDownloadUrl,
                ReleaseUrl = release.HtmlUrl ?? $"https://github.com/{RepoOwner}/{RepoName}/releases/latest",
                ReleaseNotes = release.Body
            };
        }
        catch
        {
            // Silent by design — this always runs unattended in the background (app startup,
            // or a manual "Check for Updates" click that itself handles a null result as
            // "no update found"). Network errors, rate limiting, and malformed responses all
            // collapse to the same "nothing to report" outcome.
            return null;
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var destinationPath = Path.Combine(Path.GetTempPath(), $"SonarSetup-{update.Version}.exe");

        using var response = await HttpClient
            .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes is > 0)
            {
                progress?.Report((double)totalRead / totalBytes.Value);
            }
        }

        return destinationPath;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        // GitHub's API 403s any request without a User-Agent header.
        client.DefaultRequestHeaders.Add("User-Agent", "Sonar-Soundboard-Updater");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset>? Assets { get; set; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Dasher.Windows.Services;

public class UpdateInfo
{
    public string LatestTag { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
}

public static class UpdateChecker
{
    private const string RepoOwner = "dasher-project";
    private const string RepoName = "Dasher-Windows";
    private const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    private const string ReleasesPage = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var infoVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVersion))
            return infoVersion.Split('+')[0].TrimStart('v');
        return assembly?.GetName().Version?.ToString() ?? "0.0.0";
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        var current = GetCurrentVersion();

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.Add("User-Agent", "Dasher-Windows-UpdateCheck");
            request.Headers.Add("Accept", "application/vnd.github+json");

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GithubRelease>(json);

            if (release?.TagName is null)
                return new UpdateInfo { CurrentVersion = current };

            var latestRaw = release.TagName.TrimStart('v');
            var isUpdate = IsNewerVersion(latestRaw, current);

            return new UpdateInfo
            {
                LatestTag = release.TagName,
                CurrentVersion = current,
                ReleaseUrl = release.HtmlUrl ?? ReleasesPage,
                IsUpdateAvailable = isUpdate,
            };
        }
        catch
        {
            return new UpdateInfo { CurrentVersion = current };
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var latestVer) &&
            Version.TryParse(current, out var currentVer))
        {
            return latestVer > currentVer;
        }
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    private class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

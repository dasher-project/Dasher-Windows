using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=1";
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
            var releases = JsonSerializer.Deserialize<List<GithubRelease>>(json);

            if (releases is null || releases.Count == 0 || releases[0].TagName is null)
                return new UpdateInfo { CurrentVersion = current };

            var release = releases[0];
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

    /// <summary>
    /// Semver-aware comparison. Handles prerelease labels so that
    /// 0.1.0-preview9 > 0.1.0-preview8, and 0.1.0 > 0.1.0-preview9.
    /// </summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        var l = ParseSemver(latest);
        var c = ParseSemver(current);

        int cmp = CompareSemver(l, c);
        return cmp > 0;
    }

    private record SemverParts(int Major, int Minor, int Patch, string? Prerelease)
    {
        public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);
    }

    private static readonly Regex SemverRegex =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[^+]+))?",
            RegexOptions.Compiled);

    private static SemverParts ParseSemver(string version)
    {
        var match = SemverRegex.Match(version);
        if (!match.Success)
            return new SemverParts(0, 0, 0, version);

        return new SemverParts(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
    }

    private static int CompareSemver(SemverParts a, SemverParts b)
    {
        // Compare major.minor.patch numerically
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        if (a.Patch != b.Patch) return a.Patch.CompareTo(b.Patch);

        // A release version is greater than any prerelease
        if (!a.IsPrerelease && b.IsPrerelease) return 1;
        if (a.IsPrerelease && !b.IsPrerelease) return -1;

        // Both prerelease: compare prerelease labels
        // Try numeric comparison for common patterns like "preview9" vs "preview8"
        return ComparePrerelease(a.Prerelease, b.Prerelease);
    }

    private static int ComparePrerelease(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
        if (string.IsNullOrEmpty(a)) return 1;
        if (string.IsNullOrEmpty(b)) return -1;

        // Extract trailing numbers for comparison (e.g. "preview9" vs "preview8")
        var matchA = Regex.Match(a, @"^(.*?)(\d+)$");
        var matchB = Regex.Match(b, @"^(.*?)(\d+)$");

        if (matchA.Success && matchB.Success &&
            matchA.Groups[1].Value == matchB.Groups[1].Value)
        {
            return int.Parse(matchA.Groups[2].Value)
                .CompareTo(int.Parse(matchB.Groups[2].Value));
        }

        // Fall back to lexical comparison
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

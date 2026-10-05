using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LloydsTracker;

/// <summary>A new version published on GitHub.</summary>
/// <param name="Version">Version without the "v" (tag <c>v1.7.0</c> → "1.7.0").</param>
/// <param name="Url">Release page with the .zip / .exe / .tar.gz to download.</param>
public sealed record AvailableUpdate(string Version, string Url);

/// <summary>New-version check through the GitHub API (latest release) — mirrors the macOS
/// UpdateChecker. It only notifies: nothing gets downloaded or installed, the user downloads
/// from the release page.</summary>
internal static class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/hrastnik/lloyds-tracker/releases/latest";
    /// <summary>If the response has no <c>html_url</c>, link to the latest release.</summary>
    public const string LatestReleasePage = "https://github.com/hrastnik/lloyds-tracker/releases/latest";

    /// <summary>One client for the app's lifetime (a new HttpClient per request leaks sockets).</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>This app's version, from the assembly (the csproj <c>&lt;Version&gt;</c>, which
    /// the release CI overrides with <c>-p:Version=X.Y.Z</c>) as Major.Minor.Build. Not
    /// ProductVersion / InformationalVersion — .NET 8 appends "+commit" to those. null if
    /// it can't be read (then there's no check either).</summary>
    public static string? CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                if (v == null) return null;
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The latest published release — version without the "v" (tag <c>v1.7.0</c> →
    /// "1.7.0"). Throws on no network, a timeout, a non-200 answer or unexpected JSON.</summary>
    public static async Task<AvailableUpdate> FetchLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        // GitHub rejects API requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd("LloydsTracker");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"GitHub API: HTTP {(int)response.StatusCode}");

        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String)
            throw new JsonException("GitHub API: missing tag_name");
        string tag = tagElement.GetString() ?? "";
        string version = tag.StartsWith('v') ? tag[1..] : tag;

        string url = LatestReleasePage;
        if (root.TryGetProperty("html_url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String
            && urlElement.GetString() is { Length: > 0 } htmlUrl)
            url = htmlUrl;

        return new AvailableUpdate(version, url);
    }

    /// <summary>Compares number by number ("1.10.0" is newer than "1.9.2"); a missing part is 0.</summary>
    public static bool IsNewer(string a, string than)
    {
        var pa = Parts(a);
        var pb = Parts(than);
        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            int x = i < pa.Count ? pa[i] : 0;
            int y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>Each part counts only its leading digits ("1.7.0-beta" → 1, 7, 0).</summary>
    private static List<int> Parts(string version)
        => version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                string digits = new(part.TakeWhile(char.IsAsciiDigit).ToArray());
                return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
            })
            .ToList();
}

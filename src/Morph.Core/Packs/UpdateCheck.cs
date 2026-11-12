using System.Net.Http.Headers;
using System.Text.Json;

namespace Morph.Core.Packs;

/// <summary>A newer Morph on GitHub: its version and release page.</summary>
public sealed record NewRelease(Version Version, Uri Page);

/// <summary>
/// Settings → About → Check for updates (PLAN D12): one request to GitHub's releases API, only when the user clicks.
/// The newest published, non-prerelease <c>vX.Y.Z</c> release counts; pack releases (<c>pack-…</c>) are skipped.
/// Nothing is downloaded: the user gets the release page.
/// </summary>
public static class UpdateCheck
{
    public const string ReleasesApi = "https://api.github.com/repos/asyncninja0012/Morph/releases?per_page=30";

    /// <returns>The newer release, or null if <paramref name="current"/> is the newest.</returns>
    /// <exception cref="HttpRequestException">GitHub couldn't be reached or refused.</exception>
    public static async Task<NewRelease?> NewerAsync(Version current, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Morph", current.ToString(3))); // GitHub requires one
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        await using var body = await http.GetStreamAsync(ReleasesApi, ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        NewRelease? newest = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()
                || release.GetProperty("tag_name").GetString() is not ['v', .. var tag] || !Version.TryParse(tag, out var version)
                || !Uri.TryCreate(release.GetProperty("html_url").GetString(), UriKind.Absolute, out var page) || page.Host != "github.com")
            {
                continue;
            }

            if (version > current && (newest is null || version > newest.Version))
            {
                newest = new(version, page);
            }
        }

        return newest;
    }
}

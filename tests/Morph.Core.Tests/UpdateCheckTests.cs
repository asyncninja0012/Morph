using System.Net;
using System.Text;

using Morph.Core.Packs;

namespace Morph.Core.Tests;

public sealed class UpdateCheckTests
{
    private const string Releases = """
        [
          {"tag_name": "pack-office-26.8.2", "draft": false, "prerelease": false, "html_url": "https://github.com/asyncninja0012/Morph/releases/tag/pack-office-26.8.2"},
          {"tag_name": "v1.2.0", "draft": false, "prerelease": true, "html_url": "https://github.com/asyncninja0012/Morph/releases/tag/v1.2.0"},
          {"tag_name": "v1.3.0", "draft": true, "prerelease": false, "html_url": "https://github.com/asyncninja0012/Morph/releases/tag/v1.3.0"},
          {"tag_name": "v1.1.0", "draft": false, "prerelease": false, "html_url": "https://github.com/asyncninja0012/Morph/releases/tag/v1.1.0"},
          {"tag_name": "v1.0.1", "draft": false, "prerelease": false, "html_url": "https://github.com/asyncninja0012/Morph/releases/tag/v1.0.1"},
          {"tag_name": "v9.0.0", "draft": false, "prerelease": false, "html_url": "https://evil.example/download"}
        ]
        """;

    [Fact]
    public async Task Finds_the_newest_published_release_and_skips_packs_prereleases_drafts_and_other_sites()
    {
        var server = new Server(Releases);

        var update = await UpdateCheck.NewerAsync(new Version(1, 0, 0), TestContext.Current.CancellationToken, server);

        Assert.Equal(new Version(1, 1, 0), update?.Version);
        Assert.Equal("https://github.com/asyncninja0012/Morph/releases/tag/v1.1.0", update?.Page.AbsoluteUri);
        Assert.StartsWith("Morph/1.0.0", server.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_to_date_gives_null() =>
        Assert.Null(await UpdateCheck.NewerAsync(new Version(1, 1, 0), TestContext.Current.CancellationToken, new Server(Releases)));

    [Fact]
    public async Task No_releases_or_an_error_from_GitHub()
    {
        Assert.Null(await UpdateCheck.NewerAsync(new Version(0, 9, 0), TestContext.Current.CancellationToken, new Server("[]")));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UpdateCheck.NewerAsync(new Version(0, 9, 0), TestContext.Current.CancellationToken, new Server("{}", HttpStatusCode.Forbidden)));
    }

    private sealed class Server(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string UserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

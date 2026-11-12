using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using Morph.Core.Jobs;
using Morph.Core.Packs;

namespace Morph.Core.Tests;

public sealed class PackStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("morph-packs-").FullName;
    private readonly byte[] _zip = MakeZip();
    private readonly FakeServer _server;

    public PackStoreTests() => _server = new(_zip);

    private string Root => Path.Combine(_folder, "packs");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Install_downloads_checks_and_extracts()
    {
        var store = new PackStore(Root, _server);
        var pack = Pack();

        var folder = await store.InstallAsync(pack, new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(Root, "test", "1.0"), folder);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(folder, "bin", "hello.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(folder, store.Folder(pack));
        Assert.Empty(Directory.EnumerateFiles(Root, "*.part"));
        Assert.Equal(5, store.SizeOnDisk("test"));
    }

    [Fact]
    public async Task A_partial_download_resumes_with_a_range_request()
    {
        var store = new PackStore(Root, _server);
        var pack = Pack();
        Directory.CreateDirectory(Root);
        await File.WriteAllBytesAsync(Path.Combine(Root, "test-1.0.part"), _zip[..100], TestContext.Current.CancellationToken);
        Assert.Equal(100, store.Downloaded(pack));
        Assert.Equal(_zip.Length - 100 + pack.InstalledSize, store.SpaceNeeded(pack));

        await store.InstallAsync(pack, new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken);

        Assert.Equal(100, _server.LastRangeFrom);
        Assert.NotNull(store.Folder(pack));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_starts_over()
    {
        _server.IgnoreRange = true;
        var store = new PackStore(Root, _server);
        Directory.CreateDirectory(Root);
        await File.WriteAllBytesAsync(Path.Combine(Root, "test-1.0.part"), new byte[100], TestContext.Current.CancellationToken);

        await store.InstallAsync(Pack(), new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken);

        Assert.NotNull(store.Folder(Pack()));
    }

    [Fact]
    public async Task A_dropped_connection_keeps_the_partial_download()
    {
        _server.StopAfter = 100;
        var store = new PackStore(Root, _server);

        var ex = await Assert.ThrowsAsync<EngineException>(() => store.InstallAsync(Pack(), new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken));

        Assert.Contains("picks up where it left off", ex.UserMessage);
        Assert.Equal(100, store.Downloaded(Pack()));
        Assert.Null(store.Folder(Pack()));
    }

    [Fact]
    public async Task A_wrong_hash_deletes_the_download_and_installs_nothing()
    {
        var store = new PackStore(Root, _server);
        var pack = Pack() with { Sha256 = new string('0', 64) };

        var ex = await Assert.ThrowsAsync<EngineException>(() => store.InstallAsync(pack, new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken));

        Assert.Contains("damaged", ex.UserMessage);
        Assert.Equal(0, store.Downloaded(pack));
        Assert.False(Directory.Exists(Path.Combine(Root, "test")));
    }

    [Fact]
    public async Task A_new_version_replaces_the_old_and_remove_deletes_everything()
    {
        var store = new PackStore(Root, _server);
        await store.InstallAsync(Pack() with { Version = "0.9" }, new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken);

        await store.InstallAsync(Pack(), new SyncProgress<long>(_ => { }), TestContext.Current.CancellationToken);

        Assert.Equal([Path.Combine(Root, "test", "1.0")], Directory.GetDirectories(Path.Combine(Root, "test")));
        await File.WriteAllBytesAsync(Path.Combine(Root, "test-2.0.part"), new byte[10], TestContext.Current.CancellationToken);
        store.Remove("test");
        Assert.Equal(0, store.SizeOnDisk("test"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    private PackInfo Pack() => new("test", "Test pack", "1.0", new("https://example.invalid/test.zip"), _zip.Length, 5,
        Convert.ToHexString(SHA256.HashData(_zip)), new("https://example.invalid/licence"));

    private static byte[] MakeZip()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("bin/hello.txt").Open());
            writer.Write("hello");
        }

        return bytes.ToArray();
    }

    // Serves one file with Range support; can ignore ranges or cut the body short.
    private sealed class FakeServer(byte[] file) : HttpMessageHandler
    {
        public bool IgnoreRange { get; set; }

        public int? StopAfter { get; set; }

        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = IgnoreRange ? 0 : request.Headers.Range?.Ranges.First().From ?? 0;
            LastRangeFrom = request.Headers.Range?.Ranges.First().From;
            var body = file[(int)from..];
            Stream stream = new MemoryStream(body);
            if (StopAfter is { } stop)
            {
                stream = new BrokenStream(body[..stop]);
            }

            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
            if (from > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, file.Length - 1, file.Length);
            }

            return Task.FromResult(response);
        }
    }

    // Returns its bytes, then fails like a reset connection.
    private sealed class BrokenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position < Length ? base.ReadAsync(buffer, ct) : throw new IOException("Connection reset");
    }
}

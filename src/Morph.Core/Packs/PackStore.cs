using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

using Morph.Core.Jobs;

namespace Morph.Core.Packs;

/// <summary>One on-demand pack (PK-1, PK-2): a ZIP on our GitHub Releases, pinned by its SHA-256.</summary>
/// <param name="Size">Download size in bytes (the ZIP).</param>
/// <param name="InstalledSize">Bytes on disk once extracted.</param>
public sealed record PackInfo(string Id, string Name, string Version, Uri Url, long Size, long InstalledSize, string Sha256, Uri LicenceUrl);

/// <summary>
/// Downloads, verifies, installs and removes packs under <see cref="Root"/> (PLAN §6). The only network code in Morph.
/// Layout: <c>&lt;id&gt;-&lt;version&gt;.part</c> while downloading (kept on cancel or a dropped connection, resumed with
/// an HTTP Range request), then <c>&lt;id&gt;\&lt;version&gt;\</c>, made by extracting to a temp folder and renaming it.
/// </summary>
public sealed class PackStore(string root, HttpMessageHandler? handler = null)
{
    /// <summary>The packs Morph knows. shortcut: empty until the office (4.2) and bg-model (4.3) ZIPs are published.</summary>
    public static IReadOnlyList<PackInfo> Known { get; } = [];

    private readonly Lazy<HttpClient> _http = new(() => handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false));

    /// <summary>%LOCALAPPDATA%\Morph\packs; packaged, Windows redirects it into the package for the App and the Worker alike.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morph", "packs");

    public string Root => root;

    /// <summary>A file of the newest installed version of a pack (e.g. office → program\soffice.exe), or null.</summary>
    public static string? Installed(string packsRoot, string id, string file)
    {
        var pack = Path.Combine(packsRoot, id);
        return Directory.Exists(pack)
            ? Directory.EnumerateDirectories(pack).Where(d => !d.EndsWith(".tmp", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .Select(d => Path.Combine(d, file)).LastOrDefault(File.Exists)
            : null;
    }

    /// <summary>The installed folder for this version of the pack, or null if it isn't installed.</summary>
    public string? Folder(PackInfo pack)
    {
        var folder = Path.Combine(root, pack.Id, pack.Version);
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>Bytes already downloaded towards this version (a resumed download starts here).</summary>
    public long Downloaded(PackInfo pack) => File.Exists(PartPath(pack)) ? new FileInfo(PartPath(pack)).Length : 0;

    /// <summary>Disk space the install still needs: the rest of the download plus the extracted files.</summary>
    public long SpaceNeeded(PackInfo pack) => Math.Max(0, pack.Size - Downloaded(pack)) + pack.InstalledSize;

    public long FreeSpace() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;

    /// <summary>Bytes the pack takes on disk, any version, including a partial download.</summary>
    public long SizeOnDisk(string id)
    {
        var folder = Path.Combine(root, id);
        var installed = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        return installed + Parts(id).Sum(p => new FileInfo(p).Length);
    }

    /// <summary>Deletes every version of the pack and any partial download (PK-3). Throws IOException if a file is in use.</summary>
    public void Remove(string id)
    {
        var folder = Path.Combine(root, id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        foreach (var part in Parts(id))
        {
            File.Delete(part);
        }
    }

    /// <summary>
    /// Downloads (or resumes), checks the SHA-256 and installs the pack, then removes its older versions.
    /// Progress is bytes downloaded. Cancelling or a dropped connection keeps the partial download for next time;
    /// a hash mismatch deletes it.
    /// </summary>
    /// <exception cref="EngineException">The download failed or the file isn't the expected one.</exception>
    public async Task<string> InstallAsync(PackInfo pack, IProgress<long> progress, CancellationToken ct)
    {
        if (Folder(pack) is { } done)
        {
            return done;
        }

        Directory.CreateDirectory(root);
        var part = PartPath(pack);
        await DownloadAsync(pack, part, progress, ct).ConfigureAwait(false);

        string hash;
        await using (var file = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        }

        if (!hash.Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new EngineException($"The {pack.Name} download was damaged. Try again.", $"SHA-256 {hash}, expected {pack.Sha256}");
        }

        var folder = Path.Combine(root, pack.Id, pack.Version);
        var temp = folder + ".tmp";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        // ExtractToDirectory refuses entries that would land outside temp (zip-slip).
        await ZipFile.ExtractToDirectoryAsync(part, temp, ct).ConfigureAwait(false);
        Directory.Move(temp, folder);
        File.Delete(part);

        foreach (var old in Directory.EnumerateDirectories(Path.Combine(root, pack.Id)).Where(d => !d.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            Directory.Delete(old, recursive: true);
        }

        foreach (var stale in Parts(pack.Id))
        {
            File.Delete(stale);
        }

        return folder;
    }

    private async Task DownloadAsync(PackInfo pack, string part, IProgress<long> progress, CancellationToken ct)
    {
        var have = Downloaded(pack);
        if (have > pack.Size)
        {
            File.Delete(part);
            have = 0;
        }

        if (have == pack.Size)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, pack.Url);
            if (have > 0)
            {
                request.Headers.Range = new(have, null);
            }

            using var response = await _http.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                have = 0; // the server sent the whole file
            }

            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            progress.Report(have);
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (have + read > pack.Size)
                {
                    throw new EngineException($"The {pack.Name} download was damaged. Try again.", $"Server sent more than {pack.Size} bytes");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                have += read;
                progress.Report(have);
            }
        }
        catch (EngineException)
        {
            File.Delete(part);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new EngineException($"The {pack.Name} download stopped. Check the connection and try again; it picks up where it left off.", ex.Message);
        }

        if (have < pack.Size)
        {
            throw new EngineException($"The {pack.Name} download stopped. Try again; it picks up where it left off.", $"Got {have} of {pack.Size} bytes");
        }
    }

    private string PartPath(PackInfo pack) => Path.Combine(root, $"{pack.Id}-{pack.Version}.part");

    private IEnumerable<string> Parts(string id) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, $"{id}-*.part") : [];
}

using System.Formats.Tar;
using System.IO.Compression;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Archives;

/// <summary>
/// ZIP, TAR and GZIP among each other (CV-2, task 2.6), with System.IO.Compression and System.Formats.Tar. Entries are
/// streamed from one archive into the other, never extracted to disk. GZIP holds one file, so a ZIP or TAR becomes
/// "name.tar.gz"; a ".gz" that holds a TAR is read as that TAR, any other ".gz" as the one file inside it.
/// Tool ids: convert.zip, convert.tar, convert.gz.
/// </summary>
// shortcut: TAR symlinks, hard links and device entries are skipped (ZIP can't hold them portably); encrypted ZIPs wait for 2.9.
public sealed class ArchiveEngine : IEngine
{
    private const long MaxExpandedBytes = 10L << 30; // PLAN §5: 10 GB expansion
    private const int MaxEntries = 100_000;
    private const int BufferInMemory = 32 << 20; // a TAR entry needs its length up front; bigger ones go through a temp file

    public string Id => "archive";

    private sealed record Entry(string Name, DateTimeOffset Modified, Stream? Data); // Data null = folder

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId switch
        {
            "convert.zip" => "zip",
            "convert.tar" => "tar",
            "convert.gz" => "gz",
            _ => throw new EngineException("This conversion isn't available yet.", $"archive: unsupported tool {job.ToolId}"),
        };

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var format = (await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id;
            var stem = Stem(input);
            var output = namer.Reserve(stem + ".archive", target == "gz" ? ".tar.gz" : "." + target).Path; // Reserve uses only the stem
            try
            {
                await ConvertAsync(input, format, stem, output, target, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new EngineException($"{Path.GetFileName(input)} is damaged or password-protected; Morph can't read it.", ex.ToString());
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    private static async Task ConvertAsync(string input, string format, string stem, string output, string target, CancellationToken ct)
    {
        await using var source = File.OpenRead(input);
        await using var destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        var budget = new Budget(Path.GetFileName(input));

        // TAR ↔ TAR.GZ is a byte copy through GZIP; no entry needs reading.
        if (format == "tar" && target == "gz")
        {
            await using var gzip = new GZipStream(destination, CompressionLevel.Optimal);
            await source.CopyToAsync(gzip, ct).ConfigureAwait(false);
            return;
        }

        if (format == "gz")
        {
            var isTar = await IsTarAsync(input, ct).ConfigureAwait(false);
            await using var inner = new GZipStream(source, CompressionMode.Decompress);
            if (isTar && target == "tar")
            {
                await budget.CopyAsync(inner, destination, ct).ConfigureAwait(false);
                return;
            }

            var entries = isTar ? TarEntriesAsync(inner, budget, ct) : Single(stem, File.GetLastWriteTimeUtc(input), inner);
            await WriteAsync(entries, destination, target, budget, ct).ConfigureAwait(false);
            return;
        }

        var all = format switch
        {
            "zip" => ZipEntriesAsync(source, budget, ct),
            "tar" => TarEntriesAsync(source, budget, ct),
            _ => throw new EngineException($"{Path.GetFileName(input)} isn't an archive Morph can convert.", $"archive: input format {format}"),
        };
        await WriteAsync(all, destination, target, budget, ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(IAsyncEnumerable<Entry> entries, Stream destination, string target, Budget budget, CancellationToken ct)
    {
        if (target == "zip")
        {
            await using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            await foreach (var entry in entries.ConfigureAwait(false))
            {
                var zipEntry = zip.CreateEntry(entry.Data is null ? entry.Name + "/" : entry.Name, CompressionLevel.Optimal);
                zipEntry.LastWriteTime = entry.Modified;
                if (entry.Data is not null)
                {
                    await using var data = await zipEntry.OpenAsync(ct).ConfigureAwait(false);
                    await budget.CopyAsync(entry.Data, data, ct).ConfigureAwait(false);
                }
            }

            return;
        }

        await using var gzip = target == "gz" ? new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true) : null;
        await using var tar = new TarWriter(gzip ?? destination, TarEntryFormat.Pax, leaveOpen: true);
        await foreach (var entry in entries.ConfigureAwait(false))
        {
            var tarEntry = new PaxTarEntry(entry.Data is null ? TarEntryType.Directory : TarEntryType.RegularFile, entry.Name)
            {
                ModificationTime = entry.Modified,
            };
            await using var buffered = entry.Data is null ? null : await BufferAsync(entry.Data, budget, ct).ConfigureAwait(false);
            if (buffered is not null)
            {
                tarEntry.DataStream = buffered; // a directory entry throws even on null
            }
            await tar.WriteEntryAsync(tarEntry, ct).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<Entry> ZipEntriesAsync(Stream source, Budget budget, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var zip = await ZipArchive.CreateAsync(source, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct).ConfigureAwait(false);
        foreach (var entry in zip.Entries)
        {
            budget.Count();
            var name = SafeName(entry.FullName, budget.Name);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                yield return new Entry(name, entry.LastWriteTime, null);
                continue;
            }

            await using var data = await entry.OpenAsync(ct).ConfigureAwait(false);
            yield return new Entry(name, entry.LastWriteTime, data);
        }
    }

    private static async IAsyncEnumerable<Entry> TarEntriesAsync(Stream source, Budget budget, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var tar = new TarReader(source, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile or TarEntryType.Directory))
            {
                continue;
            }

            budget.Count();
            yield return new Entry(SafeName(entry.Name, budget.Name), entry.ModificationTime,
                entry.EntryType == TarEntryType.Directory ? null : entry.DataStream ?? Stream.Null);
        }
    }

    private static async IAsyncEnumerable<Entry> Single(string name, DateTime modified, Stream data)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new Entry(name, modified, data);
    }

    // Decompresses the first TAR header ("ustar" at 257) in a separate pass; GZIP streams can't seek back.
    private static async Task<bool> IsTarAsync(string gz, CancellationToken ct)
    {
        await using var stream = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        var header = new byte[512];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return read >= 262 && header.AsSpan(257, 5).SequenceEqual("ustar"u8);
    }

    private static async Task<Stream> BufferAsync(Stream data, Budget budget, CancellationToken ct)
    {
        var memory = new MemoryStream();
        await budget.CopyAsync(data, memory, ct, BufferInMemory).ConfigureAwait(false);
        if (memory.Length < BufferInMemory)
        {
            memory.Position = 0;
            return memory;
        }

        // Too big to hold: spill to a temp file that deletes itself when closed.
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        memory.Position = 0;
        await memory.CopyToAsync(file, ct).ConfigureAwait(false);
        await memory.DisposeAsync().ConfigureAwait(false);
        await budget.CopyAsync(data, file, ct).ConfigureAwait(false);
        file.Position = 0;
        return file;
    }

    // Names come from an untrusted archive and go into one the user will extract: no absolute paths or "..".
    private static string SafeName(string name, string archive)
    {
        var parts = name.Replace('\\', '/').TrimEnd('/').Split('/');
        if (name.StartsWith('/') || name.StartsWith('\\') || name.Contains(':', StringComparison.Ordinal) || parts.Contains(".."))
        {
            throw new EngineException($"{archive} has files with unsafe paths (like ..\\ or C:\\); Morph won't copy them.", name);
        }

        return string.Join('/', parts.Where(p => p is not ("" or ".")));
    }

    private static string Stem(string input)
    {
        var name = Path.GetFileName(input);
        foreach (var ending in new[] { ".tar.gz", ".tgz", ".gz", ".zip", ".tar" })
        {
            if (name.EndsWith(ending, StringComparison.OrdinalIgnoreCase) && name.Length > ending.Length)
            {
                return name[..^ending.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Counts entries and expanded bytes across one archive, so a ZIP bomb stops at the limit.</summary>
    private sealed class Budget(string name)
    {
        private long _bytes;
        private int _entries;

        public string Name => name;

        public void Count()
        {
            if (++_entries > MaxEntries)
            {
                throw new EngineException($"{name} has more than {MaxEntries:N0} files; Morph converts archives up to that.");
            }
        }

        /// <summary>Copies until the end, or until <paramref name="max"/> bytes when given.</summary>
        public async Task CopyAsync(Stream from, Stream to, CancellationToken ct, long max = long.MaxValue)
        {
            var buffer = new byte[81920];
            long copied = 0;
            while (copied < max && await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - copied)), ct).ConfigureAwait(false) is var n and > 0)
            {
                _bytes += n;
                copied += n;
                if (_bytes > MaxExpandedBytes)
                {
                    throw new EngineException($"{name} expands to more than {MaxExpandedBytes >> 30} GB; Morph stops there.");
                }

                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
    }
}

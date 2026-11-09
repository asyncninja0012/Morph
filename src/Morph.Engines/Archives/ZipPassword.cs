using ICSharpCode.SharpZipLib.Zip;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Archives;

/// <summary>
/// Protect and unlock ZIPs (IT-5) with SharpZipLib (D5). Protect rewrites every file encrypted: ZipCrypto by default, the only
/// kind File Explorer can extract (weak), or AES-256 with { "strong": true } (WinZip AE-2, needs 7-Zip or WinRAR). Unlock
/// reads either and writes the files unencrypted. Entries are streamed through with the same name checks and expansion
/// limits as conversion. Outputs: photos-protected.zip, photos-unlocked.zip.
/// </summary>
public sealed partial class ArchiveEngine
{
    private static async Task PasswordAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var password = Presets.Password(job.Parameters);
        var protect = job.ToolId == "protect";
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            if ((await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id != "zip")
            {
                throw new EngineException($"{name} isn't a ZIP.");
            }

            using var source = OpenZip(input, name);
            if (source.Cast<ZipEntry>().Any(e => e.IsCrypted) == protect)
            {
                skipped = protect ? $"{name} is already password-protected." : $"{name} isn't password-protected.";
                continue;
            }

            if (!protect)
            {
                source.Password = password;
            }

            var output = namer.Reserve(input, ".zip", protect ? "-protected" : "-unlocked").Path;
            await using (var destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                var strong = (bool?)job.Parameters?["strong"] == true;
                await CopyAsync(source, destination, protect ? password : null, strong, name, ct).ConfigureAwait(false);
            }

            made++;
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    private static ZipFile OpenZip(string input, string name)
    {
        try
        {
            return new ZipFile(input);
        }
        catch (ZipException ex)
        {
            throw new EngineException($"{name} is damaged or isn't a ZIP Morph can read.", ex.ToString());
        }
    }

    private static async Task CopyAsync(ZipFile source, Stream destination, string? password, bool strong, string name, CancellationToken ct)
    {
        var budget = new Budget(name);
        await using var zip = new ZipOutputStream(destination) { Password = password, IsStreamOwner = false };
        zip.SetLevel(9);
        foreach (ZipEntry entry in source)
        {
            budget.Count();
            var safe = SafeName(entry.Name, name);
            if (entry.IsDirectory)
            {
                await zip.PutNextEntryAsync(new ZipEntry(safe + "/") { DateTime = entry.DateTime }, ct).ConfigureAwait(false);
                continue;
            }

            if (!entry.IsFile)
            {
                continue; // a volume label: nothing to copy
            }

            Stream data;
            try
            {
                data = source.GetInputStream(entry);
            }
            catch (ZipException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new EngineException($"That password doesn't open {name}.", ex.Message);
            }

            await using (data.ConfigureAwait(false))
            {
                await zip.PutNextEntryAsync(new ZipEntry(safe) { DateTime = entry.DateTime, AESKeySize = password is not null && strong ? 256 : 0 }, ct).ConfigureAwait(false);
                await budget.CopyAsync(data, zip, ct).ConfigureAwait(false);
                zip.CloseEntry();
            }
        }

        zip.Finish();
    }
}

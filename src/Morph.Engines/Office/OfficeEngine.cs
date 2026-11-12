using System.Diagnostics;

using ImageMagick;

using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Engines.Pdf;

namespace Morph.Engines.Office;

/// <summary>
/// Slides, spreadsheets, old Word files, ODF and Apple Pages (PK-1). Tools: convert.pdf; convert.png/jpg (slides: one
/// picture per slide, through the PDF and PDFium like PDF → PNG); convert.docx (Pages only). The user's Office does the
/// PDF when it has the app for the file's kind; else LibreOffice from the office pack, run headless in this Worker's Job Object.
/// </summary>
/// <param name="soffice">LibreOffice to use; null = the installed office pack.</param>
/// <param name="useOffice">Whether Microsoft Office may be used (tests turn it off to run LibreOffice).</param>
public sealed class OfficeEngine(string? soffice = null, bool useOffice = true) : IEngine
{
    public string Id => "office";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId switch
        {
            "convert.pdf" => "pdf",
            "convert.png" => "png",
            "convert.jpg" => "jpg",
            "convert.docx" => "docx",
            _ => throw new EngineException("This conversion isn't available yet.", $"office: unsupported tool {job.ToolId}"),
        };

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var format = await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false);
            if (format.Kind is not (FileKind.Document or FileKind.Spreadsheet or FileKind.Slides))
            {
                throw new EngineException($"{Path.GetFileName(input)} isn't an office document.", $"sniffed {format.Id}");
            }

            var temp = Directory.CreateTempSubdirectory("morph-office-");
            try
            {
                var done = i;
                void Report(double f) => progress.Report(new JobProgress((done + f) / job.Inputs.Count, null));
                if (target == "docx")
                {
                    var docx = await LibreOfficeAsync(input, "docx:MS Word 2007 XML", ".docx", temp.FullName, ct).ConfigureAwait(false);
                    File.Copy(docx, namer.Reserve(input, ".docx").Path, overwrite: true);
                    Report(1);
                    continue;
                }

                var pdf = Path.Combine(temp.FullName, "out.pdf");
                if (useOffice && OfficeApps.HasOfficeFor(format))
                {
                    await OfficeCom.ExportAsync(OfficeApps.ProgId(format.Kind)!, input, pdf, ct).ConfigureAwait(false);
                }
                else
                {
                    pdf = await LibreOfficeAsync(input, "pdf", ".pdf", temp.FullName, ct).ConfigureAwait(false);
                }

                Report(0.5);
                if (target == "pdf")
                {
                    File.Copy(pdf, namer.Reserve(input, ".pdf").Path, overwrite: true);
                }
                else
                {
                    var quality = job.Parameters?["quality"]?.GetValue<int>();
                    lock (Pdfium.Sync)
                    {
                        PdfEngine.Convert(pdf, target == "jpg" ? MagickFormat.Jpeg : MagickFormat.Png, quality, namer, f => Report(0.5 + f / 2), ct, nameAs: input);
                    }
                }

                Report(1);
            }
            finally
            {
                try
                {
                    temp.Delete(recursive: true);
                }
                catch (IOException)
                {
                    // An Office app or soffice can hold a file for a moment after quitting; it's in the temp folder.
                }
            }
        }
    }

    // soffice --convert-to on a copy. A new profile costs ~10 s (16 s vs 6 s a file here), so one is kept in the cache folder;
    // a lock file gives it to one Worker at a time, and a Worker that finds it busy uses a fresh one in its temp folder.
    private async Task<string> LibreOfficeAsync(string input, string convertTo, string extension, string temp, CancellationToken ct)
    {
        var exe = soffice ?? OfficeApps.LibreOffice(PackStore.DefaultRoot)
            ?? throw new EngineException("This needs Microsoft Office or Morph's Office pack.", "office pack not installed");
        var copy = Directory.CreateDirectory(Path.Combine(temp, "in")).FullName;
        var source = Path.Combine(copy, "in" + Path.GetExtension(input));
        File.Copy(input, source);
        var outDir = Directory.CreateDirectory(Path.Combine(temp, "out")).FullName;

        var cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morph", "cache", "libreoffice-profile");
        Directory.CreateDirectory(cached);
        FileStream? profileLock = null;
        try
        {
            profileLock = new FileStream(Path.Combine(cached, "morph.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Another Worker has it.
        }

        await using var _ = profileLock;
        var profileDir = profileLock is null ? Directory.CreateDirectory(Path.Combine(temp, "profile")).FullName : cached; // soffice keeps the profile in its user subfolder
        var profile = new Uri(profileDir).AbsoluteUri;

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--headless", "--norestore", "--nolockcheck", "--nologo", "--nodefault", $"-env:UserInstallation={profile}", "--convert-to", convertTo, "--outdir", outDir, source },
        };
        using var process = Process.Start(start) ?? throw new EngineException("LibreOffice didn't start.", exe);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = Path.Combine(outDir, "in" + extension);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            if (profileLock is not null && Directory.Exists(Path.Combine(cached, "user")))
            {
                Directory.Delete(Path.Combine(cached, "user"), recursive: true); // in case the kept profile is what broke; the next run makes a new one
            }

            throw new EngineException($"LibreOffice couldn't convert {Path.GetFileName(input)}. It may be damaged or password-protected.",
                $"exit {process.ExitCode}: {await stdout.ConfigureAwait(false)} {await stderr.ConfigureAwait(false)}");
        }

        return output;
    }
}

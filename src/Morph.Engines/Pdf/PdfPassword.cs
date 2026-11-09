using Morph.Core.Jobs;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Morph.Engines.Pdf;

/// <summary>
/// Protect and unlock PDFs (IT-5) with PDFsharp. Protect sets the password as both the open and the owner password, AES-256
/// (security handler V5). Unlock opens with the password and saves without encryption; a password that only opens the file
/// (not the owner's) can't edit it, so its pages are copied into a new PDF instead (bookmarks and forms are lost then).
/// Outputs: report-protected.pdf, report-unlocked.pdf.
/// </summary>
internal static class PdfPassword
{
    public static void Run(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var password = Presets.Password(job.Parameters);
        var protect = job.ToolId == "protect";
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            if (IsLocked(input) == protect)
            {
                skipped = protect ? $"{name} is already password-protected." : $"{name} isn't password-protected.";
            }
            else if (protect)
            {
                using var document = PdfCompressor.Open(input);
                document.SecuritySettings.UserPassword = password;
                document.SecuritySettings.OwnerPassword = password;
                document.SecurityHandler.SetEncryptionToV5();
                document.Save(namer.Reserve(input, ".pdf", "-protected").Path);
                made++;
            }
            else
            {
                using var document = Unlock(input, password, name);
                document.Save(namer.Reserve(input, ".pdf", "-unlocked").Path);
                made++;
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }

    // PDFsharp asks for a password only when the file has one (an owner-only PDF asks too: editing needs it).
    private static bool IsLocked(string input)
    {
        var asked = false;
        try
        {
            PdfReader.Open(input, PdfDocumentOpenMode.Modify, args =>
            {
                asked = true;
                args.Abort = true;
            })?.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !asked)
        {
            throw new EngineException($"{Path.GetFileName(input)} is damaged or isn't a PDF Morph can read.", ex.ToString());
        }
        catch when (asked)
        {
            // the aborted prompt
        }

        return asked;
    }

    private static PdfDocument Unlock(string input, string password, string name)
    {
        try
        {
            var document = PdfReader.Open(input, password, PdfDocumentOpenMode.Modify);
            document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            return document;
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            using var source = PdfReader.Open(input, password, PdfDocumentOpenMode.Import);
            var copy = new PdfDocument();
            foreach (var page in source.Pages)
            {
                copy.AddPage(page);
            }

            return copy;
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new EngineException($"That password doesn't open {name}.", ex.Message);
        }
    }
}

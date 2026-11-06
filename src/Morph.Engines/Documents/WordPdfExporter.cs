using System.Runtime.InteropServices;

using Morph.Core.Jobs;

namespace Morph.Engines.Documents;

/// <summary>DOCX to PDF through the user's own Word (COM, late-bound), for exact layout (PLAN §5). Used only when Word is installed.</summary>
internal static class WordPdfExporter
{
    private const int WdAlertsNone = 0;
    private const int MsoAutomationSecurityForceDisable = 3; // no macros, whatever the user's Trust Center says
    private const int WdExportFormatPdf = 17;
    private const int WdDoNotSaveChanges = 0;

    public static bool IsAvailable => Type.GetTypeFromProgID("Word.Application") is not null;

    // shortcut: Word starts outside our Job Object (COM launches it), so a Word stuck on a dialog it shows anyway (activation,
    // repair) outlives a cancelled job. Add a watchdog that kills that WINWORD.EXE if this shows up in the logs.
    public static async Task ExportAsync(string input, string output, CancellationToken ct)
    {
        // Word gets a copy in a temp folder, so its lock file (~$name.docx) never lands next to the original.
        var temp = Directory.CreateTempSubdirectory("morph-word-");
        try
        {
            var copy = Path.Combine(temp.FullName, Path.GetFileName(input));
            File.Copy(input, copy);
            await Task.Run(() => Export(copy, output, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                temp.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Word can hold the copy for a moment after Quit; it's in the temp folder.
            }
        }
    }

    private static void Export(string input, string output, CancellationToken ct)
    {
        dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application", throwOnError: true)!)!;
        try
        {
            word.Visible = false;
            word.DisplayAlerts = WdAlertsNone;
            word.AutomationSecurity = MsoAutomationSecurityForceDisable;
            ct.ThrowIfCancellationRequested();

            // A password is passed so a protected file fails instead of asking for one; an unprotected file ignores it.
            var document = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: "morph-no-password", Visible: false, NoEncodingDialog: true);
            try
            {
                ct.ThrowIfCancellationRequested();
                document.ExportAsFixedFormat(OutputFileName: output, ExportFormat: WdExportFormatPdf, OpenAfterExport: false);
            }
            finally
            {
                document.Close(SaveChanges: WdDoNotSaveChanges);
            }
        }
        catch (COMException ex)
        {
            throw new EngineException($"Word couldn't turn {Path.GetFileName(input)} into a PDF.", ex.ToString());
        }
        finally
        {
            try
            {
                if (word.Documents.Count == 0) // never close a Word the user is working in
                {
                    word.Quit(SaveChanges: WdDoNotSaveChanges);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(word);
            }
        }
    }
}

using System.Runtime.InteropServices;

using Morph.Core.Jobs;
using Morph.Engines.Documents;

namespace Morph.Engines.Office;

/// <summary>
/// Office files to PDF through the user's own Word, Excel or PowerPoint (COM, late-bound), for exact layout (PK-1).
/// Each gets a copy in a temp folder (lock files never land next to the original), opened read-only with macros
/// force-disabled and a dummy password, so a protected file fails instead of asking.
/// </summary>
// shortcut: like WordPdfExporter, the Office app starts outside our Job Object (COM launches it), so one stuck on a
// dialog outlives a cancelled job. Add a watchdog if that shows up in the logs.
internal static class OfficeCom
{
    private const string NoPassword = "morph-no-password";
    private const int MsoAutomationSecurityForceDisable = 3;
    private const int XlTypePdf = 0;
    private const int PpAlertsNone = 1;
    private const int PpSaveAsPdf = 32;
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    public static Task ExportAsync(string progId, string input, string output, CancellationToken ct) => progId switch
    {
        "Word.Application" => WordPdfExporter.ExportAsync(input, output, ct),
        "Excel.Application" => InTempCopyAsync(input, copy => Excel(copy, output, ct), ct),
        "PowerPoint.Application" => InTempCopyAsync(input, copy => PowerPoint(copy, output, ct), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(progId), progId, null),
    };

    private static async Task InTempCopyAsync(string input, Action<string> export, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("morph-office-");
        try
        {
            var copy = Path.Combine(temp.FullName, Path.GetFileName(input));
            File.Copy(input, copy);
            await Task.Run(() =>
            {
                try
                {
                    export(copy);
                }
                finally
                {
                    // Excel and PowerPoint stay running after Quit while any wrapper (workbook, collection) is alive; once export
                    // has returned they're all garbage, so collecting releases them and the app exits.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                temp.Delete(recursive: true);
            }
            catch (IOException)
            {
                // The app can hold the copy for a moment after Quit; it's in the temp folder.
            }
        }
    }

    private static void Excel(string input, string output, CancellationToken ct)
    {
        dynamic excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application", throwOnError: true)!)!;
        try
        {
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.AskToUpdateLinks = false;
            excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
            ct.ThrowIfCancellationRequested();
            var book = excel.Workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: NoPassword,
                IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false);
            try
            {
                ct.ThrowIfCancellationRequested();
                book.ExportAsFixedFormat(Type: XlTypePdf, Filename: output, IgnorePrintAreas: false, OpenAfterPublish: false);
            }
            finally
            {
                book.Close(SaveChanges: false);
            }
        }
        catch (COMException ex)
        {
            throw new EngineException($"Excel couldn't turn {Path.GetFileName(input)} into a PDF. An empty or password-protected workbook can't be converted.", ex.ToString());
        }
        finally
        {
            try
            {
                if (excel.Workbooks.Count == 0) // never close an Excel the user is working in
                {
                    excel.Quit();
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(excel);
            }
        }
    }

    private static void PowerPoint(string input, string output, CancellationToken ct)
    {
        dynamic powerPoint = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: true)!)!;
        try
        {
            powerPoint.DisplayAlerts = PpAlertsNone; // PowerPoint refuses Visible = false; WithWindow: false keeps it hidden
            powerPoint.AutomationSecurity = MsoAutomationSecurityForceDisable;
            ct.ThrowIfCancellationRequested();

            // "name::password::" is PowerPoint's way to pass an open password; an unprotected file ignores it.
            var presentation = powerPoint.Presentations.Open(FileName: $"{input}::{NoPassword}::", ReadOnly: MsoTrue, Untitled: MsoFalse, WithWindow: MsoFalse);
            try
            {
                ct.ThrowIfCancellationRequested();
                presentation.SaveAs(FileName: output, FileFormat: PpSaveAsPdf);
            }
            finally
            {
                presentation.Close();
            }
        }
        catch (COMException ex)
        {
            throw new EngineException($"PowerPoint couldn't turn {Path.GetFileName(input)} into a PDF. A password-protected file can't be converted.", ex.ToString());
        }
        finally
        {
            try
            {
                if (powerPoint.Presentations.Count == 0)
                {
                    powerPoint.Quit();
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(powerPoint);
            }
        }
    }
}

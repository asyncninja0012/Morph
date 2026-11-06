using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

using Microsoft.Web.WebView2.Core;

using Morph.Core.Jobs;

namespace Morph.Engines.Documents;

/// <summary>
/// Prints an HTML string to PDF with WebView2 (in Windows 11) on a hidden window and a dedicated STA thread (PLAN §5).
/// The page is served from memory; every other request is refused and scripts are off, so printing never goes online.
/// </summary>
internal static class HtmlPdfPrinter
{
    private const string PageUrl = "https://morph.invalid/document.html";

    public static Task PrintAsync(string html, string output, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await PrintOnThisThreadAsync(html, output, ct);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Morph PDF printer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // Awaits here resume on the Dispatcher thread (no ConfigureAwait(false)): WebView2 objects only work on the thread that made them.
    private static async Task PrintOnThisThreadAsync(string html, string output, CancellationToken ct)
    {
        CoreWebView2Environment environment;
        try
        {
            // The install folder is read-only in the MSIX, so the browser profile lives in local app data.
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morph", "WebView2");
            environment = await CoreWebView2Environment.CreateAsync(null, profile,
                new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-background-networking --disable-component-update" });
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new EngineException("Converting to PDF needs the Microsoft Edge WebView2 Runtime, which isn't installed.", ex.ToString());
        }

        const int WsPopup = unchecked((int)0x80000000); // no WS_VISIBLE: the window is never shown
        using var window = new HwndSource(new HwndSourceParameters("Morph PDF printer") { WindowStyle = WsPopup, Width = 1024, Height = 768 });
        var controller = await environment.CreateCoreWebView2ControllerAsync(window.Handle);
        try
        {
            var web = controller.CoreWebView2;
            web.Settings.IsScriptEnabled = false;
            web.Settings.IsReputationCheckingRequired = false; // SmartScreen would look the page up online
            var page = Encoding.UTF8.GetBytes(html);
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            web.WebResourceRequested += (_, e) => e.Response = e.Request.Uri == PageUrl
                ? environment.CreateWebResourceResponse(new MemoryStream(page), 200, "OK", "Content-Type: text/html; charset=utf-8")
                : environment.CreateWebResourceResponse(null, 403, "Blocked", "");

            var loaded = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e);
            using (ct.Register(() => loaded.TrySetCanceled(ct)))
            {
                web.Navigate(PageUrl);
                var navigation = await loaded.Task;
                if (!navigation.IsSuccess)
                {
                    throw new EngineException("Morph couldn't lay out the document for the PDF.", $"WebView2 navigation failed: {navigation.WebErrorStatus}");
                }
            }

            var settings = environment.CreatePrintSettings();
            settings.PageWidth = 8.27; // A4 in inches, like images to PDF
            settings.PageHeight = 11.69;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0.6;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds = true; // code block shading
            ct.ThrowIfCancellationRequested();
            if (!await web.PrintToPdfAsync(output, settings))
            {
                throw new EngineException("Morph couldn't make the PDF.", "WebView2 PrintToPdfAsync returned false");
            }
        }
        finally
        {
            controller.Close();
        }
    }
}

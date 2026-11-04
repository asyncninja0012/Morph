using System.IO;
using System.Windows;

using Morph.App.Jobs;
using Morph.Core.Jobs;
using Morph.Overlay.Input;
using Morph.Overlay.Wheel;

using Windows.Storage;
using Windows.Win32;

namespace Morph.App;

public sealed partial class App : Application
{
    // shortcut: static wedges for the task 0.3 spike; the capability matrix (task 1.2) supplies real options.
    private static readonly WheelItem[] ConvertItems =
    [
        new("convert.png", "PNG", "î¤›"), new("convert.jpg", "JPG", "î¤›"), new("convert.webp", "WebP", "î¤›"),
        new("convert.pdf", "PDF", "î¢¥"), new("convert.gif", "GIF", "îœ”"), new("convert.bmp", "BMP", "î¤›"),
    ];

    private static readonly WheelItem[] ToolItems =
    [
        new("compress", "Compress", "îœ¿"), new("resize", "Resize", "î€"), new("rotate", "Rotate", "îž­"),
        new("crop", "Crop", "îž¨"), new("strip-metadata", "Strip info", "î¥†"), new("ocr", "Text (OCR)", "î£’"),
    ];

    private static readonly WheelItem[] ActionItems =
    [
        new("action.copy-to", "Copy to", "î£ˆ"), new("action.move-to", "Move to", "î£ž"), new("action.copy-path", "Copy path", "îœ›"),
        new("action.terminal", "Terminal", "î–"), new("action.share", "Share", "îœ­"), new("action.float", "Float", "îœ˜"),
    ];

    private readonly DragGestureDetector _dragGestures = new();
    private DragWheelController? _wheel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // shortcut: spike logging to the launching console (tasks 0.2â€“0.3); replace with ILogger when logging lands in Phase 1.
        PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
        _dragGestures.GestureDetected += g => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {g}");

        _wheel = new DragWheelController(_dragGestures, key => key switch
        {
            ModifierKey.Shift => ConvertItems,
            ModifierKey.Ctrl => ToolItems,
            _ => ActionItems,
        });
        _wheel.Diagnostic += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");
        _wheel.ItemDropped += OnItemDropped;

        try
        {
            await _dragGestures.StartAsync();
            Console.WriteLine("Drag gesture detector running.");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Drag gesture detector failed to start: {ex.Message}");
        }
    }

    // shortcut: image conversions only, one Worker per drop, results to the console; the queue (1.3) and toasts (1.7) replace this.
    private static async void OnItemDropped(WheelItem item, DroppedFiles files)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DROP {item.Id}: {string.Join(", ", files.Paths)}");
        if (!item.Id.StartsWith("convert.", StringComparison.Ordinal) || item.Id == "convert.pdf" || files.Paths.Count == 0)
        {
            Console.WriteLine($"{item.Id} isn't wired up yet.");
            return;
        }

        try
        {
            // Output goes next to each original; virtual files (Outlook) have no real folder, so they go to Downloads.
            var results = await Task.WhenAll(files.Paths
                .GroupBy(p => files.Virtual ? UserDataPaths.GetDefault().Downloads : Path.GetDirectoryName(p)!)
                .Select(g => WorkerClient.RunAsync(new JobRequest(Guid.NewGuid(), item.Id, "imaging", [.. g], g.Key))));
            foreach (var r in results)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {r.Status} in {r.Elapsed.TotalMilliseconds:F0} ms: "
                    + (r.Status == JobStatus.Succeeded ? string.Join(", ", r.Outputs) : $"{r.UserMessage} {r.Details}"));
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Job failed to run: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wheel?.Dispose();
        _dragGestures.Dispose();
        base.OnExit(e);
    }
}

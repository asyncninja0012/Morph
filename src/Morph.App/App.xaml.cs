using System.IO;
using System.Windows;

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
        new("convert.png", "PNG", ""), new("convert.jpg", "JPG", ""), new("convert.webp", "WebP", ""),
        new("convert.mp4", "MP4", ""), new("convert.gif", "GIF", ""), new("convert.bmp", "BMP", ""),
    ];

    private static readonly WheelItem[] ToolItems =
    [
        new("compress", "Compress", ""), new("resize", "Resize", ""), new("rotate", "Rotate", ""),
        new("crop", "Crop", ""), new("strip-metadata", "Strip info", ""), new("ocr", "Text (OCR)", ""),
    ];

    private static readonly WheelItem[] ActionItems =
    [
        new("action.copy-to", "Copy to", ""), new("action.move-to", "Move to", ""), new("action.copy-path", "Copy path", ""),
        new("action.terminal", "Terminal", ""), new("action.share", "Share", ""), new("action.float", "Float", ""),
    ];

    private readonly DragGestureDetector _dragGestures = new();
    private DragWheelController? _wheel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // shortcut: spike logging to the launching console (tasks 0.2–0.3); replace with ILogger when logging lands in Phase 1.
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
        if (!item.Id.StartsWith("convert.", StringComparison.Ordinal) || files.Paths.Count == 0)
        {
            Console.WriteLine($"{item.Id} isn't wired up yet.");
            return;
        }

        try
        {
            // Output goes next to each original; virtual files (Outlook) have no real folder, so they go to Downloads.
            var results = await Task.WhenAll(files.Paths
                .GroupBy(p => files.Virtual ? UserDataPaths.GetDefault().Downloads : Path.GetDirectoryName(p)!)
                .Select(g => WorkerClient.RunAsync(Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe"),
                    new JobRequest(Guid.NewGuid(), item.Id, item.Id == "convert.mp4" ? "media" : "imaging", [.. g], g.Key),
                    new Progress<JobProgress>(p => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {item.Id} {p.Fraction:P0} {p.Stage}")),
                    CancellationToken.None)));
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

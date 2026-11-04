using System.Windows;

using Morph.Overlay.Input;
using Morph.Overlay.Wheel;

using Windows.Win32;

namespace Morph.App;

public sealed partial class App : Application
{
    // shortcut: static wedges for the task 0.3 spike; the capability matrix (task 1.2) supplies real options.
    private static readonly WheelItem[] ConvertItems =
    [
        new("convert.png", "PNG", ""), new("convert.jpg", "JPG", ""), new("convert.webp", "WebP", ""),
        new("convert.pdf", "PDF", ""), new("convert.gif", "GIF", ""), new("convert.bmp", "BMP", ""),
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
        _wheel.ItemDropped += (item, files) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DROP {item.Id}: {string.Join(", ", files)}");

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

    protected override void OnExit(ExitEventArgs e)
    {
        _wheel?.Dispose();
        _dragGestures.Dispose();
        base.OnExit(e);
    }
}

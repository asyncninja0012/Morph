using System.IO;
using System.Windows;

using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Overlay.Input;
using Morph.Overlay.Wheel;

using Windows.Storage;
using Windows.Win32;

namespace Morph.App;

public sealed partial class App : Application
{
    // Requirements count as missing until probed. shortcut: probed once at startup, so a Store extension installed
    // while Morph runs shows up after a restart; re-probe on wheel open if that bites.
    private static volatile bool _canEncodeHeic;
    private static readonly CapabilityMatrix Matrix = CapabilityMatrix.LoadDefault(id => id == SystemCodecs.HevcExtension && _canEncodeHeic);

    // shortcut: static Tools/Actions wedges from the task 0.3 spike; they move into capabilities.json as their engines land.
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

    private static readonly JobQueue Jobs = new((request, progress, ct) =>
        WorkerClient.RunAsync(Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe"), request, progress, ct));

    private readonly DragGestureDetector _dragGestures = new();
    private DragWheelController? _wheel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = Task.Run(async () => _canEncodeHeic = await SystemCodecs.CanEncodeHeicAsync());

        // Created before the overlay windows: WPF makes the first Window the MainWindow, and closing it exits the app.
        MainWindow = new MainWindow();
        MainWindow.Show();

        // shortcut: spike logging to the launching console (tasks 0.2–0.3); replace with ILogger when logging lands in Phase 1.
        PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
        _dragGestures.GestureDetected += g => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {g}");

        _wheel = new DragWheelController(_dragGestures, (key, files) => key switch
        {
            ModifierKey.Shift => ConvertItems(files),
            ModifierKey.Ctrl => ToolItems,
            _ => ActionItems,
        });
        _wheel.Diagnostic += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}");
        _wheel.ItemDropped += OnItemDropped;
        Jobs.Updated += u => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {u.Request.ToolId} {u.Request.Id:N} {u.Status} "
            + (u.Progress is { } p ? $"{p.Fraction:P0} {p.Stage}" : "")
            + (u.Result is { } r ? $"in {r.Elapsed.TotalMilliseconds:F0} ms: " + (r.Status == JobStatus.Succeeded ? string.Join(", ", r.Outputs) : $"{r.UserMessage} {r.Details}") : ""));

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

    // The wheel goes by extension: it opens on DragEnter (no I/O allowed) and virtual files only have names.
    // The Worker sniffs the real format and refuses a file that isn't what its extension says.
    // Options this PC can't run (e.g. HEIC output without HEVC) are left out rather than shown disabled.
    // shortcut: beta isn't drawn yet (no row is beta until PDF → DOCX in Phase 2).
    private static IReadOnlyList<WheelItem> ConvertItems(IReadOnlyList<string> files) =>
    [
        .. Matrix.OptionsFor([.. files.Select(f => FileFormat.FromExtension(Path.GetExtension(f)))], WheelGroup.Convert)
            .Where(o => o.DisabledReason is null)
            .Select(o => new WheelItem(o.Tool.Id, o.Tool.Label, o.Tool.Glyph)),
    ];

    // shortcut: conversions only, results to the console; toasts (1.7) replace this.
    private static void OnItemDropped(WheelItem item, DroppedFiles files)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DROP {item.Id}: {string.Join(", ", files.Paths)}");
        if (!item.Id.StartsWith("convert.", StringComparison.Ordinal) || files.Paths.Count == 0)
        {
            Console.WriteLine($"{item.Id} isn't wired up yet.");
            return;
        }

        // Files already in the target format are skipped. Output goes next to each original; virtual files
        // (Outlook) have no real folder, so they go to Downloads. One job per (engine, folder).
        foreach (var g in files.Paths
            .Select(p => (Path: p, Row: Matrix.RowFor(FileFormat.FromExtension(Path.GetExtension(p)), item.Id)))
            .Where(f => f.Row is not null)
            .GroupBy(f => (f.Row!.Engine, Folder: files.Virtual ? UserDataPaths.GetDefault().Downloads : Path.GetDirectoryName(f.Path)!)))
        {
            Jobs.Enqueue(new JobRequest(Guid.NewGuid(), item.Id, g.Key.Engine, [.. g.Select(f => f.Path)], g.Key.Folder,
                Matrix.Tool(item.Id)?.Defaults?.DeepClone().AsObject()));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wheel?.Dispose();
        _dragGestures.Dispose();
        base.OnExit(e);
    }
}

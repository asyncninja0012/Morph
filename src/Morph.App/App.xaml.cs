using System.Diagnostics;
using System.IO;
using System.Windows;

using Microsoft.Extensions.Logging;

using Morph.App.Merge;
using Morph.App.Toasts;
using Morph.App.Tray;
using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Core.Logging;
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
    private static readonly WheelItem[] PlaceholderTools =
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
    private FileLoggerProvider? _logs;
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private TrayIcon? _tray;
    private ToastHost? _toasts;
    private DragWheelController? _wheel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Started from a terminal (dev): echo the log there too, with drag and wheel detail.
        var console = PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
        _logs = new FileLoggerProvider(LogsDirectory(), console ? LogLevel.Debug : LogLevel.Information, echo: console ? Console.Out : null);
        _log = _logs.CreateLogger("Morph");
        DispatcherUnhandledException += (_, args) => _log.LogCritical(args.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => _log.LogError(args.Exception, "Unobserved task exception");
        _log.LogInformation("Morph {Version} starting on {Os}; logs in {Logs}",
            typeof(App).Assembly.GetName().Version, Environment.OSVersion.VersionString, _logs.Directory);

        _ = Task.Run(async () =>
        {
            _canEncodeHeic = await SystemCodecs.CanEncodeHeicAsync();
            _log.LogInformation("HEIC output {Availability}", _canEncodeHeic ? "available" : "hidden: HEVC Video Extensions missing");
        });

        using (var png = typeof(App).Assembly.GetManifestResourceStream("tray.png")!)
        using (var bytes = new MemoryStream())
        {
            png.CopyTo(bytes);
            _tray = new TrayIcon(bytes.ToArray(), _log);
        }

        _tray.OpenLogsClicked += () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logs.Directory}\"") { UseShellExecute = false })?.Dispose();
        _tray.QuitClicked += Shutdown;
        _toasts = new ToastHost(Jobs, Dispatcher, _log);

        _dragGestures.GestureDetected += g => _log.LogDebug("Gesture {Gesture}", g);
        _wheel = new DragWheelController(_dragGestures, (key, files) => key switch
        {
            ModifierKey.Shift => MatrixItems(files, WheelGroup.Convert),
            ModifierKey.Ctrl => [.. MatrixItems(files, WheelGroup.Tools), .. PlaceholderTools],
            _ => ActionItems,
        });
        _wheel.Diagnostic += m => _log.LogDebug("Wheel: {Message}", m);
        _wheel.ItemDropped += OnItemDropped;
        Jobs.Updated += LogResult;

        try
        {
            await _dragGestures.StartAsync();
            _log.LogInformation("Drag gesture detector running");
        }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "Drag gesture detector failed to start");
        }
    }

    // Packaged, %LOCALAPPDATA% is redirected into the package, where Explorer wouldn't find it; LocalState is the real folder.
    private static string LogsDirectory()
    {
        try
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs");
        }
        catch (InvalidOperationException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morph", "logs"); // not packaged (dev)
        }
    }

    private void LogResult(JobUpdate u)
    {
        if (u.Result is not { } r)
        {
            return;
        }

        if (r.Status == JobStatus.Failed)
        {
            _log.LogWarning("{Tool} failed after {Ms:F0} ms on {Inputs}: {Message}{NewLine}{Details}", u.Request.ToolId,
                r.Elapsed.TotalMilliseconds, string.Join(", ", u.Request.Inputs), r.UserMessage, Environment.NewLine, r.Details);
        }
        else
        {
            _log.LogInformation("{Tool} {Status} in {Ms:F0} ms: {Inputs} -> {Outputs}", u.Request.ToolId, r.Status,
                r.Elapsed.TotalMilliseconds, string.Join(", ", u.Request.Inputs), string.Join(", ", r.Outputs));
        }
    }

    // The wheel goes by extension: it opens on DragEnter (no I/O allowed) and virtual files only have names.
    // The Worker sniffs the real format and refuses a file that isn't what its extension says.
    // Options this PC can't run (e.g. HEIC output without HEVC) are left out rather than shown disabled.
    // shortcut: beta isn't drawn yet (no row is beta until PDF → DOCX in Phase 2).
    private static IReadOnlyList<WheelItem> MatrixItems(IReadOnlyList<string> files, WheelGroup wheel) =>
    [
        .. Matrix.OptionsFor([.. files.Select(f => FileFormat.FromExtension(Path.GetExtension(f)))], wheel)
            .Where(o => o.DisabledReason is null)
            .Select(o => new WheelItem(o.Tool.Id, o.Tool.Label, o.Tool.Glyph)),
    ];

    // shortcut: capabilities.json tools only; the placeholder Tools and the Actions wedges still just log.
    private void OnItemDropped(WheelItem item, DroppedFiles files)
    {
        _log.LogInformation("Drop on {Tool}: {Files}{Virtual}", item.Id, string.Join(", ", files.Paths), files.Virtual ? " (virtual)" : "");
        if (Matrix.Tool(item.Id) is null || files.Paths.Count == 0)
        {
            _log.LogInformation("{Tool} isn't wired up yet", item.Id);
            return;
        }

        // Merge asks for the page order first; nothing runs if the window is closed.
        if (item.Id == "merge" && files.Paths.Count > 1)
        {
            var order = new MergeOrderWindow(files.Paths);
            order.MergeClicked += ordered =>
            {
                _log.LogInformation("Merge order: {Files}", string.Join(", ", ordered));
                Enqueue(item, files with { Paths = ordered });
            };
            order.Show();
            order.Activate();
            return;
        }

        Enqueue(item, files);
    }

    private void Enqueue(WheelItem item, DroppedFiles files)
    {
        // Files already in the target format are skipped. Output goes next to each original; virtual files
        // (Outlook) have no real folder, so they go to Downloads. One job per (engine, folder); a toast only if the drop fails.
        // Merge makes one file, so it's one job, saved next to the first file.
        var merge = item.Id == "merge";
        var jobs = files.Paths
            .Select(p => (Path: p, Row: Matrix.RowFor(FileFormat.FromExtension(Path.GetExtension(p)), item.Id)))
            .Where(f => f.Row is not null)
            .GroupBy(f => (f.Row!.Engine, Folder: files.Virtual ? UserDataPaths.GetDefault().Downloads : Path.GetDirectoryName(merge ? files.Paths[0] : f.Path)!))
            .Select(g => new JobRequest(Guid.NewGuid(), item.Id, g.Key.Engine, [.. g.Select(f => f.Path)], g.Key.Folder,
                Matrix.Tool(item.Id)?.Defaults?.DeepClone().AsObject()))
            .Select(r => (r, Jobs.Enqueue(r)))
            .ToList();

        // Every file already in the target format: nothing to do, and nothing to say.
        if (jobs.Count > 0)
        {
            _toasts?.Track(jobs, files.Paths is [var one] ? Path.GetFileName(one) : $"{files.Paths.Count} files");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.LogInformation("Morph exiting");
        _tray?.Dispose();
        _wheel?.Dispose();
        _dragGestures.Dispose();
        _logs?.Dispose();
        base.OnExit(e);
    }
}

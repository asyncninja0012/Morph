# Morph — engineering plan

Status: **approved** (4 Nov 2026), including D1–D11. **Current phase: 2 — next task 2.8.** Phase 1 done (gate passed 5 Nov 2026: developer tested the conversions on the installed MSIX). Phase 0 done (gate passed 4 Nov 2026: developer ran the demo by hand on the installed MSIX; size over budget, see R3). Wheel direction: **A (Fluent ring)**, chosen 4 Nov 2026. Look: **Morph glass** (warm frosted glass + Morph orange, follows light/dark), chosen 5 Nov 2026. Look: **Morph glass** (warm frosted glass + Morph orange, follows light/dark), chosen 5 Nov 2026. Open questions in §12 still need answers before the tasks that depend on them (C++ toolchain: decide at 3.9).
Product name is Morph (PRD/Project map say "Pith"). PRD IDs are referenced as e.g. `IX-2`.

---

## 0. Where I propose changes to the docs

Each item needs your OK. Details are in the section noted.

| # | Docs say | Proposal | Why |
|---|---|---|---|
| D1 | `WH_MOUSE_LL` hook; "hooks active only while a mouse button is held" | **Raw input** (`RIDEV_INPUTSINK`) for mouse buttons/movement, always on; `WH_KEYBOARD_LL` installed only while a candidate drag is in progress | You can't know a button is held without *something* always watching the mouse. Raw input is a passive notification, not a hook: no global latency, less antivirus attention. (§3) |
| D2 | Wheel window opens under the cursor and catches drops | Two windows: an invisible full-monitor **drop catcher** plus the visible wheel | IX-4 "dropping outside a wedge cancels" is impossible if the area outside the wheel belongs to Explorer: the drop would really copy/move there. (§3) |
| D3 | Outlook attachments must work (acceptance criteria) | Also accept `CFSTR_FILEDESCRIPTORW` + `CFSTR_FILECONTENTS` (virtual files) and copy them to a temp folder | Outlook/Teams attachments are not `CF_HDROP`. (§3) |
| D4 | ED-3 "PDF text under each box removed" | 1.0: **re-render redacted pages as images** (PDFium at 300 DPI, boxes burned in), other pages untouched. True text-only removal later | No permissive .NET library edits content streams safely; rasterising is verifiably secure and ~60 lines. You lose selectable text on those pages only. (§7) |
| D5 | ZIP password via SharpCompress | **SharpZipLib** (MIT) for encrypted ZIP write | SharpCompress can read encrypted ZIPs but cannot write them. New dependency. (§10) |
| D6 | JB-3 allows opt-in overwrite; your rules say never modify originals | Overwrite = write temp → **original to Recycle Bin** → rename temp | Keeps the original recoverable. |
| D7 | Packaging via `Pith.Package` (.wapproj) | `package/AppxManifest.xml` + `build/pack.ps1` calling `makeappx` | .wapproj needs Visual Studio's MSBuild; makeappx works from the CLI and CI. (§8) |
| D8 | Velopack direct-download build | Drop from 1.0 | PRD lists it as open; it needs a paid OV certificate. |
| D9 | Seven `Engines.*` projects | `Morph.Engines` (folders) + `Morph.Engines.Office` + `Morph.Engines.Ai`, all hosted by a new **`Morph.Worker`** exe | Fewer projects. Pack engines stay separate so their heavy native dependencies are only loaded when the pack is installed. The worker puts every untrusted parser in a child process. (§1, §5) |
| D10 | Windows App SDK in core | Only in `Morph.Engines.Ai` | Core needs only WinRT APIs (OCR, share, Store licence), which the Windows TFM already projects. Keeps core smaller. |
| D11 | Self-contained .NET ~70 MB | Expect **~150–170 MB installed** for the WPF runtime (WPF can't be IL-trimmed) | 300 MB installed is tight; the size check runs from Phase 0. (§12 R3) |

---

## 1. Solution layout

```
Morph.slnx
Directory.Build.props        net10.0-windows10.0.22621.0, nullable, warnings-as-errors, x64, LangVersion 14
Directory.Packages.props     central package versions
src/
  Morph.Core                 (no UI)          ← referenced by everything
  Morph.Engines              → Core
  Morph.Engines.Office       → Core           (loaded only if Office or the Office pack exists)
  Morph.Engines.Ai           → Core           (loaded only if Windows AI or the model pack exists)
  Morph.Worker  (exe)        → Core, Engines, Engines.Office, Engines.Ai
  Morph.Overlay (WPF lib)    → Core
  Morph.Editors (WPF lib)    → Core
  Morph.App     (WPF exe)    → Core, Overlay, Editors   (starts Worker as a process; never references Engines)
  Morph.Shell   (C++/WinRT DLL, .vcxproj, built by msbuild)  no .NET refs; talks to App via a named pipe / command line
package/  AppxManifest.xml, Assets/, THIRD-PARTY-NOTICES.txt (capabilities.json is embedded in Morph.Core)
build/    pack.ps1, size-check.ps1, gen-fixtures.cs, fetch-binaries.ps1 (ffmpeg, pdfium with SHA-256 pins)
tests/
  Morph.Core.Tests           unit: sniffing, matrix, naming, queue, undo
  Morph.Engines.Tests        golden-file + engine contract tests (run in Worker, like production)
  Morph.UiTests              FlaUI smoke tests (Category=UI)
  fixtures/                  generated, git-ignored; expected/*.json checked in
```

**Morph.Shell language: C++/WinRT.** The menu is ~200 lines that rarely change, adapted from Microsoft's `ExplorerCommandVerb` sample. Loading the .NET runtime into `explorer.exe` is officially discouraged. NativeAOT C# COM would avoid that, but it's far less proven inside Explorer, and either way you need the MSVC toolchain. The DLL has no logic of its own: it reads a small `menu.json` that the app writes (Convert/Tools/Actions per file kind) and launches `Morph.exe --run <toolId> <paths>`. Windows 10 classic menu (best-effort): a static registry verb that opens the keyboard wheel. That's a late task in Phase 4, not before.

## 2. Core abstractions (`Morph.Core`)

```csharp
public enum FileKind { Unknown, Image, Video, Audio, Pdf, Document, Slides, Spreadsheet, Subtitle, Archive, Folder }

public sealed record FileFormat(string Id, FileKind Kind, string Extension, string MimeType); // "jpg","png","mp4",...

public interface IFileSniffer   // magic bytes first (64 KB header), extension as tie-breaker; ZIP-based → inspect [Content_Types].xml / mimetype entry
{
    ValueTask<FileFormat> DetectAsync(string path, CancellationToken ct);
}

public enum ToolKind { Instant, Editor, Popover, Action }

public sealed record ToolDescriptor(
    string Id,                       // "convert.png", "compress", "crop", "action.copy-path"
    ToolKind Kind,
    string Label, string Glyph, string Hint,   // Hint is the 400 ms tooltip; Narrator name = Label
    WheelGroup Wheel,                // Convert | Tools | Actions
    string OutputSuffix,             // "-compressed"; "" for conversions (extension changes instead)
    bool Beta,
    IReadOnlyList<ToolDescriptor> SubChoices, // outer ring (Compress → Small/Balanced/High)
    bool CountsTowardTrial);

// capabilities.json — one row per (input, output/tool, engine), CV-3
// { "in": "heic", "out": "png", "tool": "convert", "engine": "imaging", "defaults": { "quality": 90 },
//   "requires": ["heif-ext"], "beta": false, "install": "core" }
public sealed record CapabilityRow(string In, string Out, string Tool, string Engine,
    JsonObject Defaults, string[] Requires, bool Beta, string Install);

public interface ICapabilityMatrix
{
    // Tools valid for EVERY file (IX-3); unavailable ones are returned with DisabledReason (CV-6)
    IReadOnlyList<WheelOption> OptionsFor(IReadOnlyList<FileFormat> files, WheelGroup wheel);
}
public sealed record WheelOption(ToolDescriptor Tool, string? DisabledReason, Uri? FixLink);
```

**As built (1.2):** `Morph.Core.Capabilities`. `capabilities.json` holds `tools` (wheel order), `requirements` (id → reason + fix link) and `rows`; unknown fields and undeclared tool/requirement ids fail at load. Fields are added when a task needs them: `ToolDescriptor` is `(Id, Label, Glyph, Hint, Wheel)` for now (Kind, OutputSuffix, SubChoices, CountsTowardTrial come with tools/trial), `CapabilityRow` has no `Install` yet (packs); defaults live on the tool, not the row (1.4: CV-4 defaults are per output format, and one job can mix input formats), `WheelOption` carries `Beta`. A conversion treats files already in the target format as fitting (JPG + PNG still offers PNG; those files are skipped). The wheel looks formats up **by extension** (`FileFormat.FromExtension`): it opens on DragEnter where no I/O is allowed, and virtual files only have names; the Worker sniffs and refuses mismatches.

```csharp

public sealed record JobRequest(
    Guid Id, string ToolId, string Engine,
    IReadOnlyList<string> Inputs,    // merge uses this order (IT-3)
    string OutputDirectory,
    JsonObject Parameters);          // defaults ⊕ quality settings ⊕ editor output

public sealed record JobProgress(double Fraction, string? Stage);  // Fraction NaN = indeterminate
public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }
public sealed record JobResult(Guid Id, JobStatus Status, IReadOnlyList<string> Outputs,
    string? UserMessage, string? Details, TimeSpan Elapsed);

public interface IEngine        // implemented in Morph.Engines.*; executed inside Morph.Worker
{
    string Id { get; }          // "media", "imaging", "pdf", "documents", "archives", "office", "ai", "ocr"
    Task<IReadOnlyList<string>> RunAsync(JobRequest job, IOutputNamer namer,
        IProgress<JobProgress> progress, CancellationToken ct);  // writes ONLY to namer-provided temp paths
}

public interface IOutputNamer   // JB-3/JB-4
{
    PendingOutput Reserve(string input, string extension, string suffix); // photo.heic → photo.png; clash → "photo (2).png"
}
public sealed record PendingOutput(string TempPath, string FinalPath); // TempPath = same folder, ".morph-<guid>.tmp", hidden
// Commit = File.Move(temp, final, overwrite:false), re-reserving on a race; Abort = delete temp.

public interface IJobQueue      // JB-1: max(1, cores-1) parallel, ≤2 media; Channel<T>-based
{
    JobHandle Enqueue(JobRequest request);
    IObservable<JobUpdate> Updates { get; }   // drives toasts and history
}
public sealed record JobHandle(Guid Id, Task<JobResult> Completion, Action Cancel);
// As built (1.3): `JobQueue` class with an `Updated` event instead of IObservable (no Rx dependency); media slot taken before a general one.

public interface IUndoService   // JB-5: recycle a job's outputs within 10 min
{
    bool CanUndo(Guid jobId);
    Task UndoAsync(Guid jobId);   // Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., SendToRecycleBin)
}

public interface IEditor        // in Morph.Editors; never touches files
{
    string ToolId { get; }
    // Shows the editor for one file; returns parameters or null on Cancel. Step-through calls it per file.
    Task<JsonObject?> EditAsync(string inputPath, JsonObject? lastSettings, EditorContext ctx, CancellationToken ct);
}
```

App ↔ Worker protocol: `Morph.Worker.exe` receives one `JobRequest` JSON line on stdin and writes `WorkerMessage` JSON lines on stdout: `{"Progress":{"Fraction":0.42,"Stage":"encoding"}}` … `{"Result":{…}}`. Serialisation via `System.Text.Json` source-gen. **Cancellation (0.5):** the App closes the Worker's stdin; the Worker kills ffmpeg, deletes its temp files and reports `Cancelled`. If it hasn't exited after 3 s, the App closes the Job Object handle, which kills the Worker and any ffmpeg/LibreOffice children (temp files may then remain). Closing the handle alone would always leave temp files, because a killed Worker can't clean up. **1.4:** `JobRequest.Parameters` carries the tool's `defaults` from `capabilities.json` (settings and editor output merge in later); the Worker fails a job up front, with a plain message, if an input was moved/deleted or the output folder is gone. `WorkerProtocolTests` drive the real `Morph.Worker.exe` through `WorkerClient`.

## 3. The drag wheel (Phase 0 spike, highest risk)

**Threads.** `Morph.Overlay` owns one dedicated input thread with its own message loop (hidden message-only window). The UI thread is never involved in input detection.

**Detecting a candidate drag (D1).**
1. Raw input `RegisterRawInputDevices(mouse, RIDEV_INPUTSINK)` on the input thread: on left button down, record point + time; on movement, compare to `GetSystemMetrics(SM_CXDRAG/SM_CYDRAG)`.
2. Past the threshold with the button still down → **candidate drag**: install `WH_KEYBOARD_LL` on the same thread. Button up → uninstall. The hook exists only during drags.
3. The hook callback only records "Shift/Ctrl/Alt went down on its own" and "that hold ended" (key up, or any other key pressed) and posts a message. Auto-repeat is ignored. **Changed after 0.3 testing:** the wheel is press-and-hold, not tap: a press shorter than 200 ms does nothing, releasing the key closes the wheel. It returns within microseconds and never blocks (Windows silently removes slow LL hooks).
4. False-positive filter: ignore if the foreground window is ours; ignore if `GetCursorInfo` shows the cursor is hidden (games). Text selections and window moves are rejected in step 5, not guessed at here.

**Confirming it is a file drag (probe).** As soon as the key goes down (not after the 200 ms hold), the App shows the **drop catchers**, so the probe runs during the hold: one per monitor, borderless, `WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, constant alpha 1/255 via `SetLayeredWindowAttributes` (invisible, hardware-composited, still hit-testable), registered with `RegisterDragDrop` through WPF `AllowDrop`.
- If `DragEnter` arrives within ~150 ms with `CF_HDROP` (or `FileGroupDescriptorW`, D3), it's a file drag → show the wheel.
- If no `DragEnter` arrives, it was a text selection or window move → hide the catchers silently.
- **Spike question:** does OLE's `DoDragDrop` loop re-run target detection on a key-state change, or only on mouse move? If only on mouse move, nudge with a 0-distance `SendInput` mouse move after showing the catchers.
  **Answer (0.3):** no nudge needed. With zero mouse movement after the tap, `DragEnter` reached the catcher 15–45 ms after it appeared (ole32 `DoDragDrop` source, Release build). Verified with a WinForms source process; still to confirm by hand with Explorer, desktop, Edge and Outlook.

**The wheel window.** A separate WPF window, `AllowsTransparency=True` (per-pixel alpha), 264×264 DIP (240 DIP ring, design A at ~0.6× to match the reference size), `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, `ShowActivated=false`. Centred on the cursor and clamped to the monitor work area. The app is PerMonitorV2-aware (app.manifest). Position comes from `GetCursorPos` in physical pixels, converted per monitor DPI. Wedges are plain WPF `Path`s generated from the option count (6–8, plus an outer ring for sub-choices). Hit-testing during the drag uses `DragOver` coordinates, not mouse events (no mouse events arrive during an OLE drag).

**Drop handling.** Effects are only ever `Copy` or `None`, so the source never moves or links. Over a wedge: `Copy`; a drop enqueues the job. Over the catcher or the wheel centre: `None` (shows the "no" cursor; releasing cancels, IX-4). `Esc` (seen by the hook) hides both windows; while they are up the hook **swallows Esc**, because otherwise the source's own `QueryContinueDrag` would cancel the drag. After the button is released the windows stay until OLE delivers `Drop`/`DragLeave` (2 s cap), so a drop is never retargeted to the window underneath.

**Catcher implementation note (0.3):** WPF clears `WS_EX_LAYERED` in `WM_STYLECHANGING` on any non-per-pixel window (both `Window` and `HwndSource`), which turned the catcher into an opaque black screen. The catcher is a bare `HwndSource` with a hook that keeps the bit, plus `SetLayeredWindowAttributes(alpha 1)`.

**Modifier keys.** The held key passes through to the source; over our windows the effect is forced to `Copy`/`None`, so it has no effect there. Still to check: whether an Alt press pops up a menu bar in Edge/Outlook after the drop; if it does, swallow only Alt (return 1 from the hook). Native modifier drags (Shift = move, Ctrl = copy, Alt = shortcut) still work when the key is pressed **before** the drag starts, because the hook is not installed yet; pressing and holding one mid-drag now opens the wheel instead (R8).

**Reading files.** `DragEventArgs.Data.GetData(DataFormats.FileDrop)` → `string[]`. Virtual files: read `FileGroupDescriptorW` and copy each `FileContents` stream to `%TEMP%\Morph\in\<guid>\`, run the job on those, and write output to the user's Downloads folder (not next to a temp file).
**Done in 0.4:** names from the descriptor are reduced to safe leaf names (no `..`, no device names). The copy runs synchronously inside `Drop` (the source may release the data afterwards), so a large attachment stalls the UI for the copy; `IDataObjectAsyncCapability` is the upgrade if that shows in testing. Temp outputs are `.morph-<guid>.tmp` but **not hidden**: Magick and ffmpeg open with `CREATE_ALWAYS`, which fails on hidden files.

**Wheel ≤ 100 ms (IX-2):** the windows are created at startup and kept hidden. Show = `SetWindowPos(SWP_SHOWWINDOW | SWP_NOACTIVATE)` plus a 120 ms scale/opacity animation. The matrix lookup runs on `DragEnter` against a precomputed per-format table.
**Measured (0.3, Release ReadyToRun, injected input):** tap → wheel visible 37–62 ms once warm. The first drag of a session takes ~170 ms; ~130 ms of it is waiting for the first cross-process OLE `DragEnter`, which the startup warm-up (all windows rendered once off-screen) cannot cover. Revisit in 5.1 if it shows on the reference laptop.
**Measured after the hold change:** press → wheel visible ~225 ms (the 200 ms hold plus ~25 ms); the probe finishes inside the hold, so the cold first-use cost is mostly hidden.

**Fallback if the spike fails** (exit criterion: 20/20 drags from Explorer, desktop, Edge downloads, and Outlook convert end to end): ship hotkey-first (IX-5: Explorer selection via `IShellWindows` → `IFolderView2::GetSelection`, desktop via `SWC_DESKTOP`) plus the right-click menu. The wheel UI is reused unchanged; only the trigger changes.

**Keyboard path (IX-5):** `RegisterHotKey(Ctrl+Alt+Space)`. Here the wheel is activated (it needs keys): arrows, Enter, Tab, Esc.

## 4. UI direction

Same interaction model (wheel at the cursor, instant toasts, single-purpose editors). Morph's own identity: WPF Fluent theme (`ThemeMode="System"`), Segoe Fluent Icons plus our own SVG glyphs for formats, Windows accent colour or Morph's brand palette. Nothing orange, no soft grey "pillow" wedges, no Tangerine-like name or icon.

Two wheel directions in `docs/design/` for you to pick:
- **A — `wheel-a-fluent-ring.svg` "Fluent ring":** Mica/acrylic-tinted ring with flat segments separated by thin gaps. The hovered segment fills with the **Windows accent colour**; icon above label; the centre shows file count and kind. Light and dark variants. Feels native, like part of Windows.
- **B — `wheel-b-orbit.svg` "Orbit":** dark glass disc with a thin orbit track; options are round chips on the orbit; the hover state is an indigo→cyan **arc** sweeping to the chip; the centre shows a stack of file thumbnails. More distinctive, so stronger brand.

Toasts: bottom-right Fluent card (icon, file name, progress, Cancel → Show in folder / Undo). Editors: Fluent window with the content area, a tool strip, and a footer (Cancel · Undo · Done).

## 5. Engines

- **FFmpeg (`Engines/Media`).** `ffmpeg.exe` runs via `Process` with `-hide_banner -nostdin -progress pipe:1 -y <temp>`. Parse `out_time_us` against the duration from `ffprobe -show_format -of json` → `JobProgress`. Encoders probed once and cached: `h264_nvenc`/`h264_qsv`/`h264_amf`/`h264_mf` (likewise HEVC), test-encoding 1 frame of `lavfi color` each. Order: NVENC → QSV → AMF → MF hardware → MF software. VP9/AV1/Opus/MP3/FLAC/Vorbis/AAC use LGPL-compatible libs (libvpx, libsvtav1, libopus, libmp3lame, libvorbis, native AAC). **Build:** custom LGPL static build (BtbN FFmpeg-Builds scripts, `lgpl` variant) with only the needed muxers/codecs, target ≤ 40 MB. Published from our own GitHub release with a SHA-256 pin, plus an LGPL source offer. Fast cut = `-c copy` with keyframe snapping from `ffprobe -show_frames -skip_frame nokey`.
- **Imaging.** Magick.NET-Q8-x64. `ResourceLimits.Memory = min(1 GB, 25% RAM)`, `Width/Height ≤ 30000`, policy disables MVG/MSL/URL/PS/EPS/PDF coders (no Ghostscript). HEIC **encode** via WIC (`Windows.Graphics.Imaging.BitmapEncoder.HeifEncoderId`) when the HEVC extension is present (CV-6 detection = probe WIC encoder enumeration).
- **PDF.** PDFium (bblanchon/pdfium-binaries, BSD/Apache) through ~15 CsWin32-style hand P/Invokes (render page → BGRA). PdfPig: text, layout, tables for PDF→TXT/MD/DOCX/XLSX. PDFsharp 6: merge, split, rotate, reorder, encrypt (AES-256), strip metadata, images→PDF, compress (re-encode images via Magick).
- **Documents hub.** Intermediate model = a small record tree `Doc(Blocks: Heading|Paragraph(Runs)|List|Table|Image|PageBreak)` built by readers (Open XML SDK, Markdig, RtfPipe, EPUB=zip+XHTML, TXT) and written by writers (DOCX, MD, HTML, EPUB, TXT). PDF output = HTML writer → **WebView2** (`CoreWebView2.PrintToPdfAsync`) hosted in the Worker on a hidden STA window. If Word is installed, DOCX→PDF goes through Word COM `ExportAsFixedFormat` for exact fidelity.
- **Sheets/subtitles/archives.** ClosedXML (XLSX), ExcelDataReader (XLS/ODS read), own CSV/TSV/ODS writer, own SRT/VTT, SharpCompress + `System.IO.Compression` (+ SharpZipLib for encrypted ZIP, D5). Archive extraction guards against zip-slip, entry count and expansion ratio.
- **OCR.** `Windows.Media.Ocr` (available languages; PDFs rendered via PDFium first).
- **Sandboxing.** Every job runs in `Morph.Worker.exe` inside a Job Object: `KILL_ON_JOB_CLOSE`, `PROCESS_MEMORY` limit (2 GB; 4 GB for video), `ACTIVE_PROCESS` limit, `BELOW_NORMAL` priority class. Inputs are opened read-only with `FileShare.ReadWrite | FileShare.Delete`. Limits: 2,000 PDF pages, 30k px image edge, 10 GB archive expansion.

## 6. On-demand packs (PK-1..3)

`PackManifest` embedded in the app: `{id, version, url, size, sha256, licenceUrl}`, the only network endpoints in the app. Flow: consent dialog (size, disk free, licence link) → `HttpClient` download to `%LOCALAPPDATA%\Morph\packs\<id>.part`, resuming via `Range` → verify SHA-256 → extract to `packs\<id>\<version>\` → atomic rename. Settings → Packs lists size on disk and Remove (recursive delete). Packs: **office** (portable LibreOffice, MPL-2.0, ~350 MB) and **bg-model** (ONNX Runtime DirectML + a permissively licensed segmentation model, e.g. BiRefNet-lite (MIT) or U²-Net (Apache-2.0), never RMBG/non-commercial). Packs are hosted on our GitHub Releases.

## 7. Editors

Shared `EditorShell` window: header (file name, k of n), content, footer **Cancel · Undo · Done**; Enter/Esc/Ctrl+Z; `apply to all` vs `step through` chooser for multi-file drops; last settings stored per tool in `settings.json` (ED-6). It opens on the cursor's monitor, sized to the content (≤ 80% of the work area).

| Editor | Returns (JsonObject) |
|---|---|
| Crop (ED-1) | `{x,y,w,h}` in source pixels (PDF: points per page), `aspect` |
| Annotate (ED-2) | `{shapes:[{type:arrow|rect|ellipse|text|highlight|pixelate, points, color, stroke, text}]}` in source pixels; flattened by Magick/PDFsharp |
| Redact (ED-3) | `{boxes:[{page,x,y,w,h}]}`; PDF engine rasterises affected pages (D4) |
| Trim/split (ED-4) | `{segments:[{start,end}], mode: fast|exact}` |
| Page organizer (ED-5) | `{pages:[{src, rotate}], splitAfter:[...]}` |

Previews use WPF `Image` (thumbnails from the Worker), `InkCanvas` (annotate), and `MediaElement` plus ffmpeg filmstrip thumbnails (trim).

## 8. Packaging, signing, CI

- `dotnet publish src/Morph.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` (Worker published into the same folder) + `build/fetch-binaries.ps1` (ffmpeg, pdfium, hash-checked) → `makeappx pack` with `package/AppxManifest.xml` (`runFullTrust`, `desktop4:FileExplorerContextMenus` + `com:SurrogateServer` for Morph.Shell, startup task) → `artifacts/Morph_x64.msix`.
- Local install: self-signed test cert (`build/dev-cert.ps1`, one-off) + `signtool`. Store builds are uploaded unsigned and **signed by the Store**.
- GitHub Actions `windows-latest`: restore → format check → build → unit + golden tests → pack → `size-check.ps1` (fails if the MSIX is > 120 MB or the expanded size is > 300 MB) → upload artifact. Installs the Media Foundation server feature if missing so `h264_mf` works on CI. UI tests run locally only (they need an interactive desktop).

## 9. Testing

- **Fixtures** generated by `build/gen-fixtures.cs` (.NET 10 file-based app): ffmpeg `lavfi testsrc2/sine` clips, Magick-drawn images with known shapes, PDFsharp-generated PDFs with known text, Open XML DOCX, CSV, SRT, ZIP. Nothing binary is checked in except a few tiny real-world samples (HEIC from a phone, Outlook-style `.msg` drag test).
- **Golden tests** per capability row (auto-enumerated from `capabilities.json`, so a new row without a fixture fails): assert format by sniffing, dimensions, perceptual hash (dHash, Hamming ≤ 6) for images, ±1 frame duration and stream codecs (ffprobe) for media, page count + extracted text contains for PDF/docs, row/cell equality for sheets.
- **Engine contract tests:** cancellation leaves no temp file and no child process; a corrupt input → `Failed` with a user message; output never equals the input path.
- **FlaUI smoke:** a synthetic drag (`SendInput` from Explorer window to the cursor + held key) → wheel visible within 100 ms of the hold threshold → drop on PNG → output exists; each editor completes with keyboard only.

## 10. Dependencies (pin exact versions when first added)

| Package / binary | Licence | ~Size | Where | Phase |
|---|---|---|---|---|
| .NET 10 + WindowsDesktop runtime (self-contained) | MIT | 150–170 MB | App/Worker | 0 |
| CommunityToolkit.Mvvm | MIT | 0.3 MB | App, Editors | 0 |
| Microsoft.Windows.CsWin32 0.3.367 (build-time) | MIT | 0 | Overlay, App | 0 |
| Magick.NET-Q8-x64 14.17.2 | Apache-2.0 (bundles libheif/libde265 LGPL inside Magick.Native DLL — see R5) | 30 MB | Engines | 0 |
| FFmpeg LGPL custom build (ffmpeg.exe + ffprobe.exe). **Interim (0.5):** BtbN `n9.0.2-45-g1e65c6cd5d-win64-lgpl-9.0` autobuild, SHA-256-pinned in `build/fetch-binaries.ps1` | LGPL-2.1 | 40 MB target (BtbN static build is far larger, see 0.6 size check) | package | 0 |
| PDFium (bblanchon/pdfium-binaries chromium/8125, SHA-256-pinned in `build/fetch-binaries.ps1`) | BSD-3 / Apache-2.0 | 7.6 MB | Engines | 2 |
| PdfPig 0.1.16 (UglyToad.PdfPig.*) | Apache-2.0 | 2 MB | Engines | 2 |
| PDFsharp 6.2.4 (pulls System.Security.Cryptography.Pkcs, pinned to 10.0.13 over its vulnerable 8.0.1) | MIT | 2 MB | Engines | 2 |
| DocumentFormat.OpenXml 3.5.1 (pulls DocumentFormat.OpenXml.Framework, System.IO.Packaging) | MIT | 6 MB | Engines | 2 |
| Markdig 1.6.0 | BSD-2 | 0.5 MB | Engines | 2 |
| RtfPipe | MIT | 0.2 MB | Engines | 2 |
| ClosedXML (not used: 2.6 reads and writes XLSX with the Open XML SDK) | MIT | 2 MB | Engines | 2 |
| ExcelDataReader | MIT | 0.2 MB | Engines | 2 |
| SharpCompress (not used so far: 2.6 uses System.IO.Compression + System.Formats.Tar) | MIT | 1 MB | Engines | 2 |
| SharpZipLib (**new, D5**) | MIT | 0.3 MB | Engines | 2 |
| Microsoft.Web.WebView2 1.0.4258.31 (runtime is in Windows) | BSD-3 | 1 MB | Engines (Worker) | 2 |
| Microsoft.Extensions.Logging.Abstractions 10.0.0 (replaces the planned Serilog: `ILogger` plus a ~100-line file writer in Core, `FileLoggerProvider`) | MIT | 0.1 MB | Core, App | 1 |
| Microsoft.WindowsAppSDK (Ai only) | MIT | ~30 MB in pack | Engines.Ai | 4 |
| Microsoft.ML.OnnxRuntime.DirectML | MIT | 20 MB (pack) | Engines.Ai | 4 |
| LibreOffice portable (pack) | MPL-2.0 | 350 MB (pack) | pack | 4 |
| xunit.v3 4.0.2, FlaUI.UIA3 (tests only) | Apache-2.0 / MIT | – | tests | 0 |
| Microsoft.Windows.SDK.BuildTools 10.0.28000.2705 (build-time: makeappx, signtool; never shipped) | Microsoft SDK licence | 0 | `build/tools` | 0 |

Estimated core download ≈ 95–110 MB, installed ≈ 260–290 MB. Tight; tracked in CI from Phase 0.

## 11. Phases (PRD release plan: spike gate wk 2, alpha wk 9, beta wk 15, 1.0 wk 18)

Each task ≈ one session. A phase ends with: green build, tests, CHANGELOG, CLAUDE.md update, demo checklist.

### Phase 0 — Spikes (wk 1–2) · gate: IX-1, IX-2, IX-4 feasibility, MSIX installs
0.1 Repo skeleton: `Morph.slnx`, Directory.*.props, Core/Overlay/App/Worker/Engines + test projects, `.editorconfig`, CI workflow (build + test).
0.2 Raw-input drag detector + on-demand keyboard hook on the input thread; console log of candidate drags and taps.
0.3 Drop catchers + wheel window (static 6 wedges), NOACTIVATE/topmost/PMv2; DragEnter probe; answer the "key-state re-target" question.
0.4 Read CF_HDROP + FileGroupDescriptor in DragEnter; drop on "PNG" → Worker → Magick JPG→PNG with temp+rename.
0.5 ffmpeg.exe runner with `-progress` parsing, Job Object kill, encoder probe (MOV→MP4).
0.6 `pack.ps1` + dev cert → MSIX installs and runs; `size-check.ps1` reports numbers; measure idle RAM.
**Demo:** drag a JPG from Explorer, hold Shift, drop on PNG → `photo.png` appears; repeat from desktop, Edge downloads bar, Outlook attachment; Esc continues a normal drag; drop outside the wheel does nothing; Explorer never shows a move; MSIX installs non-elevated; report wheel latency and idle RAM.

### Phase 1 — Core pipeline + media/image conversions (wk 3–5) · CV-1/2/3/4/6, JB-1..4, IX-3
1.1 FileSniffer + tests. 1.2 capabilities.json + matrix (intersection, disabled reasons). 1.3 OutputNamer + JobQueue (parallel limits, cancel). 1.4 Worker protocol + engine host. 1.5 Imaging conversions + HEIC/WIC detection. 1.6 Video/audio conversions + GIF palette pipeline. 1.7 Toasts (progress, cancel, show in folder). **As built (1.7), developer's call:** toast only on failure (reason, Copy details, Show in folder for partial output); no progress/success toast, so no Cancel from the toast until the tray job history (2.10). **After 1.8, developer's call:** outputs appear under their final name at once (`OutputNamer.Reserve` creates the file empty, the engine fills it, failure/cancel deletes it), replacing JB-4's hidden temp + rename; a killed Worker can now leave a partial file under the real name. Video speed: streams already in the target's codecs are copied (`-c copy`: MOV/MKV H.264 → MP4, VP8/VP9 → WebM); VP9 uses `-deadline realtime -cpu-used 6` with tiles/threads (30 s 1080p: 61 s → 11.5 s, SSIM 0.979 vs 0.979, ~15% larger). **Video formats, developer's call (5 Nov 2026):** MKV and AVI added as outputs and WMV as an input only; WMV output declined (WMV8/WMA2 encoders: 3.5x larger, 3x slower, patent questions). AVI = MPEG-4 Part 2 + MP3. Per-stream copy when the target holds the codec. 1.8 Fixture generator + golden-test harness. **As built (1.8):** no `expected/*.json`; expectations come from the input fixture (size, dHash, duration, frame size) plus a per-target codec table in `GoldenTests`. Duration tolerance is ±0.1 s rather than ±1 frame (GIF at 15 fps and lossy-audio padding). 1.9 Logging + tray icon (added 5 Nov 2026): daily log files (`ILogger` → `FileLoggerProvider`, 7 days, packaged in LocalState\logs, else %LOCALAPPDATA%\Morph\logs) with drops, job results/failures and unhandled exceptions; tray icon (Open logs folder, Quit) via Shell_NotifyIcon/CsWin32 (WinForms NotifyIcon would add ~13 MB); the placeholder MainWindow is gone.
**Demo:** every image↔image, video→MP4/WebM/GIF/MP3, and audio↔audio row of CV-2 from the wheel; batch of 20 images with one grouped toast; cancel a video mid-encode → no file, no ffmpeg in Task Manager; HEIC output disabled with a reason when the HEVC extension is missing.
**Gate (5 Nov 2026):** passed. Conversions tested by the developer from the wheel; all 118 conversion rows (incl. MKV/AVI out, WMV in) pass golden tests. Changed by developer's call: no grouped/progress toast (failure-only, 1.7), so cancel is tested in code only (`MediaEngineTests`, `JobQueueTests`) until a UI cancel lands with 2.10; HEIC output hidden, not shown disabled, without HEVC.

### Phase 2 — Documents, PDF, archives, instant tools → **Alpha** (wk 6–9) · CV-2 rest, CV-5, IT-1/2/3/5, JB-5/6
2.1 PDFium interop + PDF→JPG/PNG/TXT. **As built (2.1):** PDF→TXT uses PDFium's text API (`FPDFText_GetText`), not PdfPig; PdfPig waits for layout work (2.5). Pages render at 150 DPI (edge ≤ 10,000 px), one image per page (`report-1.png`, `report-2.png`, …; a 1-page PDF gives `report.png`). Password-protected PDFs fail with a plain message until 2.9. 2.2 Images→PDF, merge PDFs (PDFsharp). **As built (2.2):** images → PDF is one PDF per image (Convert wheel, `photo.pdf`); merge is a Tools-wheel (Ctrl) wedge for 2+ PDFs and/or images, one PDF in drop order next to the first file (`report-merged.pdf`). A page is the image at its own DPI (else 96), shrunk to fit 842 pt (A4 long side); JPGs re-encoded at q92, others embedded as PNG. Tools from `capabilities.json` now come before the static placeholder Tools wedges; `minFiles` on a tool hides it for smaller drops. Merge first opens an order window (`Morph.App/Merge/MergeOrderWindow`, up/down per file) and runs only on Merge. 2.3 Document hub: DOCX/MD/HTML/TXT read/write. **As built (2.3):** all 12 pairs among DOCX/MD/HTML/TXT on the Convert wheel, engine `document` (`Morph.Engines/Documents`). The block tree is `Heading|Paragraph(Runs)|ListBlock|Table|CodeBlock`; images, quotes, page breaks and rules are dropped for now (add blocks when EPUB/PDF need them). HTML input uses an own forgiving tag scanner, not a parser package. Links are kept only for http(s), mailto and relative targets. Inputs over 50 MB are refused. 2.4 WebView2 HTML→PDF + Word COM path (DOCX→PDF). **As built (2.4):** DOCX, MD, HTML and TXT → PDF (Convert wheel, existing PDF wedge). The HTML writer's page is printed by WebView2 on a hidden `HwndSource` on its own STA Dispatcher thread inside `Morph.Engines` (WPF framework reference, not `UseWPF`); the page is served from memory, every other request gets 403, scripts and SmartScreen are off. A4, 0.6 in margins. With Word installed, DOCX → PDF uses Word's `ExportAsFixedFormat` (late-bound COM, read-only copy in temp, macros force-disabled) instead; Word errors fail the job, no fallback. Document jobs get a 16-process Job Object limit (WebView2's browser processes). 2.5 PDF→DOCX beta. **As built (2.5):** PDF → DOCX on the Convert wheel, row `beta` (the wedge label gets a small "beta", CV-5). `Documents/PdfReader` builds the block tree from PdfPig words, Docstrum blocks and reading order: blocks bigger than the body size are headings (biggest = H1), lines starting with a bullet/number or with a small drawn shape to their left are list items (nesting by indent), all-monospace blocks are code, bold/italic per letter from the font, links from link annotations; lone page numbers at the edges are dropped. Tables come out as paragraphs and images are dropped until 2.13 and 2.12; scanned PDFs have no text (OCR is 2.8). PDFs up to 500 MB, 2,000 pages. 2.6 CSV↔XLSX, SRT↔VTT, ZIP/TAR/GZIP. **As built (2.6):** no new packages: XLSX through the Open XML SDK already shipped (streamed `OpenXmlWriter`/`OpenXmlReader`), archives through `System.IO.Compression` + `System.Formats.Tar`, own CSV and SRT/VTT code. Engines `sheet`, `subtitle`, `archive` (`Morph.Engines/Sheets|Subtitles|Archives`). CSV → XLSX: one sheet, invariant numbers as numbers, all other text as inline strings (never formulas); delimiter `,` `;` or tab detected. XLSX → CSV: one CSV per sheet with data (`book-Sales.csv`; one sheet gives `book.csv`), formula results, date-formatted cells as ISO dates, UTF-8 with BOM, formula-looking text prefixed with `'` (CSV injection). Text input: BOM, else UTF-8, else Windows-1252. SRT ↔ VTT keeps b/i/u, drops other tags. Archives are re-packed entry by entry, never extracted to disk; GZIP output is `name.tar.gz`; a `.gz` holding one plain file becomes an archive with that file. Unsafe entry paths (`..`, absolute) fail the job; limits 100,000 entries and 10 GB expanded; TAR links and devices are skipped. 2.7 Compress/Resize presets (+ fit-under-X-MB for video/PDF). **As built (2.7):** `ToolDescriptor.Choices` (`ToolChoice`: id, label, defaults, optional `kinds`) are the presets; the wheel draws them on an outer ring around the hovered wedge (wheel window now 368 DIP, see-through outside what is drawn) and a preset drops as `tool/choice`, whose defaults go on top of the tool's. Compress `{preset}` or `{targetMb}`; resize `{scale}` or `{long, short}`; shared maths in `Morph.Engines.Presets`. Engines skip a file already small enough (`OutputNamer.Discard`) and fail only when all were. Video compress uses `VideoEncoder.Quality(cq)` (Small 32, Balanced 28, High 24) or `.Bitrate(kbps)`; PDF compress is `PdfCompressor` (PDFsharp + Magick, pixel caps 1200/2000/3000 px, not page DPI). **Changed (9 Nov 2026, developer):** Resize has no ring; dropping on it opens a Resize window (50%, 1080p, 720p, 2048 px, or a custom width × height that may enlarge, aspect kept or stretched). Dropping on the Compress wedge opens a Compress window (preset, picture size, optional target size, which now also works for images and audio); the Compress ring presets still run at once. The ring stayed open only while the pointer was over one shape: WPF raises DragLeave between shapes, so `WheelWindow.IsOver` now filters those out. 2.8 Rotate/flip/strip metadata/extract audio/mute/make GIF/OCR/merge. 2.9 Password protect/unlock popover. 2.10 Undo (Recycle Bin, 10 min) + tray job history (50), opened from the 1.9 tray icon; running jobs listed there with Cancel (the only UI cancel since toasts are failure-only). 2.11 Hotkey path (IX-5) with Explorer selection. 2.12 Images in the document hub (added 7 Nov 2026, developer: required for alpha): an `Image` block (bytes, format, size); read from DOCX (embedded parts), HTML (`<img>` data: and local relative files only, never fetched), Markdown, and PDF (PdfPig page images, decoded through Magick.NET); written to DOCX (embedded), HTML (inline data: URI), PDF (via the HTML page) and Markdown (images saved next to the output as `name-1.png`, … through `OutputNamer`). 2.13 Ruled tables from PDF (added 7 Nov 2026, developer: required for alpha): build the cell grid from the horizontal/vertical lines PdfPig reports, put words into cells, emit `Table`; borderless tables stay paragraphs (guessing columns misreads ordinary multi-column text).
**Demo:** full CV-2 list; 8+ instant tools; Undo restores state; hotkey wheel on an Explorer selection; developer uses it daily for a week.

### Phase 3 — Editors, actions, shell menu (wk 10–13) · ED-1..6, FA-1..3, IX-6
3.1 EditorShell. 3.2 Crop (image/video/PDF). 3.3 Annotate. 3.4 Redact (image + PDF raster). 3.5 Trim/split (fast/exact). 3.6 Page organizer. 3.7 Actions wheel: Copy/Move to, Copy path, terminal, Share. 3.8 Float shelf. 3.9 Morph.Shell C++/WinRT menu + MSIX registration.
**Demo:** each editor completes with keyboard only; multi-file step-through; Float survives restart; right-click → Morph › Convert › PNG works.

### Phase 4 — Packs, settings, onboarding, trial → **Beta** (wk 14–15) · PK-1..3, IT-4, IX-7, §6.8–6.10
4.1 Pack downloader (consent, resume, SHA-256, remove). 4.2 Office COM + LibreOffice engine. 4.3 Background removal (Windows AI / ONNX) + Keep/Discard preview. 4.4 Settings window (5 tabs, key remap, wheel pin/reorder, learned ordering). 4.5 Onboarding demo. 4.6 Store licence + 25-action trial. 4.7 Win10 classic menu (best-effort).
**Demo:** install/remove both packs offline-safe; remove background; remap Shift→Ctrl; onboarding < 60 s; trial counts tools but not actions; private Store flight.

### Phase 5 — Hardening → **1.0** (wk 16–18) · §7 NFRs, §9 acceptance
5.1 Perf pass vs §7 targets on reference laptop. 5.2 Narrator labels, high contrast, text scaling. 5.3 Long paths, non-ASCII, read-only folders, OneDrive placeholders. 5.4 Crash log (opt-in upload), THIRD-PARTY-NOTICES, LGPL source offer. 5.5 Store listing + certification.
**Demo:** run the PRD §9 acceptance checklist end to end.

## 12. Risks and open questions — please answer before Phase 0

**Questions**
1. Approve deviations D1–D11 (especially D4 redaction-by-rasterising and D5 SharpZipLib).
2. Name: "Morph" is used by several software products; do you want a trademark check before the Store name reservation? (Reserve the Store name early; it's free.)
3. Are the "Add BG" (screenshot framing) and "Edit" (adjustments) tools seen in `docs/reference/` in scope for 1.0? They aren't in the PRD.
4. Toolchain: OK to require Visual Studio 2026 Build Tools with the C++ workload (for Morph.Shell)? **Windows SDK answered (0.6):** no SDK install; makeappx/signtool come from the `Microsoft.Windows.SDK.BuildTools` NuGet package. C++ Build Tools: decide at 3.9.
5. GitHub repo public or private? (CI minutes; FFmpeg/pack binaries are hosted on Releases.)
6. ~~Wheel visual direction A or B~~ → **A**.
7. Reference laptop for §7 targets (CPU/GPU), so the perf numbers mean something.

**Risks**
- R1 Drag wheel unreliable in some sources → fallback in §3, decided at the week-2 gate.
- R2 Store policy on low-level keyboard hooks → hook only during drags, explain it in certification notes; fallback: hotkey + menu only.
- R3 Install size: WPF self-contained runtime can't be trimmed; FFmpeg must be custom-built small. **Measured (0.6, Release R2R, BtbN static FFmpeg):** MSIX 196.5 MB download, 476 MB installed (budgets 120/300). Biggest: ffmpeg.exe 126 MB + ffprobe.exe 126 MB (each statically links every codec), `Microsoft.Windows.SDK.NET.dll` 55 MB (the full WinRT projection the `-windows10.0.22621` TFM pulls in), Magick.Native 24 MB. Levers: custom FFmpeg build with shared libs and only our codecs (~40 MB for both), trimming the WinRT projection to the types we use, then re-measure.
- R4 Idle RAM ≤ 80 MB with WPF: windows created lazily except the wheel; Worker process exits after each job. **Measured (0.6, packaged, 30 s after launch):** 53.6 MB private, 109 MB working set. PRD §7 doesn't name the metric; private bytes meet it, working set doesn't (it includes shared WPF/DirectX pages). The placeholder MainWindow was removed in 1.9 (tray icon instead).
- R5 Magick.NET's native DLL statically includes LGPL libheif/libde265 (HEIC decode). **Decided 4 Nov 2026 (1.5):** read HEIC/AVIF with Magick; Magick.Native-Q8-x64.dll is the separate, replaceable LGPL unit, like ffmpeg.exe (LGPL text + source links in THIRD-PARTY-NOTICES). HEIC output goes through WIC + HEVC Video Extensions, probed at App startup by test-encoding 16x16 (`SystemCodecs.CanEncodeHeicAsync`); missing → the HEIC wedge is **hidden** (developer's call, 4 Nov 2026: no greyed wedge, no Store link; overrides CV-6's "disabled with a link" for the wheel). The matrix still reports the reason, for Settings later.
- R6 CI runners may lack Media Foundation H.264 → install the feature in CI; golden tests skip HW encoders.
- R7 PDF→DOCX/XLSX quality is heuristic → labelled beta (CV-5).
- R8 Press-and-hold mid-drag overrides Explorer's native mid-drag Shift/Ctrl/Alt (move/copy/shortcut). Pressing the key before starting the drag still works natively. Key remapping (IX-1) is the escape hatch.

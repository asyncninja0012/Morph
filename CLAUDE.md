# Morph

Offline Windows 11 utility: drag files, tap a key, a radial wheel opens at the cursor; drop on a wedge to convert, run a tool, or open a small single-purpose editor. Output lands next to the original. No accounts, no telemetry, network only for optional pack downloads. Shipped as MSIX on the Microsoft Store. Solo developer reviews; Claude writes most code.
**Name:** the product is **Morph**. `docs/PRD.md` and `docs/PROJECT_MAP.md` still say "Pith"; it is the same product.

## Docs (read the one you need, don't copy into here)
- `docs/PRD.md` requirements with IDs (IX-, CV-, IT-, ED-, FA-, JB-, PK-). Source of truth.
- `docs/PROJECT_MAP.md` stack, engine map, UI/UX, risks.
- `docs/PLAN.md` solution layout, abstractions, phases, current task list. Check the current phase before starting work.
- `docs/TOOLING.md` installed Claude plugins/MCPs. `docs/design/` wheel mockups. `docs/reference/` Tangerine screenshots (interaction only, never copy the look).
- Windows APIs: query the `microsoft_docs_mcp` MCP before guessing any Win32/WinRT/WPF/MSIX API.

## Commands (repo root, PowerShell)
- Native binaries (once after clone, and when a pin changes): `./build/fetch-binaries.ps1` → `artifacts/binaries/` (LGPL ffmpeg/ffprobe, pdfium.dll; media tests skip without ffmpeg)
- Build: `dotnet build Morph.slnx -c Debug` (warnings are errors)
- Unit + golden tests (golden needs `tests/fixtures/`, see next line): `dotnet test Morph.slnx --filter-not-trait "Category=UI"` (xunit.v3 on Microsoft.Testing.Platform, set in `global.json`; VSTest `--filter` syntax does not work)
- UI smoke tests (needs an unlocked interactive desktop): `dotnet test --project tests/Morph.UiTests` (project not created yet; first end-to-end smoke test lands once a drop produces output)
- Generate fixtures (once after clone, after fetch-binaries, and when the generator changes): `dotnet run build/gen-fixtures.cs` → `tests/fixtures/` (git-ignored)
- Run: `dotnet run --project src/Morph.App`
- Dev signing cert (once): `./build/dev-cert.ps1`, then run the printed `Import-Certificate` line in an elevated PowerShell
- Package: `./build/pack.ps1 -Configuration Release` → `artifacts/Morph_x64.msix` (signed with the dev cert; `-NoSign` for CI/Store). Install: `Add-AppxPackage artifacts/Morph_x64.msix`
- From Git Bash, run any `.ps1` as `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/<name>.ps1`
- Size budget: `./build/size-check.ps1` (fails over 120 MB download / 300 MB installed)
- Format check: `dotnet format Morph.slnx --verify-no-changes`

## Solution map
| Project | Owns |
|---|---|
| `src/Morph.App` | WPF exe: tray, settings, onboarding, toasts, job history, single-instance + IPC pipe; composition root |
| `src/Morph.Overlay` | Raw-input drag detection, keyboard hook, drop-catcher + wheel windows, hotkey, Explorer selection |
| `src/Morph.Core` | FileKind sniffing, capability matrix (`capabilities.json`), ToolDescriptor, jobs/queue, output naming, undo, settings. No WPF references |
| `src/Morph.Worker` | Console exe that runs one job per process; hosts engines; JSON lines over stdio |
| `src/Morph.Engines` | Media (ffmpeg.exe), Imaging (Magick.NET, WIC), Pdf (PDFium, PdfPig, PDFsharp), Documents, Archives, Ocr |
| `src/Morph.Engines.Office` | Office COM / LibreOffice pack |
| `src/Morph.Engines.Ai` | Background removal (Windows AI / ONNX pack) |
| `src/Morph.Editors` | Crop, annotate, redact, trim/split, page organizer. Return parameters only |
| `src/Morph.Shell` | C++/WinRT IExplorerCommand DLL (Windows 11 right-click menu). Has its own CLAUDE.md |
| `package/` | AppxManifest, assets, notices. `build/` scripts. `tests/` xUnit, golden, FlaUI |

## C# conventions
- C# 14, `net10.0-windows10.0.22621.0`, `<Nullable>enable`, `<TreatWarningsAsErrors>true`, file-scoped namespaces, `sealed` by default, records for data.
- Namespaces = project + folder. PascalCase types/members, `_camelCase` private fields, `Async` suffix on async methods.
- MVVM via CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). No logic in code-behind except pure view concerns (focus, animation, hit-testing).
- Win32 via CsWin32 only (`NativeMethods.txt` per project). No hand-written `DllImport` unless CsWin32 cannot generate it; then comment why.
- Async all the way; every long operation takes `CancellationToken` and `IProgress<JobProgress>`. No `.Result`/`.Wait()`. `ConfigureAwait(false)` in Core/Engines/Worker, not in UI projects.
- Errors: engines throw `EngineException(userMessage, details)`; the queue turns it into a failed `JobResult` with plain-language text. Never swallow exceptions silently; log via `ILogger` (`Morph.Core.Logging.FileLoggerProvider`; logs: tray → Open logs folder).
- Use built-in .NET/Windows APIs before writing code or adding packages (`System.Text.Json` source-gen, `System.IO.Compression`, `FileSystem.DeleteFile(... SendToRecycleBin)`, WinForms `NotifyIcon`, `Windows.Media.Ocr`).
- Look: Morph glass (warm frosted glass, Morph orange, light/dark with Windows). Overlay surfaces use `Morph.Overlay.Theme.MorphGlass`; App windows call `MorphTheme.Apply` and use the `MorphSurfaceBrush`/`MorphCardBrush` resources. Inspired by Tangerine's feel, never its exact colours or layout.
- Look: Morph glass (warm frosted glass, Morph orange, light/dark with Windows). Overlay surfaces use `Morph.Overlay.Theme.MorphGlass`; App windows call `MorphTheme.Apply` and use the `MorphSurfaceBrush`/`MorphCardBrush` resources. Inspired by Tangerine's feel, never its exact colours or layout.
- Don't reformat code you aren't changing. Keep diffs small.

## Engines and licences
- Allowed: MIT, BSD, Apache-2.0, MS-PL, MPL-2.0, LGPL (LGPL only as a separate replaceable binary, e.g. ffmpeg.exe). **No GPL or AGPL**, no "non-commercial" model weights.
- FFmpeg: LGPL build only; H.264/HEVC via Media Foundation or hardware encoders (never libx264/libx265).
- Every new package or binary: ask first, then add a row to the dependency table in `docs/PLAN.md` and to `package/THIRD-PARTY-NOTICES.txt`.
- Untrusted input: parsing and encoding runs in `Morph.Worker` (child process in a Job Object with memory limit, kill-on-close), never in the App process. Editors show files through the Worker's `preview` job (`Morph.Core.Jobs.PreviewFile`, raw pixels), never by decoding them. A player (Trim, Speed, Save frames) plays only the Worker's `playback` copy (H.264/AAC written by Morph's own encoder), never the dropped file.

## Never
- Modify, move or delete an original file (except the explicit Move to… action, and opt-in overwrite, which recycles the original).
- Write output anywhere except the name `OutputNamer.Reserve` gave you in the target folder (created empty at once, so the file shows under its real name while it fills in; a failed or cancelled job deletes it).
- Block the UI thread or do I/O on it; never block inside a low-level hook callback.
- Make network calls outside the pack downloader. No telemetry.
- Add a dependency, change a licence-relevant build flag, or swap a library without asking.
- Steal focus from the drag source (the overlay is `WS_EX_NOACTIVATE`).
- Copy Tangerine's visuals, name, icons or colours.
- Run elevated or require admin.

## Before saying "done"
1. `dotnet build Morph.slnx` is clean (zero warnings).
2. Relevant tests pass; new behaviour has a test (golden test for any new capability row).
3. For UI changes: run the app (or the FlaUI MCP) and check the behaviour; say so explicitly if you could not.
4. Report briefly: what changed, how it was verified, what's next. Update `CHANGELOG.md`; update this file if a convention changed.
5. Commit after every task: a short, plain, human one-line message (e.g. `Add drop catchers and the first wheel`). No `Co-Authored-By` or any Claude attribution.

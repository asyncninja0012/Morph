# Changelog

## Unreleased

### Added
- Repo skeleton (task 0.1): `Morph.slnx`, `Directory.Build.props` (net10.0-windows10.0.22621.0, C# 14, nullable, warnings as errors, x64), central package versions, `global.json` (SDK 10.0.403, Microsoft.Testing.Platform runner), `.editorconfig`.
- Projects: `Morph.Core`, `Morph.Engines`, `Morph.Worker`, `Morph.Overlay`, `Morph.App` (placeholder window), `Morph.Core.Tests`, `Morph.Engines.Tests` (xunit.v3 dependency-rule tests).
- GitHub Actions CI: restore, format check, build, unit tests.
- Drag gesture detector (task 0.2): `Morph.Overlay.Input.DragGestureDetector` on a dedicated STA input thread with a message-only window; raw mouse input (`RIDEV_INPUTSINK`, honours swapped buttons) detects drags past `SM_CXDRAG/SM_CYDRAG`; `WH_KEYBOARD_LL` is installed only during a candidate drag and reports Shift/Ctrl/Alt taps (≤ 400 ms, alone) and Esc. Drags are ignored when Morph is foreground or the cursor is hidden. `Morph.exe` prints gestures to the launching console (spike logging). CsWin32 0.3.367 added (build-time).
- `Morph.Overlay.Tests`: unit tests for the drag-threshold and modifier-tap state machines.
- Drop catchers and wheel (task 0.3): per-monitor invisible drop catchers (layered, alpha 1/255, no-activate, topmost) and the design A "Fluent ring" wheel (6 static wedges per wheel: Shift = Convert, Ctrl = Tools, Alt = Actions; light/dark; Windows accent on hover; 120 ms open animation). App is PerMonitorV2. A modifier tap shows the catchers; a file `DragEnter` within 250 ms opens the wheel at the cursor. Dropping on a wedge reports the item and paths (job wiring is task 0.4). Dropping on the centre or outside cancels, Esc closes the wheel and the drag continues. Windows are warmed up off-screen at startup.
- `WheelGeometryTests` for wedge hit-testing.
- First conversion end to end (task 0.4): dropping files on a Convert wedge (PNG, JPG, WebP, GIF, BMP) runs `Morph.Worker.exe`, which converts with Magick.NET and writes the output next to the original via a temp file renamed on success; a name clash becomes `photo (2).png`, nothing is overwritten. Virtual files (Outlook/Teams attachments, `FileGroupDescriptorW` + `FileContents`) are copied to `%TEMP%\Morph\in\<guid>\` and their output goes to Downloads.
- `Morph.Core.Jobs`: `JobRequest`, `JobResult`, `IEngine`, `EngineException`, `OutputNamer`, and a source-generated JSON context for the App ↔ Worker wire format (request on stdin, one result line on stdout).
- Input format is taken from magic bytes, never from ImageMagick's own detection, so script-like coders are unreachable. Magick memory capped at min(1 GB, 25% RAM), 30k px edge.
- Magick.NET-Q8-x64 14.17.2 (Apache-2.0); `package/THIRD-PARTY-NOTICES.txt`.
- Video conversion (task 0.5): MOV/MP4 → MP4 (H.264 + AAC) via the bundled ffmpeg.exe, from a new MP4 wedge on the Convert wheel. `-progress` output is parsed against the ffprobe duration and reported as job progress. The H.264 encoder is probed once by test-encoding one frame (NVENC → QSV → AMF → Media Foundation hardware → Media Foundation software; never libx264) and cached in `%LOCALAPPDATA%\Morph\cache\encoders.json`. The input demuxer is forced and only the `file` protocol allowed, so playlists can't open other files or URLs.
- Worker sandbox: each Worker runs in a Job Object (kill on close, 2 GB per process or 4 GB for video, at most 4 processes, below-normal priority, no crash dialog). Cancel closes the Worker's stdin; it stops ffmpeg and deletes temp files, and the Job Object is closed if it hasn't exited after 3 s. The Worker now reports progress lines before its result. `WorkerClient` moved to `Morph.Core`.
- `build/fetch-binaries.ps1`: downloads the LGPL FFmpeg build with a SHA-256 pin into `artifacts/binaries/`; CI runs it and tries to install Media Foundation.
- Packaging (task 0.6): `build/pack.ps1` publishes the App and Worker self-contained (ReadyToRun) into one layout with ffmpeg, assets and notices, packs `artifacts/Morph_x64.msix` with makeappx and signs it with the dev cert (`-NoSign` for CI and Store upload). makeappx/signtool come from the `Microsoft.Windows.SDK.BuildTools` NuGet package (build-time only). `build/dev-cert.ps1` creates the `CN=Morph Dev` cert in the user store and prints the one elevated command that trusts it. `package/AppxManifest.xml` (full trust, Windows 10 2004+) and placeholder logos.
- `build/size-check.ps1`: reports download/installed size and the largest files; fails over 120/300 MB. First numbers: 196.5 MB / 476 MB (over budget; FFmpeg and the WinRT projection dominate, see PLAN R3). CI packs unsigned, runs the size check report-only, and uploads the MSIX.
- First idle RAM measurement of the installed package: 53.6 MB private, 109 MB working set (PLAN R4).
- Tests: `MediaEngineTests` (progress parsing; MOV→MP4 codecs and duration; cancel kills ffmpeg and leaves no temp; non-video rejected; ffmpeg tests skip until binaries are fetched), `WorkerJobObjectTests`.
- Tests: `OutputNamerTests`, `ImagingEngineTests` (JPG→PNG, original untouched, no temp left; non-image rejected), `DragFilesTests` (descriptor parsing, unsafe names).

### Fixed
- Wheel glyphs in `App.xaml.cs` were garbled by an encoding round-trip in task 0.4; restored.

### Changed
- The wheel opens on press-and-hold instead of a tap: hold Shift/Ctrl/Alt for 200 ms during a drag. A quick tap does nothing; releasing the key closes the wheel and the drag continues. The probe starts on key down, so it finishes inside the hold.
- The wheel is smaller, matching the reference size: 240 DIP ring (was 400), 264 DIP window, smaller text and icons.

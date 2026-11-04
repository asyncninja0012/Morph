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

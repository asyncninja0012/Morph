# Morph

Convert, edit and act on files the moment you drag them, on Windows 11, entirely offline.

Start dragging any files in File Explorer, on the desktop or from another app, hold a key, and a wheel opens around the
pointer. Drop on a wedge:

- **Shift: Convert.** Images (JPG, PNG, WebP, HEIC, AVIF, TIFF, BMP), video (MP4, MOV, MKV, WebM, AVI, GIF), audio (MP3, M4A,
  WAV, FLAC, OGG, Opus), PDF, Word, Markdown, HTML, text, spreadsheets, slides, subtitles and archives.
- **Ctrl: Tools.** Compress, resize, rotate, crop, remove background, annotate, redact, trim and split, change speed, save
  frames, PDF pages, strip info, text from pictures (OCR), extract audio, mute, make GIF, merge, password-protect and unlock.
- **Alt: Actions.** Copy or move to a recent folder, copy the path, open a terminal, share, or park files on the Float shelf.

The result is saved next to the original (`photo.png` beside `photo.jpg`); the original is never changed. No mouse? Select
files in File Explorer and press **Alt+Shift+Space**. Every job can be undone for 10 minutes from the tray icon.

Morph is free and open source (MIT). It has no account, no telemetry and no ads, and it uses the network only to download an
optional pack you ask for, or to check for a new version when you click "Check for updates".

## Install

1. Download `Morph-<version>-setup.exe` from [Releases](https://github.com/asyncninja0012/Morph/releases).
2. Run it. It installs for your Windows account only, no admin needed.
3. Morph isn't code-signed yet, so Windows SmartScreen says "Windows protected your PC": click **More info → Run anyway**. You
   can check the download against the `.sha256` file on the release: `Get-FileHash Morph-<version>-setup.exe`.

A short tour opens the first time. Settings are in the tray icon's menu (keys, wheel order, quality, where results go).
Uninstall from Settings → Apps → Installed apps.

### Optional packs

Two tools need a larger download, offered the first time you use them:

| Pack | For | Size |
|---|---|---|
| Office pack (LibreOffice) | Slides, spreadsheets, DOC, OpenDocument and Pages when Microsoft Office isn't installed (with Office, Morph uses it instead) | ~350 MB |
| Background pack | Remove background (BiRefNet-lite model, ONNX Runtime) | ~210 MB |

They can be removed in Settings → Packs.

## Build from source

Needs Windows 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell.

```powershell
git clone https://github.com/asyncninja0012/Morph.git
cd Morph
./build/fetch-binaries.ps1                 # FFmpeg (LGPL) and PDFium, checked by SHA-256
dotnet build Morph.slnx -c Debug
dotnet run --project src/Morph.App
```

Tests: `dotnet run build/gen-fixtures.cs` once, then `dotnet test Morph.slnx --filter-not-trait "Category=UI"`.
Installer: `./build/installer.ps1` → `artifacts/Morph-<version>-setup.exe`. Packs: `./build/make-office-pack.ps1 -Version <LibreOffice version>`
and `./build/make-bg-pack.ps1`.

The plan, requirements and conventions are in [`docs/`](docs/) and [`CLAUDE.md`](CLAUDE.md).

## Licence

MIT, see [LICENSE](LICENSE). Morph uses FFmpeg (LGPL, as separate replaceable programs), ImageMagick through Magick.NET, PDFium,
PdfPig, PDFsharp, the Open XML SDK and others; see [THIRD-PARTY-NOTICES](package/THIRD-PARTY-NOTICES.txt).

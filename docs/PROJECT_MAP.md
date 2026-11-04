Windows file-tools app — project map, PRD & build plan
4 Nov 2026 · @pushkal
What we're building
A Windows desktop utility that converts, edits and acts on files the moment you drag them, entirely offline. You start dragging a file in Explorer or on the desktop, hold a key, and a radial wheel opens at the cursor. Drop on a wedge and the job runs. Tools that need no input run instantly and drop a new file next to the original. Tools that need your hands (crop, annotate, trim) open a small single-purpose editor inside the app, never Windows Photos.
Working name in this doc: Pith (placeholder, rename freely).
Reference product. Tangerine for Mac by Leap Studio, launched September 2026: 275 conversion options, 26 file tools, 6 file actions, everything local, $29.99 one-time after a 20-action trial. We match its scope on Windows except where the OS makes it impossible.
In scope for Windows
• Conversions across images, video, audio, PDF, documents, slides, spreadsheets, EPUB, subtitles and archives
• File tools: compress, resize, crop, rotate, annotate, redact, remove background, strip metadata, trim, split, merge, extract audio, make GIF, PDF page tools
• File actions: Float (a pinned shelf), Copy to, Move to, Copy path, Open terminal here, Share (Windows share sheet / Nearby Share in place of AirDrop)
• Explorer right-click menu as a second entry point, plus a keyboard-only path
Out of scope
• AirDrop itself (no Windows equivalent; replaced by the Windows share sheet)
• Writing Apple .pages files (proprietary; we read them only if the optional office pack is installed)
• Cloud anything: no uploads, no accounts, no telemetry by default
Dependency stance. Pragmatic, not purist. Use a mature open-source engine wherever writing our own would be painful (video, PDF rendering, image codecs, archives). Write our own code only where it is small and simple (subtitles, CSV, the document hub, glue). Keep the core install light by bundling only compact engines and downloading the two heavy ones (the office engine and the background-removal model) on first use.
How it works end to end
Every tool, instant or interactive, ends as the same thing: a set of parameters handed to one engine job. Editors are just a way of collecting parameters, so the processing pipeline is shared by all 300-odd operations.
The drag hook watches for a left-button drag, holding a key opens the wheel, and the drop hands file paths to the registry. Adding a conversion means adding one row to a capability table, not new UI.
Tech stack and why
C# on .NET 10 with WPF is the pick. It is the most reliable way on Windows to draw a transparent, always-on-top wheel that accepts drops from Explorer, and it calls Win32 and Windows Runtime APIs with little friction.
Layer
Choice
Why
Language / runtime
C# 14, .NET 10 LTS (supported to Nov 2028)
One language for UI, hooks and job orchestration; LTS covers the app's first two years
UI framework
WPF with the built-in Fluent theme; MVVM via CommunityToolkit.Mvvm
Transparent layered windows, OLE drag-and-drop and a working XAML designer. WinUI 3 still has no designer and weaker transparent-window support
Win32 interop
CsWin32 (source-generated P/Invoke)
Low-level mouse and keyboard hooks, window styles (WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW), cursor position, Explorer selection
Windows APIs
Windows App SDK 2.x NuGet (unpackaged-capable)
Windows AI imaging APIs, share sheet, notifications
Job engine
In-process queue + engines run as child processes where possible
A crashing codec never takes the app down; progress parsed from stdout
Image engine
Magick.NET Q8 (Apache-2.0) + Windows Imaging Component for HEIC encode
One library covers JPG, PNG, WebP, TIFF, AVIF, BMP, HEIC decode, SVG input
Video / audio
FFmpeg, LGPL build, run as ffmpeg.exe
The only practical choice. H.264/HEVC/AAC encoding goes through Windows Media Foundation and GPU encoders, so we ship no GPL x264 and no codec patent burden
PDF
PDFium (render), PdfPig (text/layout), PDFsharp 6 (merge, split, encrypt, rebuild)
All permissive licences, small, no AGPL (no MuPDF, no Ghostscript)
Documents
Own "document hub" + Open XML SDK, Markdig, RtfPipe, own EPUB writer; HTML → PDF via WebView2 print
Avoids bundling Pandoc (~200 MB, GPL). WebView2 is already on every Windows 11 PC
Office fidelity
Microsoft Office via COM when installed; else an on-demand "Office pack" (portable LibreOffice, ~350 MB)
PPTX/XLSX → PDF needs a real layout engine; most users never download it
Archives
SharpCompress (MIT) + System.IO.Compression
ZIP, TAR, GZIP read/write; RAR read
OCR
Windows.Media.Ocr (built in)
Free, offline, decent for scanned PDFs
Background removal
Windows AI Image Foreground Extractor on Copilot+ PCs; ONNX model via ONNX Runtime + DirectML elsewhere
The Windows AI imaging APIs require a Copilot+ PC with an NPU, so most PCs need the fallback
Editors
WPF canvas + InkCanvas (annotate), MediaElement + FFmpeg thumbnails (trim)
No extra UI library needed
Settings / logs
JSON in %LOCALAPPDATA%, Serilog to rolling files
Local only, nothing phones home
Tests
xUnit + golden-file fixtures per converter; FlaUI for UI smoke tests
Conversions are deterministic enough to assert on
Packaging
MSIX for the Microsoft Store (primary) + Velopack installer for direct download
Store gives free hosting, signing and updates; MSIX identity also unlocks the Windows 11 right-click menu
CI
GitHub Actions on windows-latest: build, test, package
Free for public repos and within free minutes for private
Signing reality for you. Azure Artifact Signing ($9.99/month) only accepts individual developers in the USA and Canada. From India, the clean route is the Microsoft Store, which signs and hosts MSIX packages for free. A direct-download installer would need a paid OV certificate from a commercial CA.
Install-size budget. About 120 MB download and 300 MB installed for the core app (self-contained .NET ~70 MB, trimmed FFmpeg ~40 MB, Magick.NET ~30 MB, PDFium ~6 MB, the rest small). The office pack and the background-removal model download only when first needed.
Engine map
About 90% of Tangerine's matrix runs on the core install. Only office-document fidelity and background removal pull extra downloads, and only the PDF → Office conversions are inherently approximate.
Conversions
Category
Inputs → outputs
Engine
Install
Quality
Images
JPG, PNG, WebP, TIFF, AVIF, BMP, HEIC ↔ each other; SVG input; → PDF
Magick.NET; HEIC encode via WIC + HEVC extension
Core
Exact
Video
MP4, MOV, MKV, WebM, AVI, WMV ↔ each other; → GIF, → MP3
FFmpeg; H.264/HEVC via Media Foundation or GPU
Core
Exact
Audio
MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA ↔ each other
FFmpeg
Core
Exact
PDF → images / TXT / MD / EPUB
All pages, images at 300 DPI
PDFium + PdfPig + own EPUB writer
Core
Exact for images, good for text
PDF → DOCX
Text, headings, images in reading order
PdfPig layout analysis → Open XML SDK
Core
Approximate (label "beta")
PDF → PPTX
One slide per page, page rendered as a full-slide picture
PDFium + Open XML SDK
Core
Exact look, not editable
PDF → XLSX
Detected tables → sheets
PdfPig table heuristics → ClosedXML
Core
Approximate (beta)
Documents
DOCX, Markdown, TXT, EPUB, RTF in; HTML, PDF out
Own document hub (Open XML SDK, Markdig, RtfPipe); PDF via WebView2 print
Core
Good for text documents
Pages (.pages)
Input only
LibreOffice import
Office pack
Approximate
Slides
PPT/PPTX → PDF, JPG, PNG
Office COM, else LibreOffice
Office pack (unless Office installed)
Exact with Office, good with LibreOffice
Spreadsheets
XLSX, ODS, CSV, TSV ↔ each other; XLS input; → PDF
ClosedXML, ExcelDataReader, own ODS writer; PDF via Office or LibreOffice
Core (PDF needs Office pack)
Exact data
Subtitles
SRT ↔ VTT; → TXT
Own parser
Core
Exact
Archives
ZIP, TAR, GZIP ↔ each other; RAR input
SharpCompress
Core
Exact
File tools
Tool
Applies to
Kind
Engine
Compress
Images, video, audio, PDF
Instant (preset ring: Small / Balanced / High)
Magick.NET, FFmpeg, PDFsharp + image re-encode
Resize / scale
Images, video
Instant (preset ring) or small popover for exact size
Magick.NET, FFmpeg
Rotate / flip
Images, video, PDF pages
Instant
Magick.NET, FFmpeg, PDFsharp
Strip metadata
Images, video, audio, PDF, Office files
Instant
Magick.NET, FFmpeg, PDFsharp, Open XML SDK
Remove background
Images
Instant + Keep/Discard preview
Windows AI or ONNX model
Extract audio / mute
Video
Instant
FFmpeg (stream copy)
Make GIF
Video
Instant (defaults) or editor for range
FFmpeg palette pipeline
OCR to text
Images, scanned PDF
Instant
Windows.Media.Ocr
Merge
Images → PDF, PDFs, videos, audio
Instant, files ordered by selection
PDFsharp, FFmpeg concat
Crop
Images, video, PDF pages
Editor
WPF canvas → engine params
Annotate
Images, PDF pages
Editor
InkCanvas + shapes, flattened by Magick.NET or PDFsharp
Redact
Images, PDF
Editor
Burned-in boxes; PDF text under boxes removed, not just covered
Trim / split
Video, audio
Editor
FFmpeg (stream copy when cuts land on keyframes)
Page organizer
PDF (reorder, delete, extract, split)
Editor
PDFsharp
Password protect / unlock
PDF, ZIP
Small popover
PDFsharp, SharpCompress
File actions (Control wheel, never count against the trial): Float shelf, Copy to…, Move to…, Copy path, Open terminal here, Share (Windows share sheet). AirDrop has no Windows equivalent and is dropped.
UI/UX
The rule: one gesture for anything that needs no input, one small focused window for anything that does. Nothing ever opens Windows Photos or another app.
Entry points
1. Drag wheel (primary). Start dragging any file, hold a key, drop on a wedge.
2. Right-click menu. "Pith ›" submenu in Explorer with Convert, Tools and Actions.
3. Keyboard. Select files in Explorer, press a global hotkey (default Ctrl+Alt+Space), arrows to choose, Enter to run.
4. Tray icon. Opens the Float shelf, recent jobs, settings and pause.
Keys during a drag (all remappable)
Key held mid-drag
Wheel
Note
Shift
Convert
Same muscle memory as Tangerine
Ctrl
Tools
Replaces Tangerine's Command
Alt
Actions
Replaces Tangerine's Control
Esc
Close wheel
Keep dragging normally
Explorer already gives these keys meanings during a drag (move, copy, shortcut), so its cursor badge changes for a moment before the wheel opens. That is harmless: the drop lands on our window, which always reports "copy" and never moves or links the original.
The wheel
• Opens centred on the cursor, 6–8 wedges, the most-used option at 12 o'clock and learned over time.
• Only options valid for the dragged file type appear. A mixed selection shows only shared options.
• Wedges with sub-choices (Compress → Small / Balanced / High) open an outer ring on hover. Still one drop.
• Wheel is a non-activating, topmost overlay, so the source window keeps focus and nothing flashes.
• Hovering a wedge for 400 ms shows a one-line hint ("PNG · keeps transparency").
Instant tools
• A compact toast appears at the bottom-right: file name, progress bar, cancel.
• On finish: "Saved photo.png · Show in folder · Undo". Undo deletes the output, never the original.
• Output lands next to the original with a suffix (photo-compressed.jpg); a name clash adds  (2). Originals are never overwritten unless the user turns that on per tool.
• Batches run in parallel up to CPU cores minus one and show one grouped toast.
Editor windows (one per interactive tool)
• Open at the cursor's monitor, sized to the content, Fluent styling, dark/light from Windows.
• Each holds one job only. Crop: handles plus aspect chips (Free, 1:1, 4:5, 16:9, Original). Annotate: arrow, box, text, highlighter, pixelate, colour, undo. Redact: drag boxes. Trim: player, filmstrip, in/out handles, split markers, "fast cut" toggle. Page organizer: page thumbnails to drag, delete, rotate.
• Enter = Done, Esc = Cancel, Ctrl+Z = Undo, everywhere.
• Editors remember the last setting per tool. Multi-file drops either apply one setting to all or step through files with Next.
Float shelf
A small always-on-top panel at a screen edge. Drop files onto it to park them, then drag them out to any app or folder later. It also accepts finished outputs, so a converted file can go straight into an email or chat window.
Settings (single window, five tabs): General (launch at startup, output folder rule, overwrite), Keys, Wheel (pin and reorder options), Quality defaults per format, Packs (install or remove the office pack and background model, see disk use).
Onboarding: a three-step interactive demo on first launch. The user drags a sample file, holds Shift, drops on PNG, sees the toast. No account, no email.
Timeline
About 18 weeks part-time to a public 1.0, with a useful alpha at week 9. Full-time, roughly halve every phase.
Phase 0 is deliberately a throwaway spike: if the drag wheel can't be made reliable, the fallback is a right-click and hotkey-first product, and we learn that in two weeks rather than ten.
Risks and the hard parts
The single biggest risk is the drag wheel itself, which is why it is the first spike. Everything else is known engineering.
Risk
Why it bites
Mitigation
Detecting a drag that Explorer started
Windows has no public "a drag is in progress" event
Low-level mouse hook: button down + movement past the system drag threshold + cursor over Explorer, desktop or any window; confirm with a held key. Spike in week 1
Wheel stealing focus or failing to accept the drop
A focused overlay cancels some drags; UIPI blocks drops across integrity levels
Non-activating layered window registered as a drop target; app runs non-elevated like Explorer
Antivirus flags low-level hooks
Keyloggers use the same API
Hook only while a mouse button is down, sign every build, ship through the Store
Codec patents and licences
H.264/HEVC/AAC encoders and GPL code
LGPL FFmpeg only; hardware and Media Foundation encoders; a THIRD-PARTY-NOTICES file and LGPL source offer in the app
HEIC encoding missing on many PCs
Needs Microsoft's HEVC extension
Detect it; hide HEIC output with a one-line explanation and a Store link
PDF → Office quality
Layout reconstruction is heuristic everywhere
Label as beta, set expectations in the hint, keep PDF → images/text exact
Install size creep
FFmpeg and LibreOffice grow fast
Trimmed FFmpeg build, on-demand packs, size budget checked in CI
Code signing from India
Azure Artifact Signing excludes individuals outside the USA and Canada
Store-first distribution (free signing); buy an OV certificate only if direct download matters
Scope (275 conversions)
Easy to stall on the long tail
Ship the top 40 conversions and 12 tools first; matrix is data-driven so more are cheap to add
Solo time
You have a job and final-year project
Phases are independent; Claude Code does the bulk; alpha after phase 2 is already useful
Sources
• Tangerine for Mac and Digital Trends review
• Windows AI imaging APIs and hardware requirements
• Windows App SDK 2.0 release
• .NET 10 LTS and WPF/WinUI guidance
• Azure Artifact Signing eligibility
• Microsoft Store free individual registration
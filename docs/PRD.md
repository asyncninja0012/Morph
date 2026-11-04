PRD: Pith for Windows, v1.0
Pith is an offline Windows utility that converts, edits and acts on files from a single drag gesture. Version 1.0 ships on the Microsoft Store about 18 weeks after kickoff, covering Tangerine's Mac feature set except AirDrop. "Pith" is a working name.
1. Problem
Changing a file's format or doing a small edit on Windows means opening a heavy app (Photoshop, Premiere, Acrobat) or uploading the file to a website. Websites are slow, capped, ad-heavy and send private files to servers you don't control. Built-in tools (Photos, Paint, Clipchamp) each cover a sliver and none are reachable from the file itself.
2. Goals and non-goals
Goals
1. Any supported conversion or quick edit takes one drag, one held key and one drop, with no window to manage.
2. Everything runs locally. No account, no upload, no telemetry unless the user opts in.
3. Tools that need input open a small single-purpose editor inside Pith, never another app.
4. The core install stays under about 120 MB to download; heavy capabilities download on demand.
Non-goals for 1.0
• AirDrop or any cross-device transfer beyond the Windows share sheet
• Writing Apple .pages files
• Full editors (layers, timelines with multiple tracks, PDF text editing)
• macOS, Linux, ARM64 builds (ARM64 targeted for 1.1)
• Cloud sync, accounts, collaboration
3. Users
Persona
Typical jobs
What they value
Student / office worker
PDF ↔ Word, merge PDFs, compress for email or portal limits, HEIC → JPG from phone
Speed, privacy of documents
Creator / social media
Video → MP4/GIF, trim clips, compress for upload limits, crop to 9:16 or 1:1, remove background
One-gesture workflow, quality presets
Developer / power user
WebP/AVIF, strip metadata, copy path, open terminal, archives
Keyboard path, no bloat
4. Success metrics (first 90 days after 1.0)
• Median time from drag start to finished output under 3 s for images and documents under 20 MB
• At least 40% of installs run 10+ jobs in their first week
• Crash-free sessions above 99.5% (local crash log, opt-in upload)
• Store rating 4.5 or higher
• Trial-to-paid conversion of 5% or more (if paid; see 6.10)
5. Platform and constraints
• Windows 11 22H2 or later, x64. Windows 10 22H2 best-effort (no Windows 11 right-click menu; classic menu instead).
• C# on .NET 10 LTS, WPF with Fluent theme, packaged as MSIX for the Microsoft Store.
• Runs non-elevated. No admin rights needed at install or runtime.
• Fully offline. The only network calls are optional pack downloads and Store updates.
• Third-party engines allowed when they save real effort; licences must be permissive or LGPL (no GPL or AGPL code linked into the app).
6. Functional requirements
6.1 Interaction model
• IX-1 While the user drags one or more files from Explorer, the desktop or any app that drags file paths, pressing and holding Shift opens the Convert wheel, Ctrl the Tools wheel, Alt the Actions wheel. A quick tap (released within 200 ms) does nothing. Keys are remappable.
• IX-2 The wheel opens centred on the cursor within 100 ms of the 200 ms hold threshold, stays open while the key is held until drop, release or Esc, and never takes focus from the source window.
• IX-3 The wheel shows only options valid for every file in the drag. Options with sub-choices expand an outer ring on hover.
• IX-4 Dropping outside a wedge cancels; releasing the key or pressing Esc without dropping closes the wheel and the drag continues as a normal drag.
• IX-5 Keyboard path: with files selected in Explorer, a global hotkey (default Ctrl+Alt+Space) opens the same wheel at the cursor; arrows choose, Enter applies, Tab switches wheel.
• IX-6 Right-click menu: a "Pith" submenu in Explorer with Convert, Tools and Actions, mirroring the wheel.
• IX-7 Wheel ordering learns from use; users can pin and reorder options in Settings.
6.2 Conversions
• CV-1 Support the conversion matrix in the Project map's Engine map, grouped as images, video, audio, PDF, documents, slides, spreadsheets, subtitles and archives.
• CV-2 1.0 must-have set (ships at alpha): JPG/PNG/WebP/HEIC/AVIF/TIFF/BMP among each other; MP4/MOV/MKV/WebM/AVI → MP4, WebM, GIF, MP3; MP3/M4A/WAV/FLAC/OGG/Opus among each other; PDF → JPG/PNG/TXT; images → PDF; DOCX ↔ PDF (PDF → DOCX beta); MD ↔ DOCX/HTML; CSV ↔ XLSX; SRT ↔ VTT; ZIP/TAR/GZIP among each other.
• CV-3 The matrix is data-driven: one row per (input type, output type, engine, default options). Adding a conversion needs no UI work.
• CV-4 Each output uses sensible defaults (e.g. JPG quality 90, MP4 H.264 CRF-equivalent quality, audio 192 kbps) editable in Settings → Quality.
• CV-5 Conversions marked approximate (PDF → DOCX/XLSX) show "beta" on the wedge and in the hint.
• CV-6 Formats needing a missing system codec (HEIC output without the HEVC extension) are shown disabled with a one-line reason and a link to fix it.
6.3 Instant tools
• IT-1 Compress (images, video, audio, PDF) with three presets: Small, Balanced, High quality; plus "fit under X MB" for video and PDF.
• IT-2 Resize/scale with presets (50%, 1080p, 720p, 2048 px long edge) and a small popover for exact size.
• IT-3 Rotate 90°/180°, flip; strip metadata; extract audio; mute; make GIF (defaults); OCR to TXT; merge (images → PDF, PDFs, videos, audio) in selection order.
• IT-4 Remove background: runs instantly and shows a Keep / Discard preview before saving a PNG with transparency.
• IT-5 Password-protect and unlock PDF and ZIP through a small popover asking only for the password.
6.4 Editor tools
• ED-1 Crop (images, video, PDF pages): handles, aspect chips (Free, Original, 1:1, 4:5, 9:16, 16:9), pixel readout.
• ED-2 Annotate (images, PDF pages): arrow, rectangle, ellipse, text, highlighter, pixelate, colour, stroke width, undo/redo.
• ED-3 Redact (images, PDF): drag boxes; for PDFs the text and images under each box are removed from the file, not only covered.
• ED-4 Trim and split (video, audio): player, filmstrip, in/out handles, multiple split points, "fast cut" (no re-encode, snaps to keyframes) vs "exact cut".
• ED-5 PDF page organizer: thumbnails to reorder, rotate, delete, extract a range, split into files.
• ED-6 Every editor: Enter = Done, Esc = Cancel, Ctrl+Z = Undo; remembers last settings per tool; multi-file drops offer "apply to all" or "step through".
6.5 File actions
• FA-1 Float: an always-on-top shelf at a screen edge that holds dropped files and lets them be dragged out later. Survives restart; cleared manually.
• FA-2 Copy to…, Move to… (recent folders listed first), Copy path (quoted, with option for Unix style), Open terminal here (Windows Terminal, else PowerShell), Share (Windows share sheet).
• FA-3 Actions work on folders and mixed selections and never count against the trial.
6.6 Jobs, output and undo
• JB-1 Every operation becomes a job in a queue. Jobs run in parallel up to (logical cores − 1); video jobs run at most 2 at a time.
• JB-2 A toast shows progress and cancel; on success it offers Show in folder and Undo; on failure a plain-language reason and "Copy details".
• JB-3 Output is written next to the original as name-<suffix>.<ext> (suffix per tool, e.g. -compressed, -cropped); clashes add  (2). Originals are never modified unless the user enables overwrite for that tool.
• JB-4 Writes go to a temp file first and are renamed on success, so a cancelled or failed job leaves nothing behind.
• JB-5 Undo removes the outputs of that job (to the Recycle Bin) for 10 minutes after completion.
• JB-6 A job history panel in the tray lists the last 50 jobs with input, output and status.
6.7 On-demand packs
• PK-1 Office pack: used only when Microsoft Office is not installed. Downloads a portable LibreOffice (~350 MB) after explicit consent showing size; enables slides/spreadsheet → PDF and .pages input.
• PK-2 Background model: on Copilot+ PCs use the Windows AI foreground extractor; elsewhere download an ONNX segmentation model under a commercial-friendly licence after consent.
• PK-3 Packs show size and disk use in Settings → Packs and can be removed.
6.8 Settings
General (launch at sign-in, output location rule, overwrite per tool, language), Keys, Wheel (pin, hide, reorder), Quality defaults per format, Packs, About (licences, version, open logs folder).
6.9 Onboarding
First launch shows a three-step interactive demo using a bundled sample file: drag it, hold Shift, drop on PNG. Then one screen on the right-click menu and hotkey. Skippable; no sign-up.
6.10 Monetisation
• Free trial of 25 conversion or tool actions, then a one-time Pro unlock. Actions never count.
• Price is an open question (Tangerine charges $29.99 one-time).
• Licence check is local (Store licence API); the app never stops working offline.
7. Non-functional requirements
Area
Requirement
Responsiveness
Wheel visible ≤ 100 ms after the 200 ms hold threshold; editor window visible ≤ 400 ms after drop
Speed
12 MP JPG → PNG ≤ 1.5 s; 1-minute 1080p MOV → MP4 ≤ 20 s with a hardware encoder; 50-page PDF → JPGs ≤ 8 s (mid-range laptop)
Footprint
Idle RAM ≤ 80 MB; idle CPU ≈ 0%; download ≤ 120 MB; installed core ≤ 300 MB
Privacy
No network calls except pack downloads and Store updates; no telemetry by default; crash reports opt-in
Reliability
Engine crashes never crash the app; failed jobs leave no partial files; crash-free sessions ≥ 99.5%
Security
Runs non-elevated; low-level hooks active only while a mouse button is held; every binary signed; input files treated as untrusted (engines in child processes, size and page limits)
Accessibility
Full keyboard path; Narrator labels on every wheel option and editor control; respects high contrast and text scaling
Compatibility
Works with Explorer, desktop, browser download bars, Outlook/Teams attachments, and any app that drags CF_HDROP file lists; handles long paths and non-ASCII names
Licences
Only permissive or LGPL dependencies; THIRD-PARTY-NOTICES shipped; LGPL engines replaceable as separate binaries
8. Release plan
Milestone
Week
Exit criteria
Spike gate
2
A file dragged from Explorer converts through the wheel end to end; MSIX installs
Alpha
9
CV-2 set and 8 instant tools work; used daily by the developer for a week
Beta
15
All editors and packs work; private Store flight to 20–50 testers
1.0
18
Acceptance criteria below met; public Store listing
9. Acceptance criteria for 1.0
[ ] Every row in CV-2 passes golden-file tests on CI
[ ] Wheel opens and accepts drops from Explorer, desktop, Edge downloads bar and Outlook attachments
[ ] All five editors complete their job with keyboard only
[ ] Cancel at any point leaves no output file and no orphan process
[ ] Footprint and speed targets in section 7 met on a reference laptop
[ ] Store certification passes; THIRD-PARTY-NOTICES complete
[ ] Onboarding completes in under 60 seconds for a first-time tester
10. Open questions
• Final product name and trademark check
• Price and whether to use Store commerce (15% fee) or own payments (0% Store fee)
• Whether to offer a direct-download build (needs a paid code-signing certificate from India)
• ARM64 build timing (FFmpeg and Magick.NET have ARM64 builds; needs test hardware)
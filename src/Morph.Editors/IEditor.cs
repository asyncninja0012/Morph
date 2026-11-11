using System.Text.Json.Nodes;
using System.Windows;

using Morph.Core.Jobs;

namespace Morph.Editors;

/// <summary>Gets the Worker's preview of a file's <paramref name="page"/> (1-based; PDF only), so the App never decodes dropped files itself.</summary>
public delegate Task<PreviewImage> PreviewLoader(string path, int page, CancellationToken ct);

/// <summary>
/// Gets the Worker's timeline picture of a video (as many frames as fill a strip of <paramref name="shape"/>, width ÷ height) or
/// sound file (its waveform), with its length.
/// </summary>
public delegate Task<PreviewImage> TimelineLoader(string path, double shape, CancellationToken ct);

/// <summary>
/// Has the Worker make a small copy of a video or sound file that the App may play (H.264/AAC written by Morph's own encoder, never
/// the dropped file), reporting 0..1; returns the copy's path. The App deletes it after the editor closes.
/// </summary>
public delegate Task<string> PlaybackLoader(string path, IProgress<double> progress, CancellationToken ct);

/// <summary>
/// One editor's content inside <see cref="EditorShell"/>. An editor only collects parameters for the job; it never writes files.
/// The shell calls <see cref="LoadAsync"/> once per file shown, then reads <see cref="Parameters"/> and <see cref="Settings"/> on Done.
/// </summary>
public interface IEditor
{
    /// <summary>Window title, e.g. "Crop".</summary>
    string Title { get; }

    FrameworkElement View { get; }

    /// <summary>Shows <paramref name="path"/>. <paramref name="settings"/> are the last ones remembered for this tool (or the previous file's), null the first time.</summary>
    Task LoadAsync(string path, JsonObject? settings, CancellationToken ct);

    bool CanUndo { get; }

    void Undo();

    /// <summary>Raised when <see cref="CanUndo"/> may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>The job parameters for the file shown.</summary>
    JsonObject Parameters();

    /// <summary>What to remember for next time (ED-6), e.g. the aspect chip but not the crop box.</summary>
    JsonObject Settings();
}

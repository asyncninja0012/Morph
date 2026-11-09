using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Subtitles;

/// <param name="Text">Cue text with lines joined by "\n" and only &lt;b&gt;, &lt;i&gt;, &lt;u&gt; tags; no entities.</param>
internal sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>SRT ↔ VTT (CV-2, task 2.6). Tool ids: convert.srt, convert.vtt.</summary>
public sealed partial class SubtitleEngine : IEngine
{
    private const long MaxBytes = 20L * 1024 * 1024; // a subtitle file this large is not one

    public string Id => "subtitle";

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex Time();

    // Keeps b/i/u (the tags both formats share); every other tag (<font>, <v Name>, <c.x>, <00:01.000>, ...) is dropped.
    // A tag starts right after "<" with a name ("i", "c.loud", "v") or a timestamp ("00:01.000"), so "a < b" is text.
    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9.]*|\d[\d:.]*)(?:\s[^<>]*)?>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssTag(); // {\an8} and friends from SSA/ASS tools

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var target = job.ToolId switch
        {
            "convert.srt" => "srt",
            "convert.vtt" => "vtt",
            _ => throw new EngineException("This conversion isn't available yet.", $"subtitle: unsupported tool {job.ToolId}"),
        };

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var cues = await ReadAsync(input, ct).ConfigureAwait(false);
            var output = namer.Reserve(input, "." + target).Path;
            if (target == "srt")
            {
                await File.WriteAllTextAsync(output, WriteSrt(cues), new UTF8Encoding(true), ct).ConfigureAwait(false); // BOM: old players guess otherwise
            }
            else
            {
                await File.WriteAllTextAsync(output, WriteVtt(cues), new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    internal static async Task<List<Cue>> ReadAsync(string input, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        if (new FileInfo(input).Length > MaxBytes)
        {
            throw new EngineException($"{name} is over {MaxBytes / 1024 / 1024} MB, too big for a subtitle file.");
        }

        var format = (await FileSniffer.Instance.DetectAsync(input, ct).ConfigureAwait(false)).Id;
        var cues = Parse(await TextFiles.ReadAsync(input, ct).ConfigureAwait(false), vtt: format == "vtt");
        return cues.Count > 0 ? cues : throw new EngineException($"{name} has no subtitles to convert.");
    }

    // Both formats are blocks separated by blank lines with a "start --> end" line; SRT puts a number before it,
    // VTT an optional id. VTT's header, NOTE, STYLE and REGION blocks have no "-->" line (or come first) and are skipped.
    internal static List<Cue> Parse(string text, bool vtt)
    {
        var cues = new List<Cue>();
        var blocks = text.Split("\n\n");
        foreach (var block in blocks)
        {
            var lines = block.Trim('\n').Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0 || timing > 1 || (vtt && lines[0].StartsWith("NOTE", StringComparison.Ordinal)))
            {
                continue;
            }

            var parts = lines[timing].Split("-->");
            var start = Time().Match(parts[0]);
            var end = Time().Match(parts[1]);
            if (!start.Success || !end.Success)
            {
                continue;
            }

            var body = string.Join("\n", lines.Skip(timing + 1)).Trim();
            body = vtt ? WebUtility.HtmlDecode(CleanTags(body)) : CleanTags(AssTag().Replace(body, ""));
            var (from, to) = (ToTime(start), ToTime(end));
            cues.Add(new Cue(from, to < from ? from : to, body));
        }

        return cues;
    }

    private static string CleanTags(string text) =>
        Tag().Replace(text, m => m.Groups[2].Value.ToLowerInvariant() is "b" or "i" or "u" ? $"<{m.Groups[1].Value}{m.Groups[2].Value.ToLowerInvariant()}>" : "");

    private static TimeSpan ToTime(Match m) => new TimeSpan(0,
        m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));

    internal static string WriteSrt(IReadOnlyList<Cue> cues) =>
        string.Concat(cues.Select((c, i) => $"{i + 1}\r\n{Stamp(c.Start, ',')} --> {Stamp(c.End, ',')}\r\n{c.Text.ReplaceLineEndings("\r\n")}\r\n\r\n"));

    // VTT cue text is HTML-like: & and < must be escaped (which also keeps "-->" out of it); b/i/u tags stay.
    internal static string WriteVtt(IReadOnlyList<Cue> cues) =>
        "WEBVTT\n\n" + string.Concat(cues.Select(c => $"{Stamp(c.Start, '.')} --> {Stamp(c.End, '.')}\n{EscapeVtt(c.Text)}\n\n"));

    private static string EscapeVtt(string text)
    {
        var s = new StringBuilder();
        var last = 0;
        foreach (Match m in Tag().Matches(text))
        {
            s.Append(Escape(text[last..m.Index])).Append(m.Value);
            last = m.Index + m.Length;
        }

        return s.Append(Escape(text[last..])).ToString();
    }

    // Not WebUtility.HtmlEncode: it would also turn é into &#233;.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Stamp(TimeSpan t, char separator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";
}

using System.IO.Compression;
using System.Text;

using Morph.Core.Jobs;
using Morph.Engines.Archives;
using Morph.Engines.Sheets;
using Morph.Engines.Subtitles;

namespace Morph.Engines.Tests;

// Each sheet, subtitle and archive row runs on the samples in GoldenTests; these cover the edges.
public sealed class DataEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("morph-data-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Zip_with_unsafe_paths_fails_and_leaves_no_output()
    {
        var input = Path.Combine(_dir, "evil.zip");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        {
            zip.CreateEntry("ok.txt");
            zip.CreateEntry("../outside.txt");
        }

        var namer = new OutputNamer(_dir);
        var ex = await Assert.ThrowsAsync<EngineException>(() => RunAsync(new ArchiveEngine(), "convert.tar", "archive", input, namer));

        Assert.StartsWith("evil.zip has files with unsafe paths", ex.UserMessage, StringComparison.Ordinal);
        namer.AbortAll();
        Assert.Equal([input], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Plain_gz_becomes_an_archive_holding_its_one_file()
    {
        var input = Path.Combine(_dir, "notes.txt.gz");
        await using (var gz = new GZipStream(File.Create(input), CompressionLevel.Fastest))
        {
            await gz.WriteAsync("just text"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        var output = await ConvertAsync(new ArchiveEngine(), "convert.zip", "archive", input);

        Assert.Equal("notes.txt.zip", Path.GetFileName(output));
        await using var zip = await ZipFile.OpenReadAsync(output, TestContext.Current.CancellationToken);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("notes.txt", entry.FullName);
        using var reader = new StreamReader(await entry.OpenAsync(TestContext.Current.CancellationToken));
        Assert.Equal("just text", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Csv_formulas_stay_text_both_ways_and_semicolons_are_detected()
    {
        var input = Path.Combine(_dir, "book.csv");
        await File.WriteAllTextAsync(input, "a;b\n=HYPERLINK(\"x\");007\n-5;-x\n", TestContext.Current.CancellationToken);

        var xlsx = await ConvertAsync(new SheetEngine(), "convert.xlsx", "sheet", input);
        File.Delete(input);
        var csv = await ConvertAsync(new SheetEngine(), "convert.csv", "sheet", xlsx);

        // Leading zeros keep "007" text; "-5" is a number; formula-looking text gets an apostrophe in the CSV.
        Assert.Equal([["a", "b"], ["=HYPERLINK(\"x\")", "007"], ["-5", "-x"]], Assert.Single(SheetEngine.ReadXlsx(xlsx, TestContext.Current.CancellationToken)).Rows);
        Assert.Equal("a,b\r\n\"'=HYPERLINK(\"\"x\"\")\",007\r\n-5,'-x\r\n", await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Srt_in_windows_1252_with_font_tags_becomes_clean_utf8_vtt()
    {
        var input = Path.Combine(_dir, "old.srt");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await File.WriteAllBytesAsync(input, Encoding.GetEncoding(1252).GetBytes(
            "1\r\n00:00:01,000 --> 00:00:02,000\r\n<font color=\"red\">Café</font> {\\an8}<b>a < b</b>\r\n"), TestContext.Current.CancellationToken);

        var vtt = await ConvertAsync(new SubtitleEngine(), "convert.vtt", "subtitle", input);

        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nCafé <b>a &lt; b</b>\n\n", await File.ReadAllTextAsync(vtt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Vtt_voice_and_class_tags_are_dropped()
    {
        var cue = Assert.Single(SubtitleEngine.Parse("WEBVTT\n\n00:01.000 --> 00:02.000\n<v Bob><c.loud>Hi</c> <i>there</i></v> &amp; bye\n", vtt: true));

        Assert.Equal("Hi <i>there</i> & bye", cue.Text);
    }

    private Task RunAsync(IEngine engine, string tool, string engineId, string input, OutputNamer namer) =>
        engine.RunAsync(new JobRequest(Guid.NewGuid(), tool, engineId, [input], _dir), namer, new Progress<JobProgress>(), TestContext.Current.CancellationToken);

    private async Task<string> ConvertAsync(IEngine engine, string tool, string engineId, string input)
    {
        var namer = new OutputNamer(_dir);
        await RunAsync(engine, tool, engineId, input, namer);
        return Assert.Single(namer.CommitAll());
    }
}

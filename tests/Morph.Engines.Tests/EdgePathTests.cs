using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Engines.Media;

namespace Morph.Engines.Tests;

/// <summary>
/// PRD §7 compatibility (task 5.3): every engine through the real Worker on a file whose folder path is past MAX_PATH (260) and
/// whose folder and file names aren't ASCII (accents, Cyrillic, CJK, an emoji outside the BMP). The output must land next to it.
/// </summary>
public sealed class EdgePathTests : IDisposable
{
    private static readonly CapabilityMatrix Matrix = CapabilityMatrix.LoadDefault(_ => true);
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, "Morph.Worker.exe");

    private readonly string _root = Directory.CreateTempSubdirectory("morph-edge-").FullName;
    private readonly string _dir;

    public EdgePathTests()
    {
        _dir = Path.Combine(_root, "Ünïcödé Фото 写真 😀", new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(_dir);
    }

    public static TheoryData<string, string, string> Cases() => new()
    {
        { "jpg", "convert.png", "imaging" },
        { "jpg", "convert.pdf", "pdf" },
        { "pdf", "convert.jpg", "pdf" },
        { "pdf", "convert.txt", "pdf" },
        { "wav", "convert.mp3", "media" },
        { "mov", "convert.mp4", "media" },
        { "docx", "convert.md", "document" },
        { "md", "convert.pdf", "document" },
        { "csv", "convert.xlsx", "sheet" },
        { "srt", "convert.vtt", "subtitle" },
        { "zip", "convert.tar", "archive" },
        { "pptx", "convert.pdf", "office" },
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // An output can still be open in a virus scan; it's a temp folder.
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Long_non_ascii_path_converts(string from, string tool, string engine)
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(GoldenTests.Fixtures, $"sample.{from}");
        if (engine == "office")
        {
            Assert.SkipUnless(File.Exists(fixture), "no office fixture: gen-fixtures saves them with Microsoft Office");
            Assert.SkipUnless(OfficeApps.HasOfficeFor(FileFormat.FromExtension(from)) || OfficeApps.LibreOffice(PackStore.DefaultRoot) is not null,
                "needs Microsoft Office or the office pack");
        }

        Assert.True(File.Exists(fixture), $"{fixture} is missing. Run: dotnet run build/gen-fixtures.cs");
        if (engine == "media")
        {
            Assert.SkipUnless(Ffmpeg.Bundled.IsAvailable, "ffmpeg not fetched: run ./build/fetch-binaries.ps1");
            Assert.SkipWhen(tool == "convert.mp4" && await new EncoderProbe(Ffmpeg.Bundled, _root).H264Async(ct) is null, "no H.264 encoder on this machine");
        }

        var input = Path.Combine(_dir, $"Ünïcödé фото 写真 😀.{from}");
        Assert.True(input.Length > 260, $"path is only {input.Length} characters");
        File.Copy(fixture, input);

        var result = await WorkerClient.RunAsync(WorkerPath,
            new JobRequest(Guid.NewGuid(), tool, engine, [input], _dir, Matrix.Tool(tool)?.Defaults?.DeepClone().AsObject()),
            new SyncProgress<JobProgress>(_ => { }), ct);

        Assert.True(result.Status == JobStatus.Succeeded, $"{result.UserMessage} {result.Details}");
        var target = tool["convert.".Length..];
        Assert.NotEmpty(result.Outputs);
        foreach (var output in result.Outputs)
        {
            Assert.Equal(_dir, Path.GetDirectoryName(output));
            Assert.StartsWith("Ünïcödé фото 写真 😀", Path.GetFileName(output), StringComparison.Ordinal);
            Assert.True(new FileInfo(output).Length > 0, $"{output} is empty");
            Assert.Equal(target, (await FileSniffer.Instance.DetectAsync(output, ct)).Id);
        }
    }
}

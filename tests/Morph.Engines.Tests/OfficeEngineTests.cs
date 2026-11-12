using Morph.Core.Capabilities;
using Morph.Core.Formats;
using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Engines.Office;

namespace Morph.Engines.Tests;

/// <summary>
/// The LibreOffice path of the office engine (PK-1), with Microsoft Office turned off. Needs LibreOffice: the installed office
/// pack, or MORPH_SOFFICE pointing at a soffice.exe (an unpacked LibreOffice MSI: msiexec /a LibreOffice.msi TARGETDIR=...).
/// The golden tests cover whichever path this PC uses through the real Worker.
/// </summary>
public sealed class OfficeEngineTests : IDisposable
{
    private static readonly string? Soffice = Environment.GetEnvironmentVariable("MORPH_SOFFICE") is { Length: > 0 } path && File.Exists(path)
        ? path : OfficeApps.LibreOffice(PackStore.DefaultRoot);

    private readonly string _dir = Directory.CreateTempSubdirectory("morph-office-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("pptx", "pdf")]
    [InlineData("ppt", "pdf")]
    [InlineData("odp", "png")]
    [InlineData("pptx", "jpg")]
    [InlineData("xlsx", "pdf")]
    [InlineData("xls", "pdf")]
    [InlineData("ods", "pdf")]
    [InlineData("doc", "pdf")]
    [InlineData("odt", "pdf")]
    public async Task LibreOffice_converts(string from, string to)
    {
        var ct = TestContext.Current.CancellationToken;
        var input = CopyFixture(from);
        var namer = new OutputNamer(_dir);

        await new OfficeEngine(Soffice, useOffice: false).RunAsync(new JobRequest(Guid.NewGuid(), $"convert.{to}", "office", [input], _dir),
            namer, new SyncProgress<JobProgress>(_ => { }), ct);

        await GoldenTests.AssertOfficeOutputAsync(from, to, namer.CommitAll(), ct);
    }

    // The Pages → DOCX route (no .pages sample exists, so an ODT stands in).
    [Fact]
    public async Task LibreOffice_writes_docx()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = CopyFixture("odt");
        var namer = new OutputNamer(_dir);

        await new OfficeEngine(Soffice, useOffice: false).RunAsync(new JobRequest(Guid.NewGuid(), "convert.docx", "office", [input], _dir),
            namer, new SyncProgress<JobProgress>(_ => { }), ct);

        Assert.Equal("docx", (await FileSniffer.Instance.DetectAsync(Assert.Single(namer.CommitAll()), ct)).Id);
    }

    [Fact]
    public async Task A_damaged_file_fails_with_a_plain_message()
    {
        Assert.SkipWhen(Soffice is null, "needs LibreOffice: the office pack or MORPH_SOFFICE");
        var input = Path.Combine(_dir, "broken.pptx");
        using (var zip = System.IO.Compression.ZipFile.Open(input, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ppt/presentation.xml").Open());
            writer.Write("not xml");
        }

        Assert.Equal("pptx", (await FileSniffer.Instance.DetectAsync(input, TestContext.Current.CancellationToken)).Id);

        var ex = await Assert.ThrowsAsync<EngineException>(() => new OfficeEngine(Soffice, useOffice: false).RunAsync(
            new JobRequest(Guid.NewGuid(), "convert.pdf", "office", [input], _dir), new OutputNamer(_dir), new SyncProgress<JobProgress>(_ => { }),
            TestContext.Current.CancellationToken));

        Assert.StartsWith("LibreOffice couldn't convert broken.pptx", ex.UserMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.pdf"));
    }

    private string CopyFixture(string from)
    {
        Assert.SkipWhen(Soffice is null, "needs LibreOffice: the office pack or MORPH_SOFFICE");
        var fixture = Path.Combine(GoldenTests.Fixtures, $"sample.{from}");
        Assert.SkipUnless(File.Exists(fixture), "no office fixture: gen-fixtures saves them with Microsoft Office");
        var input = Path.Combine(_dir, Path.GetFileName(fixture));
        File.Copy(fixture, input);
        return input;
    }
}

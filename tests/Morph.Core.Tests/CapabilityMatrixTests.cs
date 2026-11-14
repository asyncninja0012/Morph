using System.Text;
using System.Text.Json;

using Morph.Core.Capabilities;
using Morph.Core.Formats;

namespace Morph.Core.Tests;

public sealed class CapabilityMatrixTests
{
    private static readonly CapabilityMatrix Shipped = CapabilityMatrix.LoadDefault(_ => true);

    [Fact]
    public void Shipped_rows_use_known_formats_and_every_tool_has_a_row()
    {
        Assert.All(Shipped.Rows, r =>
        {
            Assert.Equal(r.In, FileFormat.FromExtension(r.In).Id);
            Assert.True(r.Out is null || FileFormat.FromExtension(r.Out).Id == r.Out, $"unknown output format {r.Out}");
            Assert.NotEqual(r.In, r.Out);
        });
        Assert.All(Shipped.OptionsFor([], WheelGroup.Convert).Concat(Shipped.OptionsFor([], WheelGroup.Tools)), o => Assert.Contains(Shipped.Rows, r => r.ToolId == o.Tool.Id));
    }

    [Fact]
    public void Options_fit_every_file()
    {
        Assert.Equal(["convert.png", "convert.webp", "convert.heic", "convert.avif", "convert.tiff", "convert.gif", "convert.bmp", "convert.pdf"], Ids([FileFormat.Jpg]));
        Assert.Contains("convert.jpg", Ids([FileFormat.Heic, FileFormat.Avif]));
        Assert.Equal(["convert.mp4", "convert.webm", "convert.mkv", "convert.avi", "convert.gif", "convert.mp3"], Ids([FileFormat.Mov]));
        Assert.Equal(["convert.mp4", "convert.webm", "convert.mkv", "convert.avi", "convert.gif", "convert.mp3"], Ids([FileFormat.Wmv]));
        Assert.Equal(["convert.mp3"], Ids([FileFormat.Mkv, FileFormat.Wav]));
        Assert.Equal(["convert.mp3", "convert.m4a", "convert.wav", "convert.ogg", "convert.opus"], Ids([FileFormat.Flac]));
        Assert.Equal(["convert.gif"], Ids([FileFormat.Jpg, FileFormat.Mov])); // imaging and media rows, one job each
        Assert.Equal(["convert.png", "convert.jpg", "convert.txt", "convert.docx", "convert.md", "convert.xlsx", "convert.pptx"], Ids([FileFormat.Pdf]));
        Assert.Equal(["convert.png", "convert.jpg", "convert.pdf"], Ids([FileFormat.Pdf, FileFormat.Jpg])); // pdf and imaging rows, one job each
        Assert.Empty(Ids([FileFormat.Jpg, FileFormat.Wav]));
        Assert.Empty(Ids([FileFormat.Unknown]));
    }

    [Fact]
    public void Target_format_is_hidden_only_when_every_file_already_has_it()
    {
        Assert.DoesNotContain("convert.png", Ids([FileFormat.Png, FileFormat.Png]));
        Assert.Contains("convert.png", Ids([FileFormat.Jpg, FileFormat.Png]));
        Assert.Contains("convert.mp4", Ids([FileFormat.Mp4, FileFormat.Mov]));
        Assert.Null(Shipped.RowFor(FileFormat.Png, "convert.png"));
    }

    [Fact]
    public void No_files_offers_every_tool_of_the_wheel() =>
        Assert.Equal(["convert.png", "convert.jpg", "convert.webp", "convert.heic", "convert.avif", "convert.tiff", "convert.mp4", "convert.webm", "convert.mkv", "convert.avi",
            "convert.gif", "convert.mp3", "convert.m4a", "convert.wav", "convert.flac", "convert.ogg", "convert.opus", "convert.bmp", "convert.txt", "convert.pdf",
            "convert.docx", "convert.md", "convert.html", "convert.xlsx", "convert.pptx", "convert.csv", "convert.srt", "convert.vtt", "convert.zip", "convert.tar",
            "convert.gz"], Ids([]));

    [Fact]
    public void Merge_needs_two_files_it_can_join()
    {
        Assert.Equal(["compress", "crop", "annotate", "redact", "organize", "strip-metadata", "ocr", "protect", "unlock"], Ids([FileFormat.Pdf], WheelGroup.Tools));
        Assert.Equal(["protect", "unlock"], Ids([FileFormat.Zip], WheelGroup.Tools));
        Assert.Equal(["protect", "unlock"], Ids([FileFormat.Zip, FileFormat.Pdf], WheelGroup.Tools));
        Assert.Equal(["compress", "crop", "annotate", "redact", "organize", "strip-metadata", "ocr", "protect", "unlock", "merge"], Ids([FileFormat.Pdf, FileFormat.Pdf], WheelGroup.Tools));
        Assert.Equal(["ocr", "merge"], Ids([FileFormat.Heic, FileFormat.Pdf, FileFormat.Png], WheelGroup.Tools));
        Assert.Equal(["compress", "crop"], Ids([FileFormat.Pdf, FileFormat.Mp4], WheelGroup.Tools));
        Assert.Contains("merge", Ids([FileFormat.Mp4, FileFormat.Mov], WheelGroup.Tools));
        Assert.Contains("merge", Ids([FileFormat.Mp3, FileFormat.Wav], WheelGroup.Tools));
        Assert.Equal(["compress", "resize", "rotate", "crop", "remove-bg", "annotate", "redact", "trim", "speed", "frames", "organize", "strip-metadata", "ocr", "extract-audio", "mute", "make-gif", "protect", "unlock", "merge"], Ids([], WheelGroup.Tools));
    }

    [Fact]
    public void Video_gets_the_sound_and_picture_tools() =>
        Assert.Equal(["compress", "rotate", "crop", "trim", "speed", "frames", "extract-audio", "mute", "make-gif"], Ids([FileFormat.Mp4], WheelGroup.Tools));

    [Fact]
    public void A_flip_preset_turns_nothing()
    {
        var rotate = Shipped.Tool("rotate")!;

        Assert.Equal("""{"turn":90}""", rotate.ParametersFor(null)!.ToJsonString());
        Assert.Equal("""{"turn":0,"flip":"horizontal"}""", rotate.ParametersFor("mirror")!.ToJsonString());
    }

    [Fact]
    public void Fit_under_presets_show_only_for_video_and_pdf()
    {
        static string[] Choices(params FileFormat[] files) =>
            [.. Shipped.OptionsFor(files, WheelGroup.Tools).Single(o => o.Tool.Id == "compress").Choices.Select(c => c.Id)];

        Assert.Equal(["small", "balanced", "high", "10mb", "25mb"], Choices(FileFormat.Mp4, FileFormat.Pdf));
        Assert.Equal(["small", "balanced", "high"], Choices(FileFormat.Mp4, FileFormat.Jpg));
        Assert.Equal(["small", "balanced", "high"], Choices(FileFormat.FromExtension("mp3")));
        Assert.DoesNotContain("resize", Ids([FileFormat.Jpg, FileFormat.Mov], WheelGroup.Tools)); // no Resize for video (developer's call)
    }

    [Fact]
    public void A_preset_adds_its_parameters_to_the_tools()
    {
        var compress = Shipped.Tool("compress")!;

        Assert.Equal("""{"preset":"balanced"}""", compress.ParametersFor(null)!.ToJsonString());
        Assert.Equal("""{"preset":"balanced","targetMb":25}""", compress.ParametersFor("25mb")!.ToJsonString());
        Assert.Equal("""{"preset":"small"}""", compress.ParametersFor("small")!.ToJsonString());
    }

    [Fact]
    public void Heic_output_is_disabled_without_hevc()
    {
        var option = CapabilityMatrix.LoadDefault(_ => false).OptionsFor([FileFormat.Jpg], WheelGroup.Convert).Single(o => o.Tool.Id == "convert.heic");

        Assert.NotNull(option.DisabledReason);
    }

    [Fact]
    public void Row_names_the_engine()
    {
        Assert.Equal("media", Shipped.RowFor(FileFormat.Mov, "convert.mp4")?.Engine);
        Assert.Equal("media", Shipped.RowFor(FileFormat.Mp4, "convert.gif")?.Engine);
        Assert.Equal("imaging", Shipped.RowFor(FileFormat.Png, "convert.gif")?.Engine);
    }

    [Fact]
    public void Missing_requirement_disables_with_reason_and_link()
    {
        var json = """
            {
              "tools": [ { "id": "convert.heic", "label": "HEIC", "glyph": "x", "hint": "h", "wheel": "Convert" } ],
              "requirements": { "hevc": { "reason": "Needs the HEVC extension.", "fix": "ms-windows-store://pdp/?ProductId=X" } },
              "rows": [
                { "in": "jpg", "out": "heic", "tool": "convert", "engine": "imaging", "requires": ["hevc"] },
                { "in": "png", "out": "heic", "tool": "convert", "engine": "imaging", "beta": true }
              ]
            }
            """;

        var missing = Load(json, _ => false).OptionsFor([FileFormat.Jpg, FileFormat.Png], WheelGroup.Convert).Single();
        var present = Load(json, _ => true).OptionsFor([FileFormat.Jpg], WheelGroup.Convert).Single();

        Assert.Equal("Needs the HEVC extension.", missing.DisabledReason);
        Assert.Equal(new Uri("ms-windows-store://pdp/?ProductId=X"), missing.FixLink);
        Assert.True(missing.Beta);
        Assert.Null(present.DisabledReason);
        Assert.False(present.Beta);
    }

    [Theory]
    [InlineData("""{ "tools": [], "requirements": {}, "rows": [ { "in": "jpg", "out": "png", "tool": "convert", "engine": "imaging" } ] }""")]
    [InlineData("""{ "tools": [], "requirements": {}, "rows": [ { "in": "jpg", "tool": "x", "engine": "e", "requires": ["nope"] } ] }""")]
    public void Undeclared_tool_or_requirement_is_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => Load(json, _ => true));

    [Fact]
    public void Misspelt_field_is_rejected() =>
        Assert.ThrowsAny<JsonException>(() => Load("""{ "tools": [], "requirements": {}, "rows": [], "row": [] }""", _ => true));

    private static string[] Ids(FileFormat[] files, WheelGroup wheel = WheelGroup.Convert) => [.. Shipped.OptionsFor(files, wheel).Select(o => o.Tool.Id)];

    private static CapabilityMatrix Load(string json, Func<string, bool> isAvailable) =>
        CapabilityMatrix.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), isAvailable);
}

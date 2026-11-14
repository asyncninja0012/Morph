using System.Globalization;
using System.Text;

using DocumentFormat.OpenXml.Packaging;

using ImageMagick;

namespace Morph.Engines.Pdf;

/// <summary>
/// PDF → PPTX: one slide per page, the page as a picture (150 DPI JPEG) filling the slide, so it looks exactly like the PDF
/// but its text isn't editable. The slide size is the first page's; other page sizes are fitted and centred.
/// The parts are written as XML text: a blank master, layout and theme are all PowerPoint needs.
/// </summary>
internal static class PdfSlides
{
    private const double Dpi = 150;
    private const long EmuPerPoint = 12700;
    private const long MinEmu = 914400, MaxEmu = 51206400; // PowerPoint's slide size limits: 1 to 56 inches

    private const string Ns = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;
    private const string EmptyTree = """<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>""";

    /// <summary>The caller holds <see cref="Pdfium.Sync"/>.</summary>
    public static void Write(string input, string output, Action<double> progress, CancellationToken ct)
    {
        var name = Path.GetFileName(input);
        var (doc, pages) = PdfEngine.Open(input);
        try
        {
            using var package = PresentationDocument.Create(output, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
            var presentation = package.AddPresentationPart();
            var master = presentation.AddNewPart<SlideMasterPart>("rIdM");
            var layout = master.AddNewPart<SlideLayoutPart>("rIdL");
            layout.AddPart(master, "rIdM");
            var theme = master.AddNewPart<ThemePart>("rIdT");
            presentation.AddPart(theme, "rIdT");
            Feed(theme, Theme);
            Feed(master, $"""<p:sldMaster {Ns}><p:cSld><p:spTree>{EmptyTree}</p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rIdL"/></p:sldLayoutIdLst></p:sldMaster>""");
            Feed(layout, $"""<p:sldLayout {Ns}type="blank" preserve="1"><p:cSld name="Blank"><p:spTree>{EmptyTree}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>""");

            (long Cx, long Cy) slide = default;
            var ids = new StringBuilder();
            for (var p = 0; p < pages; p++)
            {
                ct.ThrowIfCancellationRequested();
                using var image = PdfEngine.RenderPage(doc, p, name, Dpi);
                var (w, h) = (image.Width * 72.0 / Dpi * EmuPerPoint, image.Height * 72.0 / Dpi * EmuPerPoint);
                if (p == 0)
                {
                    var scale = Math.Min(1, (double)MaxEmu / Math.Max(w, h));
                    scale = Math.Max(scale, (double)MinEmu / Math.Min(w * scale, h * scale)); // tiny pages are scaled up
                    slide = (Math.Clamp((long)(w * scale), MinEmu, MaxEmu), Math.Clamp((long)(h * scale), MinEmu, MaxEmu));
                }

                var fit = Math.Min(slide.Cx / w, slide.Cy / h);
                var (cx, cy) = ((long)(w * fit), (long)(h * fit));
                image.Quality = 90;
                var part = presentation.AddNewPart<SlidePart>($"rIdS{p + 1}");
                part.AddPart(layout, "rIdL");
                var picture = part.AddImagePart(ImagePartType.Jpeg, "rIdI");
                using (var stream = picture.GetStream(FileMode.Create))
                {
                    image.Write(stream, MagickFormat.Jpeg);
                }

                Feed(part, string.Create(CultureInfo.InvariantCulture,
                    $"""<p:sld {Ns}><p:cSld><p:spTree>{EmptyTree}<p:pic><p:nvPicPr><p:cNvPr id="2" name="Page {p + 1}"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rIdI"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="{(slide.Cx - cx) / 2}" y="{(slide.Cy - cy) / 2}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>"""));
                ids.Append(CultureInfo.InvariantCulture, $"""<p:sldId id="{256 + p}" r:id="rIdS{p + 1}"/>""");
                progress((p + 1.0) / pages);
            }

            Feed(presentation, string.Create(CultureInfo.InvariantCulture,
                $"""<p:presentation {Ns}><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdM"/></p:sldMasterIdLst><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx="{slide.Cx}" cy="{slide.Cy}"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>"""));
        }
        finally
        {
            Pdfium.FPDF_CloseDocument(doc);
        }
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = part.GetStream(FileMode.Create);
        stream.Write(Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + xml));
    }

    // A plain Office theme: PowerPoint refuses a master without one.
    private const string Theme = """<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Morph"><a:themeElements><a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme><a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>""";
}

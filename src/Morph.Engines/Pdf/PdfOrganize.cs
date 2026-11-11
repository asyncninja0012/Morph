using Morph.Core.Jobs;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Morph.Engines.Pdf;

/// <summary>
/// The page organizer (ED-5): copies the original's pages into one or more new PDFs in the editor's order, turned as asked
/// (name-organized.pdf, or name-part1.pdf, name-part2.pdf, ... when split). Pages are imported, so bookmarks, form fields and
/// links between pages don't carry over. Pages a shorter PDF doesn't have ("Same for all") are left out.
/// </summary>
internal static class PdfOrganize
{
    public static void Run(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var files = PageOrder.Parse(job.Parameters);
        string? skipped = null;
        var made = 0;
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            using var source = PdfCompressor.Open(input, PdfDocumentOpenMode.Import);
            var count = source.PageCount;
            var kept = files.Select(f => f.Where(p => p.Source <= count).ToList()).Where(f => f.Count > 0).ToList();
            if (kept.Count == 0)
            {
                skipped = $"{name} doesn't have those pages.";
                continue;
            }

            if (kept is [var all] && all.Count == count && all.Select((p, n) => p.Source == n + 1 && p.Rotate == 0).All(same => same))
            {
                skipped = $"{name} is already in that order.";
                continue;
            }

            for (var k = 0; k < kept.Count; k++)
            {
                using var output = new PdfDocument();
                foreach (var pick in kept[k])
                {
                    var page = output.AddPage(source.Pages[pick.Source - 1]);
                    page.Rotate = (((page.Rotate + pick.Rotate) % 360) + 360) % 360;
                }

                output.Save(namer.Reserve(input, ".pdf", kept.Count == 1 ? "-organized" : $"-part{k + 1}").Path);
            }

            made++;
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }

        if (made == 0)
        {
            throw new EngineException(skipped!);
        }
    }
}

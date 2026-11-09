using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using Morph.Core.Formats;
using Morph.Core.Jobs;

namespace Morph.Engines.Sheets;

/// <summary>
/// CSV ↔ XLSX (CV-2, task 2.6) with the Open XML SDK. CSV → XLSX: one sheet, numbers as numbers, everything else as text
/// (never as a formula). XLSX → CSV: one CSV per sheet with data ("book-Sales.csv"; a one-sheet book gives "book.csv"),
/// cells as Excel shows their value (formulas give their last result, dates come out as ISO dates).
/// Tool ids: convert.xlsx, convert.csv.
/// </summary>
// shortcut: CSV numbers are recognised in the invariant format only ("1234.5"); "1.234,5" and dates stay text.
public sealed partial class SheetEngine : IEngine
{
    private const long MaxBytes = 100L * 1024 * 1024;
    private const int MaxRows = 1_048_576; // Excel's limits
    private const int MaxColumns = 16_384;

    public string Id => "sheet";

    [GeneratedRegex(@"^-?(?:0|[1-9]\d{0,14})(?:\.\d{1,15})?$")]
    private static partial Regex Number();

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        for (var i = 0; i < job.Inputs.Count; i++)
        {
            var input = job.Inputs[i];
            var name = Path.GetFileName(input);
            if (new FileInfo(input).Length > MaxBytes)
            {
                throw new EngineException($"{name} is over {MaxBytes / 1024 / 1024} MB; Morph converts sheets up to that size.");
            }

            switch (job.ToolId)
            {
                case "convert.xlsx":
                    var rows = Csv.Parse(await TextFiles.ReadAsync(input, ct).ConfigureAwait(false));
                    if (rows.Count == 0 || rows.All(r => r.All(string.IsNullOrEmpty)))
                    {
                        throw new EngineException($"{name} has no data to convert.");
                    }

                    if (rows.Count > MaxRows || rows.Max(r => r.Count) > MaxColumns)
                    {
                        throw new EngineException($"{name} has more rows or columns than Excel allows ({MaxRows:N0} × {MaxColumns:N0}).");
                    }

                    WriteXlsx(rows, Path.GetFileNameWithoutExtension(input), namer.Reserve(input, ".xlsx").Path, ct);
                    break;
                case "convert.csv":
                    var sheets = ReadXlsx(input, ct);
                    var withData = sheets.Where(s => s.Rows.Count > 0).ToList();
                    if (withData.Count == 0)
                    {
                        throw new EngineException($"{name} has no data to convert.");
                    }

                    foreach (var (sheet, sheetRows) in withData)
                    {
                        var suffix = withData.Count == 1 ? "" : "-" + string.Concat(sheet.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                        // UTF-8 with a BOM: without one Excel reads the CSV in the PC's ANSI code page.
                        await File.WriteAllTextAsync(namer.Reserve(input, ".csv", suffix).Path, Csv.Write(sheetRows), new UTF8Encoding(true), ct)
                            .ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new EngineException("This conversion isn't available yet.", $"sheet: unsupported tool {job.ToolId}");
            }

            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    // Streamed with OpenXmlWriter, so a big CSV doesn't build a DOM. Text goes in as inline strings, which Excel never evaluates.
    internal static void WriteXlsx(List<List<string>> rows, string title, string output, CancellationToken ct)
    {
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(worksheet))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            for (var r = 0; r < rows.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                writer.WriteStartElement(new Row { RowIndex = (uint)(r + 1) });
                for (var c = 0; c < rows[r].Count; c++)
                {
                    var value = rows[r][c];
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    var reference = ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                    writer.WriteElement(Number().IsMatch(value)
                        ? new Cell { CellReference = reference, CellValue = new CellValue(value) }
                        : new Cell(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }))
                        {
                            CellReference = reference,
                            DataType = CellValues.InlineString,
                        });
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var sheetName = new string([.. title.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).Take(31)]).Trim('\'');
        workbook.Workbook = new Workbook(new DocumentFormat.OpenXml.Spreadsheet.Sheets(new Sheet
        {
            Id = workbook.GetIdOfPart(worksheet),
            SheetId = 1,
            Name = sheetName.Length > 0 ? sheetName : "Sheet1",
        }));
    }

    internal static List<(string Name, List<List<string>> Rows)> ReadXlsx(string input, CancellationToken ct)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(input, isEditable: false);
            var workbook = document.WorkbookPart ?? throw new InvalidDataException("no workbook part");
            var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
            var dateStyles = DateStyles(workbook);
            var book = workbook.Workbook ?? throw new InvalidDataException("no workbook");
            var date1904 = book.WorkbookProperties?.Date1904?.Value == true;

            var result = new List<(string, List<List<string>>)>();
            foreach (var sheet in book.Sheets?.Elements<Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
                {
                    continue; // a chart sheet
                }

                var rows = new List<List<string>>();
                using var reader = OpenXmlReader.Create(part);
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.ElementType != typeof(Cell) || !reader.IsStartElement)
                    {
                        continue;
                    }

                    var cell = (Cell)reader.LoadCurrentElement()!;
                    var (column, row) = Position(cell.CellReference?.Value, rows);
                    var value = Value(cell, strings, dateStyles, date1904);
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    while (rows.Count <= row)
                    {
                        rows.Add([]);
                    }

                    while (rows[row].Count <= column)
                    {
                        rows[row].Add("");
                    }

                    rows[row][column] = value;
                }

                result.Add((sheet.Name?.Value ?? "Sheet", rows));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EngineException)
        {
            throw new EngineException($"{Path.GetFileName(input)} is damaged or isn't an Excel workbook Morph can read.", ex.ToString());
        }
    }

    private static string Value(Cell cell, List<string> strings, HashSet<uint> dateStyles, bool date1904)
    {
        var raw = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < strings.Count ? strings[i] : "";
        }

        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? "";
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        if (type is null || type == CellValues.Number)
        {
            if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 0 and < 2958466)
            {
                var date = DateTime.FromOADate(serial + (date1904 ? 1462 : 0));
                return serial < 1 ? date.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return raw;
        }

        return raw; // str (formula text result), e (error like #DIV/0!), d (ISO date)
    }

    // Styles whose number format shows a date or time: Excel's built-in date ids, or a custom code with d/m/y/h/s outside
    // quotes and [brackets] ("[Red]0.00" is not a date, "dd/mm/yyyy" is).
    private static HashSet<uint> DateStyles(WorkbookPart workbook)
    {
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var custom = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null && LooksLikeDate(f.FormatCode?.Value ?? ""))
            .Select(f => f.NumberFormatId!.Value).ToHashSet() ?? [];
        var styles = new HashSet<uint>();
        var index = 0u;
        foreach (var format in stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.Contains(id))
            {
                styles.Add(index);
            }

            index++;
        }

        return styles;
    }

    private static bool LooksLikeDate(string code)
    {
        var bare = Regex.Replace(code, @"\[[^\]]*\]|""[^""]*""|\\.", "");
        return bare.Any(c => "dmyhsDMYHS".Contains(c));
    }

    // "BC12" → (column 54, row 11). A cell without a reference follows the previous one.
    private static (int Column, int Row) Position(string? reference, List<List<string>> rows)
    {
        if (string.IsNullOrEmpty(reference))
        {
            var last = Math.Max(rows.Count - 1, 0);
            return (rows.Count == 0 ? 0 : rows[last].Count, last);
        }

        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
        {
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }

        var row = int.Parse(reference.AsSpan(i), CultureInfo.InvariantCulture);
        if (column is < 1 or > MaxColumns || row is < 1 or > MaxRows)
        {
            throw new InvalidDataException($"cell reference {reference} is outside the sheet");
        }

        return (column - 1, row - 1);
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26)
        {
            name = (char)('A' + (index - 1) % 26) + name;
        }

        return name;
    }
}

/// <summary>RFC 4180 CSV: quoted fields may hold the delimiter, quotes ("") and line breaks.</summary>
internal static class Csv
{
    /// <param name="text">Line endings already "\n".</param>
    public static List<List<string>> Parse(string text)
    {
        var delimiter = Delimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    // Comma, semicolon (Excel in comma-decimal locales) or tab: whichever the first line has most of, outside quotes.
    private static char Delimiter(string text)
    {
        var line = text.AsSpan(0, text.IndexOf('\n') is var n and >= 0 ? n : text.Length);
        int comma = 0, semicolon = 0, tab = 0;
        var quoted = false;
        foreach (var c in line)
        {
            quoted ^= c == '"';
            if (!quoted)
            {
                comma += c == ',' ? 1 : 0;
                semicolon += c == ';' ? 1 : 0;
                tab += c == '\t' ? 1 : 0;
            }
        }

        return tab > comma && tab > semicolon ? '\t' : semicolon > comma ? ';' : ',';
    }

    public static string Write(List<List<string>> rows)
    {
        var s = new StringBuilder();
        foreach (var row in rows)
        {
            s.AppendJoin(',', row.Select(Field)).Append("\r\n");
        }

        return s.ToString();
    }

    // Text that Excel would run as a formula when the CSV is opened (=, +, -, @) gets a leading apostrophe (OWASP CSV
    // injection); plain negative numbers are left alone.
    private static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

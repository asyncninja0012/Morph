using Morph.Core.Formats;
using Morph.Core.Packs;

namespace Morph.Core.Capabilities;

/// <summary>
/// What turns office files into PDF on this PC (PK-1): the user's Microsoft Office app for the file's kind (COM),
/// else LibreOffice from the office pack. Shared by the App (which wedges to show, whether to offer the pack) and the
/// Worker (which one to run).
/// </summary>
public static class OfficeApps
{
    public const string PackId = "office";

    /// <summary>The Office app's COM ProgID for a kind of file, or null for kinds Office doesn't open.</summary>
    public static string? ProgId(FileKind kind) => kind switch
    {
        FileKind.Document => "Word.Application",
        FileKind.Spreadsheet => "Excel.Application",
        FileKind.Slides => "PowerPoint.Application",
        _ => null,
    };

    /// <summary>Whether Microsoft Office can open this format: it can't read Apple Pages.</summary>
    public static bool HasOfficeFor(FileFormat format) =>
        format != FileFormat.Pages && ProgId(format.Kind) is { } id && Type.GetTypeFromProgID(id) is not null;

    /// <summary>soffice.exe of the installed office pack (newest version folder), or null.</summary>
    public static string? LibreOffice(string packsRoot)
    {
        var pack = Path.Combine(packsRoot, PackId);
        return Directory.Exists(pack)
            ? Directory.EnumerateDirectories(pack).Where(d => !d.EndsWith(".tmp", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "program", "soffice.exe")).LastOrDefault(File.Exists)
            : null;
    }
}

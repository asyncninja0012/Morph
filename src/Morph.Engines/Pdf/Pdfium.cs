using System.Runtime.InteropServices;

namespace Morph.Engines.Pdf;

/// <summary>
/// The few PDFium calls Morph uses (PLAN §5). pdfium.dll (bblanchon/pdfium-binaries) is fetched by build/fetch-binaries.ps1.
/// Hand-written imports because CsWin32 only generates Win32 APIs. PDFium is not thread-safe: callers hold <see cref="Sync"/>.
/// </summary>
internal static partial class Pdfium
{
    private const string Dll = "pdfium";

    public const int ErrPassword = 4;
    public const int RenderAnnotations = 0x01;

    public static readonly Lock Sync = new();

    static Pdfium() => FPDF_InitLibrary();

    [LibraryImport(Dll)]
    private static partial void FPDF_InitLibrary();

    /// <remarks>PDFium takes the path as UTF-8.</remarks>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadDocument(string path, string? password);

    [LibraryImport(Dll)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Dll)]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Dll)]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Dll)]
    public static partial nint FPDF_LoadPage(nint document, int index);

    [LibraryImport(Dll)]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Dll)]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Dll)]
    public static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Dll)]
    public static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Dll)]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Dll)]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Dll)]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Dll)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);

    [LibraryImport(Dll)]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Dll)]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Dll)]
    public static partial int FPDFText_CountChars(nint textPage);

    /// <returns>Characters written to <paramref name="buffer"/>, including the terminating NUL.</returns>
    [LibraryImport(Dll)]
    public static unsafe partial int FPDFText_GetText(nint textPage, int start, int count, char* buffer);
}

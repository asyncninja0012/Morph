using System.Runtime.InteropServices;

using Morph.Core.Jobs;

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

    // Open documents' file handles and the read-callback struct PDFium keeps a pointer to, freed by FPDF_CloseDocument.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (GCHandle File, nint Access)> Open = [];

    /// <summary>
    /// Opens a PDF; 0 on failure (then <see cref="FPDF_GetLastError"/>). PDFium reads it through a callback on a .NET file handle:
    /// <c>FPDF_LoadDocument</c> opens the path with narrow Windows calls, which fail past MAX_PATH and on names the ANSI code page
    /// can't spell (task 5.3). Close with <see cref="FPDF_CloseDocument"/>.
    /// </summary>
    public static unsafe nint LoadDocument(string path)
    {
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(file);
        if (length > uint.MaxValue)
        {
            file.Dispose();
            throw new EngineException($"{Path.GetFileName(path)} is over 4 GB; Morph can't open a PDF that big.");
        }

        var handle = GCHandle.Alloc(file);
        var access = (FileAccessCallback*)NativeMemory.Alloc((nuint)sizeof(FileAccessCallback));
        *access = new FileAccessCallback { FileLength = (uint)length, GetBlock = &GetBlock, Param = GCHandle.ToIntPtr(handle) };
        var doc = FPDF_LoadCustomDocument(access, null);
        if (doc == 0)
        {
            Free(handle, (nint)access);
        }
        else
        {
            Open[doc] = (handle, (nint)access);
        }

        return doc;
    }

    public static void FPDF_CloseDocument(nint document)
    {
        CloseDocument(document);
        if (Open.TryRemove(document, out var opened))
        {
            Free(opened.File, opened.Access);
        }
    }

    private static unsafe void Free(GCHandle file, nint access)
    {
        ((Microsoft.Win32.SafeHandles.SafeFileHandle)file.Target!).Dispose();
        file.Free();
        NativeMemory.Free((void*)access);
    }

    // PDFium's FPDF_FILEACCESS (unsigned long is 32-bit on Windows).
    private unsafe struct FileAccessCallback
    {
        public uint FileLength;
        public delegate* unmanaged<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [UnmanagedCallersOnly]
    private static unsafe int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        try
        {
            var file = (Microsoft.Win32.SafeHandles.SafeFileHandle)GCHandle.FromIntPtr(param).Target!;
            var span = new Span<byte>(buffer, checked((int)size));
            for (var read = 0; read < span.Length;)
            {
                var n = RandomAccess.Read(file, span[read..], position + (long)read);
                if (n == 0)
                {
                    return 0; // past the end: PDFium treats the file as damaged
                }

                read += n;
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or ObjectDisposedException)
        {
            return 0; // an exception mustn't unwind into native code; PDFium reports a file error instead
        }
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint FPDF_LoadCustomDocument(FileAccessCallback* access, string? password);

    [LibraryImport(Dll)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Dll, EntryPoint = "FPDF_CloseDocument")]
    private static partial void CloseDocument(nint document);

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

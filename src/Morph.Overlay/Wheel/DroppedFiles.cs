using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;

using Windows.Win32;
using Windows.Win32.Foundation;

using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Morph.Overlay.Wheel;

/// <summary>What a drop carried. <paramref name="Virtual"/>: the files were copied out of the source (Outlook, Teams; D3).</summary>
public sealed record DroppedFiles(IReadOnlyList<string> Paths, bool Virtual);

/// <summary>Reads CF_HDROP and FileGroupDescriptorW/FileContents from an OLE data object (PLAN §3, D3).</summary>
internal static class DragFiles
{
    public const string DescriptorFormat = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";
    private const int DescriptorSize = 592; // sizeof(FILEDESCRIPTORW)
    private const int NameOffset = 72;
    private const uint FileAttributeDirectory = 0x10;

    /// <summary>Real paths, or the virtual file names (not yet on disk), or null when the drag carries no files.</summary>
    public static (IReadOnlyList<string> Names, bool Virtual)? Peek(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            return (paths, false);
        }

        if (data.GetDataPresent(DescriptorFormat) && data.GetData(DescriptorFormat) is MemoryStream descriptor)
        {
            return (ParseDescriptor(descriptor.ToArray()), true);
        }

        return null;
    }

    /// <summary>
    /// Parses FILEGROUPDESCRIPTORW into safe leaf file names; folders are skipped as null.
    /// Index i of the result is lindex i of FileContents.
    /// </summary>
    internal static IReadOnlyList<string?> ParseDescriptorEntries(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return [];
        }

        var count = (int)Math.Min(BitConverter.ToUInt32(bytes), (uint)((bytes.Length - 4) / DescriptorSize));
        var names = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.Slice(4 + i * DescriptorSize, DescriptorSize);
            var flagsHasAttributes = (BitConverter.ToUInt32(entry) & 0x4) != 0; // FD_ATTRIBUTES
            var attributes = BitConverter.ToUInt32(entry[36..]);
            var raw = MemoryMarshal.Cast<byte, char>(entry[NameOffset..]);
            var end = raw.IndexOf('\0');
            var name = new string(end < 0 ? raw : raw[..end]);
            names[i] = flagsHasAttributes && (attributes & FileAttributeDirectory) != 0 ? null : SafeName(name);
        }

        return names;
    }

    /// <summary>Untrusted name from another process → a leaf name that can't escape the folder or hit a device name.</summary>
    internal static string SafeName(string name)
    {
        var leaf = name.Replace('/', '\\');
        leaf = leaf[(leaf.LastIndexOf('\\') + 1)..];
        var sb = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
        {
            sb.Append(c < 32 || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        var safe = sb.ToString().Trim().TrimEnd('.');
        var stem = Path.GetFileNameWithoutExtension(safe).ToUpperInvariant();
        if (safe.Length == 0 || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3])))
        {
            safe = "attachment" + Path.GetExtension(safe);
        }

        return safe;
    }

    /// <summary>
    /// Copies each FileContents stream to %TEMP%\Morph\in\&lt;guid&gt;\. Must run inside Drop: the source may release
    /// the data once Drop returns.
    /// </summary>
    // shortcut: copies on the UI thread inside Drop, so a large attachment stalls the UI for the copy; move to
    // IDataObjectAsyncCapability when large attachments show up in testing.
    public static IReadOnlyList<string> ExtractVirtual(IDataObject data)
    {
        if (data.GetData(DescriptorFormat) is not MemoryStream descriptor || data is not ComDataObject com)
        {
            return [];
        }

        var names = ParseDescriptorEntries(descriptor.ToArray());
        var folder = Path.Combine(Path.GetTempPath(), "Morph", "in", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] is not { } name)
            {
                continue;
            }

            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
            }

            var path = Path.Combine(folder, unique);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                CopyContents(com, i, file);
            }

            paths.Add(path);
        }

        return paths;
    }

    private static IReadOnlyList<string> ParseDescriptor(byte[] bytes) =>
        [.. ParseDescriptorEntries(bytes).OfType<string>()];

    private static unsafe void CopyContents(ComDataObject data, int index, Stream destination)
    {
        var format = new FORMATETC
        {
            cfFormat = (short)DataFormats.GetDataFormat(ContentsFormat).Id,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try
                {
                    var buffer = new byte[81920];
                    int read;
                    do
                    {
                        stream.Read(buffer, buffer.Length, (nint)(&read));
                        destination.Write(buffer, 0, read);
                    }
                    while (read > 0);
                }
                finally
                {
                    Marshal.ReleaseComObject(stream);
                }
            }
            else if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                var global = new HGLOBAL((void*)medium.unionmember);
                var size = (int)PInvoke.GlobalSize(global);
                var ptr = PInvoke.GlobalLock(global);
                try
                {
                    destination.Write(new ReadOnlySpan<byte>(ptr, size));
                }
                finally
                {
                    PInvoke.GlobalUnlock(global);
                }
            }
            else
            {
                throw new InvalidOperationException($"FileContents {index} came as {medium.tymed}, which Morph can't read.");
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    // Hand-written: CsWin32 generates its own STGMEDIUM, not the ComTypes one that WPF's data object returns.
    // DllImport, not LibraryImport: STGMEDIUM holds an object reference, which source-generated marshalling rejects.
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

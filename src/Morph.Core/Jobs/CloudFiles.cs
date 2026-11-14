using System.Runtime.CompilerServices;

using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace Morph.Core.Jobs;

/// <summary>
/// OneDrive (and other cloud) placeholders (task 5.3): a cloud-only file is downloaded before an engine reads it, so a failed
/// download says so plainly instead of surfacing as a damaged file from PDFium or ffmpeg.
/// </summary>
public static class CloudFiles
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: reading the data fetches it from the cloud. .NET's FileAttributes has no name for it.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static bool IsCloudOnly(string path) => (File.GetAttributes(path) & (RecallOnDataAccess | FileAttributes.Offline)) != 0;

    /// <summary>Downloads <paramref name="path"/> if it's cloud-only; blocks until it's on disk.</summary>
    public static void Hydrate(string path)
    {
        if (!IsCloudOnly(path))
        {
            return;
        }

        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // No OVERLAPPED: synchronous (CF_EOF = -1, the whole file).
        var hr = PInvoke.CfHydratePlaceholder(file, 0, -1, CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE, ref Unsafe.NullRef<NativeOverlapped>());
        if (hr.Failed)
        {
            throw new EngineException($"{Path.GetFileName(path)} is only in the cloud and Morph couldn't download it. Check that you're online and OneDrive is running, or right-click it and choose \"Always keep on this device\".",
                $"CfHydratePlaceholder 0x{hr.Value:X8}: {path}");
        }
    }
}

using System.ComponentModel;
using System.IO;

using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Morph.App.Tray;

/// <summary>
/// Undo (JB-5) sends a job's outputs to the Recycle Bin. SHFileOperation through CsWin32: the documented
/// FileSystem.DeleteFile(..., SendToRecycleBin) lives in WinForms' VisualBasic assembly (~13 MB, PLAN R3).
/// </summary>
internal static class RecycleBin
{
    /// <summary>Recycles the files that still exist; does file I/O, so never call it on the UI thread.</summary>
    /// <exception cref="Win32Exception">The shell refused or the user cancelled.</exception>
    public static void Send(IReadOnlyList<string> files)
    {
        var existing = files.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0)
        {
            return;
        }

        // pFrom is a list of NUL-terminated paths ended by an extra NUL.
        var from = string.Join('\0', existing) + "\0\0";
        unsafe
        {
            fixed (char* p = from)
            {
                var op = new SHFILEOPSTRUCTW
                {
                    wFunc = PInvoke.FO_DELETE,
                    pFrom = p,
                    // No prompt unless the file can't be recycled (too big, or a drive without a Recycle Bin): then Windows asks
                    // before deleting it for good.
                    fFlags = (ushort)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION | FILEOPERATION_FLAGS.FOF_SILENT
                        | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING),
                };
                var error = PInvoke.SHFileOperation(ref op);
                if (error != 0 || op.fAnyOperationsAborted)
                {
                    throw new Win32Exception(error, error != 0 ? $"SHFileOperation failed with 0x{error:X}" : "Cancelled");
                }
            }
        }
    }
}

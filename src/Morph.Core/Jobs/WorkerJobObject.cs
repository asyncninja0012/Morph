using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Morph.Core.Jobs;

/// <summary>
/// The sandbox around one Worker (PLAN §5): memory cap per process, below-normal priority, a few processes at most,
/// and every process in it (Worker, ffmpeg) dies when this is disposed or the App exits.
/// </summary>
internal sealed class WorkerJobObject : IDisposable
{
    private const uint BelowNormalPriorityClass = 0x4000;
    private readonly SafeFileHandle _handle;

    public unsafe WorkerJobObject(ulong processMemoryLimit)
    {
        _handle = PInvoke.CreateJobObject(null, null);
        if (_handle.IsInvalid)
        {
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION, // no WER dialog for a crashing decoder
                ActiveProcessLimit = 4,
                PriorityClass = BelowNormalPriorityClass,
            },
            ProcessMemoryLimit = (nuint)processMemoryLimit,
        };
        if (!PInvoke.SetInformationJobObject((HANDLE)_handle.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            var error = Marshal.GetLastPInvokeErrorMessage();
            _handle.Dispose();
            throw new InvalidOperationException($"SetInformationJobObject failed: {error}");
        }
    }

    /// <summary>Call before the process can start children; children it starts later join automatically.</summary>
    public void Assign(Process process)
    {
        if (!PInvoke.AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    /// <summary>Kills everything in the job.</summary>
    public void Dispose() => _handle.Dispose();
}

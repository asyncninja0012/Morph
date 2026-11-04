using System.Diagnostics;

using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class WorkerJobObjectTests
{
    [Fact]
    public void Disposing_the_job_kills_the_process()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var job = new WorkerJobObject(512UL << 20);
        job.Assign(process);
        Thread.Sleep(300);
        Assert.False(process.HasExited); // ping -n 60 runs for about a minute on its own

        job.Dispose();

        Assert.True(process.WaitForExit(5000), "process still running after the job closed");
    }
}

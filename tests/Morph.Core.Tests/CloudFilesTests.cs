using Morph.Core.Jobs;

namespace Morph.Core.Tests;

public sealed class CloudFilesTests
{
    // A placeholder needs a running sync provider, so only the local case is testable here; the Worker hydrates before any engine reads.
    [Fact]
    public void A_local_file_is_left_alone()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.False(CloudFiles.IsCloudOnly(path));
            CloudFiles.Hydrate(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

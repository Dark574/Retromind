using Retromind.Services.Downloads;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class LegendaryDownloadProgressTests
{
    [Fact]
    public void Read_UsesOnlyNumericRawTransferMetrics()
    {
        var events = new List<DownloadProgress>();
        var parser = new LegendaryDownloadProgress(events.Add);
        parser.Read("[Core] INFO: Download size: 32.00 MiB");
        parser.Read("[DLM] INFO: = Progress: 12.50% (1/8)");
        parser.Read("[DLM] INFO: - Downloaded: 4.00 MiB, Written: 12.00 MiB");
        parser.Read("[DLM] INFO: + Download\t- 3.00 MiB/s (raw) / 10.00 MiB/s (decompressed)");
        parser.Read("https://example.test?secret=private");

        Assert.Equal(4, events.Count);
        Assert.Equal(new DownloadProgress(4 * 1048576, 32 * 1048576, 3 * 1048576, 0.125), events[^1]);
    }
}

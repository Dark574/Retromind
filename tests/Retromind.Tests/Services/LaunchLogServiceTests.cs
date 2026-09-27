using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LaunchLogServiceTests
{
    [Fact]
    public async Task TryDeleteForSubtree_RemovesNestedItemLogsAndKeepsUnrelatedLogs()
    {
        using var temp = new TemporaryDirectory();
        var service = new LaunchLogService(temp.GetPath("launch-logs"));
        var directItem = new MediaItem("Direct item");
        var nestedItem = new MediaItem("Nested item");
        var unrelatedItem = new MediaItem("Unrelated item");
        var subtree = new MediaNode
        {
            Name = "Deleted subtree",
            Items = [directItem],
            Children =
            [
                new MediaNode
                {
                    Name = "Child",
                    Items = [nestedItem]
                }
            ]
        };

        await service.TryWriteAsync(directItem.Id, "direct");
        await service.TryWriteAsync(nestedItem.Id, "nested");
        await service.TryWriteAsync(unrelatedItem.Id, "unrelated");

        service.TryDeleteForSubtree(subtree);

        Assert.False(service.HasLog(directItem.Id));
        Assert.False(service.HasLog(nestedItem.Id));
        Assert.True(service.HasLog(unrelatedItem.Id));
    }
}

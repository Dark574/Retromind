using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class LinuxFileSystemHelperTests
{
    [Fact]
    public void EnsureExecutableBitBestEffort_AddsExecuteBitsWithoutRemovingExistingMode()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("start.sh");
        var originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(filePath, originalMode);

        LinuxFileSystemHelper.EnsureExecutableBitBestEffort(filePath);

        var mode = File.GetUnixFileMode(filePath);
        Assert.Equal(originalMode, mode & originalMode);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
    }
}

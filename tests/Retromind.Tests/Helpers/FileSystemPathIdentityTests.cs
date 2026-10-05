using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class FileSystemPathIdentityTests
{
    [Fact]
    public void Comparison_FollowsHostFileSystemContract()
    {
        var upperCasePath = Path.Combine(Path.GetTempPath(), "Library", "Game.exe");
        var lowerCasePath = Path.Combine(Path.GetTempPath(), "library", "game.exe");

        Assert.Equal(OperatingSystem.IsWindows(),
            FileSystemPathIdentity.Equals(upperCasePath, lowerCasePath));
    }
}

using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallPayloadTrackerTests
{
    [Fact]
    public void Capture_IgnoresInstallerAndMojoMetadataButDetectsGamePayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("game");
        var baseline = GogInstallPayloadTracker.Capture(installPath);

        temporaryDirectory.CreateFile("game/.retromind-gog-installers/setup.bin");
        temporaryDirectory.CreateFile("game/.mojosetup/manifest");

        Assert.False(GogInstallPayloadTracker.HasChanged(installPath, baseline));

        temporaryDirectory.CreateFile("game/bin/game-data.bin");

        Assert.True(GogInstallPayloadTracker.HasChanged(installPath, baseline));
    }
}

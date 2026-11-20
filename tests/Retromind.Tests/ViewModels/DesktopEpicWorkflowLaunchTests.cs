using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopEpicWorkflowLaunchTests
{
    [Fact]
    public void MergeEpicLaunchEnvironment_PreservesEpicValuesWithoutRetromindOverride()
    {
        var merged = DesktopEpicWorkflow.MergeEpicLaunchEnvironment(
            effectiveEnvironment: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EpicApp"] = "ExampleGame"
            });

        Assert.NotNull(merged);
        Assert.Equal("ExampleGame", merged["EpicApp"]);
    }

    [Fact]
    public void MergeEpicLaunchEnvironment_PrefersExplicitRetromindOverride()
    {
        var merged = DesktopEpicWorkflow.MergeEpicLaunchEnvironment(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LANG"] = "en_US.UTF-8",
                ["DXVK_HUD"] = "fps"
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LANG"] = "de_DE.UTF-8",
                ["EpicApp"] = "ExampleGame"
            });

        Assert.NotNull(merged);
        Assert.Equal("en_US.UTF-8", merged["LANG"]);
        Assert.Equal("fps", merged["DXVK_HUD"]);
        Assert.Equal("ExampleGame", merged["EpicApp"]);
    }
}

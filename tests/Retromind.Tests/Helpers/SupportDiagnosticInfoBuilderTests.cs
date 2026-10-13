using System.Runtime.InteropServices;
using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class SupportDiagnosticInfoBuilderTests
{
    [Fact]
    public void Build_ContainsSupportEnvironmentWithoutPersonalPaths()
    {
        var result = SupportDiagnosticInfoBuilder.Build(
            "0.2.2-alpha",
            ".NET 10.0",
            "Test Linux",
            Architecture.X64,
            Architecture.X64,
            "KDE",
            "wayland",
            isWaylandRequested: true,
            isAppImage: true,
            portableHomeEnabled: true);

        Assert.Contains("Retromind: 0.2.2-alpha", result);
        Assert.Contains("Runtime: .NET 10.0", result);
        Assert.Contains("Operating system: Test Linux", result);
        Assert.Contains("Desktop: KDE", result);
        Assert.Contains("Session type: wayland", result);
        Assert.Contains("Avalonia backend: Wayland", result);
        Assert.Contains("Packaging: AppImage", result);
        Assert.Contains("Portable AppImage HOME: enabled", result);
        Assert.DoesNotContain("/home/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesSafeFallbacksAndKeepsEachValueOnOneLine()
    {
        var result = SupportDiagnosticInfoBuilder.Build(
            "test\nversion",
            ".NET 10.0",
            "Test Linux",
            Architecture.Arm64,
            Architecture.Arm64,
            null,
            "wayland\ninjected",
            isWaylandRequested: false,
            isAppImage: false,
            portableHomeEnabled: false);

        Assert.Contains("Retromind: test version", result);
        Assert.Contains("Desktop: unavailable", result);
        Assert.Contains("Session type: wayland injected", result);
        Assert.Contains("Avalonia backend: X11", result);
        Assert.Contains("Packaging: Other", result);
        Assert.Contains("Portable AppImage HOME: disabled", result);
    }
}

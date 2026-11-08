using System.Runtime.CompilerServices;
using Avalonia.Skia;

namespace Retromind.Tests.TestInfrastructure;

internal static class AvaloniaRenderingSetup
{
    // Existing file-management/VM tests can touch AsyncImageHelper before the image tests run.
    // Set up the real CPU renderer before any test can initialize its shared placeholder bitmap.
    [ModuleInitializer]
    internal static void Initialize()
    {
        SkiaPlatform.Initialize();
        AvaloniaTestDispatcher.EnsureStarted();
    }
}

using System;
using System.Linq;
using Avalonia;
using LibVLCSharp.Shared;
using Retromind.Helpers;

namespace Retromind;

internal sealed class Program
{
    // NOTE:
    // Do not touch Avalonia / UI APIs before AppMain is called. Things aren't initialized yet.

    [STAThread]
    public static void Main(string[] args)
    {
        bool isBigModeOnly = args.Contains("--bigmode");
        bool useWayland = ConfigureLinuxDisplayBackend(args);

        // AppImage portability: redirect XDG dirs into a local "Home" folder.
        // Safe to call before Avalonia initialization.
        PortableEnvironment.ApplyPortableXdgPaths();

        // VLC is REQUIRED for this build.
        try
        {
            Core.Initialize(); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VLC Init Failed: {ex.Message}");
            Environment.Exit(1);
        }
        
        BuildAvaloniaApp(isBigModeOnly, useWayland)
            .StartWithClassicDesktopLifetime(args);
    }

    private static bool ConfigureLinuxDisplayBackend(string[] args)
    {
        var platformArg = args.FirstOrDefault(a =>
            a.StartsWith("--avalonia-platform=", StringComparison.OrdinalIgnoreCase));
        var platformValue = platformArg?.Split('=', 2).ElementAtOrDefault(1)?.Trim();
        var useWayland = string.Equals(platformValue, "wayland", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(platformValue) &&
            !useWayland &&
            !platformValue.Equals("x11", StringComparison.OrdinalIgnoreCase) &&
            !platformValue.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Startup] Unknown Avalonia platform '{platformValue}'. Using x11.");
        }
        else if (platformValue?.Equals("auto", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("[Startup] Native Wayland requires explicit opt-in. Using x11 for --avalonia-platform=auto.");
        }

        var selectedPlatform = useWayland ? "wayland" : "x11";
        Environment.SetEnvironmentVariable("AVALONIA_PLATFORM", selectedPlatform);

        if (useWayland)
        {
            Console.WriteLine("[Startup] Native Wayland requested; x11 remains the initialization fallback.");
        }

        return useWayland;
    }

    // Avalonia configuration, used by the application
    public static AppBuilder BuildAvaloniaApp(bool isBigModeOnly, bool useWayland = false)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsLinux() && useWayland)
            builder = builder.UseWaylandWithFallback();

        return builder
            .WithInterFont()
            // Only log errors (suppress most binding warnings like "Value is null")
            .LogToTrace(Avalonia.Logging.LogEventLevel.Error)
            .AfterSetup(builder => 
            {
                if (App.Current is App app)
                    app.IsBigModeOnly = isBigModeOnly;

            });
    }
    
    // Avalonia designer configuration, MUST be parameterless
    // The designer looks for a public static BuildAvaloniaApp() -> AppBuilder
    public static AppBuilder BuildAvaloniaApp()
    {
        // Designer: always start in "normal" mode (no --bigmode).
        return BuildAvaloniaApp(isBigModeOnly: false);
    }
}

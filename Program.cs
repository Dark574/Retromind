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

        // Linux: Wayland is intentionally disabled for now.
        // We force X11 for stable VLC embedding and embedded auth flows.
        if (OperatingSystem.IsLinux())
        {
            var platformArg = args.FirstOrDefault(a => a.StartsWith("--avalonia-platform=", StringComparison.OrdinalIgnoreCase));
            var platformValue = platformArg?.Split('=', 2).ElementAtOrDefault(1)?.Trim();
            if (!string.IsNullOrWhiteSpace(platformValue) &&
                !platformValue.Equals("x11", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Startup] --avalonia-platform={platformValue} is currently disabled. Forcing x11.");
            }

            Environment.SetEnvironmentVariable("AVALONIA_PLATFORM", "x11");
        }

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
        
        BuildAvaloniaApp(isBigModeOnly)
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, used by the application
    public static AppBuilder BuildAvaloniaApp(bool isBigModeOnly)
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
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

using System;
using Avalonia;
using LibVLCSharp.Shared;

namespace Retromind;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        bool isBigModeOnly = args.Contains("--bigmode");
        
        // 1. FIX FÜR WAYLAND:
        // Wir zwingen Avalonia, X11 (via XWayland) zu nutzen.
        // Das ermöglicht es VLC, das Video korrekt in das Fenster einzubetten.
        // Das MUSS vor "BuildAvaloniaApp" passieren!
        Environment.SetEnvironmentVariable("AVALONIA_PLATFORM", "x11");

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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp(bool isBigModeOnly)
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(builder => 
            {
                // NEU: Flag in App speichern
                if (App.Current is App app)
                {
                    app.IsBigModeOnly = isBigModeOnly;
                }
            });
    }
}
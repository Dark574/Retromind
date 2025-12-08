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
        // 1. FIX FÜR WAYLAND:
        // Wir zwingen Avalonia, X11 (via XWayland) zu nutzen.
        // Das ermöglicht es VLC, das Video korrekt in das Fenster einzubetten.
        // Das MUSS vor "BuildAvaloniaApp" passieren!
        Environment.SetEnvironmentVariable("AVALONIA_PLATFORM", "x11");

        // Initialisiere die VLC Core Engine VOR Avalonia
        try
        {
            Core.Initialize(); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VLC Init Failed: {ex.Message}");
            // App läuft trotzdem weiter, nur ohne Video
        }
        
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
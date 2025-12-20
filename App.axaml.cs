using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind;

public partial class App : Application
{
    /// <summary>
    /// Provides static access to the current App instance.
    /// </summary>
    public new static App? Current => Application.Current as App;

    /// <summary>
    /// Gets the <see cref="IServiceProvider"/> instance to resolve application services.
    /// </summary>
    public IServiceProvider? Services { get; private set; }
    
    /// <summary>
    /// Global flag indicating if the application should run in BigMode (UI optimized for controllers/TV).
    /// </summary>
    public bool IsBigModeOnly { get; set; } = false;
    
    public override void Initialize()
    {
        // For testing specific cultures, uncomment the following:
        // System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // Ensure portable themes are present in the user directory.
            AppPaths.EnsurePortableThemes();

            // Build a minimal container to load settings first (sync startup, async IO inside).
            var bootstrapServices = new ServiceCollection();
            bootstrapServices.AddSingleton<SettingsService>();
            using var bootstrapProvider = bootstrapServices.BuildServiceProvider();

            AppSettings settings;
            try
            {
                var settingsService = bootstrapProvider.GetRequiredService<SettingsService>();
                settings = Task.Run(settingsService.LoadAsync).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Warning: Failed to load settings. Using defaults. Error: {ex.Message}");
                settings = new AppSettings();
            }

            // Now build the final container exactly once (single Settings instance for the whole app).
            var services = new ServiceCollection();
            services.AddSingleton(settings);
            ConfigureServices(services);

            Services = services.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();

                // Check CLI arguments for BigMode override
                if (desktop.Args is { Length: > 0 } && desktop.Args.Any(a => a == "--bigmode"))
                {
                    IsBigModeOnly = true;
                    Debug.WriteLine("[App] CLI: --bigmode detected.");
                }

                var mainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };
                desktop.MainWindow = mainWindow;

                // Fire-and-forget data loading to keep the UI responsive.
                var dataLoadingTask = mainViewModel.LoadData();

                if (IsBigModeOnly)
                {
                    mainWindow.Loaded += async (_, _) =>
                    {
                        await dataLoadingTask;

                        if (mainViewModel.EnterBigModeCommand.CanExecute(null))
                            mainViewModel.EnterBigModeCommand.Execute(null);
                    };
                }

                // Ensure resources (like music playback) are cleaned up on exit
                desktop.Exit += (_, _) => { mainViewModel.Cleanup(); };
            }
        }
        catch (Exception ex)
        {
            // Catch critical startup errors that might otherwise be swallowed by the framework
            Debug.WriteLine($"[App] CRITICAL STARTUP ERROR: {ex}");
            throw;
        }
        
        base.OnFrameworkInitializationCompleted();
    }
    
    /// <summary>
    /// Configures the application's dependency injection container.
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // --- Infrastructure ---
        services.AddSingleton<HttpClient>(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Retromind/1.0 (Linux Portable Media Manager)");
            return client;
        });

        // --- Path-based Services (AppImage/Portable Support) ---
        var libraryPath = AppPaths.LibraryRoot;
        if (!Directory.Exists(libraryPath))
            Directory.CreateDirectory(libraryPath);

        // --- Core Services ---
        services.AddSingleton<AudioService>();
        services.AddSingleton<SoundEffectService>();
        services.AddSingleton<MediaDataService>();
        services.AddSingleton<FileManagementService>(_ => new FileManagementService(libraryPath));
        services.AddSingleton<LauncherService>(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new LauncherService(libraryPath, settings);
        });
        services.AddSingleton<ImportService>();
        services.AddSingleton<StoreImportService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<MetadataService>();

        // --- ViewModels ---
        // MainWindowViewModel is the long-lived app coordinator -> singleton is safer.
        services.AddSingleton<MainWindowViewModel>();
    }
}
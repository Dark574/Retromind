using System;
using System.Diagnostics;
using System.IO;
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
            
            // 1. Load Application Settings
            // Offload async loading to prevent UI deadlocks during synchronous startup.
            AppSettings settings;
            try
            {
                settings = Task.Run(async () => await new SettingsService().LoadAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Warning: Failed to load settings. Using defaults. Error: {ex.Message}");
                settings = new AppSettings();
            }

            // 2. Setup Dependency Injection
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(settings);
            ConfigureServices(serviceCollection);

            Services = serviceCollection.BuildServiceProvider();

            // 3. UI Initialization
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Resolve the MainViewModel. 
                // The DI container automatically injects all required services (Audio, Data, Metadata, etc.).
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                
                // Check CLI arguments for BigMode override
                if (desktop.Args?.Contains("--bigmode") == true)
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
                // For 30,000+ items, this is crucial.
                var dataLoadingTask = mainViewModel.LoadData();
                
                if (IsBigModeOnly)
                {
                    mainWindow.Loaded += async (sender, args) =>
                    {
                        // Wait for the initial data scan to finish before switching view
                        await dataLoadingTask;
                        
                        if (mainViewModel.EnterBigModeCommand.CanExecute(null))
                        {
                            mainViewModel.EnterBigModeCommand.Execute(null);
                        }
                    };
                }

                // Ensure resources (like music playback) are cleaned up on exit
                desktop.Exit += (sender, args) => { mainViewModel.Cleanup(); };
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
        
        // --- Core Services ---
        services.AddSingleton<AudioService>();
        services.AddSingleton<SoundEffectService>();
        services.AddSingleton<MediaDataService>();
        
        // --- Path-based Services (AppImage/Portable Support) ---
        string libraryPath = AppPaths.LibraryRoot;
        
        if (!Directory.Exists(libraryPath))
        {
            Directory.CreateDirectory(libraryPath);
        }

        services.AddSingleton<FileManagementService>(provider => new FileManagementService(libraryPath));
        services.AddSingleton<LauncherService>(provider => new LauncherService(libraryPath));
        services.AddSingleton<ImportService>();
        services.AddSingleton<StoreImportService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<MetadataService>();

        // --- ViewModels ---
        // Registered as Transient to allow fresh instances if needed (though MainVM is usually long-lived)
        services.AddTransient<MainWindowViewModel>();
    }
}
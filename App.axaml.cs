using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
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
    
    public override void Initialize()
    {
        // for tests in a specific language, change culture info here
        // Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // 1. Load Settings safely
            // We use Task.Run to offload the async loading to a ThreadPool thread,
            // avoiding a deadlock with the UI thread during the synchronous startup phase.
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

            serviceCollection.AddSingleton(settings); // Register the pre-loaded settings
            ConfigureServices(serviceCollection);

            Services = serviceCollection.BuildServiceProvider();

            // 3. Start the UI
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Resolve the MainViewModel. 
                // // The DI container automatically injects all required services (Audio, Data, Metadata, etc.).
                var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
                
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainViewModel
                };

                // Fire-and-forget data loading to keep the UI responsive during startup
                _ = mainViewModel.LoadData();
                
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
    /// Configures the DI container services.
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // --- Infrastructure ---
        // // Register HttpClient as Singleton to prevent socket exhaustion (replaces static instances)
        services.AddSingleton<HttpClient>();
        
        // --- Core Services (Singletons) ---
        services.AddSingleton<AudioService>();
        services.AddSingleton<MediaDataService>();
        services.AddSingleton<FileManagementService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<LauncherService>();
        services.AddSingleton<StoreImportService>();
        services.AddSingleton<SettingsService>();
        
        // MetadataService (automatically receives HttpClient and AppSettings via DI)
        services.AddSingleton<MetadataService>();

        // --- ViewModels ---
        // Registered as Transient to allow fresh instances if needed (though MainVM is usually long-lived)
        services.AddTransient<MainWindowViewModel>();
    }
}
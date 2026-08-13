using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    private bool CanSave()
    {
        // Prevent persisting half-configured scraper entries.
        return Scrapers.All(s => s.Type != ScraperType.None);
    }

    private void RefreshHintProperties()
    {
        OnPropertyChanged(nameof(IsTmdbSelected));
        OnPropertyChanged(nameof(IsIgdbSelected));
        OnPropertyChanged(nameof(IsEmuMoviesSelected));
        OnPropertyChanged(nameof(IsTheGamesDbSelected));
        OnPropertyChanged(nameof(IsSteamGridDbSelected));
        OnPropertyChanged(nameof(IsGoogleBooksSelected));
        OnPropertyChanged(nameof(IsComicVineSelected));
        OnPropertyChanged(nameof(IsApiKeyUsedSelected));
        OnPropertyChanged(nameof(IsApiKeyRequiredSelected));
        OnPropertyChanged(nameof(IsLanguageSelectionSupported));
    }

    // --- Emulator wrapper editor actions ---

    private void AddEmulatorWrapper()
    {
        EmulatorNativeWrappers.Add(new LaunchWrapperRow());
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void RemoveEmulatorWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;
        EmulatorNativeWrappers.Remove(row);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveEmulatorWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;

        var idx = EmulatorNativeWrappers.IndexOf(row);
        if (idx <= 0) return;

        EmulatorNativeWrappers.Move(idx, idx - 1);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveEmulatorWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;

        var idx = EmulatorNativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= EmulatorNativeWrappers.Count - 1) return;

        EmulatorNativeWrappers.Move(idx, idx + 1);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }
    
    // --- Emulator env-var editor actions ---

    private void AddEmulatorEnvVar()
    {
        EmulatorEnvironmentOverrides.Add(new EnvVarRow());
    }

    private void RemoveEmulatorEnvVar(EnvVarRow? row)
    {
        if (row == null) return;
        EmulatorEnvironmentOverrides.Remove(row);
    }

    private void ApplyPortableXdgPreset()
    {
        ApplyPortableOverrides(includeHome: false);
    }

    private void ApplyPortableXdgAndHomePreset()
    {
        ApplyPortableOverrides(includeHome: true);
    }

    private void ApplyPortableOverrides(bool includeHome)
    {
        if (SelectedEmulator == null)
            return;

        SelectedEmulator.XdgMode = EmulatorConfig.XdgOverrideMode.Custom;
        SelectedEmulator.XdgConfigPath = "Home/.config";
        SelectedEmulator.XdgDataPath = "Home/.local/share";
        SelectedEmulator.XdgCachePath = "Home/.cache";
        SelectedEmulator.XdgStatePath = "Home/.local/state";

        if (includeHome)
            UpsertEmulatorEnvVar("HOME", "Home");
    }

    private void UpsertEmulatorEnvVar(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var existing = EmulatorEnvironmentOverrides
            .FirstOrDefault(row => string.Equals(row.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        EmulatorEnvironmentOverrides.Add(new EnvVarRow
        {
            Key = key,
            Value = value
        });
    }

    // --- Actions ---

    private void AddEmulator()
    {
        var newEmu = new EmulatorConfig { Name = Strings.Profile_New };
        newEmu.PropertyChanged += OnAnyEmulatorPropertyChanged;
        Emulators.Add(newEmu);
        SelectedEmulator = newEmu; 
    }

    private void RemoveEmulator()
    {
        if (SelectedEmulator != null)
        {
            SelectedEmulator.PropertyChanged -= OnAnyEmulatorPropertyChanged;
            Emulators.Remove(SelectedEmulator);
            SelectedEmulator = null;
            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
        }
    }

    private void AddScraper()
    {
        var newScraper = new ScraperConfig
        {
            // Start unconfigured; user picks the provider manually.
            Type = ScraperType.None
        };
        
        Scrapers.Add(newScraper);
        SelectedScraper = newScraper;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RemoveScraper()
    {
        if (SelectedScraper != null)
        {
            // Unsubscribe from event to prevent leaks
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;
            
            Scrapers.Remove(SelectedScraper);
            SelectedScraper = null;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }
    
    private async Task ConvertExistingToPortableAsync()
    {
        // Forward the request to whoever is listening (typically MainWindowViewModel)
        if (RequestPortableMigration != null)
        {
            try
            {
                await RequestPortableMigration.Invoke();
            }
            catch
            {
                // Best-effort: migration errors are handled by the subscriber
            }
        }
    }

    private async Task ChangeParentalPasswordAsync()
    {
        if (RequestParentalPasswordChange == null)
            return;

        try
        {
            await RequestParentalPasswordChange.Invoke();
        }
        catch
        {
            // best-effort: caller owns error handling/UI feedback
        }
    }
    
    private void Save()
    {
        if (!CanSave())
            return;

        EnsureRunnerVersionIds();

        // Persist emulator wrapper & env configuration from UI into the selected emulator model
        if (SelectedEmulator != null)
        {
            if (UseGlobalWrapperDefaults)
            {
                SelectedEmulator.NativeWrapperMode = EmulatorConfig.WrapperMode.Inherit;
                SelectedEmulator.NativeWrappersOverride = null;
            }
            else
            {
                var wrappers = EmulatorNativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

                if (PreferPortableLaunchPaths)
                    PortablePathHelper.ConvertWrapperPathsToPortable(wrappers);

                SelectedEmulator.NativeWrappersOverride = wrappers;
                SelectedEmulator.NativeWrapperMode = wrappers.Count == 0
                    ? EmulatorConfig.WrapperMode.None
                    : EmulatorConfig.WrapperMode.Override;
            }
            
            // Sync environment overrides back into the model dictionary
            SelectedEmulator.EnvironmentOverrides.Clear();
            foreach (var row in EmulatorEnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(row.Key))
                    continue;

                SelectedEmulator.EnvironmentOverrides[row.Key.Trim()] = row.Value ?? string.Empty;
            }
        }

        if (PreferPortableLaunchPaths)
        {
            foreach (var emulator in Emulators)
            {
                if (emulator == null)
                    continue;

                ConvertEmulatorPathsToPortable(emulator);
            }

            PortablePathHelper.ConvertWrapperPathsToPortable(_appSettings.DefaultNativeWrappers);

            foreach (var runner in RunnerVersions)
            {
                runner.Path = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(runner.Path) ?? runner.Path;
            }
        }
        
        // Complete the working model first, then copy only the settings owned by
        // this dialog into the shared runtime settings object.
        _appSettings.Emulators = Emulators.ToList();
        _appSettings.Scrapers = Scrapers.ToList();
        _appSettings.RunnerVersions = RunnerVersions.Select(r => r.ToModel()).ToList();
        _appSettings.SteamLibraryPaths = SteamLibraryPaths.ToList();
        _appSettings.HeroicEpicConfigPaths = HeroicEpicConfigPaths.ToList();

        ApplyWorkingCopyToTarget();
        IsSaved = true;
        RequestClose?.Invoke();
    }

    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    private void ApplyWorkingCopyToTarget()
    {
        // Clone again so the closed dialog cannot retain mutable objects that are
        // now part of the application's active settings.
        var committed = CreateWorkingCopy(_appSettings);

        _targetSettings.PreferPortableLaunchPaths = committed.PreferPortableLaunchPaths;
        _targetSettings.UsePortableHomeInAppImage = committed.UsePortableHomeInAppImage;
        _targetSettings.ForcePortableHomeInAppImage = committed.ForcePortableHomeInAppImage;
        _targetSettings.EnableSelectionMusicPreview = committed.EnableSelectionMusicPreview;
        _targetSettings.IgnoreLeadingArticlesInSort = committed.IgnoreLeadingArticlesInSort;
        _targetSettings.DefaultNativeWrappers = committed.DefaultNativeWrappers;
        _targetSettings.Emulators = committed.Emulators;
        _targetSettings.Scrapers = committed.Scrapers;
        _targetSettings.RunnerVersions = committed.RunnerVersions;
        _targetSettings.SteamLibraryPaths = committed.SteamLibraryPaths;
        _targetSettings.HeroicEpicConfigPaths = committed.HeroicEpicConfigPaths;
        _targetSettings.ScraperImport = committed.ScraperImport;
    }

    private void EnsureRunnerVersionIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runner in RunnerVersions)
        {
            var id = runner.Id?.Trim();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                id = Guid.NewGuid().ToString();
                runner.Id = id;
                seen.Add(id);
            }
            else if (!string.Equals(runner.Id, id, StringComparison.Ordinal))
            {
                runner.Id = id;
            }
        }
    }

    private async Task BrowsePathAsync()
    {
        if (SelectedEmulator == null) return;

        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Emulator Executable",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0)
        {
            var path = result[0].Path.LocalPath;
            if (PreferPortableLaunchPaths &&
                PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
            {
                SelectedEmulator.Path = relativePath;
            }
            else
            {
                SelectedEmulator.Path = path;
            }
        }
    }

    private static void ConvertEmulatorPathsToPortable(EmulatorConfig emulator)
    {
        emulator.Path = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(emulator.Path) ?? emulator.Path;
        emulator.XdgConfigPath = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(emulator.XdgConfigPath);
        emulator.XdgDataPath = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(emulator.XdgDataPath);
        emulator.XdgCachePath = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(emulator.XdgCachePath);
        emulator.XdgStatePath = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(emulator.XdgStatePath);
        PortablePathHelper.ConvertWrapperPathsToPortable(emulator.NativeWrappersOverride);

        if (emulator.EnvironmentOverrides is not { Count: > 0 })
            return;

        var keys = emulator.EnvironmentOverrides.Keys.ToList();
        foreach (var key in keys)
        {
            if (!EnvironmentPathHelper.IsDataRootPathKey(key))
                continue;

            if (!emulator.EnvironmentOverrides.TryGetValue(key, out var rawValue))
                continue;

            var converted = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(rawValue);
            if (!string.IsNullOrWhiteSpace(converted))
                emulator.EnvironmentOverrides[key] = converted;
        }
    }

    private async Task BrowseSteamLibraryPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectSteamLibraryFolder,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0) return;

        AddSteamLibraryPath(result[0].Path.LocalPath);
    }

    private async Task BrowseHeroicEpicPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectHeroicEpicFolder,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0) return;

        AddHeroicEpicPath(result[0].Path.LocalPath);
    }

    private void AddSteamLibraryPath()
    {
        AddSteamLibraryPath(SteamLibraryPathInput);
    }

    private void AddSteamLibraryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim();
        var normalized = NormalizePathSafe(trimmed);

        if (SteamLibraryPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SteamLibraryPathInput = string.Empty;
            return;
        }

        SteamLibraryPaths.Add(normalized);
        SteamLibraryPathInput = string.Empty;
    }

    private void RemoveSteamLibraryPath()
    {
        if (SelectedSteamLibraryPath == null) return;
        SteamLibraryPaths.Remove(SelectedSteamLibraryPath);
        SelectedSteamLibraryPath = null;
    }

    private void AddHeroicEpicPath()
    {
        AddHeroicEpicPath(HeroicEpicPathInput);
    }

    private void AddHeroicEpicPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim();
        var normalized = NormalizePathSafe(trimmed);

        if (HeroicEpicConfigPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            HeroicEpicPathInput = string.Empty;
            return;
        }

        HeroicEpicConfigPaths.Add(normalized);
        HeroicEpicPathInput = string.Empty;
    }

    private void RemoveHeroicEpicPath()
    {
        if (SelectedHeroicEpicPath == null) return;
        HeroicEpicConfigPaths.Remove(SelectedHeroicEpicPath);
        SelectedHeroicEpicPath = null;
    }

    private static string NormalizePathSafe(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private IStorageProvider? ResolveStorageProvider()
    {
        // Try to resolve StorageProvider:
        // 1. Injected property (Priority)
        // 2. Fallback to active window (Pragmatic approach for dialogs)
        var provider = StorageProvider;
        if (provider == null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var activeWindow = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            provider = activeWindow?.StorageProvider;
        }

        return provider;
    }

    private static HttpClient CreateGitHubHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Retromind", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!IsSaved && IgnoreLeadingArticlesInSort != _originalIgnoreLeadingArticlesInSort)
        {
            MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(_originalIgnoreLeadingArticlesInSort);
            RequestSortPreviewRefresh?.Invoke();
        }

        if (SelectedScraper != null)
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;

        if (SelectedEmulator != null)
            SelectedEmulator.PropertyChanged -= OnEmulatorPropertyChanged;

        foreach (var emulator in Emulators)
            emulator.PropertyChanged -= OnAnyEmulatorPropertyChanged;
    }
}

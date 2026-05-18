using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public sealed partial class GogInstallDialogViewModel : ViewModelBase
{
    public enum WindowsInstallerPreference
    {
        AutoPrefer64 = 0,
        Prefer64 = 1,
        Prefer32 = 2
    }

    public sealed record PlatformOption(GogInstallPlatform Platform, string Name);

    public sealed record RunnerOption(
        string Id,
        string Name,
        RunnerVersionKind Kind,
        string Path)
    {
        public string DisplayName => $"{Name} ({(Kind == RunnerVersionKind.Wine ? "Wine" : "Proton")})";
    }

    public sealed record WindowsInstallerPreferenceOption(WindowsInstallerPreference Value, string Name);

    public sealed record GogInstallDialogResult(
        string InstallPath,
        GogInstallPlatform Platform,
        RunnerOption? Runner,
        WindowsInstallerPreference WindowsInstallerPreference,
        bool CleanInstall,
        bool DeleteStagingAfterSuccess);

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private PlatformOption? _selectedPlatform;

    [ObservableProperty]
    private RunnerOption? _selectedRunner;

    [ObservableProperty]
    private WindowsInstallerPreferenceOption? _selectedWindowsInstallerPreference;

    [ObservableProperty]
    private bool _cleanInstall = true;

    [ObservableProperty]
    private bool _deleteStagingAfterSuccess = true;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public ObservableCollection<PlatformOption> Platforms { get; } = new();
    public ObservableCollection<RunnerOption> RunnerOptions { get; } = new();
    public ObservableCollection<WindowsInstallerPreferenceOption> WindowsInstallerPreferences { get; } = new();

    public IAsyncRelayCommand BrowseInstallPathCommand { get; }
    public IRelayCommand<Window?> ConfirmCommand { get; }
    public IRelayCommand<Window?> CancelCommand { get; }

    public event Func<Task<string?>>? RequestBrowseInstallPath;

    public GogInstallDialogResult? Result { get; private set; }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool IsWindowsPlatformSelected => SelectedPlatform?.Platform == GogInstallPlatform.Windows;
    public bool HasRunnerOptions => RunnerOptions.Count > 0;
    public bool ShowMissingRunnerHint => IsWindowsPlatformSelected && !HasRunnerOptions;
    public string InstallPathLabel => T("Gog.InstallPathLabel", "Install path");
    public string PlatformLabel => T("Gog.InstallPlatformLabel", "Version");
    public string RunnerLabel => T("Gog.InstallRunnerLabel", "Wine/Proton runner");
    public string RunnerMissingHintText => T(
        "Gog.InstallRunnerMissingHint",
        "No Wine/Proton runner configured yet. Configure one in Settings -> Runner.");
    public string WindowsInstallerPreferenceLabel => T("Gog.Install.WindowsInstallerPreferenceLabel", "Windows installer");
    public string CleanInstallLabel => T("Gog.Install.CleanInstallLabel", "Clean install");
    public string CleanInstallHint => T(
        "Gog.Install.CleanInstallHint",
        "Delete existing files in the target folder before installation.");
    public string DeleteStagingAfterSuccessLabel => T(
        "Gog.Install.DeleteStagingAfterSuccessLabel",
        "Delete staging data after successful installation");
    public string DeleteStagingAfterSuccessHint => T(
        "Gog.Install.DeleteStagingAfterSuccessHint",
        "Removes downloaded installer files after a successful install.");
    public string InstallButtonText => T("Button.Install", "Install");
    public string CancelButtonText => T("Button.Cancel", "Cancel");

    public GogInstallDialogViewModel(
        string gameTitle,
        string defaultInstallPath,
        IEnumerable<RunnerVersionConfig>? availableRunnerConfigs,
        IEnumerable<GogInstallPlatform>? availablePlatforms = null,
        GogInstallPlatform? preferredPlatform = null,
        string? preferredRunnerVersionId = null,
        WindowsInstallerPreference? preferredWindowsInstallerPreference = null)
    {
        Title = T("Gog.Install.DialogTitle", "Install GOG game");
        Message = string.Format(
            T("Gog.Install.DialogMessageFormat", "Install \"{0}\""),
            string.IsNullOrWhiteSpace(gameTitle) ? "GOG" : gameTitle);

        InstallPath = defaultInstallPath ?? string.Empty;

        var platformSet = availablePlatforms?
            .Distinct()
            .ToHashSet() ??
            new HashSet<GogInstallPlatform>
            {
                GogInstallPlatform.Linux,
                GogInstallPlatform.Windows
            };

        if (platformSet.Contains(GogInstallPlatform.Linux))
        {
            Platforms.Add(new PlatformOption(
                GogInstallPlatform.Linux,
                T("Gog.Install.PlatformLinux", "Linux")));
        }

        if (platformSet.Contains(GogInstallPlatform.Windows))
        {
            Platforms.Add(new PlatformOption(
                GogInstallPlatform.Windows,
                T("Gog.Install.PlatformWindows", "Windows")));
        }

        SelectedPlatform = preferredPlatform.HasValue
            ? Platforms.FirstOrDefault(p => p.Platform == preferredPlatform.Value) ?? Platforms.FirstOrDefault()
            : Platforms.FirstOrDefault();

        if (availableRunnerConfigs != null)
        {
            foreach (var runner in availableRunnerConfigs
                         .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                         .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                         .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                         .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                RunnerOptions.Add(new RunnerOption(
                    runner.Id,
                    runner.Name.Trim(),
                    runner.Kind,
                    runner.Path.Trim()));
            }
        }

        SelectedRunner = !string.IsNullOrWhiteSpace(preferredRunnerVersionId)
            ? RunnerOptions.FirstOrDefault(r => string.Equals(r.Id, preferredRunnerVersionId, StringComparison.Ordinal))
              ?? RunnerOptions.FirstOrDefault()
            : RunnerOptions.FirstOrDefault();

        WindowsInstallerPreferences.Add(new WindowsInstallerPreferenceOption(
            WindowsInstallerPreference.AutoPrefer64,
            T("Gog.Install.WindowsInstallerPreference.AutoPrefer64", "Auto (prefer 64-bit)")));
        WindowsInstallerPreferences.Add(new WindowsInstallerPreferenceOption(
            WindowsInstallerPreference.Prefer64,
            T("Gog.Install.WindowsInstallerPreference.Prefer64", "64-bit installer")));
        WindowsInstallerPreferences.Add(new WindowsInstallerPreferenceOption(
            WindowsInstallerPreference.Prefer32,
            T("Gog.Install.WindowsInstallerPreference.Prefer32", "32-bit installer")));

        var preferredInstallerPreference = preferredWindowsInstallerPreference ?? WindowsInstallerPreference.AutoPrefer64;
        SelectedWindowsInstallerPreference = WindowsInstallerPreferences.FirstOrDefault(
            option => option.Value == preferredInstallerPreference) ?? WindowsInstallerPreferences.FirstOrDefault();

        BrowseInstallPathCommand = new AsyncRelayCommand(BrowseInstallPathAsync);
        ConfirmCommand = new RelayCommand<Window?>(Confirm);
        CancelCommand = new RelayCommand<Window?>(window => window?.Close(false));
    }

    partial void OnSelectedPlatformChanged(PlatformOption? value)
    {
        if (!IsWindowsPlatformSelected)
            ValidationMessage = string.Empty;

        OnPropertyChanged(nameof(IsWindowsPlatformSelected));
        OnPropertyChanged(nameof(ShowMissingRunnerHint));
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    private async Task BrowseInstallPathAsync()
    {
        var callback = RequestBrowseInstallPath;
        if (callback == null)
            return;

        var selectedPath = await callback().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(selectedPath))
            InstallPath = selectedPath;
    }

    private void Confirm(Window? window)
    {
        ValidationMessage = string.Empty;

        var path = InstallPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            ValidationMessage = T("Gog.Install.ValidationPathRequired", "Install path is required.");
            return;
        }

        if (!Path.IsPathRooted(path))
        {
            ValidationMessage = T("Gog.Install.ValidationPathAbsolute", "Install path must be absolute.");
            return;
        }

        var platform = SelectedPlatform?.Platform ?? GogInstallPlatform.Linux;
        if (platform == GogInstallPlatform.Windows)
        {
            if (!HasRunnerOptions)
            {
                ValidationMessage = T(
                    "Gog.Install.ValidationRunnerMissing",
                    "No Wine/Proton runner is configured. Add one in Settings -> Runner.");
                return;
            }

            if (SelectedRunner == null)
            {
                ValidationMessage = T(
                    "Gog.Install.ValidationRunnerRequired",
                    "Select a Wine/Proton runner for Windows installation.");
                return;
            }

            if (SelectedRunner.Kind == RunnerVersionKind.Proton && !IsUmuRunAvailable())
            {
                ValidationMessage = T(
                    "Gog.Install.ValidationUmuRequired",
                    "Proton runner requires umu-run in PATH. Install umu or select a Wine runner.");
                return;
            }
        }

        var installerPreference = SelectedWindowsInstallerPreference?.Value ?? WindowsInstallerPreference.AutoPrefer64;
        Result = new GogInstallDialogResult(
            path,
            platform,
            SelectedRunner,
            installerPreference,
            CleanInstall,
            DeleteStagingAfterSuccess);
        window?.Close(true);
    }

    private static bool IsUmuRunAvailable()
        => !string.IsNullOrWhiteSpace(TryFindExecutableInCurrentPath("umu-run"));

    private static string? TryFindExecutableInCurrentPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim();
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(directory, executableName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }
}

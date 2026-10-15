using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

public sealed partial class EpicInstallDialogViewModel : ViewModelBase
{
    public sealed record RunnerOption(
        string Id,
        string Name,
        RunnerVersionKind Kind,
        string Path)
    {
        public string DisplayName => $"{Name} ({(Kind == RunnerVersionKind.Wine ? "Wine" : "Proton")})";
    }

    public sealed record EpicInstallDialogResult(string InstallPath, RunnerOption Runner);

    [ObservableProperty]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private RunnerOption? _selectedRunner;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public EpicInstallDialogViewModel(
        string gameTitle,
        string defaultInstallPath,
        IEnumerable<RunnerVersionConfig>? availableRunners,
        string? preferredRunnerVersionId)
    {
        Title = T("Epic.Install.DialogTitle", "Install Epic Games title");
        Message = string.Format(
            T("Epic.Install.DialogMessageFormat", "Install \"{0}\""),
            string.IsNullOrWhiteSpace(gameTitle) ? "Epic Games" : gameTitle);
        InstallPath = defaultInstallPath ?? string.Empty;

        if (availableRunners != null)
        {
            foreach (var runner in availableRunners
                         .Where(runner => !string.IsNullOrWhiteSpace(runner.Id))
                         .Where(runner => !string.IsNullOrWhiteSpace(runner.Name))
                         .Where(runner => !string.IsNullOrWhiteSpace(runner.Path))
                         .OrderBy(runner => runner.Name, MediaSortHelper.NaturalStringComparer))
            {
                RunnerOptions.Add(new RunnerOption(
                    runner.Id.Trim(),
                    runner.Name.Trim(),
                    runner.Kind,
                    runner.Path.Trim()));
            }
        }

        var preferredId = preferredRunnerVersionId?.Trim();
        SelectedRunner = string.IsNullOrWhiteSpace(preferredId)
            ? RunnerOptions.FirstOrDefault()
            : RunnerOptions.FirstOrDefault(runner => string.Equals(
                  runner.Id,
                  preferredId,
                  StringComparison.Ordinal)) ?? RunnerOptions.FirstOrDefault();

        BrowseInstallPathCommand = new AsyncRelayCommand(BrowseInstallPathAsync);
        ConfirmCommand = new RelayCommand<Window?>(Confirm);
        CancelCommand = new RelayCommand<Window?>(window => window?.Close(false));
    }

    public string Title { get; }
    public string Message { get; }
    public ObservableCollection<RunnerOption> RunnerOptions { get; } = new();
    public bool HasRunnerOptions => RunnerOptions.Count > 0;
    public bool ShowMissingRunnerHint => !HasRunnerOptions;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public string InstallPathLabel => T("Epic.Install.PathLabel", "Install path");
    public string RunnerLabel => T("Epic.Install.RunnerLabel", "Wine/Proton runner");
    public string RunnerMissingHintText => T(
        "Epic.Install.RunnerMissingHint",
        "No Wine/Proton runner is configured. Add one in Settings -> Runner.");
    public string InstallButtonText => T("Button.Install", "Install");
    public string CancelButtonText => T("Button.Cancel", "Cancel");
    public IAsyncRelayCommand BrowseInstallPathCommand { get; }
    public IRelayCommand<Window?> ConfirmCommand { get; }
    public IRelayCommand<Window?> CancelCommand { get; }
    public event Func<Task<string?>>? RequestBrowseInstallPath;
    public EpicInstallDialogResult? Result { get; private set; }

    partial void OnValidationMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasValidationMessage));

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
        var installPath = InstallPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            ValidationMessage = T("Epic.Install.ValidationPathRequired", "Install path is required.");
            return;
        }

        if (!Path.IsPathRooted(installPath))
        {
            ValidationMessage = T("Epic.Install.ValidationPathAbsolute", "Install path must be absolute.");
            return;
        }

        if (SelectedRunner == null)
        {
            ValidationMessage = HasRunnerOptions
                ? T("Epic.Install.ValidationRunnerRequired", "Select a Wine/Proton runner.")
                : RunnerMissingHintText;
            return;
        }

        if (SelectedRunner.Kind == RunnerVersionKind.Proton &&
            string.IsNullOrWhiteSpace(EnvironmentPathHelper.TryFindExecutableInCurrentPath("umu-run")))
        {
            ValidationMessage = T(
                "Epic.Install.ValidationUmuRequired",
                "Proton runner requires umu-run in PATH. Install umu or select a Wine runner.");
            return;
        }

        Result = new EpicInstallDialogResult(installPath, SelectedRunner);
        window?.Close(true);
    }
}

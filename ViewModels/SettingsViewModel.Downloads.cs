using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    public sealed record UpdateModeOption(AutomaticUpdateMode Mode, string Label);
    public UpdateModeOption[] DownloadUpdateModes =>
    [
        new(AutomaticUpdateMode.Disabled, T("Downloads.ModeDisabled", "Disabled")),
        new(AutomaticUpdateMode.DownloadOnly, T("Downloads.ModeDownloadOnly", "Download only")),
        new(AutomaticUpdateMode.DownloadAndInstall, T("Downloads.ModeInstall", "Download and install when safe"))
    ];

    private DownloadSettings DownloadOptions
    {
        get => _appSettings.Downloads ?? new();
        set => _appSettings.Downloads = value;
    }

    public int DownloadUpdateModeIndex
    {
        get => (int)DownloadOptions.AutomaticUpdates;
        set
        {
            if (Enum.IsDefined((AutomaticUpdateMode)value))
                DownloadOptions = DownloadOptions with { AutomaticUpdates = (AutomaticUpdateMode)value };
        }
    }

    public bool LimitDownloadWindow
    {
        get => DownloadOptions.LimitToTimeWindow;
        set => DownloadOptions = DownloadOptions with { LimitToTimeWindow = value };
    }

    public TimeSpan? DownloadWindowStart
    {
        get => DownloadOptions.WindowStart.ToTimeSpan();
        set
        {
            if (value is { } time)
                DownloadOptions = DownloadOptions with { WindowStart = TimeOnly.FromTimeSpan(time) };
        }
    }

    public TimeSpan? DownloadWindowEnd
    {
        get => DownloadOptions.WindowEnd.ToTimeSpan();
        set
        {
            if (value is { } time)
                DownloadOptions = DownloadOptions with { WindowEnd = TimeOnly.FromTimeSpan(time) };
        }
    }

    public decimal? DownloadSpeedLimit
    {
        get => DownloadOptions.MegabytesPerSecond;
        set => DownloadOptions = DownloadOptions with { MegabytesPerSecond = value ?? 0 };
    }

    public string GogArchiveHint => string.Format(DownloadStrings.ArchiveHintFormat, GogInstallerArchive.DefaultRoot);

    public string? GogInstallerArchiveDirectory
    {
        get => DownloadOptions.GogArchiveDirectory;
        set
        {
            DownloadOptions = DownloadOptions with { GogArchiveDirectory = value?.Trim() };
            OnPropertyChanged();
        }
    }

    public bool KeepGogOfflineInstallers
    {
        get => DownloadOptions.KeepGogInstallers;
        set => DownloadOptions = DownloadOptions with { KeepGogInstallers = value };
    }

    [RelayCommand]
    private async Task BrowseGogInstallerArchiveAsync()
    {
        if (ResolveStorageProvider() is not { } provider)
            return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Downloads.Archive", "GOG offline installer archive"),
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            GogInstallerArchiveDirectory = PreferPortableLaunchPaths
                ? PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(path)
                : path;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.GameSystems;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private void Save()
    {
        // File changes are prepared before publishing the edited metadata.
        _originalItem.Title = Title;
        _originalItem.Publisher = NormalizeOptionalText(Publisher);
        _originalItem.Platform = NormalizeOptionalText(Platform);
        _originalItem.GameSystemId = GameSystemCatalog.NormalizeId(SelectedGameSystem?.Id);
        _originalItem.RetroAchievementsGame = CloneRetroAchievementsGameIdentity(
            _stagedRetroAchievementsGame);
        _originalItem.Source = NormalizeOptionalText(Source);
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.Series = NormalizeOptionalText(Series);
        _originalItem.ReleaseType = NormalizeOptionalText(ReleaseType);
        _originalItem.SortTitle = NormalizeOptionalText(SortTitle);
        _originalItem.PlayMode = NormalizeOptionalText(PlayMode);
        _originalItem.MaxPlayers = NormalizeOptionalText(MaxPlayers);
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.CustomFields = BuildCustomFieldsDictionary();
        SavePlayStatistics();

        ApplyLaunchConfiguration(_originalItem);
    }

    private void ApplyLaunchConfiguration(MediaItem item)
    {
        // Prefix: store null when not used.
        // In portable mode, absolute paths inside LibraryRoot are normalized to library-relative.
        var prefixPathToStore = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        if (_settings.PreferPortableLaunchPaths && !string.IsNullOrWhiteSpace(prefixPathToStore))
        {
            prefixPathToStore = PrefixPathHelper.ConvertPathToLibraryRelativeIfInsideLibraryRoot(
                prefixPathToStore,
                AppPaths.LibraryRoot);
        }

        item.PrefixPath = string.IsNullOrWhiteSpace(prefixPathToStore) ? null : prefixPathToStore;
        item.RunnerVersionId = string.IsNullOrWhiteSpace(SelectedRunnerVersion?.Id)
            ? null
            : SelectedRunnerVersion.Id;

        // Always store per-item launcher arguments (used for both Native and Emulator modes)
        item.LauncherArgs = LauncherArgs;
        item.Files = CloneFiles(_editedFiles);

        item.WorkingDirectory = NormalizeOptionalPortableDataPath(WorkingDirectory);
        item.XdgConfigPath = NormalizeOptionalPortableDataPath(XdgConfigPath);
        item.XdgDataPath = NormalizeOptionalPortableDataPath(XdgDataPath);
        item.XdgCachePath = NormalizeOptionalPortableDataPath(XdgCachePath);
        item.XdgStatePath = NormalizeOptionalPortableDataPath(XdgStatePath);
        item.XdgBasePath = NormalizeOptionalPortableDataPath(XdgBasePath);

        // Always store process monitor override (null when empty)
        item.OverrideWatchProcess = string.IsNullOrWhiteSpace(OverrideWatchProcess)
            ? null
            : OverrideWatchProcess;

        // 2. Emulator / launcher path handling (selection-driven)
        switch (SelectedEmulatorProfile?.Kind)
        {
            case EmulatorProfileOption.OptionKind.Emulator:
                item.MediaType = MediaType.Emulator;
                item.EmulatorId = SelectedEmulatorProfile.Emulator?.Id;
                item.LauncherPath = null;
                break;

            case EmulatorProfileOption.OptionKind.Manual:
                item.MediaType = MediaType.Emulator;
                item.EmulatorId = null;
                if (_settings.PreferPortableLaunchPaths &&
                    !string.IsNullOrWhiteSpace(LauncherPath) &&
                    Path.IsPathRooted(LauncherPath) &&
                    PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(LauncherPath, out var relativeLauncherPath))
                {
                    item.LauncherPath = relativeLauncherPath;
                }
                else
                {
                    item.LauncherPath = LauncherPath;
                }
                break;

            case EmulatorProfileOption.OptionKind.Inherit:
                item.MediaType = MediaType.Emulator;
                item.EmulatorId = null;
                item.LauncherPath = null;
                break;

            case EmulatorProfileOption.OptionKind.Native:
                item.MediaType = MediaType.Native;
                item.EmulatorId = null;
                item.LauncherPath = null;
                break;

            default:
                item.MediaType = MediaType;
                item.EmulatorId = null;
                item.LauncherPath = null;
                break;
        }

        // 3. Native wrapper override (tri-state, item-level)
        switch (NativeWrapperMode)
        {
            case WrapperMode.Inherit:
                item.NativeWrappersOverride = null;
                break;

            case WrapperMode.None:
                item.NativeWrappersOverride = new List<LaunchWrapper>();
                break;

            case WrapperMode.Override:
                var wrapperOverrides = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

                if (_settings.PreferPortableLaunchPaths)
                    PortablePathHelper.ConvertWrapperPathsToPortable(wrapperOverrides);

                item.NativeWrappersOverride = wrapperOverrides;
                break;
        }

        // 4. Environment overrides: sync back into the model dictionary
        item.EnvironmentOverrides.Clear();
        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            item.EnvironmentOverrides[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        if (_settings.PreferPortableLaunchPaths)
            EnvironmentPathHelper.ConvertDataRootPathsToPortable(item.EnvironmentOverrides);
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? NormalizeOptionalPortableDataPath(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (!_settings.PreferPortableLaunchPaths || normalized == null)
            return normalized;

        return PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(normalized);
    }

    private Dictionary<string, string> BuildCustomFieldsDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in _originalItem.CustomFields)
        {
            if (CustomFieldKeyHelper.IsInternal(pair.Key))
                result[pair.Key] = pair.Value;
        }

        foreach (var row in CustomFields)
        {
            var key = row.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || CustomFieldKeyHelper.IsInternal(key))
                continue;

            var value = row.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            result[key] = value;
        }

        return result;
    }

}

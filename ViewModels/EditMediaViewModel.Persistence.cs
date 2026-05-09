using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private void Save()
    {
        // 1. Write metadata back to the original item
        var oldTitle = _originalItem.Title;
        var newTitle = Title;

        if (!string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
        {
            var renamed = _fileService.RenameItemAssets(_originalItem, oldTitle, newTitle, _nodePath);
            if (renamed)
            {
                _originalItem.ResetActiveAssets();
                _originalItem.NotifyAssetPathsChanged();
            }
        }

        _originalItem.Title = newTitle;
        _originalItem.Publisher = NormalizeOptionalText(Publisher);
        _originalItem.Platform = NormalizeOptionalText(Platform);
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

        // Prefix: store null when not used.
        // In portable mode, absolute paths inside LibraryRoot are normalized to library-relative.
        var prefixPathToStore = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        if (_settings.PreferPortableLaunchPaths && !string.IsNullOrWhiteSpace(prefixPathToStore))
        {
            prefixPathToStore = PrefixPathHelper.ConvertPathToLibraryRelativeIfInsideLibraryRoot(
                prefixPathToStore,
                AppPaths.LibraryRoot);
        }

        _originalItem.PrefixPath = string.IsNullOrWhiteSpace(prefixPathToStore) ? null : prefixPathToStore;
        _originalItem.WineArchOverride = WineArchSelection switch
        {
            WineArchOption.Win32 => "win32",
            WineArchOption.Win64 => "win64",
            _ => null
        };
        _originalItem.RunnerVersionId = string.IsNullOrWhiteSpace(SelectedRunnerVersion?.Id)
            ? null
            : SelectedRunnerVersion.Id;

        // Always store per-item launcher arguments (used for both Native and Emulator modes)
        _originalItem.LauncherArgs = LauncherArgs;

        _originalItem.WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
            ? null
            : WorkingDirectory.Trim();

        _originalItem.XdgConfigPath = string.IsNullOrWhiteSpace(XdgConfigPath)
            ? null
            : XdgConfigPath.Trim();

        _originalItem.XdgDataPath = string.IsNullOrWhiteSpace(XdgDataPath)
            ? null
            : XdgDataPath.Trim();

        _originalItem.XdgCachePath = string.IsNullOrWhiteSpace(XdgCachePath)
            ? null
            : XdgCachePath.Trim();

        _originalItem.XdgStatePath = string.IsNullOrWhiteSpace(XdgStatePath)
            ? null
            : XdgStatePath.Trim();

        _originalItem.XdgBasePath = string.IsNullOrWhiteSpace(XdgBasePath)
            ? null
            : XdgBasePath.Trim();

        // Always store process monitor override (null when empty)
        _originalItem.OverrideWatchProcess = string.IsNullOrWhiteSpace(OverrideWatchProcess)
            ? null
            : OverrideWatchProcess;

        // 2. Emulator / launcher path handling (selection-driven)
        switch (SelectedEmulatorProfile?.Kind)
        {
            case EmulatorProfileOption.OptionKind.Emulator:
                _originalItem.MediaType = MediaType.Emulator;
                _originalItem.EmulatorId = SelectedEmulatorProfile.Emulator?.Id;
                _originalItem.LauncherPath = null;
                break;

            case EmulatorProfileOption.OptionKind.Manual:
                _originalItem.MediaType = MediaType.Emulator;
                _originalItem.EmulatorId = null;
                if (_settings.PreferPortableLaunchPaths &&
                    !string.IsNullOrWhiteSpace(LauncherPath) &&
                    Path.IsPathRooted(LauncherPath) &&
                    PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(LauncherPath, out var relativeLauncherPath))
                {
                    _originalItem.LauncherPath = relativeLauncherPath;
                }
                else
                {
                    _originalItem.LauncherPath = LauncherPath;
                }
                break;

            case EmulatorProfileOption.OptionKind.Inherit:
                _originalItem.MediaType = MediaType.Emulator;
                _originalItem.EmulatorId = null;
                _originalItem.LauncherPath = null;
                break;

            case EmulatorProfileOption.OptionKind.Native:
                _originalItem.MediaType = MediaType.Native;
                _originalItem.EmulatorId = null;
                _originalItem.LauncherPath = null;
                break;

            default:
                _originalItem.MediaType = MediaType;
                _originalItem.EmulatorId = null;
                _originalItem.LauncherPath = null;
                break;
        }

        // 3. Native wrapper override (tri-state, item-level)
        switch (NativeWrapperMode)
        {
            case WrapperMode.Inherit:
                _originalItem.NativeWrappersOverride = null;
                break;

            case WrapperMode.None:
                _originalItem.NativeWrappersOverride = new List<LaunchWrapper>();
                break;

            case WrapperMode.Override:
                var wrapperOverrides = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

                if (_settings.PreferPortableLaunchPaths)
                    PortablePathHelper.ConvertWrapperPathsToPortable(wrapperOverrides);

                _originalItem.NativeWrappersOverride = wrapperOverrides;
                break;
        }

        // 4. Environment overrides: sync back into the model dictionary
        _originalItem.EnvironmentOverrides.Clear();
        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            if (string.Equals(row.Key.Trim(), "WINEARCH", StringComparison.OrdinalIgnoreCase))
                continue;

            _originalItem.EnvironmentOverrides[row.Key.Trim()] = row.Value ?? string.Empty;
        }
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Dictionary<string, string> BuildCustomFieldsDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in CustomFields)
        {
            var key = row.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var value = row.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            result[key] = value;
        }

        return result;
    }

    private static WineArchOption ResolveWineArchSelection(string? overrideValue, Dictionary<string, string> env)
    {
        var parsed = ParseWineArch(overrideValue);
        if (parsed != WineArchOption.Auto)
            return parsed;

        if (env.TryGetValue("WINEARCH", out var envValue))
            return ParseWineArch(envValue);

        return WineArchOption.Auto;
    }

    private static WineArchOption ParseWineArch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return WineArchOption.Auto;

        var normalized = value.Trim();
        if (normalized.Equals("win32", StringComparison.OrdinalIgnoreCase))
            return WineArchOption.Win32;
        if (normalized.Equals("win64", StringComparison.OrdinalIgnoreCase))
            return WineArchOption.Win64;

        return WineArchOption.Auto;
    }
}

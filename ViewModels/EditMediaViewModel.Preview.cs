using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    // --- Computed Properties & Helpers ---

    public bool IsManualEmulator =>
        SelectedEmulatorProfile?.Kind == EmulatorProfileOption.OptionKind.Manual;

    public bool IsEmulatorMode => MediaType == MediaType.Emulator;

    public string PreviewText => FormatLaunchPreview(includeNotes: true);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(PreviewText))
            OnPropertyChanged(nameof(PrefixDiagnosis));
    }

    public string PrefixDiagnosis
    {
        get
        {
            try
            {
                if (BuildLaunchPreview() is not { } preview)
                    return string.Empty;
                if (preview.Prefix is { } prefix)
                    return LaunchSecretRedactor.Redact(string.Join("\n", WinePrefixService.Describe(prefix, T)), preview.Overrides)
                           ?? string.Empty;
                return !string.IsNullOrWhiteSpace(PrefixPath) ||
                       preview.Overrides.ContainsKey("WINEPREFIX") || preview.Overrides.ContainsKey("STEAM_COMPAT_DATA_PATH")
                    ? T("Launch.Prefix.UnresolvedTarget", "The Windows prefix link could not be resolved.")
                    : string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
            {
                return T("Launch.Preview.Unavailable", "The current launch configuration could not be prepared.");
            }
        }
    }

    private string FormatLaunchPreview(bool includeNotes)
    {
        try
        {
            var preview = BuildLaunchPreview();
            if (preview == null)
                return string.Empty;

            var (startInfo, overrides, playlistRequested, playlistPlanned, epicManaged, _) = preview.Value;
            var environment = LaunchLogBuilder.CaptureEnvironment(startInfo, overrides);
            var secrets = environment.Concat(overrides);
            var command = LaunchSecretRedactor.Redact(LaunchProcessService.BuildCommandDisplay(startInfo), secrets);
            var assignments = environment.Select(pair => $"{pair.Key}={QuotePreviewValue(
                LaunchSecretRedactor.IsSensitiveName(pair.Key)
                    ? "<redacted>"
                    : LaunchSecretRedactor.Redact(pair.Value, secrets) ?? string.Empty)}");
            var removedKeys = System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(key => !startInfo.Environment.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => "-u " + QuotePreviewValue(key))
                .ToArray();
            var changes = string.Join(' ', removedKeys.Concat(assignments));
            if (changes.Length > 0)
                command = $"env {changes} {command}";
            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                var directory = LaunchSecretRedactor.Redact(startInfo.WorkingDirectory, secrets);
                command = $"cd {QuotePreviewValue(directory!)} && {command}";
            }

            var text = $"> {command}";
            if (includeNotes && playlistRequested)
            {
                text += "\n" + (playlistPlanned
                    ? T("Launch.Preview.PlaylistPlanned", "The playlist is written at launch. If writing fails, Retromind uses the primary file.")
                    : T("Launch.Preview.PlaylistFallback", "The configured discs cannot form a playlist. The primary file is shown."));
            }
            if (includeNotes && epicManaged)
                text += "\n" + T("Launch.Preview.EpicDynamic", "Epic launch arguments and environment from Legendary are resolved at launch.");
            return text;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException)
        {
            return T("Launch.Preview.Unavailable", "The current launch configuration could not be prepared.");
        }
    }

    public string EffectiveWorkingDirectory
    {
        get
        {
            try
            {
                var preview = BuildLaunchPreview();
                if (preview is { } plan && !string.IsNullOrWhiteSpace(plan.StartInfo.WorkingDirectory))
                    return LaunchSecretRedactor.Redact(plan.StartInfo.WorkingDirectory, plan.Overrides)!;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException)
            {
                // The editable fields may temporarily be invalid while the user types.
            }
            return Strings.Launch_WorkingDirectoryNotSet;
        }
    }

    private (ProcessStartInfo StartInfo, Dictionary<string, string> Overrides,
        bool PlaylistRequested, bool PlaylistPlanned, bool EpicManaged, PrefixResolution? Prefix)? BuildLaunchPreview()
    {
        var item = new MediaItem(Title) { Id = _originalItem.Id, CustomFields = _originalItem.CustomFields };
        ApplyLaunchConfiguration(item);

        var emulator = item.MediaType == MediaType.Emulator ? ResolveSelectedEmulatorConfig() : null;
        var environmentService = new LaunchEnvironmentService();
        var overrides = LaunchInheritanceResolver.ResolveEnvironmentOverrides(
            _settings, emulator, _parentNode, _rootNodes, item.EnvironmentOverrides, item.RunnerVersionId, matchNodesById: true);
        var configuredOverrides = overrides.Count == 0 ? null : overrides;
        var epicManaged = EpicMediaItemStateHelper.IsRetromindManagedInstallation(item);
        if (item.MediaType == MediaType.Command)
        {
            if (string.IsNullOrWhiteSpace(item.GetPrimaryLaunchPath()))
                return null;

            var (command, _) = LauncherService.PrepareCommandStartInfo(item, configuredOverrides, environmentService);
            return (command, overrides, false, false, epicManaged, null);
        }

        var playlistRequested = LaunchPlaylistService.UsesPlaylist(item, _nodePath, emulator?.UsePlaylistForMultiDisc == true);
        var playlistPath = playlistRequested
            ? LaunchPlaylistService.GetPlannedPlaylistPath(item, _nodePath, AppPaths.LibraryRoot)
            : null;
        var launchPath = playlistPath ?? (playlistRequested && item.Files.Exists(file => file == null)
            ? null
            : item.GetPrimaryLaunchPath());
        if (string.IsNullOrWhiteSpace(launchPath) && emulator == null && string.IsNullOrWhiteSpace(item.LauncherPath))
            return null;

        var wrappers = LaunchInheritanceResolver.ResolveNativeWrappers(
            emulator, _parentNode, _rootNodes, item.NativeWrappersOverride, matchNodesById: true);
        var (startInfo, prefix) = LauncherService.PrepareNativeOrEmulatorStartInfo(item, emulator, wrappers, launchPath,
            configuredOverrides, environmentService, new WinePrefixService(AppPaths.LibraryRoot, _settings),
            preview: true, plannedPlaylist: playlistPath != null);
        return (startInfo, overrides, playlistRequested, playlistPath != null, epicManaged, prefix);
    }

    private static string QuotePreviewValue(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.' or ':')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private void RefreshInheritedWrappers()
    {
        var (wrappers, sources) = ResolveInheritedNativeWrappersForDisplay();

        InheritedWrappers.Clear();
        foreach (var wrapper in wrappers)
            InheritedWrappers.Add(new LaunchWrapperRow(wrapper.Wrapper, wrapper.Source));

        _inheritedWrappersInfo = wrappers.Count == 0
            ? Strings.EditMedia_InheritedWrappersNone
            : string.Format(Strings.EditMedia_InheritedWrappersInfoFormat, wrappers.Count, string.Join(" + ", sources));

        OnPropertyChanged(nameof(HasInheritedWrappers));
        OnPropertyChanged(nameof(InheritedWrappersInfo));
    }

    private (List<(LaunchWrapper Wrapper, string Source)> Wrappers, List<string> Sources) ResolveInheritedNativeWrappersForDisplay()
    {
        List<LaunchWrapper> baseWrappers = new();
        string? baseSource = null;
        var sources = new List<string>();

        EmulatorConfig? effectiveEmulator = null;
        if (MediaType == MediaType.Emulator)
            effectiveEmulator = ResolveSelectedEmulatorConfig();

        if (effectiveEmulator?.NativeWrappersOverride is { Count: > 0 })
        {
            var emulatorName = string.IsNullOrWhiteSpace(effectiveEmulator.Name)
                ? effectiveEmulator.Id
                : effectiveEmulator.Name;

            baseWrappers = new List<LaunchWrapper>(effectiveEmulator.NativeWrappersOverride);
            baseSource = string.Format(Strings.EditMedia_InheritedWrappersSourceEmulatorFormat, emulatorName);
            sources.Add(baseSource);
        }

        var resolved = new List<(LaunchWrapper Wrapper, string Source)>();

        var nodeOverride = LaunchInheritanceResolver.FindNearestNativeWrapperOverrideNode(
            _parentNode,
            _rootNodes);
        if (nodeOverride?.NativeWrappersOverride is { Count: > 0 } nodeWrappers)
        {
            var nodeSource = string.Format(
                Strings.EditMedia_InheritedWrappersSourceNodeFormat,
                nodeOverride.Name);
            foreach (var wrapper in nodeWrappers)
                resolved.Add((wrapper, nodeSource));

            if (!string.IsNullOrWhiteSpace(nodeOverride.Name))
                sources.Insert(0, nodeSource);
        }

        if (baseWrappers.Count > 0)
        {
            var source = baseSource ?? string.Empty;
            foreach (var wrapper in baseWrappers)
                resolved.Add((wrapper, source));
        }

        return (resolved, sources);
    }

    /// <summary>
    /// Returns true when a launcher argument string is effectively "trivial",
    /// i.e. empty or just the simple "{file}" placeholder. This is used to
    /// distinguish between auto-generated defaults and real user overrides.
    /// </summary>
    private static bool IsTrivialLauncherArgs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "{file}", StringComparison.Ordinal) ||
               string.Equals(trimmed, "\"{file}\"", StringComparison.Ordinal);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private void AddEnvironmentVariable()
    {
        EnvironmentOverrides.Add(new EnvVarRow
        {
            IsInherited = false,
            Source = Strings.Common_SourceItem
        });
    }

    private void RemoveEnvironmentVariable(EnvVarRow? row)
    {
        if (row == null) return;
        EnvironmentOverrides.Remove(row);
    }

    private void AddCustomField()
    {
        CustomFields.Add(new CustomFieldRow(_metadataSuggestionService));
    }

    private void RemoveCustomField(CustomFieldRow? row)
    {
        if (row == null) return;
        CustomFields.Remove(row);
    }

    private void GeneratePrefix()
    {
        // Only generate if not already set (user might have a custom path)
        if (HasPrefix) return;

        var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(Title);
        var folderName = $"{_originalItem.Id}_{safeTitle}";
        PrefixPath = Path.Combine("Prefixes", folderName);

        try
        {
            var prefixRoot = ResolvePrefixRoot();
            if (string.IsNullOrWhiteSpace(prefixRoot))
                return;

            var env = BuildEffectiveEnvironmentOverrides();
            var isUmu = IsUmuBased(env);
            var isProton = isUmu || IsProtonBased(env);
            _winetricksService.PreparePrefix(prefixRoot, isProton, isUmu);
        }
        catch
        {
            // best-effort: generation should still provide the path even if folder creation fails
        }
    }

    private void OpenPrefixFolder()
    {
        try
        {
            if (!HasPrefix) return;

            var folder = ResolvePrefixRoot();
            _winetricksService.TryOpenPrefixFolder(folder);
        }
        catch
        {
            // best-effort: opening a folder must not break the dialog
        }
    }

    private void ClearPrefix()
    {
        PrefixPath = string.Empty;
    }

    private bool CanRunWinetricks(Window? _)
        => HasPrefix &&
           !IsWinetricksRunning &&
           !string.IsNullOrWhiteSpace(WinetricksVerbs);

    private void SetWinetricksRunning(bool value)
    {
        if (UiThreadHelper.CheckAccess())
            IsWinetricksRunning = value;
        else
            UiThreadHelper.Post(() => IsWinetricksRunning = value);
    }

    private async Task RunWinetricksAsync(Window? owner)
    {
        if (!CanRunWinetricks(owner))
            return;

        var verbs = WinetricksVerbs.Trim();
        var prefixRoot = ResolvePrefixRoot();

        if (string.IsNullOrWhiteSpace(prefixRoot))
            return;

        SetWinetricksRunning(true);
        ProcessLogViewModel? logVm = null;

        try
        {
            var env = BuildEffectiveEnvironmentOverrides();
            var isUmu = IsUmuBased(env);
            var isProton = isUmu || IsProtonBased(env);

            foreach (var key in env.Keys.ToList())
                env[key] = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, env[key]);

            logVm = new ProcessLogViewModel("Winetricks", true);
            var logView = new ProcessLogView { DataContext = logVm };

            if (owner != null)
                logView.Show(owner);
            else
                logView.Show();

            var request = new WinetricksRequest(
                prefixRoot,
                verbs,
                env,
                isProton,
                isUmu,
                ResolveUmuRunnerPath());
            await _winetricksService.RunAsync(
                    request,
                    line => AppendLog(logVm, line))
                .ConfigureAwait(false);
        }
        finally
        {
            if (logVm != null)
                UiThreadHelper.Post(() => logVm.IsRunning = false);
            SetWinetricksRunning(false);
        }
    }

    private static void AppendLog(ProcessLogViewModel logVm, string line)
    {
        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }

    private Dictionary<string, string> BuildEffectiveEnvironmentOverrides()
    {
        var emulator = ResolveSelectedEmulatorConfig();
        var itemOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            itemOverrides[row.Key] = row.Value ?? string.Empty;
        }

        var env = LaunchInheritanceResolver.ResolveEnvironmentOverrides(
            _settings,
            emulator,
            _parentNode,
            _rootNodes,
            itemOverrides,
            SelectedRunnerVersion?.Id);

        ApplyEmulatorXdgOverridesForPreview(env, emulator);
        ApplyXdgOverridesForPreview(env);

        return env;
    }

    private static void ApplyEmulatorXdgOverridesForPreview(Dictionary<string, string> env, EmulatorConfig? emulator)
    {
        if (emulator == null)
            return;

        switch (emulator.XdgMode)
        {
            case EmulatorConfig.XdgOverrideMode.Inherit:
                return;

            case EmulatorConfig.XdgOverrideMode.Host:
                env.Remove("XDG_CONFIG_HOME");
                env.Remove("XDG_DATA_HOME");
                env.Remove("XDG_CACHE_HOME");
                env.Remove("XDG_STATE_HOME");
                return;

            case EmulatorConfig.XdgOverrideMode.Custom:
                ApplyXdgOverride(env, "XDG_CONFIG_HOME", emulator.XdgConfigPath);
                ApplyXdgOverride(env, "XDG_DATA_HOME", emulator.XdgDataPath);
                ApplyXdgOverride(env, "XDG_CACHE_HOME", emulator.XdgCachePath);
                ApplyXdgOverride(env, "XDG_STATE_HOME", emulator.XdgStatePath);
                return;
        }
    }

    private void ApplyXdgOverridesForPreview(Dictionary<string, string> env)
    {
        // Keep preview behavior aligned with runtime launch behavior:
        // item-level XDG applies to Native + Emulator, but not Command.
        if (MediaType == MediaType.Command)
            return;

        ApplyXdgOverride(env, "XDG_CONFIG_HOME", XdgConfigPath);
        ApplyXdgOverride(env, "XDG_DATA_HOME", XdgDataPath);
        ApplyXdgOverride(env, "XDG_CACHE_HOME", XdgCachePath);
        ApplyXdgOverride(env, "XDG_STATE_HOME", XdgStatePath);
    }

    private static void ApplyXdgOverride(
        Dictionary<string, string> env,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var resolved = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);

        env[key] = resolved;
    }

    private string ResolveUmuRunnerPath()
    {
        var candidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            candidate = LauncherPath;

        if (string.IsNullOrWhiteSpace(candidate))
            return "umu-run";

        if (!LaunchRuntimeHelper.ContainsUmuToken(candidate))
            return "umu-run";

        return ResolveExecutablePath(candidate);
    }

    private static string ResolveExecutablePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        if (path.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            path.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return AppPaths.ResolveDataPath(path);
        }

        return path;
    }

    private string ResolvePrefixRoot()
    {
        if (string.IsNullOrWhiteSpace(PrefixPath))
            return string.Empty;

        return PrefixPathHelper.ResolveAbsolutePrefixPath(PrefixPath, AppPaths.LibraryRoot);
    }

    private bool IsProtonBased(Dictionary<string, string> env)
    {
        if (LaunchRuntimeHelper.ContainsProtonHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) &&
               LaunchRuntimeHelper.ContainsProtonToken(pathCandidate);
    }

    private bool IsUmuBased(Dictionary<string, string> env)
    {
        if (LaunchRuntimeHelper.ContainsUmuHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) &&
               LaunchRuntimeHelper.ContainsUmuToken(pathCandidate);
    }

}

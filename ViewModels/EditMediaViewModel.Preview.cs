using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    // --- Computed Properties & Helpers ---

    public bool IsManualEmulator =>
        SelectedEmulatorProfile?.Kind == EmulatorProfileOption.OptionKind.Manual;

    public bool IsEmulatorMode => MediaType == MediaType.Emulator;

    public string PreviewText
    {
        get
        {
            // Use the primary launch file (Disc 1 / first entry). If missing, fall back to a sample path
            var primaryPath = _originalItem.GetPrimaryLaunchPath();
            var launchPath = !string.IsNullOrWhiteSpace(primaryPath)
                ? primaryPath
                : "/Games/SuperMario.smc";

            var realFileQuoted = $"\"{launchPath}\"";

            // Resolve effective wrapper chain once (global/emulator/node/item logic)
            var wrappers = ResolveEffectiveNativeWrappersForPreview();

            // Helper to prepend per-item environment overrides to a final command line
            string WithEnvPrefix(string command)
            {
                var env = BuildEnvironmentPrefixForPreview();
                if (string.IsNullOrWhiteSpace(env))
                    return command;

                return $"{env} {command}".Trim();
            }

            string WrapPreview(string command)
            {
                var withEnv = WithEnvPrefix(command);
                var withCwd = ApplyWorkingDirectoryPreview(withEnv);
                return $"> {withCwd}".Trim();
            }

            var selectedEmulator = ResolveSelectedEmulatorConfig();

            // --- Emulator via profile or inherited profile ---
            if (MediaType == MediaType.Emulator && selectedEmulator != null)
            {
                var baseArgs = selectedEmulator.Arguments ?? string.Empty;
                var itemArgs = LauncherArgs ?? string.Empty;

                // Keep the combination logic in sync with LauncherService.CombineTemplateArguments(...)
                var combinedTemplate = CombineTemplateArguments(baseArgs, itemArgs);

                var expandedArgs = ExpandPreviewArguments(combinedTemplate, launchPath);

                // Inner command: emulator binary + expanded args + file
                string inner;
                if (string.IsNullOrWhiteSpace(expandedArgs))
                    inner = $"{selectedEmulator.Path} {realFileQuoted}".Trim();
                else
                    inner = $"{selectedEmulator.Path} {expandedArgs}".Trim();

                // If there is a wrapper chain, wrap it; otherwise return inner directly
                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return WrapPreview(final);
            }

            // --- Manual emulator (no profile selected) ---
            if (MediaType == MediaType.Emulator && IsManualEmulator)
            {
                var expandedArgs = ExpandPreviewArguments(LauncherArgs, launchPath);

                string inner;
                if (string.IsNullOrWhiteSpace(LauncherPath))
                {
                    if (string.IsNullOrWhiteSpace(expandedArgs))
                        return string.Empty;

                    inner = expandedArgs;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(expandedArgs))
                        inner = $"{LauncherPath} {realFileQuoted}".Trim();
                    else
                        inner = $"{LauncherPath} {expandedArgs}".Trim();
                }

                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return WrapPreview(final);
            }

            // --- Native execution (direct or via wrappers) ---
            if (MediaType == MediaType.Native || MediaType == MediaType.Emulator)
            {
                var nativeArgs = BuildNativeArgumentsForPreview(LauncherArgs);

                // Inner command = the real executable + native args
                var inner = string.IsNullOrWhiteSpace(nativeArgs)
                    ? realFileQuoted
                    : $"{realFileQuoted} {nativeArgs}";

                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return WrapPreview(final);
            }

            return string.Empty;
        }
    }

    public string EffectiveWorkingDirectory
    {
        get
        {
            var overridePath = NormalizeWorkingDirectoryForPreview(WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            var launchDir = ResolveLaunchFileWorkingDirectory();
            return string.IsNullOrWhiteSpace(launchDir) ? Strings.Launch_WorkingDirectoryNotSet : launchDir;
        }
    }

    /// <summary>
    /// Builds a shell-style environment prefix (e.g. VAR1=value1 VAR2="foo bar")
    /// from the effective EnvironmentOverrides (emulator + node + item). Returns an empty string
    /// when no overrides are defined.
    /// This is only used for the human-readable PreviewText; the real launcher
    /// uses typed EnvironmentOverrides dictionaries at runtime.
    /// </summary>
    private string BuildEnvironmentPrefixForPreview()
    {
        var env = BuildEffectiveEnvironmentOverrides();
        if (env.Count == 0)
            return string.Empty;

        var parts = new List<string>(env.Count);

        foreach (var kv in env)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            var key = kv.Key.Trim();
            var value = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, kv.Value);

            // Simple, shell-like quoting: wrap in "..." if value contains whitespace
            if (value.Contains(' ', StringComparison.Ordinal) ||
                value.Contains('\t', StringComparison.Ordinal))
            {
                parts.Add($"{key}=\"{value}\"");
            }
            else
            {
                parts.Add($"{key}={value}");
            }
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join(' ', parts);
    }

    private string ApplyWorkingDirectoryPreview(string command)
    {
        var workingDir = NormalizeWorkingDirectoryForPreview(WorkingDirectory);
        if (string.IsNullOrWhiteSpace(workingDir))
            return command;

        var quoted = QuoteForShell(workingDir);
        return $"cd {quoted} && {command}";
    }

    private static string NormalizeWorkingDirectoryForPreview(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return string.Empty;

        var trimmed = workingDirectory.Trim();
        return Path.IsPathRooted(trimmed)
            ? trimmed
            : AppPaths.ResolveDataPath(trimmed);
    }

    private static string QuoteForShell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Contains(' ', StringComparison.Ordinal) ||
               value.Contains('\t', StringComparison.Ordinal)
            ? $"\"{value}\""
            : value;
    }

    private string? ResolveLaunchFileWorkingDirectory()
    {
        var launchPath = _originalItem.GetPrimaryLaunchPath();
        if (string.IsNullOrWhiteSpace(launchPath))
            return null;

        if (Directory.Exists(launchPath))
            return launchPath;

        var dir = Path.GetDirectoryName(launchPath);
        return string.IsNullOrWhiteSpace(dir) ? string.Empty : dir;
    }

    /// <summary>
    /// Expands preview argument templates using the same placeholder semantics as the runtime launcher:
    /// {file}     -> full path to the launch file (quoted if necessary)
    /// {fileDir}  -> directory of the launch file
    /// {fileName} -> file name with extension
    /// {fileBase} -> file name without extension (e.g. ROM short name for MAME)
    /// </summary>
    private static string ExpandPreviewArguments(string? templateArgs, string launchFilePath)
    {
        var fullPath = string.IsNullOrWhiteSpace(launchFilePath)
            ? string.Empty
            : Path.GetFullPath(launchFilePath);

        var fileDir = string.Empty;
        var fileName = string.Empty;
        var fileBase = string.Empty;

        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            fileDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            fileName = Path.GetFileName(fullPath);
            fileBase = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(fileName);
        }

        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        var result = templateArgs
            .Replace("{fileDir}", fileDir, StringComparison.Ordinal)
            .Replace("{fileName}", fileName, StringComparison.Ordinal)
            .Replace("{fileBase}", fileBase, StringComparison.Ordinal);

        // If the user explicitly wrote "\"{file}\"", preserve that quoting style.
        if (result.Contains("\"{file}\"", StringComparison.Ordinal))
        {
            return result.Replace("{file}", fullPath, StringComparison.Ordinal).Trim();
        }

        var quotedPath = (!string.IsNullOrEmpty(fullPath) && fullPath.Contains(' ', StringComparison.Ordinal))
            ? $"\"{fullPath}\""
            : fullPath;

        return result.Replace("{file}", quotedPath, StringComparison.Ordinal).Trim();
    }

    private List<LaunchWrapper> ResolveEffectiveNativeWrappersForPreview()
    {
        // 1) Item-level tri-state (based on current UI state in the dialog)
        //    This reflects unsaved overrides directly from the edit UI.
        switch (NativeWrapperMode)
        {
            case WrapperMode.None:
                // Explicit "no wrappers" for this item.
                return new List<LaunchWrapper>();

            case WrapperMode.Override:
                // Use the item-level override list from the UI (ignoring node/emulator).
                return NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

            case WrapperMode.Inherit:
            default:
                // Fall through to emulator/node chain resolution.
                break;
        }

        // 2) Emulator-level base
        List<LaunchWrapper>? wrappers = null;

        EmulatorConfig? effectiveEmulator = null;
        if (MediaType == MediaType.Emulator)
        {
            effectiveEmulator = ResolveSelectedEmulatorConfig();
        }

        if (effectiveEmulator != null)
        {
            switch (effectiveEmulator.NativeWrapperMode)
            {
                case EmulatorConfig.WrapperMode.Inherit:
                    // Inherit from global defaults (may be null).
                    wrappers = _settings.DefaultNativeWrappers;
                    break;

                case EmulatorConfig.WrapperMode.None:
                    // Explicitly no wrappers for this emulator (unless item overrides, which it doesn't in Inherit mode).
                    wrappers = new List<LaunchWrapper>();
                    break;

                case EmulatorConfig.WrapperMode.Override:
                    // Use emulator-level override list (may be empty to mean "none").
                    wrappers = effectiveEmulator.NativeWrappersOverride != null
                        ? new List<LaunchWrapper>(effectiveEmulator.NativeWrappersOverride)
                        : new List<LaunchWrapper>();
                    break;
            }
        }
        else
        {
            // No emulator: start with global defaults only.
            wrappers = _settings.DefaultNativeWrappers;
        }

        // 3) Node-level inheritance (nearest override wins, tri-state via null/empty/non-empty).
        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = PathHelper.GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            List<LaunchWrapper>? nodeWrappers = null;
            bool nodeOverrideFound = false;

            foreach (var node in chain)
            {
                if (node.NativeWrappersOverride == null)
                {
                    // Inherit -> nothing to do here, continue upwards.
                    continue;
                }

                nodeOverrideFound = true;
                nodeWrappers = node.NativeWrappersOverride.Count == 0
                    ? new List<LaunchWrapper>()
                    : new List<LaunchWrapper>(node.NativeWrappersOverride);
                break;
            }

            if (nodeOverrideFound && nodeWrappers != null && nodeWrappers.Count > 0)
            {
                var baseWrappers = wrappers ?? new List<LaunchWrapper>();
                var merged = new List<LaunchWrapper>(nodeWrappers.Count + baseWrappers.Count);
                merged.AddRange(nodeWrappers);
                merged.AddRange(baseWrappers);
                wrappers = merged;
            }
        }

        // 4) Final normalization: return a concrete list (never null).
        return wrappers != null
            ? wrappers.ToList()
            : new List<LaunchWrapper>();
    }

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

        if (effectiveEmulator != null)
        {
            var emulatorName = string.IsNullOrWhiteSpace(effectiveEmulator.Name)
                ? effectiveEmulator.Id
                : effectiveEmulator.Name;

            switch (effectiveEmulator.NativeWrapperMode)
            {
                case EmulatorConfig.WrapperMode.Inherit:
                    baseWrappers = _settings.DefaultNativeWrappers != null
                        ? new List<LaunchWrapper>(_settings.DefaultNativeWrappers)
                        : new List<LaunchWrapper>();
                    if (baseWrappers.Count > 0)
                    {
                        baseSource = string.Format(Strings.EditMedia_InheritedWrappersSourceGlobalViaEmulatorFormat, emulatorName);
                        sources.Add(baseSource);
                    }
                    break;
                case EmulatorConfig.WrapperMode.None:
                    baseWrappers = new List<LaunchWrapper>();
                    break;
                case EmulatorConfig.WrapperMode.Override:
                    baseWrappers = effectiveEmulator.NativeWrappersOverride != null
                        ? new List<LaunchWrapper>(effectiveEmulator.NativeWrappersOverride)
                        : new List<LaunchWrapper>();
                    if (baseWrappers.Count > 0)
                    {
                        baseSource = string.Format(Strings.EditMedia_InheritedWrappersSourceEmulatorFormat, emulatorName);
                        sources.Add(baseSource);
                    }
                    break;
            }
        }
        else
        {
            baseWrappers = _settings.DefaultNativeWrappers != null
                ? new List<LaunchWrapper>(_settings.DefaultNativeWrappers)
                : new List<LaunchWrapper>();
            if (baseWrappers.Count > 0)
            {
                baseSource = Strings.EditMedia_InheritedWrappersSourceGlobal;
                sources.Add(baseSource);
            }
        }

        var resolved = new List<(LaunchWrapper Wrapper, string Source)>();

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = PathHelper.GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            List<LaunchWrapper>? nodeWrappers = null;
            bool nodeOverrideFound = false;
            string? nodeSourceName = null;

            foreach (var node in chain)
            {
                if (node.NativeWrappersOverride == null)
                    continue;

                nodeOverrideFound = true;
                nodeSourceName = node.Name;
                nodeWrappers = node.NativeWrappersOverride.Count == 0
                    ? new List<LaunchWrapper>()
                    : new List<LaunchWrapper>(node.NativeWrappersOverride);
                break;
            }

            if (nodeOverrideFound && nodeWrappers != null && nodeWrappers.Count > 0)
            {
                var nodeSource = string.Format(Strings.EditMedia_InheritedWrappersSourceNodeFormat, nodeSourceName);
                foreach (var wrapper in nodeWrappers)
                    resolved.Add((wrapper, nodeSource));

                if (!string.IsNullOrWhiteSpace(nodeSourceName))
                    sources.Insert(0, nodeSource);
            }
        }

        if (baseWrappers.Count > 0)
        {
            var source = baseSource ?? string.Empty;
            foreach (var wrapper in baseWrappers)
                resolved.Add((wrapper, source));
        }

        return (resolved, sources);
    }

    private static string BuildWrappedCommandLine(string innerCommand, IReadOnlyList<LaunchWrapper> wrappers)
    {
        var current = innerCommand;

        for (var i = wrappers.Count - 1; i >= 0; i--)
        {
            var wrapper = wrappers[i];
            if (string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            var templateArgs = string.IsNullOrWhiteSpace(wrapper.Args) ? "{file}" : wrapper.Args;

            var expandedArgs = templateArgs.Contains("{file}", StringComparison.Ordinal)
                ? templateArgs.Replace("{file}", current, StringComparison.Ordinal)
                : $"{templateArgs} {current}";

            current = $"{wrapper.Path} {expandedArgs}".Trim();
        }

        return NormalizeWhitespace(current);
    }

    private static string CombineTemplateArguments(string? baseArgs, string? itemArgs)
    {
        baseArgs ??= string.Empty;
        itemArgs ??= string.Empty;

        if (string.IsNullOrWhiteSpace(itemArgs))
            return baseArgs;

        // Matches LauncherService.CombineTemplateArguments(...)
        if (baseArgs.Contains("{file}", StringComparison.Ordinal) &&
            itemArgs.Contains("{file}", StringComparison.Ordinal))
        {
            return baseArgs.Replace("{file}", itemArgs, StringComparison.Ordinal);
        }

        return $"{baseArgs} {itemArgs}".Trim();
    }

    private static string BuildNativeArgumentsForPreview(string? templateArgs)
    {
        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        var args = templateArgs;

        // DAU-Rule:
        // If user types "{file}" in native args, treat it as a leftover from emulator templates.
        // Keep only what comes AFTER {file} (so "prefix {file} --arg" does NOT show "prefix").
        var idxQuoted = args.IndexOf("\"{file}\"", StringComparison.Ordinal);
        if (idxQuoted >= 0)
        {
            args = args[(idxQuoted + "\"{file}\"".Length)..];
        }
        else
        {
            var idx = args.IndexOf("{file}", StringComparison.Ordinal);
            if (idx >= 0)
                args = args[(idx + "{file}".Length)..];
        }

        return NormalizeWhitespace(args);
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        Span<char> buffer = stackalloc char[value.Length];
        int w = 0;
        bool lastWasSpace = false;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace) continue;
                buffer[w++] = ' ';
                lastWasSpace = true;
            }
            else
            {
                buffer[w++] = c;
                lastWasSpace = false;
            }
        }

        int start = 0;
        int length = w;

        if (length > 0 && buffer[0] == ' ')
        {
            start++;
            length--;
        }
        if (length > 0 && buffer[start + length - 1] == ' ')
        {
            length--;
        }

        return length <= 0 ? string.Empty : new string(buffer.Slice(start, length));
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

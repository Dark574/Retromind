using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel : ViewModelBase
{
    private readonly EmulatorConfig? _inheritedEmulator;
    private EmulatorConfig? _resolvedInheritedEmulator;
    private string? _resolvedInheritedEmulatorSource;
    private readonly MediaItem _originalItem;
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;
    private NotifyCollectionChangedEventHandler? _assetsChangedHandler;
    
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly MediaNode? _parentNode;
    
    // Keep a reference to global settings so preview can resolve emulator profiles
    // and default native wrappers in the same way as the runtime launcher.
    private readonly AppSettings _settings;

    // --- Prefix (Wine/Proton/UMU) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrefix))]
    [NotifyCanExecuteChangedFor(nameof(OpenPrefixFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPrefixCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    [NotifyPropertyChangedFor(nameof(ShowWineArchExistingPrefixWarning))]
    private string _prefixPath = string.Empty;

    public bool HasPrefix => !string.IsNullOrWhiteSpace(PrefixPath);

    public IRelayCommand GeneratePrefixCommand { get; }
    public IRelayCommand OpenPrefixFolderCommand { get; }
    public IRelayCommand ClearPrefixCommand { get; }
    public IAsyncRelayCommand<Window?> RunWinetricksCommand { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    private string _winetricksVerbs = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    private bool _isWinetricksRunning;

    /// <summary>
    /// Human-readable path of the primary launch file used by this item
    /// This resolves exactly what the launcher will use via GetPrimaryLaunchPath()
    /// </summary>
    public string PrimaryFileDisplayPath
    {
        get
        {
            var path = _originalItem.GetPrimaryLaunchPath();
            return string.IsNullOrWhiteSpace(path) ? "(no launch file set)" : path;
        }
    }

    /// <summary>
    /// Command to change the primary launch file (Disc 1 / main executable)
    /// This updates MediaItem.Files so the launcher and preview both use the new path
    /// </summary>
    public IAsyncRelayCommand ChangePrimaryFileCommand { get; }
    
    // --- Per-item environment overrides (e.g. PROTONPATH, PROTON_LOG) ---

    public sealed partial class EnvVarRow : ObservableObject
    {
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
        [ObservableProperty] private string _source = string.Empty;
        [ObservableProperty] private bool _isInherited;
    }

    public sealed class EmulatorProfileOption
    {
        public enum OptionKind
        {
            Inherit,
            Native,
            Manual,
            Emulator
        }

        public EmulatorProfileOption(OptionKind kind, string name, EmulatorConfig? emulator = null)
        {
            Kind = kind;
            Name = name ?? string.Empty;
            Emulator = emulator;
        }

        public OptionKind Kind { get; }
        public string Name { get; }
        public EmulatorConfig? Emulator { get; }
    }

    /// <summary>
    /// Editable list of per-item environment overrides.
    /// </summary>
    public ObservableCollection<EnvVarRow> EnvironmentOverrides { get; } = new();

    public IRelayCommand AddEnvironmentVariableCommand { get; }
    public IRelayCommand<EnvVarRow?> RemoveEnvironmentVariableCommand { get; }

    public enum WineArchOption
    {
        Auto,
        Win64,
        Win32
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWineArchOverrideActive))]
    [NotifyPropertyChangedFor(nameof(ShowWineArchExistingPrefixWarning))]
    private WineArchOption _wineArchSelection = WineArchOption.Auto;

    public List<WineArchOption> WineArchOptions { get; } =
        new() { WineArchOption.Auto, WineArchOption.Win64, WineArchOption.Win32 };

    public bool IsWineArchOverrideActive => WineArchSelection != WineArchOption.Auto;

    public bool ShowWineArchExistingPrefixWarning => HasPrefix && IsWineArchOverrideActive;

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

    
    private void GeneratePrefix()
    {
        // Only generate if not already set (user might have a custom path)
        if (HasPrefix) return;

        var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(Title);
        var folderName = $"{_originalItem.Id}_{safeTitle}";
        PrefixPath = Path.Combine("Prefixes", folderName);
    }

    private void OpenPrefixFolder()
    {
        try
        {
            if (!HasPrefix) return;

            var folder = Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, PrefixPath));
            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { folder }
            })?.Dispose();
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

        try
        {
        var env = BuildEffectiveEnvironmentOverrides();
        var isUmu = IsUmuBased(env);
        var isProton = isUmu || IsProtonBased(env);
        var (compatRoot, winePrefix) = ResolvePrefixPathsForWinetricks(prefixRoot, isProton, isUmu);

        if (!PrefixPathHelper.IsWinePrefixInitialized(winePrefix))
            ApplyWineArchOverride(env, WineArchSelection);

        foreach (var key in env.Keys.ToList())
            env[key] = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, env[key]);

        var useUmu = isUmu;
        string? protonPathValue = null;
        string? protonWinetricksPath = null;

            if (isProton && env.TryGetValue("PROTONPATH", out protonPathValue) &&
                !string.IsNullOrWhiteSpace(protonPathValue))
            {
                protonWinetricksPath = Path.Combine(protonPathValue, "protonfixes", "winetricks");
                if (!File.Exists(protonWinetricksPath))
                {
                    useUmu = false;
                    ApplyProtonWineFallback(env, protonPathValue);
                }
            }

            if (useUmu && isProton)
                EnsureUmuWinetricksCwd(env);

            Directory.CreateDirectory(compatRoot);
            Directory.CreateDirectory(winePrefix);

            ApplyPrefixEnvironment(env, compatRoot, winePrefix, isProton);

            var (fileName, arguments) = BuildWinetricksCommand(verbs, useUmu);

            var logVm = new ProcessLogViewModel("Winetricks");
            var logView = new ProcessLogView { DataContext = logVm };

            if (owner != null)
                logView.Show(owner);
            else
                logView.Show();

            AppendLog(logVm, $"Prefix: {compatRoot}");
            if (env.TryGetValue("PROTONPATH", out var protonPathEnv))
            {
                AppendLog(logVm, $"PROTONPATH: {protonPathEnv}");
                if (!string.IsNullOrWhiteSpace(protonWinetricksPath) &&
                    !File.Exists(protonWinetricksPath))
                {
                    AppendLog(logVm, $"Note: missing {protonWinetricksPath} (using system winetricks)");
                }
            }
            if (env.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatPath))
                AppendLog(logVm, $"STEAM_COMPAT_DATA_PATH: {compatPath}");
            if (env.TryGetValue("WINEPREFIX", out var winePrefixValue))
                AppendLog(logVm, $"WINEPREFIX: {winePrefixValue}");
            if (!useUmu && isProton && env.TryGetValue("WINE", out var wineValue))
                AppendLog(logVm, $"WINE: {wineValue}");

            if (isProton && !env.ContainsKey("UMU_LOG"))
            {
                env["UMU_LOG"] = "debug";
                AppendLog(logVm, "UMU_LOG=debug (verbose winetricks output)");
            }

            var argsText = arguments.Count > 0 ? string.Join(' ', arguments) : string.Empty;
            AppendLog(logVm, $"> {fileName} {argsText}".Trim());

            await RunProcessWithLogAsync(fileName, arguments, env, logVm).ConfigureAwait(false);
            AppendWinetricksLogSummary(logVm, winePrefix);
        }
        finally
        {
            SetWinetricksRunning(false);
        }
    }

    private static async Task RunProcessWithLogAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environmentOverrides,
        ProcessLogViewModel logVm)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        foreach (var kv in environmentOverrides)
            startInfo.EnvironmentVariables[kv.Key] = kv.Value;

        try
        {
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AppendLog(logVm, e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AppendLog(logVm, e.Data);
            };

            if (!process.Start())
            {
                AppendLog(logVm, "Failed to start winetricks process.");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync().ConfigureAwait(false);
            AppendLog(logVm, $"Exit code: {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            AppendLog(logVm, $"Error: executable not found: {fileName}");
            AppendLog(logVm, "Check that winetricks/umu-run is installed and in PATH.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13)
        {
            AppendLog(logVm, $"Error: permission denied when launching: {fileName}");
        }
        catch (Exception ex)
        {
            AppendLog(logVm, $"Error: {ex.Message}");
        }
        finally
        {
            UiThreadHelper.Post(() => logVm.IsRunning = false);
        }
    }

    private static void AppendLog(ProcessLogViewModel logVm, string line)
    {
        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }

    private Dictionary<string, string> BuildEffectiveEnvironmentOverrides()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        var emulator = ResolveSelectedEmulatorConfig();
        if (emulator?.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in emulator.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                env[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            foreach (var node in chain)
            {
                if (node.EnvironmentOverrides == null)
                    continue;

                if (node.EnvironmentOverrides.Count > 0)
                {
                    foreach (var kv in node.EnvironmentOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;

                        env[kv.Key.Trim()] = kv.Value ?? string.Empty;
                    }
                }

                break;
            }
        }

        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            env[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        ApplyXdgOverridesForPreview(env);

        return env;
    }

    private void ApplyXdgOverridesForPreview(Dictionary<string, string> env)
    {
        if (MediaType != MediaType.Native)
            return;

        ApplyXdgOverrideIfMissing(env, "XDG_CONFIG_HOME", XdgConfigPath);
        ApplyXdgOverrideIfMissing(env, "XDG_DATA_HOME", XdgDataPath);
        ApplyXdgOverrideIfMissing(env, "XDG_CACHE_HOME", XdgCachePath);
        ApplyXdgOverrideIfMissing(env, "XDG_STATE_HOME", XdgStatePath);
    }

    private static void ApplyXdgOverrideIfMissing(
        Dictionary<string, string> env,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (env.ContainsKey(key))
            return;

        var resolved = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);

        env[key] = resolved;
    }

    private static void ApplyPrefixEnvironment(
        Dictionary<string, string> env,
        string compatRoot,
        string winePrefix,
        bool isProton)
    {
        if (isProton)
            env["STEAM_COMPAT_DATA_PATH"] = compatRoot;

        env["WINEPREFIX"] = winePrefix;
    }

    private static void ApplyWineArchOverride(Dictionary<string, string> env, WineArchOption selection)
    {
        var value = selection switch
        {
            WineArchOption.Win32 => "win32",
            WineArchOption.Win64 => "win64",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(value))
            return;

        env["WINEARCH"] = value;
    }

    private static void AppendWinetricksLogSummary(ProcessLogViewModel logVm, string winePrefix)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        try
        {
            var logPath = Path.Combine(winePrefix, "winetricks.log");
            if (!File.Exists(logPath))
            {
                AppendLog(logVm, $"winetricks.log not found: {logPath}");
                return;
            }

            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                AppendLog(logVm, $"winetricks.log is empty: {logPath}");
                return;
            }

            AppendLog(logVm, "winetricks.log:");
            const int maxLines = 50;
            var start = Math.Max(0, lines.Length - maxLines);
            for (var i = start; i < lines.Length; i++)
                AppendLog(logVm, lines[i]);
        }
        catch (Exception ex)
        {
            AppendLog(logVm, $"Failed to read winetricks.log: {ex.Message}");
        }
    }

    private static void EnsureUmuWinetricksCwd(Dictionary<string, string> env)
    {
        if (!env.TryGetValue("PROTONPATH", out var protonPath) ||
            string.IsNullOrWhiteSpace(protonPath))
        {
            return;
        }

        try
        {
            var dir = Path.Combine(protonPath, "protonfixes");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // best-effort: missing access should not block winetricks entirely
        }
    }

    private static (string CompatRoot, string WinePrefix) ResolvePrefixPathsForWinetricks(
        string prefixRoot,
        bool isProton,
        bool isUmu)
    {
        var compatRoot = prefixRoot;
        var winePrefix = prefixRoot;

        if (isUmu)
        {
            if (PrefixPathHelper.IsPfxPath(prefixRoot))
            {
                compatRoot = GetParentOrSelf(prefixRoot);
                winePrefix = compatRoot;
            }
            else
            {
                compatRoot = prefixRoot;
                winePrefix = prefixRoot;
            }

            return (compatRoot, winePrefix);
        }

        if (isProton)
        {
            if (PrefixPathHelper.IsPfxPath(prefixRoot))
            {
                winePrefix = prefixRoot;
                compatRoot = GetParentOrSelf(prefixRoot);
            }
            else
            {
                var pfxPath = Path.Combine(prefixRoot, "pfx");
                var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixRoot);
                var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);

                compatRoot = prefixRoot;
                winePrefix = rootInitialized && !pfxInitialized ? prefixRoot : pfxPath;
            }

            return (compatRoot, winePrefix);
        }

        if (PrefixPathHelper.IsPfxPath(prefixRoot))
        {
            winePrefix = prefixRoot;
            compatRoot = GetParentOrSelf(prefixRoot);
            return (compatRoot, winePrefix);
        }

        var driveC = Path.Combine(prefixRoot, "drive_c");
        if (!Directory.Exists(driveC))
        {
            var pfxDir = Path.Combine(prefixRoot, "pfx");
            var pfxDriveC = Path.Combine(pfxDir, "drive_c");
            if (Directory.Exists(pfxDriveC) || Directory.Exists(pfxDir))
                winePrefix = pfxDir;
        }

        return (compatRoot, winePrefix);
    }


    private static string GetParentOrSelf(string path)
    {
        var parent = Directory.GetParent(path)?.FullName;
        return string.IsNullOrWhiteSpace(parent) ? path : parent;
    }

    private (string FileName, List<string> Arguments) BuildWinetricksCommand(string verbs, bool useUmu)
    {
        var args = SplitArgs(verbs);

        if (useUmu)
        {
            var runner = ResolveUmuRunnerPath();
            args.Insert(0, "winetricks");
            return (runner, args);
        }

        return ("winetricks", args);
    }

    private static void ApplyProtonWineFallback(Dictionary<string, string> env, string protonPath)
    {
        if (string.IsNullOrWhiteSpace(protonPath))
            return;

        var binDir = Path.Combine(protonPath, "files", "bin");
        var wine = Path.Combine(binDir, "wine");
        var wineserver = Path.Combine(binDir, "wineserver");
        var wine64 = Path.Combine(binDir, "wine64");

        if (File.Exists(wine))
            env["WINE"] = wine;
        if (File.Exists(wineserver))
            env["WINESERVER"] = wineserver;
        if (File.Exists(wine64))
            env["WINE64"] = wine64;

        if (Directory.Exists(binDir))
        {
            var basePath = env.TryGetValue("PATH", out var existingPath)
                ? existingPath
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            env["PATH"] = string.IsNullOrWhiteSpace(basePath)
                ? binDir
                : binDir + Path.PathSeparator + basePath;
        }

        var lib64 = Path.Combine(protonPath, "files", "lib64");
        var lib32 = Path.Combine(protonPath, "files", "lib");

        var ldParts = new List<string>();
        if (Directory.Exists(lib64)) ldParts.Add(lib64);
        if (Directory.Exists(lib32)) ldParts.Add(lib32);

        if (ldParts.Count > 0)
        {
            var existingLd = env.TryGetValue("LD_LIBRARY_PATH", out var ldPath)
                ? ldPath
                : Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;

            env["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(existingLd)
                ? string.Join(Path.PathSeparator, ldParts)
                : string.Join(Path.PathSeparator, ldParts) + Path.PathSeparator + existingLd;
        }

        var dllPaths = new List<string>();
        var wineLib64 = Path.Combine(lib64, "wine");
        var wineLib32 = Path.Combine(lib32, "wine");
        if (Directory.Exists(wineLib64)) dllPaths.Add(wineLib64);
        if (Directory.Exists(wineLib32)) dllPaths.Add(wineLib32);
        if (dllPaths.Count > 0)
            env["WINEDLLPATH"] = string.Join(Path.PathSeparator, dllPaths);
    }

    private string ResolveUmuRunnerPath()
    {
        var candidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            candidate = LauncherPath;

        if (string.IsNullOrWhiteSpace(candidate))
            return "umu-run";

        if (!ContainsUmuToken(candidate))
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

        return Path.IsPathRooted(PrefixPath)
            ? PrefixPath
            : Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, PrefixPath));
    }

    private bool IsProtonBased(Dictionary<string, string> env)
    {
        if (ContainsProtonHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) && ContainsProtonToken(pathCandidate);
    }

    private static bool ContainsProtonHints(Dictionary<string, string> env)
        => env.Keys.Any(k =>
            string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k, "STEAM_COMPAT_DATA_PATH", StringComparison.OrdinalIgnoreCase));

    private bool IsUmuBased(Dictionary<string, string> env)
    {
        if (ContainsUmuHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) && ContainsUmuToken(pathCandidate);
    }

    private static bool ContainsUmuHints(Dictionary<string, string> env)
        => env.Keys.Any(k => k.StartsWith("UMU_", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsUmuToken(string path)
        => path.Contains("umu", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsProtonToken(string path)
        => path.Contains("proton", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitArgs(string input)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return args;

        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var c in input)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }
    
    // --- Native wrapper chain (Tri-state; item-level) ---

    private static bool TryMakeDataRelativeIfInsideDataRoot(string absolutePath, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(absolutePath);

        if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal) ||
            fullPath.StartsWith(dataRootWithSep, StringComparison.Ordinal))
        {
            relativePath = Path.GetRelativePath(dataRoot, fullPath);
            return true;
        }

        return false;
    }
    
    public enum WrapperMode
    {
        Inherit,
        None,
        Override
    }

    public sealed partial class LaunchWrapperRow : ObservableObject
    {
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string _args = string.Empty;
        [ObservableProperty] private string _source = string.Empty;

        public LaunchWrapperRow()
        {
        }

        public LaunchWrapperRow(LaunchWrapper wrapper, string? source = null)
        {
            Path = wrapper.Path ?? string.Empty;
            Args = wrapper.Args ?? string.Empty;
            Source = source ?? string.Empty;
        }

        public LaunchWrapper ToModel()
            => new LaunchWrapper
            {
                Path = Path?.Trim() ?? string.Empty,
                Args = string.IsNullOrWhiteSpace(Args) ? null : Args
            };
    }

    [ObservableProperty]
    private WrapperMode _nativeWrapperMode = WrapperMode.Inherit;

    public ObservableCollection<LaunchWrapperRow> NativeWrappers { get; } = new();
    public ObservableCollection<LaunchWrapperRow> InheritedWrappers { get; } = new();

    private string _inheritedWrappersInfo = Strings.EditMedia_InheritedWrappersNone;

    public bool HasInheritedWrappers => InheritedWrappers.Count > 0;
    public string InheritedWrappersInfo => _inheritedWrappersInfo;

    public IRelayCommand AddNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperDownCommand { get; }

    public bool IsNativeWrapperInherit
    {
        get => NativeWrapperMode == WrapperMode.Inherit;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Inherit;
        }
    }

    public bool IsNativeWrapperNone
    {
        get => NativeWrapperMode == WrapperMode.None;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.None;
        }
    }

    public bool IsNativeWrapperOverride
    {
        get => NativeWrapperMode == WrapperMode.Override;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Override;
        }
    }

    private void InitializeNativeWrapperUiFromItem()
    {
        // Important: detach old rows (in case the view model instance is reused).
        foreach (var row in NativeWrappers)
            UnwireWrapperRow(row);
        
        NativeWrappers.Clear();

        if (_originalItem.NativeWrappersOverride == null)
        {
            NativeWrapperMode = WrapperMode.Inherit;
            return;
        }

        if (_originalItem.NativeWrappersOverride.Count == 0)
        {
            NativeWrapperMode = WrapperMode.None;
            return;
        }

        NativeWrapperMode = WrapperMode.Override;
        foreach (var w in _originalItem.NativeWrappersOverride)
        {
            var row = new LaunchWrapperRow(w, Strings.Common_SourceItem);
            WireWrapperRow(row);
            NativeWrappers.Add(row);
        }
    }

    private void WireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged += OnWrapperRowPropertyChanged;
    }

    private void UnwireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged -= OnWrapperRowPropertyChanged;
    }

    private void OnWrapperRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Whenever Path/Args change, update the preview (users expect it to be live).
        if (e.PropertyName == nameof(LaunchWrapperRow.Path) ||
            e.PropertyName == nameof(LaunchWrapperRow.Args))
        {
            if (sender is LaunchWrapperRow row && !string.Equals(row.Source, Strings.Common_SourceItem, StringComparison.Ordinal))
                row.Source = Strings.Common_SourceItem;

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        }
    }
    
    /// <summary>
    /// Attach change tracking to an environment override row so that
    /// edits to Key/Value immediately refresh the launch preview.
    /// </summary>
    private void WireEnvRow(EnvVarRow row)
    {
        row.PropertyChanged += OnEnvRowPropertyChanged;
    }

    /// <summary>
    /// Detach change tracking from an environment override row.
    /// </summary>
    private void UnwireEnvRow(EnvVarRow row)
    {
        row.PropertyChanged -= OnEnvRowPropertyChanged;
    }

    private void OnEnvRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only rebuild the preview for changes that actually affect the
        // environment prefix (Key/Value). This keeps updates lean
        if (e.PropertyName == nameof(EnvVarRow.Key) ||
            e.PropertyName == nameof(EnvVarRow.Value))
        {
            if (sender is EnvVarRow row && row.IsInherited)
            {
                row.IsInherited = false;
                row.Source = Strings.Common_SourceItem;
            }

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private void InitializeEnvironmentOverridesFromItem()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_originalItem.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in _originalItem.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                overrides[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        RebuildEnvironmentOverridesFromInheritance(overrides);
    }

    private Dictionary<string, string> CaptureCurrentEnvironmentOverrides()
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            overrides[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        return overrides;
    }

    private void RebuildEnvironmentOverridesFromInheritance(Dictionary<string, string> itemOverrides)
    {
        var (inheritedEnv, inheritedSources) = ResolveInheritedEnvironmentOverridesWithSources();

        EnvironmentOverrides.Clear();
        var rowsByKey = new Dictionary<string, EnvVarRow>(StringComparer.Ordinal);

        foreach (var kv in inheritedEnv)
        {
            var source = inheritedSources.TryGetValue(kv.Key, out var label) ? label : string.Empty;
            var row = new EnvVarRow
            {
                Key = kv.Key,
                Value = kv.Value,
                IsInherited = true,
                Source = source
            };

            rowsByKey[kv.Key] = row;
        }

        foreach (var kv in itemOverrides)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            var key = kv.Key.Trim();
            var value = kv.Value ?? string.Empty;

            if (rowsByKey.TryGetValue(key, out var row))
            {
                row.Value = value;
                row.IsInherited = false;
                row.Source = Strings.Common_SourceItem;
            }
            else
            {
                row = new EnvVarRow
                {
                    Key = key,
                    Value = value,
                    IsInherited = false,
                    Source = Strings.Common_SourceItem
                };
                rowsByKey[key] = row;
            }
        }

        foreach (var row in rowsByKey.Values.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            EnvironmentOverrides.Add(row);
    }

    private (Dictionary<string, string> Env, Dictionary<string, string> Sources) ResolveInheritedEnvironmentOverridesWithSources()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        EmulatorConfig? effectiveEmulator = null;
        if (MediaType == MediaType.Emulator)
            effectiveEmulator = ResolveSelectedEmulatorConfig();

        if (effectiveEmulator?.EnvironmentOverrides is { Count: > 0 })
        {
            var emulatorName = string.IsNullOrWhiteSpace(effectiveEmulator.Name)
                ? effectiveEmulator.Id
                : effectiveEmulator.Name;

            foreach (var kv in effectiveEmulator.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                var key = kv.Key.Trim();
                env[key] = kv.Value ?? string.Empty;
                sources[key] = string.Format(Strings.Common_SourceEmulatorFormat, emulatorName);
            }
        }

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            foreach (var node in chain)
            {
                if (node.EnvironmentOverrides == null)
                    continue;

                if (node.EnvironmentOverrides.Count > 0)
                {
                    foreach (var kv in node.EnvironmentOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;

                        var key = kv.Key.Trim();
                        env[key] = kv.Value ?? string.Empty;
                        sources[key] = string.Format(Strings.Common_SourceNodeFormat, node.Name);
                    }
                }

                break;
            }
        }

        return (env, sources);
    }
    
    partial void OnNativeWrapperModeChanged(WrapperMode value)
    {
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();

        if (value == WrapperMode.Override &&
            NativeWrappers.Count == 0 &&
            InheritedWrappers.Count > 0)
        {
            foreach (var wrapper in InheritedWrappers)
            {
                var row = new LaunchWrapperRow(wrapper.ToModel(), wrapper.Source);
                WireWrapperRow(row);
                NativeWrappers.Add(row);
            }
        }
    }

    private void AddNativeWrapper()
    {
        var row = new LaunchWrapperRow();
        row.Source = Strings.Common_SourceItem;
        WireWrapperRow(row);
        NativeWrappers.Add(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void RemoveNativeWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;

        UnwireWrapperRow(row);
        NativeWrappers.Remove(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx <= 0) return;
        NativeWrappers.Move(idx, idx - 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= NativeWrappers.Count - 1) return;
        NativeWrappers.Move(idx, idx + 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }
    
    // --- Metadata Properties (Temporary Buffer) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssetFilePrefix))]
    [NotifyPropertyChangedFor(nameof(AssetFileExample))]
    private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private DateTimeOffset? _releaseDate; 
    [ObservableProperty] private PlayStatus _status;

    // --- Launch Config Properties ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsEmulatorMode))]
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(IsNativeMode))]
    [NotifyPropertyChangedFor(nameof(IsWrapperEditorVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private MediaType _mediaType;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private EmulatorProfileOption? _selectedEmulatorProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherArgs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgConfigPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgDataPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgCachePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgStatePath = string.Empty;

    [ObservableProperty]
    private string _xdgBasePath = string.Empty;
    
    /// <summary>
    /// Ensures that switching from "manual emulator" defaults to a proper
    /// emulator profile does not accidentally shadow the profile's own
    /// default arguments. If the current per-item arguments are still
    /// trivial (empty / "{file}"), they are cleared so the profile template
    /// can act alone.
    /// </summary>
    partial void OnSelectedEmulatorProfileChanged(EmulatorProfileOption? value)
    {
        if (value == null)
            return;

        switch (value.Kind)
        {
            case EmulatorProfileOption.OptionKind.Native:
                MediaType = MediaType.Native;
                break;

            case EmulatorProfileOption.OptionKind.Inherit:
            case EmulatorProfileOption.OptionKind.Manual:
            case EmulatorProfileOption.OptionKind.Emulator:
                MediaType = MediaType.Emulator;
                break;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Emulator &&
            IsTrivialLauncherArgs(LauncherArgs))
        {
            LauncherArgs = string.Empty;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Inherit &&
            ResolveInheritedEmulator() != null &&
            IsTrivialLauncherArgs(LauncherArgs))
        {
            LauncherArgs = string.Empty;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Manual &&
            string.IsNullOrWhiteSpace(LauncherArgs))
        {
            LauncherArgs = "{file}";
        }

        // Keep preview and "Copy" button state in sync with the new profile
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();

        RefreshInheritedWrappers();
        RebuildEnvironmentOverridesFromInheritance(CaptureCurrentEnvironmentOverrides());
    }
    
    // hard guarantee that PreviewText updates when LauncherArgs changes
    partial void OnLauncherArgsChanged(string? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
    
    // if typing LauncherPath should also update preview immediately
    partial void OnLauncherPathChanged(string? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(EffectiveWorkingDirectory));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgConfigPathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgDataPathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgCachePathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgStatePathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgBasePathChanged(string value)
    {
        ApplyXdgBaseCommand.NotifyCanExecuteChanged();
    }

    partial void OnTitleChanged(string value)
    {
        CopyAssetPrefixCommand.NotifyCanExecuteChanged();
    }
    
    [ObservableProperty] private string _overrideWatchProcess = string.Empty;

    public bool IsCustomLauncherVisible => IsManualEmulator;
    
    public bool IsNativeMode => MediaType == MediaType.Native;
    
    /// <summary>
    /// Controls visibility of the wrapper editor section in the UI.
    /// Wrappers are meaningful for both Native and Emulator items,
    /// but not for pure Command-type entries.
    /// </summary>
    public bool IsWrapperEditorVisible =>
        MediaType == MediaType.Native ||
        MediaType == MediaType.Emulator;
    
    // --- Asset Management ---
    
    // We bind directly to the item's Assets collection.
    // Since FileService updates the list live, the UI immediately reflects all changes.
    public ObservableCollection<MediaAsset> Assets => _originalItem.Assets;

    public string AssetFilePrefix => FileManagementService.BuildItemAssetPrefix(Title, _originalItem.Id);

    public string AssetFileExample => string.Format(Strings.EditMedia_AssetsPrefixExampleFormat, AssetFilePrefix);

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(DeleteAssetCommand))]
    private MediaAsset? _selectedAsset;

    // --- Commands ---
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IAsyncRelayCommand DeleteAssetCommand { get; }
    
    public IAsyncRelayCommand BrowseLauncherCommand { get; }
    public IAsyncRelayCommand BrowseWorkingDirectoryCommand { get; }
    public IAsyncRelayCommand BrowseXdgConfigCommand { get; }
    public IAsyncRelayCommand BrowseXdgDataCommand { get; }
    public IAsyncRelayCommand BrowseXdgCacheCommand { get; }
    public IAsyncRelayCommand BrowseXdgStateCommand { get; }
    public IAsyncRelayCommand BrowseXdgBaseCommand { get; }
    public IRelayCommand ApplyXdgBaseCommand { get; }
    public IRelayCommand<Window?> SaveAndCloseCommand { get; }
    public IRelayCommand<Window?> CancelAndCloseCommand { get; }


    public IStorageProvider? StorageProvider { get; set; }
    
    public bool HasAssetChanges { get; private set; }

    // --- UI Lists ---
    public ObservableCollection<EmulatorProfileOption> AvailableEmulators { get; } = new();
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    public IAsyncRelayCommand<Window?> CopyPreviewCommand { get; }
    public IAsyncRelayCommand<Window?> CopyAssetPrefixCommand { get; }
    
    public EditMediaViewModel(
        MediaItem item,
        AppSettings settings,
        FileManagementService fileService,
        List<string> nodePath,
        EmulatorConfig? inheritedEmulator = null,
        ObservableCollection<MediaNode>? rootNodes = null,
        MediaNode? parentNode = null)
    {
        _originalItem = item;
        _fileService = fileService;
        _nodePath = nodePath; 
        _inheritedEmulator = inheritedEmulator;
        
        _rootNodes = rootNodes ?? new ObservableCollection<MediaNode>();
        _parentNode = parentNode;
        _settings = settings;
        _assetsChangedHandler = (_, _) => ScheduleSortAssets();
        _originalItem.Assets.CollectionChanged += _assetsChangedHandler;

        // Prefix commands
        GeneratePrefixCommand = new RelayCommand(GeneratePrefix);
        OpenPrefixFolderCommand = new RelayCommand(OpenPrefixFolder, () => HasPrefix);
        ClearPrefixCommand = new RelayCommand(ClearPrefix, () => HasPrefix);
        RunWinetricksCommand = new AsyncRelayCommand<Window?>(RunWinetricksAsync, CanRunWinetricks);
        
        // Primary launch file command
        ChangePrimaryFileCommand = new AsyncRelayCommand(ChangePrimaryFileAsync);
        
        // Environment overrides commands
        AddEnvironmentVariableCommand = new RelayCommand(AddEnvironmentVariable);
        RemoveEnvironmentVariableCommand = new RelayCommand<EnvVarRow?>(RemoveEnvironmentVariable);

        // General commands.
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);
        BrowseWorkingDirectoryCommand = new AsyncRelayCommand(BrowseWorkingDirectoryAsync);
        BrowseXdgConfigCommand = new AsyncRelayCommand(BrowseXdgConfigAsync);
        BrowseXdgDataCommand = new AsyncRelayCommand(BrowseXdgDataAsync);
        BrowseXdgCacheCommand = new AsyncRelayCommand(BrowseXdgCacheAsync);
        BrowseXdgStateCommand = new AsyncRelayCommand(BrowseXdgStateAsync);
        BrowseXdgBaseCommand = new AsyncRelayCommand(BrowseXdgBaseAsync);
        ApplyXdgBaseCommand = new RelayCommand(ApplyXdgBase, CanApplyXdgBase);
        
        // Generic asset commands
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new AsyncRelayCommand(DeleteSelectedAssetAsync, () => SelectedAsset != null);

        // Native wrapper editor commands
        AddNativeWrapperCommand = new RelayCommand(AddNativeWrapper);
        RemoveNativeWrapperCommand = new RelayCommand<LaunchWrapperRow?>(RemoveNativeWrapper);
        MoveNativeWrapperUpCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperUp,
            row => row != null && NativeWrappers.IndexOf(row) > 0);

        MoveNativeWrapperDownCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperDown,
            row => row != null && NativeWrappers.IndexOf(row) >= 0 && NativeWrappers.IndexOf(row) < NativeWrappers.Count - 1);

        CopyPreviewCommand = new AsyncRelayCommand<Window?>(CopyPreviewAsync, CanCopyPreview);
        CopyAssetPrefixCommand = new AsyncRelayCommand<Window?>(CopyAssetPrefixAsync, CanCopyAssetPrefix);
        
        NativeWrappers.CollectionChanged += (_, e) =>
        {
            // Wire/unwire rows on Add/Remove – otherwise Preview would only update when clicking "+ Wrapper"
            if (e.OldItems != null)
                foreach (var oldItem in e.OldItems.OfType<LaunchWrapperRow>())
                    UnwireWrapperRow(oldItem);

            if (e.NewItems != null)
                foreach (var newItem in e.NewItems.OfType<LaunchWrapperRow>())
                    WireWrapperRow(newItem);

            MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
            MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PreviewText));
        };
        
        // Keep environment overrides in sync with the preview. Whenever rows
        // are added/removed, we attach/detach change tracking so that edits
        // to Key/Value immediately refresh the preview prefix.
        EnvironmentOverrides.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (var oldItem in e.OldItems.OfType<EnvVarRow>())
                    UnwireEnvRow(oldItem);

            if (e.NewItems != null)
                foreach (var newItem in e.NewItems.OfType<EnvVarRow>())
                    WireEnvRow(newItem);

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        };
        
        // Dialog closes itself (less window manager / modal noise)
        SaveAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            try
            {
                Save();
            }
            finally
            {
                DetachAssetHandlers();
                win?.Close(true);
            }
        });

        CancelAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            DetachAssetHandlers();
            win?.Close(false);
        });
        
        LoadItemData();
        InitializeEmulators(settings);
        InitializeNativeWrapperUiFromItem();
        RefreshInheritedWrappers();
        InitializeEnvironmentOverridesFromItem();
        
        // After initialization, ensure commands reflect the current list state
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        
        // Ensure preview reflects the current primary file at startup
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(PreviewText));

        SortAssets();
    }

    private bool CanCopyPreview(Window? _)
        => !string.IsNullOrWhiteSpace(PreviewText);

    private bool CanCopyAssetPrefix(Window? _)
        => !string.IsNullOrWhiteSpace(AssetFilePrefix);
    
    private async Task CopyPreviewAsync(Window? win)
    {
        try
        {
            // Window is a TopLevel, so Clipboard is available here.
            var text = PreviewText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Optional: trim to keep copy clean (no trailing whitespace)
            text = text.Trim();

            // Strip leading prompt marker ("> ") so the copied command can be
            // pasted directly into a terminal.
            if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                text = text.Substring(2).TrimStart();
            }
            
            if (win?.Clipboard != null)
                await win.Clipboard.SetTextAsync(text);
        }
        catch
        {
            // best-effort: clipboard should never break the dialog
        }
    }

    private async Task CopyAssetPrefixAsync(Window? win)
    {
        try
        {
            var text = AssetFilePrefix ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (win?.Clipboard != null)
                await win.Clipboard.SetTextAsync(text.Trim());
        }
        catch
        {
            // best-effort: clipboard should never break the dialog
        }
    }
    
    private void LoadItemData()
    {
        // Load metadata into the temporary buffer
        Title = _originalItem.Title;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        
        // Load launch configuration
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        WorkingDirectory = _originalItem.WorkingDirectory ?? string.Empty;
        XdgConfigPath = _originalItem.XdgConfigPath ?? string.Empty;
        XdgDataPath = _originalItem.XdgDataPath ?? string.Empty;
        XdgCachePath = _originalItem.XdgCachePath ?? string.Empty;
        XdgStatePath = _originalItem.XdgStatePath ?? string.Empty;
        XdgBasePath = _originalItem.XdgBasePath ?? string.Empty;
        
        // Prefix
        PrefixPath = _originalItem.PrefixPath ?? string.Empty;
        WineArchSelection = ResolveWineArchSelection(_originalItem.WineArchOverride, _originalItem.EnvironmentOverrides);
        
        // Arguments: load exactly what is stored on the item
        LauncherArgs = _originalItem.LauncherArgs ?? string.Empty;
        
        // Assets do not need to be loaded separately because we bind directly to _originalItem.Assets
        // The FileService should ensure the assets list is up to date before opening this dialog
        // (via something like RefreshItemAssets)
    }

    private void DetachAssetHandlers()
    {
        if (_assetsChangedHandler == null)
            return;

        _originalItem.Assets.CollectionChanged -= _assetsChangedHandler;
        _assetsChangedHandler = null;
    }

    private static readonly AssetType[] AssetTypeOrder =
    {
        AssetType.Cover,
        AssetType.Wallpaper,
        AssetType.Logo,
        AssetType.Video,
        AssetType.Music,
        AssetType.Marquee,
        AssetType.Banner,
        AssetType.Bezel,
        AssetType.ControlPanel,
        AssetType.Manual
    };

    private bool _isSortingAssets;
    private bool _isSortAssetsScheduled;

    private void ScheduleSortAssets()
    {
        if (_isSortingAssets || _isSortAssetsScheduled)
            return;

        _isSortAssetsScheduled = true;

        // Defer sorting to avoid modifying the collection inside CollectionChanged.
        // NOTE: UiThreadHelper.Post executes immediately when already on the UI thread,
        // so we must use Dispatcher.UIThread.Post to ensure true deferral.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isSortAssetsScheduled = false;
            SortAssets();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void SortAssets()
    {
        if (_isSortingAssets)
            return;

        _isSortingAssets = true;
        try
        {
            if (_originalItem.Assets.Count <= 1)
                return;

            var orderMap = new Dictionary<AssetType, int>(AssetTypeOrder.Length);
            for (var i = 0; i < AssetTypeOrder.Length; i++)
                orderMap[AssetTypeOrder[i]] = i;

            var indexed = _originalItem.Assets
                .Select((asset, index) => new { asset, index })
                .ToList();

            var sorted = indexed
                .OrderBy(entry => orderMap.TryGetValue(entry.asset.Type, out var order) ? order : int.MaxValue)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.asset)
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var asset = sorted[i];
                var oldIndex = _originalItem.Assets.IndexOf(asset);
                if (oldIndex != i)
                    _originalItem.Assets.Move(oldIndex, i);
            }
        }
        finally
        {
            _isSortingAssets = false;
        }
    }

    partial void OnMediaTypeChanged(MediaType value)
    {
        // When switching to Native, strip any leftover {file} placeholder (friendlier defaults)
        if (value == MediaType.Native)
        {
            if (string.Equals(LauncherArgs?.Trim(), "{file}", StringComparison.Ordinal) ||
                string.Equals(LauncherArgs?.Trim(), "\"{file}\"", StringComparison.Ordinal))
            {
                LauncherArgs = string.Empty;
            }
        }

        // When switching to Emulator and we are in manual-emulator mode (no profile selected),
        // insert a default {file} placeholder if the field is empty
        if (value == MediaType.Emulator && IsManualEmulator)
        {
            if (string.IsNullOrWhiteSpace(LauncherArgs))
                LauncherArgs = "{file}";
        }

        RefreshInheritedWrappers();
        RebuildEnvironmentOverridesFromInheritance(CaptureCurrentEnvironmentOverrides());
    }
    
    private void InitializeEmulators(AppSettings settings)
    {
        AvailableEmulators.Clear();
        ResolveInheritedEmulatorInfo();

        var inheritedEmulatorName = _resolvedInheritedEmulator != null
            ? (string.IsNullOrWhiteSpace(_resolvedInheritedEmulator.Name)
                ? _resolvedInheritedEmulator.Id
                : _resolvedInheritedEmulator.Name)
            : null;

        var inheritedLabel = !string.IsNullOrWhiteSpace(inheritedEmulatorName) &&
                             !string.IsNullOrWhiteSpace(_resolvedInheritedEmulatorSource)
            ? string.Format(Strings.NodeSettings_InheritedEmulatorInfoFormat, inheritedEmulatorName, _resolvedInheritedEmulatorSource)
            : $"{Strings.NodeSettings_InheritedEmulatorNone} {Strings.EditMedia_InheritedEmulatorFallbackHint}";

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Inherit,
            inheritedLabel,
            _resolvedInheritedEmulator));

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Native,
            Strings.Type_Native));

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Manual,
            "Custom / Manual"));

        foreach (var emu in settings.Emulators)
        {
            AvailableEmulators.Add(new EmulatorProfileOption(
                EmulatorProfileOption.OptionKind.Emulator,
                emu.Name,
                emu));
        }

        if (!string.IsNullOrEmpty(_originalItem.EmulatorId))
        {
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                e => e.Kind == EmulatorProfileOption.OptionKind.Emulator &&
                     string.Equals(e.Emulator?.Id, _originalItem.EmulatorId, StringComparison.Ordinal));
        }
        else if (_originalItem.MediaType == MediaType.Emulator)
        {
            if (!string.IsNullOrWhiteSpace(_originalItem.LauncherPath))
            {
                SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                    e => e.Kind == EmulatorProfileOption.OptionKind.Manual);
            }
            else
            {
                SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                    e => e.Kind == EmulatorProfileOption.OptionKind.Inherit);
            }
        }
        else
        {
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                e => e.Kind == EmulatorProfileOption.OptionKind.Native);
        }

        SelectedEmulatorProfile ??= AvailableEmulators.FirstOrDefault();
    }

    private void ResolveInheritedEmulatorInfo()
    {
        _resolvedInheritedEmulator = null;
        _resolvedInheritedEmulatorSource = null;

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse();

            foreach (var node in chain)
            {
                if (string.IsNullOrWhiteSpace(node.DefaultEmulatorId))
                    continue;

                _resolvedInheritedEmulator = _settings.Emulators
                    .FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
                _resolvedInheritedEmulatorSource = node.Name;
                break;
            }
        }

        if (_resolvedInheritedEmulator == null && _inheritedEmulator != null)
            _resolvedInheritedEmulator = _inheritedEmulator;
    }

    private EmulatorConfig? ResolveInheritedEmulator()
    {
        if (_resolvedInheritedEmulator == null && _resolvedInheritedEmulatorSource == null)
            ResolveInheritedEmulatorInfo();

        return _resolvedInheritedEmulator;
    }

    private EmulatorConfig? ResolveSelectedEmulatorConfig()
    {
        var selection = SelectedEmulatorProfile;
        if (selection == null)
            return null;

        return selection.Kind switch
        {
            EmulatorProfileOption.OptionKind.Emulator => selection.Emulator,
            EmulatorProfileOption.OptionKind.Inherit => ResolveInheritedEmulator(),
            _ => null
        };
    }

    // --- Asset Actions ---

    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null) return;

        // Create file type filters based on the selected asset type.
        var fileTypes = type switch
        {
            AssetType.Music => new[]
            {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.sid" }
                }
            },
            AssetType.Video => new[]
            {
                new FilePickerFileType("Video")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm" }
                }
            },
            AssetType.Manual => new[]
            {
                new FilePickerFileType("Documents")
                {
                    Patterns = new[] { "*.pdf", "*.cbz", "*.txt", "*.md", "*.rtf", "*.html", "*.htm", "*.jpg", "*.jpeg", "*.png" }
                }
            },
            _ => new[] { FilePickerFileTypes.ImageAll } // Default for Cover, Logo, Wallpaper, Marquee, etc
        };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {type}",
            AllowMultiple = true,
            FileTypeFilter = fileTypes
        });

        if (result == null || result.Count == 0) return;

        foreach (var file in result)
        {
            // The FileManagementService handles copying, renaming, and adding the asset to the list
            var imported = await _fileService.ImportAssetAsync(file.Path.LocalPath, _originalItem, _nodePath, type);
            if (imported != null)
            {
                await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(imported));
                HasAssetChanges = true;
            }
        }
    }

    private async Task DeleteSelectedAssetAsync()
    {
        if (SelectedAsset == null) 
            return;

        var asset = SelectedAsset;

        // 1) Remove from collection on UI thread (immediate UI feedback)
        await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Remove(asset));

        try
        {
            // 2) Delete file (IO-bound)
            _fileService.DeleteAssetFile(asset);
            HasAssetChanges = true;

            // Clear selection so the delete button hides/updates correctly
            SelectedAsset = null;
        }
        catch
        {
            // 3) Rollback in collection if delete failed
            await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(asset));
        }
    }

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
            var chain = GetNodeChain(_parentNode, _rootNodes);
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
            var chain = GetNodeChain(_parentNode, _rootNodes);
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

    private static List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<MediaNode> { node };

            var chain = GetNodeChain(target, node.Children);
            if (chain.Count > 0)
            {
                chain.Insert(0, node);
                return chain;
            }
        }
        return new List<MediaNode>();
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
    
    private async Task ChangePrimaryFileAsync()
    {
        if (StorageProvider == null)
            return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select launch file (main executable / Disc 1)",
            AllowMultiple = false,
            // No FileTypeFilter on purpose: executables / scripts may have no extension
        });

        var file = result?.FirstOrDefault();
        if (file == null)
            return;

        var path = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Decide how to store the launch file path based on global settings:
        // - PreferPortableLaunchPaths == false (default): store absolute paths (classic behavior)
        // - PreferPortableLaunchPaths == true: store a DataRoot-relative path so Retromind + Games
        //   can be moved together as a portable bundle
        string storedPath;
        MediaFileKind storedKind;

        if (_settings.PreferPortableLaunchPaths)
        {
            if (TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
            {
                storedPath = relativePath;
                storedKind = MediaFileKind.LibraryRelative;
            }
            else
            {
                storedPath = path;
                storedKind = MediaFileKind.Absolute;
            }
        }
        else
        {
            storedPath = path;
            storedKind = MediaFileKind.Absolute;
        }
        
        // Update the primary file in the item's Files list
        // If there is no file yet, add a new entry
        var primary = _originalItem.GetPrimaryFile();
        if (primary == null)
        {
            primary = new MediaFileRef
            {
                Kind = storedKind,
                Path = storedPath,
                Index = 1
            };
            var list = _originalItem.Files ?? new List<MediaFileRef>();
            list.Add(primary);
            _originalItem.Files = list;
        }
        else
        {
            primary.Path = storedPath;
            primary.Kind = storedKind;
        }

        // Notify UI about the change:
        // - display path
        // - effective working directory
        // - preview command line (uses GetPrimaryLaunchPath())
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(EffectiveWorkingDirectory));
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
    
    private async Task BrowseLauncherAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Launcher Executable",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0) LauncherPath = result[0].Path.LocalPath;
    }

    private async Task BrowseWorkingDirectoryAsync()
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectWorkingDirectory,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        var path = result[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settings.PreferPortableLaunchPaths &&
            TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
        {
            WorkingDirectory = relativePath;
        }
        else
        {
            WorkingDirectory = path;
        }
    }

    private async Task BrowseXdgConfigAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgConfigFolder, path => XdgConfigPath = path);

    private async Task BrowseXdgDataAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgDataFolder, path => XdgDataPath = path);

    private async Task BrowseXdgCacheAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgCacheFolder, path => XdgCachePath = path);

    private async Task BrowseXdgStateAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgStateFolder, path => XdgStatePath = path);

    private async Task BrowseXdgBaseAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgBaseFolder, path => XdgBasePath = path);

    private async Task BrowseXdgFolderAsync(string title, Action<string> setPath)
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        var path = result[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settings.PreferPortableLaunchPaths &&
            TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
        {
            setPath(relativePath);
        }
        else
        {
            setPath(path);
        }
    }

    private bool CanApplyXdgBase()
        => !string.IsNullOrWhiteSpace(XdgBasePath);

    private void ApplyXdgBase()
    {
        var basePath = XdgBasePath?.Trim();
        if (string.IsNullOrWhiteSpace(basePath))
            return;

        XdgConfigPath = Path.Combine(basePath, "config");
        XdgDataPath = Path.Combine(basePath, "data");
        XdgCachePath = Path.Combine(basePath, "cache");
        XdgStatePath = Path.Combine(basePath, "state");
    }

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
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;

        // Prefix: store null when not used
        _originalItem.PrefixPath = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        _originalItem.WineArchOverride = WineArchSelection switch
        {
            WineArchOption.Win32 => "win32",
            WineArchOption.Win64 => "win64",
            _ => null
        };
        
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
                _originalItem.LauncherPath = LauncherPath;
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
                _originalItem.NativeWrappersOverride = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();
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

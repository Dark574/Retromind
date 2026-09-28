using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Prepares the environment inherited by external launch processes.
/// Prefix creation remains a separate launcher concern.
/// </summary>
public sealed class LaunchEnvironmentService
{
    private static readonly string[] PortableEnvironmentKeys =
    [
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME",
        "DOTNET_CLI_HOME"
    ];

    public bool IsRunningInsideAppImageRuntime =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDIR"));

    public void PrepareHostCommand(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        HostProcessEnvironmentSanitizer.Sanitize(startInfo);
        PrepareExternalProcess(startInfo);
        ApplyEnvironmentOverrides(startInfo, environmentOverrides);
    }

    public void PrepareCommand(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        PrepareExternalProcess(startInfo);
        ApplyEnvironmentOverrides(startInfo, environmentOverrides);
    }

    public void PrepareNativeOrEmulator(
        ProcessStartInfo startInfo,
        MediaItem item,
        EmulatorConfig? emulator,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        PrepareExternalProcess(startInfo);

        if (environmentOverrides is { Count: > 0 })
        {
            ApplyEnvironmentOverrides(startInfo, environmentOverrides);
        }
        else
        {
            ApplyEnvironmentOverrides(startInfo, emulator?.EnvironmentOverrides);
            ApplyEnvironmentOverrides(startInfo, item.EnvironmentOverrides);
        }

        ApplyEmulatorXdgOverrides(startInfo, emulator);
        ApplyItemXdgOverrides(startInfo, item);

        LogIfSet(startInfo, "PROTONPATH");
        LogIfSet(startInfo, "STEAM_COMPAT_DATA_PATH");
        LogIfSet(startInfo, "WINEPREFIX");
    }

    private static void PrepareExternalProcess(ProcessStartInfo startInfo)
    {
        SanitizeAppImageRuntimeEnvironment(
            startInfo,
            Environment.GetEnvironmentVariable("APPIMAGE"),
            Environment.GetEnvironmentVariable("APPDIR"));
        SanitizePortableEnvironment(startInfo);
    }

    internal static void SanitizeAppImageRuntimeEnvironment(
        ProcessStartInfo startInfo,
        string? appImage,
        string? appDir)
    {
        if (startInfo.UseShellExecute ||
            (string.IsNullOrWhiteSpace(appImage) && string.IsNullOrWhiteSpace(appDir)))
        {
            return;
        }

        if (startInfo.EnvironmentVariables.ContainsKey("LD_LIBRARY_PATH"))
        {
            var currentValue = startInfo.EnvironmentVariables["LD_LIBRARY_PATH"];
            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                var appDirPrefixes = BuildAppImageLdPrefixes(appDir);
                var filtered = currentValue
                    .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path => !IsAppImageInjectedLdSegment(path, appDirPrefixes))
                    .ToArray();

                if (filtered.Length == 0)
                    startInfo.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
                else
                    startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = string.Join(':', filtered);
            }
        }

        startInfo.EnvironmentVariables.Remove("VLC_PLUGIN_PATH");
    }

    internal static void SanitizePortableEnvironment(ProcessStartInfo startInfo)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var portableHomeRoot = NormalizePath(Path.Combine(AppPaths.DataRoot, "Home"));
        if (string.IsNullOrWhiteSpace(portableHomeRoot))
            return;

        foreach (var key in PortableEnvironmentKeys)
            RemoveIfUnderPortableHome(startInfo, key, portableHomeRoot);

        if (!startInfo.EnvironmentVariables.ContainsKey("HOME"))
            return;

        var homeValue = startInfo.EnvironmentVariables["HOME"];
        if (string.IsNullOrWhiteSpace(homeValue) || !IsUnderPortableHome(homeValue, portableHomeRoot))
            return;

        var realHome = EnvironmentPathHelper.TryGetRealUserHomePath();
        if (!string.IsNullOrWhiteSpace(realHome))
            startInfo.EnvironmentVariables["HOME"] = realHome;
        else
            startInfo.EnvironmentVariables.Remove("HOME");
    }

    internal static void ApplyEnvironmentOverrides(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is not { Count: > 0 })
            return;

        foreach (var (rawKey, rawValue) in overrides)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
                continue;

            var key = rawKey.Trim();
            var value = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, rawValue);
            startInfo.EnvironmentVariables[key] = value;
        }
    }

    private static void ApplyEmulatorXdgOverrides(ProcessStartInfo startInfo, EmulatorConfig? emulator)
    {
        if (emulator == null)
            return;

        switch (emulator.XdgMode)
        {
            case EmulatorConfig.XdgOverrideMode.Inherit:
                return;

            case EmulatorConfig.XdgOverrideMode.Host:
                startInfo.EnvironmentVariables.Remove("XDG_CONFIG_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_DATA_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_CACHE_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_STATE_HOME");
                return;

            case EmulatorConfig.XdgOverrideMode.Custom:
                SetXdgPath(startInfo, "XDG_CONFIG_HOME", emulator.XdgConfigPath);
                SetXdgPath(startInfo, "XDG_DATA_HOME", emulator.XdgDataPath);
                SetXdgPath(startInfo, "XDG_CACHE_HOME", emulator.XdgCachePath);
                SetXdgPath(startInfo, "XDG_STATE_HOME", emulator.XdgStatePath);
                return;
        }
    }

    private static void ApplyItemXdgOverrides(ProcessStartInfo startInfo, MediaItem item)
    {
        if (item.MediaType == MediaType.Command)
            return;

        SetXdgPath(startInfo, "XDG_CONFIG_HOME", item.XdgConfigPath);
        SetXdgPath(startInfo, "XDG_DATA_HOME", item.XdgDataPath);
        SetXdgPath(startInfo, "XDG_CACHE_HOME", item.XdgCachePath);
        SetXdgPath(startInfo, "XDG_STATE_HOME", item.XdgStatePath);
    }

    private static void SetXdgPath(ProcessStartInfo startInfo, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        startInfo.EnvironmentVariables[key] = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);
    }

    private static void RemoveIfUnderPortableHome(
        ProcessStartInfo startInfo,
        string key,
        string portableHomeRoot)
    {
        if (!startInfo.EnvironmentVariables.ContainsKey(key))
            return;

        var value = startInfo.EnvironmentVariables[key];
        if (!string.IsNullOrWhiteSpace(value) && IsUnderPortableHome(value, portableHomeRoot))
            startInfo.EnvironmentVariables.Remove(key);
    }

    private static bool IsUnderPortableHome(string value, string portableHomeRoot)
    {
        var normalizedValue = NormalizePath(value);
        return normalizedValue.Equals(portableHomeRoot, StringComparison.Ordinal) ||
               normalizedValue.StartsWith(portableHomeRoot + "/", StringComparison.Ordinal);
    }

    private static string[] BuildAppImageLdPrefixes(string? appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir))
            return [];

        return
        [
            NormalizePath(Path.Combine(appDir, "usr", "lib", "vlc", "lib")),
            NormalizePath(Path.Combine(appDir, "usr", "lib"))
        ];
    }

    private static bool IsAppImageInjectedLdSegment(string segment, IReadOnlyList<string> appDirPrefixes)
    {
        var normalizedSegment = NormalizePath(segment);
        foreach (var prefix in appDirPrefixes)
        {
            if (normalizedSegment.Equals(prefix, StringComparison.Ordinal) ||
                normalizedSegment.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return normalizedSegment.StartsWith("/tmp/.mount_", StringComparison.Ordinal) &&
               (normalizedSegment.Contains("/usr/lib/vlc/lib", StringComparison.Ordinal) ||
                normalizedSegment.EndsWith("/usr/lib", StringComparison.Ordinal));
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var value = path.Replace('\\', '/').Trim();
        while (value.EndsWith("/", StringComparison.Ordinal))
            value = value[..^1];

        return value;
    }

    private static void LogIfSet(ProcessStartInfo startInfo, string key)
    {
        if (startInfo.EnvironmentVariables.ContainsKey(key))
        {
            var value = startInfo.EnvironmentVariables[key];
            if (!string.IsNullOrWhiteSpace(value))
                Debug.WriteLine($"[Launcher] ENV {key}={value}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

internal readonly record struct LaunchPlan(
    string FileName,
    string Arguments,
    bool UseShellExecute);

/// <summary>
/// Builds the executable and arguments for an already resolved media file.
/// Playlist creation, environment preparation and process execution remain
/// separate launcher concerns.
/// </summary>
internal static class LaunchPlanBuilder
{
    public static LaunchPlan Build(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        string? launchFilePath)
    {
        ArgumentNullException.ThrowIfNull(item);

        // An explicit item launcher always has priority over inherited configuration.
        if (!string.IsNullOrWhiteSpace(item.LauncherPath))
        {
            var templateArgs = string.IsNullOrWhiteSpace(item.LauncherArgs) ? "{file}" : item.LauncherArgs;
            var arguments = LaunchCommandLineHelper.BuildTemplateArguments(launchFilePath, templateArgs);
            var fileName = LaunchExecutablePathHelper.ResolveConfiguredPath(item.LauncherPath);

            return ApplyWrappers(fileName, arguments, nativeWrappers);
        }

        if (inheritedConfig != null)
        {
            var templateArgs = LaunchArgumentHelper.CombineTemplateArguments(
                inheritedConfig.Arguments,
                item.LauncherArgs);
            var arguments = LaunchCommandLineHelper.BuildTemplateArguments(launchFilePath, templateArgs);
            var fileName = LaunchExecutablePathHelper.ResolveConfiguredPath(inheritedConfig.Path);

            return ApplyWrappers(fileName, arguments, nativeWrappers);
        }

        if (string.IsNullOrWhiteSpace(launchFilePath))
        {
            throw new InvalidOperationException(
                "MediaItem.Files must contain at least one valid file for native execution.");
        }

        var nativeArguments = LaunchCommandLineHelper.BuildNativeArguments(item.LauncherArgs);
        if (nativeWrappers is { Count: > 0 })
        {
            var innerCommand = BuildInnerCommand(launchFilePath, nativeArguments);
            var wrapped = FoldWrappers(innerCommand, nativeWrappers);
            return new LaunchPlan(
                wrapped.FileName,
                wrapped.Arguments,
                wrapped.UseShellExecute);
        }

        // Linux executables must be started directly instead of being handed to xdg-open.
        var useShellExecute = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        return new LaunchPlan(launchFilePath, nativeArguments, useShellExecute);
    }

    private static LaunchPlan ApplyWrappers(
        string fileName,
        string arguments,
        IReadOnlyList<LaunchWrapper>? wrappers)
    {
        if (wrappers is not { Count: > 0 })
            return new LaunchPlan(fileName, arguments, UseShellExecute: false);

        var wrapped = FoldWrappers(BuildInnerCommand(fileName, arguments), wrappers);
        return new LaunchPlan(
            wrapped.FileName,
            wrapped.Arguments,
            wrapped.UseShellExecute);
    }

    private static string BuildInnerCommand(string fileName, string arguments)
        => string.IsNullOrWhiteSpace(arguments)
            ? LaunchCommandLineHelper.QuoteIfNeeded(fileName)
            : $"{LaunchCommandLineHelper.QuoteIfNeeded(fileName)} {arguments}";

    /// <summary>
    /// Wrapper order is outer-to-inner. For example, gamemoderun followed by
    /// mangohud produces: gamemoderun mangohud &lt;inner command&gt;.
    /// </summary>
    private static (string FileName, string Arguments, bool UseShellExecute) FoldWrappers(
        string innerCommand,
        IReadOnlyList<LaunchWrapper> wrappers)
    {
        var current = innerCommand;
        string? outerFileName = null;
        var outerArguments = string.Empty;

        for (var index = wrappers.Count - 1; index >= 0; index--)
        {
            var wrapper = wrappers[index];
            if (string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            var template = string.IsNullOrWhiteSpace(wrapper.Args) ? "{file}" : wrapper.Args;
            var argumentsWithChild = template.Contains("{file}", StringComparison.Ordinal)
                ? template.Replace("{file}", current, StringComparison.Ordinal)
                : $"{template} {current}";

            var resolvedPath = LaunchExecutablePathHelper.ResolveConfiguredPath(wrapper.Path);
            if (string.IsNullOrWhiteSpace(resolvedPath))
                continue;

            outerFileName = resolvedPath;
            // Once a wrapper becomes the child of another wrapper, its path is
            // part of the parent's argument string and must be quoted there.
            // Only trim the composed arguments: normalizing whitespace would
            // also alter valid quoted paths containing consecutive spaces.
            outerArguments = argumentsWithChild.Trim();
            current = BuildInnerCommand(outerFileName, outerArguments);
        }

        if (string.IsNullOrWhiteSpace(outerFileName))
        {
            var fallback = LaunchCommandLineHelper.SplitCommandLinePreservingArgs(current);
            return (
                fallback.FileName,
                fallback.Arguments,
                ShouldUseShellExecute(fallback.FileName));
        }

        return (outerFileName, outerArguments, ShouldUseShellExecute(outerFileName));
    }

    private static bool ShouldUseShellExecute(string fileName)
        => !RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
           !fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
}

internal static class LaunchCommandLineHelper
{
    public static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    public static (string FileName, string Arguments) SplitCommandLinePreservingArgs(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (string.Empty, string.Empty);

        var index = 0;
        while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            index++;

        if (index >= commandLine.Length)
            return (string.Empty, string.Empty);

        var inQuotes = false;
        var quoteCharacter = '"';
        var fileName = new StringBuilder();

        for (; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (inQuotes)
            {
                if (character == quoteCharacter)
                {
                    inQuotes = false;
                    continue;
                }

                fileName.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                inQuotes = true;
                quoteCharacter = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
                break;

            fileName.Append(character);
        }

        var arguments = index < commandLine.Length
            ? commandLine[index..].TrimStart()
            : string.Empty;

        return (fileName.ToString(), arguments);
    }

    public static string BuildNativeArguments(string? templateArguments)
    {
        if (string.IsNullOrWhiteSpace(templateArguments))
            return string.Empty;

        var arguments = templateArguments;
        arguments = arguments.Replace("\"{file}\"", string.Empty, StringComparison.Ordinal);
        arguments = arguments.Replace("{file}", string.Empty, StringComparison.Ordinal);
        return LaunchArgumentHelper.NormalizeWhitespace(arguments);
    }

    public static string BuildTemplateArguments(string? filePath, string? templateArguments)
    {
        var fullPath = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetFullPath(filePath);
        var fileDirectory = string.Empty;
        var fileName = string.Empty;
        var fileBase = string.Empty;

        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            fileDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            fileName = Path.GetFileName(fullPath);
            fileBase = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(fileName);
        }

        if (string.IsNullOrWhiteSpace(templateArguments))
            return QuoteIfNeeded(fullPath);

        var result = templateArguments;
        result = ReplacePlaceholder(result, "fileDir", fileDirectory);
        result = ReplacePlaceholder(result, "fileName", fileName);
        result = ReplacePlaceholder(result, "fileBase", fileBase);
        result = ReplacePlaceholder(result, "file", fullPath);
        return result;
    }

    private static string ReplacePlaceholder(string input, string name, string rawValue)
    {
        var explicitToken = $"\"{{{name}}}\"";
        if (input.Contains(explicitToken, StringComparison.Ordinal))
            return input.Replace($"{{{name}}}", rawValue, StringComparison.Ordinal);

        var quotedValue = string.IsNullOrEmpty(rawValue)
            ? string.Empty
            : rawValue.Contains(' ', StringComparison.Ordinal)
                ? $"\"{rawValue}\""
                : rawValue;
        return input.Replace($"{{{name}}}", quotedValue, StringComparison.Ordinal);
    }
}

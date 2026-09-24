using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Retromind.Services.Stores.Gog;

internal static class GogPlayTaskParser
{
    public static IReadOnlyList<GogPlayTaskInfo> Parse(JsonElement playTasks)
    {
        if (playTasks.ValueKind != JsonValueKind.Array)
            return Array.Empty<GogPlayTaskInfo>();

        var result = new List<GogPlayTaskInfo>();
        foreach (var task in playTasks.EnumerateArray())
        {
            var path = GetString(task, "path");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var arguments = ParseArguments(task);
            var workingDirectory = GetString(task, "workingDir");
            var isPrimary = task.TryGetProperty("isPrimary", out var isPrimaryElement) &&
                            isPrimaryElement.ValueKind == JsonValueKind.True;

            result.Add(new GogPlayTaskInfo(path, arguments, workingDirectory, isPrimary));
        }

        return result;
    }

    public static GogPlayTaskInfo? SelectPrimary(IReadOnlyList<GogPlayTaskInfo> playTasks)
    {
        for (var index = 0; index < playTasks.Count; index++)
        {
            if (playTasks[index].IsPrimary)
                return playTasks[index];
        }

        return playTasks.Count > 0 ? playTasks[0] : null;
    }

    private static string? ParseArguments(JsonElement task)
    {
        if (!task.TryGetProperty("arguments", out var arguments))
            return null;

        if (arguments.ValueKind == JsonValueKind.String)
            return arguments.GetString();

        if (arguments.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var value in arguments.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var argument = value.GetString();
            if (string.IsNullOrWhiteSpace(argument))
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(QuoteArgumentIfNeeded(argument));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    internal static string QuoteArgumentIfNeeded(string value)
    {
        if (value.IndexOfAny([' ', '\t', '"']) < 0)
            return value;

        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }
}

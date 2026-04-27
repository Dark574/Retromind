using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static class RunnerVersionEnvironmentHelper
{
    public static RunnerVersionConfig? FindRunnerVersionById(AppSettings settings, string? id)
    {
        if (settings == null || string.IsNullOrWhiteSpace(id))
            return null;

        return settings.RunnerVersions?.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
    }

    public static void ApplyRunnerToEnvironment(
        Dictionary<string, string> env,
        AppSettings settings,
        EmulatorConfig? emulator,
        string? runnerVersionId)
    {
        if (env == null || settings == null || string.IsNullOrWhiteSpace(runnerVersionId))
            return;

        var version = FindRunnerVersionById(settings, runnerVersionId);
        if (version == null)
            return;

        if (string.IsNullOrWhiteSpace(version.Path))
            return;

        var key = ResolveEnvironmentVariableKey(emulator, version.Kind);
        if (string.IsNullOrWhiteSpace(key))
            return;

        env[key] = version.Path.Trim();
    }

    private static string ResolveEnvironmentVariableKey(EmulatorConfig? emulator, RunnerVersionKind kind)
    {
        var intent = emulator?.RunnerType ?? EmulatorConfig.RunnerIntent.Auto;

        return intent switch
        {
            EmulatorConfig.RunnerIntent.UmuProton => "PROTONPATH",
            EmulatorConfig.RunnerIntent.Wine => "WINE",
            EmulatorConfig.RunnerIntent.Generic => kind == RunnerVersionKind.Wine ? "WINE" : "PROTONPATH",
            _ => kind == RunnerVersionKind.Wine ? "WINE" : "PROTONPATH"
        };
    }
}

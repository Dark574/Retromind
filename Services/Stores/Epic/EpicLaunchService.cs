using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Services.Stores.Epic;

public sealed class EpicLaunchService
{
    private static readonly HashSet<string> RetromindManagedEnvironmentKeys = new(
        ["WINEPREFIX", "STEAM_COMPAT_DATA_PATH", "PROTONPATH"],
        StringComparer.Ordinal);

    private readonly LegendaryCliClient _client;

    internal EpicLaunchService(LegendaryCliClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal async Task<EpicLaunchPreparation> PrepareAsync(
        string appName,
        CancellationToken ct = default)
    {
        var parameters = await _client.GetLaunchParametersAsync(appName, ct).ConfigureAwait(false);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in parameters.Environment)
        {
            if (!RetromindManagedEnvironmentKeys.Contains(entry.Key))
                environment[entry.Key] = entry.Value;
        }

        return new EpicLaunchPreparation(parameters.Arguments, environment);
    }
}

internal sealed record EpicLaunchPreparation(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

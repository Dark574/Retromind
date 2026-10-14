using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed record LegendaryVersionInfo(string Version, string CodeName);

internal sealed record LegendaryStatus(
    bool IsAuthenticated,
    string? AccountName,
    int AvailableGameCount,
    int InstalledGameCount,
    bool EpicLauncherSyncEnabled,
    string ConfigDirectory);

internal sealed partial class LegendaryCliClient
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);

    private readonly ILegendaryCliProcessRunner _processRunner;

    internal LegendaryCliClient(ILegendaryCliProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    internal async Task<LegendaryVersionInfo> GetVersionAsync(CancellationToken ct = default)
    {
        var result = await ExecuteAsync(
                new LegendaryCliCommand("version check", ["--version"], VersionTimeout),
                ct)
            .ConfigureAwait(false);

        var match = VersionOutputRegex().Match(result.StandardOutput.Trim());
        if (!match.Success)
            throw CreateInvalidOutputException("version check", result.StandardOutput, result.StandardError);

        return new LegendaryVersionInfo(
            match.Groups["version"].Value,
            match.Groups["codename"].Value);
    }

    internal async Task<LegendaryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var result = await ExecuteAsync(
                new LegendaryCliCommand("status check", ["status", "--offline", "--json"], StatusTimeout),
                ct)
            .ConfigureAwait(false);

        try
        {
            var dto = JsonSerializer.Deserialize<LegendaryStatusDto>(result.StandardOutput);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Account) ||
                string.IsNullOrWhiteSpace(dto.ConfigDirectory))
            {
                throw new JsonException("Required Legendary status fields are missing.");
            }

            var authenticated = !string.Equals(
                dto.Account,
                "<not logged in>",
                StringComparison.OrdinalIgnoreCase);

            return new LegendaryStatus(
                authenticated,
                authenticated ? dto.Account : null,
                dto.GamesAvailable,
                dto.GamesInstalled,
                dto.EglSyncEnabled,
                dto.ConfigDirectory);
        }
        catch (JsonException ex)
        {
            throw new LegendaryCliException(
                "Legendary returned an invalid response for the 'status check' operation.",
                ex);
        }
    }

    private async Task<LegendaryCliResult> ExecuteAsync(
        LegendaryCliCommand command,
        CancellationToken ct)
    {
        var result = await _processRunner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return result;

        var detail = GetSafeErrorDetail(result.StandardError, result.StandardOutput);
        throw new LegendaryCliException(
            $"Legendary's '{command.Operation}' operation failed with exit code {result.ExitCode}{detail}.");
    }

    private static LegendaryCliException CreateInvalidOutputException(
        string operation,
        string standardOutput,
        string standardError)
    {
        var detail = GetSafeErrorDetail(standardError, standardOutput);
        return new LegendaryCliException(
            $"Legendary returned an invalid response for the '{operation}' operation{detail}.");
    }

    private static string GetSafeErrorDetail(string primary, string secondary)
    {
        var detail = string.IsNullOrWhiteSpace(primary) ? secondary?.Trim() : primary.Trim();
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        const int maxLength = 240;
        if (detail.Length > maxLength)
            detail = detail[..maxLength] + "...";

        return $": {detail}";
    }

    [GeneratedRegex(
        "^legendary\\s+version\\s+\"(?<version>[^\"]+)\",\\s+codename\\s+\"(?<codename>[^\"]*)\"$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionOutputRegex();

    private sealed class LegendaryStatusDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("account")]
        public string? Account { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("games_available")]
        public int GamesAvailable { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("games_installed")]
        public int GamesInstalled { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("egl_sync_enabled")]
        public bool EglSyncEnabled { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("config_directory")]
        public string? ConfigDirectory { get; init; }
    }
}

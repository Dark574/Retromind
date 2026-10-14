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
    internal static readonly Uri AuthorizationUri = new("https://legendary.gl/epiclogin");

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OnlineStatusTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromMinutes(2);

    private readonly ILegendaryCliProcessRunner _processRunner;

    public LegendaryCliClient(ILegendaryCliProcessRunner processRunner)
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
        return await GetStatusAsync(offline: true, ct).ConfigureAwait(false);
    }

    internal async Task<LegendaryStatus> GetValidatedStatusAsync(CancellationToken ct = default)
    {
        return await GetStatusAsync(offline: false, ct).ConfigureAwait(false);
    }

    internal async Task<LegendaryStatus> AuthenticateWithAuthorizationCodeAsync(
        string authorizationCode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);

        await ExecuteAsync(
                new LegendaryCliCommand(
                    "Epic sign-in",
                    ["auth", "--code", authorizationCode.Trim()],
                    AuthenticationTimeout),
                ct)
            .ConfigureAwait(false);

        // Legendary 0.21.1 can return exit code 0 even when Epic rejects the code.
        // An online status request is therefore the authoritative success check.
        var status = await GetValidatedStatusAsync(ct).ConfigureAwait(false);
        if (!status.IsAuthenticated)
        {
            throw new LegendaryCliException(
                "Legendary did not establish an authenticated Epic session.");
        }

        return status;
    }

    internal async Task SignOutAsync(CancellationToken ct = default)
    {
        await ExecuteAsync(
                new LegendaryCliCommand(
                    "Epic sign-out",
                    ["auth", "--delete"],
                    StatusTimeout),
                ct)
            .ConfigureAwait(false);
    }

    private async Task<LegendaryStatus> GetStatusAsync(bool offline, CancellationToken ct)
    {
        var arguments = offline
            ? new[] { "status", "--offline", "--json" }
            : new[] { "status", "--json" };
        var operation = offline ? "status check" : "online status check";
        var timeout = offline ? StatusTimeout : OnlineStatusTimeout;

        var result = await ExecuteAsync(
                new LegendaryCliCommand(operation, arguments, timeout),
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
                $"Legendary returned an invalid response for the '{operation}' operation.",
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed record LegendaryVersionInfo(string Version, string CodeName);

internal sealed record LegendaryStatus(
    bool IsAuthenticated,
    string? AccountName,
    int AvailableGameCount,
    int InstalledGameCount,
    bool EpicLauncherSyncEnabled,
    string ConfigDirectory);

internal sealed record LegendaryGame(
    string AppName,
    string AppTitle,
    string? Platform,
    string? Version);

internal sealed record LegendaryDlc(
    string AppName,
    string Title,
    string? AvailableVersion,
    bool IsInstalled,
    string? InstalledVersion,
    long InstalledSize)
{
    internal bool HasUpdateAvailable =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(InstalledVersion) &&
        !string.IsNullOrWhiteSpace(AvailableVersion) &&
        !string.Equals(InstalledVersion, AvailableVersion, StringComparison.Ordinal);
}

internal sealed record LegendaryInstallEstimate(
    long InstalledBytes,
    int FileCount);

internal sealed record LegendaryInstalledGame(
    string AppName,
    string Title,
    string InstallPath,
    string Executable,
    string Version,
    string Platform,
    long InstallSize,
    bool IsDlc);

internal sealed record LegendaryUpdateStatus(
    string InstalledVersion,
    string AvailableVersion,
    bool IsUpdateAvailable);

internal sealed record LegendaryLaunchParameters(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

internal sealed partial class LegendaryCliClient
{
    internal static readonly Uri AuthorizationUri = new("https://legendary.gl/epiclogin");

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OnlineStatusTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LibraryTimeout = TimeSpan.FromMinutes(5);

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
        var trimmedAuthorizationCode = authorizationCode.Trim();
        if (trimmedAuthorizationCode.Contains('\r') || trimmedAuthorizationCode.Contains('\n'))
            throw new ArgumentException("The Epic authorization code must be a single line.", nameof(authorizationCode));

        try
        {
            await ExecuteAsync(
                    new LegendaryCliCommand(
                        "Epic sign-in",
                        ["auth", "--disable-webview"],
                        AuthenticationTimeout,
                        StandardInput: trimmedAuthorizationCode,
                        SuppressBrowserLaunch: true,
                        IncludeErrorDetails: false),
                    ct)
                .ConfigureAwait(false);

            // Legendary 0.21.1 can return exit code 0 even when Epic rejects the code.
            // An online status request is therefore the authoritative success check.
            var status = await GetStatusAsync(offline: false, ct, includeErrorDetails: false).ConfigureAwait(false);
            if (!status.IsAuthenticated)
                throw new LegendaryCliException("Epic sign-in was not accepted.");

            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LegendaryCliException)
        {
            // Authentication output can contain credentials or tokens. Never
            // propagate Legendary's raw output through UI errors or logs.
            throw new LegendaryCliException(
                "Epic sign-in failed. The authorization code may be invalid or expired.");
        }
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

    internal async Task UninstallAsync(
        string appName,
        bool keepFiles,
        Action<string>? outputReceived = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var normalizedAppName = appName.Trim();
        var arguments = new List<string>
        {
            "--yes",
            "uninstall",
            normalizedAppName,
            "--skip-uninstaller"
        };
        if (keepFiles)
            arguments.Add("--keep-files");

        await ExecuteAsync(
                new LegendaryCliCommand(
                    keepFiles ? "installation registration cleanup" : "game uninstallation",
                    arguments,
                    Timeout.InfiniteTimeSpan,
                    outputReceived),
                ct)
            .ConfigureAwait(false);

        var installedGames = await GetInstalledGamesAsync(ct).ConfigureAwait(false);
        if (installedGames.Any(game => string.Equals(
                game.AppName,
                normalizedAppName,
                StringComparison.Ordinal)))
        {
            throw new LegendaryCliException(
                "Legendary finished without removing the game's installation registration.");
        }
    }

    internal async Task<IReadOnlyList<LegendaryGame>> GetGamesAsync(CancellationToken ct = default)
    {
        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    "library load",
                    ["list", "--json"],
                    LibraryTimeout),
                ct)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Legendary game list is not an array.");

            var games = new Dictionary<string, LegendaryGame>(StringComparer.Ordinal);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var appName = ReadString(element, "app_name")?.Trim();
                if (string.IsNullOrWhiteSpace(appName))
                    continue;

                var appTitle = ReadString(element, "app_title")?.Trim();
                if (string.IsNullOrWhiteSpace(appTitle))
                    appTitle = appName;

                var (platform, version) = ReadPreferredAsset(element);
                games.TryAdd(appName, new LegendaryGame(appName, appTitle, platform, version));
            }

            return games.Values
                .OrderBy(game => game.AppTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.AppName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new LegendaryCliException(
                "Legendary returned an invalid response for the 'library load' operation.",
                ex);
        }
    }

    internal async Task<IReadOnlyList<LegendaryDlc>> GetDlcCatalogAsync(
        string baseAppName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAppName);
        var normalizedBaseAppName = baseAppName.Trim();
        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    "DLC catalog load",
                    ["list", "--json"],
                    LibraryTimeout),
                ct)
            .ConfigureAwait(false);

        List<(string AppName, string Title, string? AvailableVersion)> availableDlcs;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Legendary game list is not an array.");

            JsonElement? baseGame = null;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    string.Equals(
                        ReadString(element, "app_name")?.Trim(),
                        normalizedBaseAppName,
                        StringComparison.Ordinal))
                {
                    baseGame = element;
                    break;
                }
            }

            if (!baseGame.HasValue)
            {
                throw new LegendaryCliException(
                    $"Legendary did not return the Epic Games title '{normalizedBaseAppName}' while loading its DLC catalog.");
            }

            if (!baseGame.Value.TryGetProperty("dlcs", out var dlcsElement))
                return Array.Empty<LegendaryDlc>();
            if (dlcsElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Legendary DLC data is not an array.");

            availableDlcs = [];
            foreach (var dlc in dlcsElement.EnumerateArray())
            {
                if (dlc.ValueKind != JsonValueKind.Object)
                    continue;

                var appName = ReadString(dlc, "app_name")?.Trim();
                if (string.IsNullOrWhiteSpace(appName))
                    continue;

                var title = ReadString(dlc, "app_title")?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                    title = appName;
                var (_, version) = ReadPreferredAsset(dlc);
                availableDlcs.Add((appName, title, version?.Trim()));
            }
        }
        catch (JsonException ex)
        {
            throw new LegendaryCliException(
                "Legendary returned an invalid response for the 'DLC catalog load' operation.",
                ex);
        }

        var installedByAppName = (await GetInstalledGamesAsync(ct).ConfigureAwait(false))
            .Where(static game => game.IsDlc)
            .GroupBy(static game => game.AppName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);

        return availableDlcs
            .Select(dlc =>
            {
                installedByAppName.TryGetValue(dlc.AppName, out var installed);
                return new LegendaryDlc(
                    dlc.AppName,
                    dlc.Title,
                    dlc.AvailableVersion,
                    installed != null,
                    installed?.Version,
                    installed?.InstallSize ?? 0);
            })
            .OrderBy(static dlc => dlc.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static dlc => dlc.AppName, StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<LegendaryInstallEstimate> GetInstallEstimateAsync(
        string appName,
        string platform = "Windows",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    "install size check",
                    [
                        "list-files",
                        appName.Trim(),
                        "--force-download",
                        "--platform",
                        platform.Trim(),
                        "--json"
                    ],
                    LibraryTimeout),
                ct)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Legendary file list is not an array.");

            var installedBytes = 0L;
            var fileCount = 0;
            foreach (var file in document.RootElement.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object ||
                    !file.TryGetProperty("file_size", out var fileSize) ||
                    fileSize.ValueKind != JsonValueKind.Number ||
                    !fileSize.TryGetInt64(out var bytes) ||
                    bytes < 0)
                {
                    throw new JsonException("Legendary file size is missing or invalid.");
                }

                installedBytes = checked(installedBytes + bytes);
                fileCount++;
            }

            return new LegendaryInstallEstimate(installedBytes, fileCount);
        }
        catch (Exception ex) when (ex is JsonException or OverflowException)
        {
            throw new LegendaryCliException(
                "Legendary returned an invalid response for the 'install size check' operation.",
                ex);
        }
    }

    internal async Task<IReadOnlyList<LegendaryInstalledGame>> GetInstalledGamesAsync(
        CancellationToken ct = default)
    {
        return await GetInstalledGamesAsync(checkForUpdates: false, ct).ConfigureAwait(false);
    }

    internal async Task<LegendaryUpdateStatus> GetUpdateStatusAsync(
        string appName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        var normalizedAppName = appName.Trim();
        var installedGames = await GetInstalledGamesAsync(checkForUpdates: true, ct).ConfigureAwait(false);
        var installed = installedGames.FirstOrDefault(game => string.Equals(
            game.AppName,
            normalizedAppName,
            StringComparison.Ordinal));
        if (installed == null || string.IsNullOrWhiteSpace(installed.Version))
            throw new LegendaryCliException("Legendary could not determine the installed game version.");

        // list-installed --check-updates refreshes Legendary's asset cache, but its
        // JSON output contains only installed data. Read the refreshed catalog to
        // obtain the currently available build version.
        var availableGames = await GetGamesAsync(ct).ConfigureAwait(false);
        var available = availableGames.FirstOrDefault(game => string.Equals(
            game.AppName,
            normalizedAppName,
            StringComparison.Ordinal));
        if (available == null || string.IsNullOrWhiteSpace(available.Version))
            throw new LegendaryCliException("Legendary could not determine the available game version.");

        return new LegendaryUpdateStatus(
            installed.Version,
            available.Version,
            !string.Equals(installed.Version, available.Version, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<LegendaryInstalledGame>> GetInstalledGamesAsync(
        bool checkForUpdates,
        CancellationToken ct)
    {
        var arguments = checkForUpdates
            ? new[] { "list-installed", "--check-updates", "--json" }
            : new[] { "list-installed", "--json" };
        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    checkForUpdates ? "game update check" : "installed games check",
                    arguments,
                    checkForUpdates ? LibraryTimeout : StatusTimeout),
                ct)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Legendary installed game list is not an array.");

            var games = new List<LegendaryInstalledGame>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var appName = ReadString(element, "app_name")?.Trim();
                var installPath = ReadString(element, "install_path")?.Trim();
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installPath))
                    continue;

                games.Add(new LegendaryInstalledGame(
                    appName,
                    ReadString(element, "title")?.Trim() ?? appName,
                    installPath,
                    ReadString(element, "executable")?.Trim() ?? string.Empty,
                    ReadString(element, "version")?.Trim() ?? string.Empty,
                    ReadString(element, "platform")?.Trim() ?? "Windows",
                    ReadLong(element, "install_size"),
                    ReadBool(element, "is_dlc")));
            }

            return games;
        }
        catch (JsonException ex)
        {
            throw new LegendaryCliException(
                $"Legendary returned an invalid response for the '{(checkForUpdates ? "game update check" : "installed games check")}' operation.",
                ex);
        }
    }

    internal async Task<LegendaryInstalledGame> InstallAsync(
        string appName,
        string installPath,
        Action<string>? outputReceived = null,
        CancellationToken ct = default,
        bool repair = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);

        var fullInstallPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        var normalizedAppName = appName.Trim();
        var basePath = Path.GetDirectoryName(fullInstallPath);
        var gameFolder = Path.GetFileName(fullInstallPath);
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(gameFolder))
            throw new ArgumentException("The Legendary install path must point to a game directory.", nameof(installPath));

        await RemoveStaleInstallRegistrationAsync(
                normalizedAppName,
                fullInstallPath,
                outputReceived,
                ct)
            .ConfigureAwait(false);

        Directory.CreateDirectory(basePath);
        var installArguments = new List<string>
        {
            "--yes",
            "install",
            normalizedAppName,
            "--base-path",
            basePath,
            "--game-folder",
            gameFolder,
            "--platform",
            "Windows",
            "--skip-sdl",
            "--skip-dlcs"
        };
        if (repair)
            installArguments.Add("--repair");

        await ExecuteAsync(
                new LegendaryCliCommand(
                    repair ? "game repair" : "game installation",
                    installArguments,
                    Timeout.InfiniteTimeSpan,
                    outputReceived),
                ct)
            .ConfigureAwait(false);

        var installedGames = await GetInstalledGamesAsync(ct).ConfigureAwait(false);
        var installedGame = installedGames.FirstOrDefault(game => string.Equals(
            game.AppName,
            normalizedAppName,
            StringComparison.Ordinal));
        if (installedGame == null)
        {
            throw new LegendaryCliException(
                "Legendary finished without registering the game as installed.");
        }

        var registeredInstallPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installedGame.InstallPath));
        if (!FileSystemPathIdentity.Equals(registeredInstallPath, fullInstallPath))
        {
            throw new LegendaryCliException(
                $"Legendary registered the game at '{registeredInstallPath}' instead of the requested path '{fullInstallPath}'.");
        }

        return installedGame;
    }

    private async Task RemoveStaleInstallRegistrationAsync(
        string appName,
        string requestedInstallPath,
        Action<string>? outputReceived,
        CancellationToken ct)
    {
        var installedGames = await GetInstalledGamesAsync(ct).ConfigureAwait(false);
        var registeredGame = installedGames.FirstOrDefault(game => string.Equals(
            game.AppName,
            appName,
            StringComparison.Ordinal));
        if (registeredGame == null)
            return;

        string registeredInstallPath;
        try
        {
            registeredInstallPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(registeredGame.InstallPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LegendaryCliException(
                "Legendary contains an invalid existing installation path for this game.",
                ex);
        }

        if (FileSystemPathIdentity.Equals(registeredInstallPath, requestedInstallPath))
            return;

        if (Directory.Exists(registeredInstallPath) || File.Exists(registeredInstallPath))
        {
            throw new LegendaryCliException(
                $"Legendary already has this game registered at the existing path '{registeredInstallPath}'. " +
                "Uninstall or move that installation before selecting a different destination.");
        }

        outputReceived?.Invoke(
            $"[Retromind] Removing stale Legendary registration for missing path '{registeredInstallPath}'...");
        await UninstallAsync(appName, keepFiles: true, outputReceived, ct).ConfigureAwait(false);
    }

    internal async Task<LegendaryLaunchParameters> GetLaunchParametersAsync(
        string appName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    "launch preparation",
                    [
                        "launch",
                        appName.Trim(),
                        "--json",
                        "--no-wine",
                        "--skip-version-check"
                    ],
                    OnlineStatusTimeout),
                ct)
            .ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Legendary launch parameters are not an object.");

            var arguments = new List<string>();
            AppendStringArray(document.RootElement, "game_parameters", arguments);
            AppendStringArray(document.RootElement, "user_parameters", arguments);
            AppendStringArray(document.RootElement, "egl_parameters", arguments);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("environment", out var environmentElement))
            {
                if (environmentElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Legendary launch environment is not an object.");

                foreach (var property in environmentElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.Name))
                    {
                        environment[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }

            return new LegendaryLaunchParameters(arguments, environment);
        }
        catch (JsonException ex)
        {
            throw new LegendaryCliException(
                "Legendary returned an invalid response for the 'launch preparation' operation.",
                ex);
        }
    }

    private async Task<LegendaryStatus> GetStatusAsync(
        bool offline,
        CancellationToken ct,
        bool includeErrorDetails = true)
    {
        var arguments = offline
            ? new[] { "status", "--offline", "--json" }
            : new[] { "status", "--json" };
        var operation = offline ? "status check" : "online status check";
        var timeout = offline ? StatusTimeout : OnlineStatusTimeout;

        var result = await ExecuteAsync(
                new LegendaryCliCommand(
                    operation,
                    arguments,
                    timeout,
                    IncludeErrorDetails: includeErrorDetails),
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

    private static (string? Platform, string? Version) ReadPreferredAsset(JsonElement game)
    {
        if (!game.TryGetProperty("asset_infos", out var assetInfos) ||
            assetInfos.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        if (assetInfos.TryGetProperty("Windows", out var windowsAsset))
            return ("Windows", ReadString(windowsAsset, "build_version"));

        foreach (var asset in assetInfos.EnumerateObject())
            return (asset.Name, ReadString(asset.Value, "build_version"));

        return (null, null);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True;
    }

    private static void AppendStringArray(
        JsonElement element,
        string propertyName,
        ICollection<string> target)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return;
        if (property.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Legendary property '{propertyName}' is not an array.");

        foreach (var value in property.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new JsonException($"Legendary property '{propertyName}' contains a non-string value.");

            target.Add(value.GetString() ?? string.Empty);
        }
    }

    private async Task<LegendaryCliResult> ExecuteAsync(
        LegendaryCliCommand command,
        CancellationToken ct)
    {
        var result = await _processRunner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return result;

        var detail = command.IncludeErrorDetails
            ? GetSafeErrorDetail(result.StandardError, result.StandardOutput)
            : string.Empty;
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

        detail = SensitiveErrorValueRegex().Replace(
            detail,
            match => match.Groups["prefix"].Value + "[REDACTED]");
        detail = BearerTokenRegex().Replace(detail, "Bearer [REDACTED]");

        const int maxLength = 240;
        if (detail.Length > maxLength)
            detail = detail[..maxLength] + "...";

        return $": {detail}";
    }

    [GeneratedRegex(
        "^legendary\\s+version\\s+\"(?<version>[^\"]+)\",\\s+codename\\s+\"(?<codename>[^\"]*)\"$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionOutputRegex();

    [GeneratedRegex(
        "(?<prefix>[\\\"']?(?:authorization_?code|authorizationCode|access_?token|accessToken|refresh_?token|refreshToken|exchange_?(?:code|token)|exchange(?:Code|Token)|-AUTH_(?:PASSWORD|LOGIN)|code|token|sid)[\\\"']?\\s*(?:=|:)\\s*[\\\"']?)[^\\s,\\\"'}&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveErrorValueRegex();

    [GeneratedRegex(
        "\\bBearer\\s+[^\\s,\\\"'}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

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

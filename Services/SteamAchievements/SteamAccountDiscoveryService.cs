using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.SteamAchievements;

public sealed class SteamAccountDiscoveryService
{
    private static readonly Regex QuotedEntryRegex = new(
        @"^\s*""(?<key>[^""]+)""(?:\s+""(?<value>(?:\\.|[^""])*)"")?\s*$",
        RegexOptions.Compiled);

    private static readonly string[] LoginUsersPathSuffixes =
    [
        Path.Combine(".steam", "steam", "config", "loginusers.vdf"),
        Path.Combine(".local", "share", "Steam", "config", "loginusers.vdf"),
        Path.Combine(
            ".var",
            "app",
            "com.valvesoftware.Steam",
            ".local",
            "share",
            "Steam",
            "config",
            "loginusers.vdf")
    ];

    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string>? _loginUsersPathsOverride;

    public SteamAccountDiscoveryService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    internal SteamAccountDiscoveryService(IEnumerable<string> loginUsersPaths)
    {
        ArgumentNullException.ThrowIfNull(loginUsersPaths);

        _settings = new AppSettings();
        _loginUsersPathsOverride = loginUsersPaths.ToArray();
    }

    public async Task<IReadOnlyList<SteamAccountInfo>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = new Dictionary<string, SteamAccountInfo>(StringComparer.Ordinal);

        foreach (var path in GetLoginUsersPaths()
                     .Distinct(FileSystemPathIdentity.Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(path))
                    continue;

                var content = await File
                    .ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var detectedAccount in Parse(content))
                {
                    if (!accounts.TryGetValue(
                            detectedAccount.SteamId64,
                            out var existingAccount))
                    {
                        accounts.Add(detectedAccount.SteamId64, detectedAccount);
                        continue;
                    }

                    accounts[detectedAccount.SteamId64] = new SteamAccountInfo(
                        detectedAccount.SteamId64,
                        FirstNonEmpty(
                            existingAccount.AccountName,
                            detectedAccount.AccountName),
                        FirstNonEmpty(
                            existingAccount.PersonaName,
                            detectedAccount.PersonaName),
                        existingAccount.IsMostRecent ||
                        detectedAccount.IsMostRecent);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Local account detection is best-effort. One inaccessible
                // Steam installation must not prevent checking another one.
            }
        }

        return accounts.Values
            .OrderByDescending(account => account.IsMostRecent)
            .ThenBy(account => account.PersonaName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<string> GetLoginUsersPaths()
    {
        if (_loginUsersPathsOverride != null)
            return _loginUsersPathsOverride;

        return GetHomeCandidates()
            .SelectMany(home => LoginUsersPathSuffixes.Select(
                suffix => Path.Combine(home, suffix)));
    }

    private IEnumerable<string> GetHomeCandidates()
    {
        var currentHome = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(currentHome))
            yield return currentHome;

        if (!ShouldAddRealHomeFallback())
            yield break;

        var realHome = EnvironmentPathHelper.TryGetRealUserHomePath();
        if (string.IsNullOrWhiteSpace(realHome))
            yield break;

        if (!FileSystemPathIdentity.Equals(currentHome, realHome))
            yield return realHome;
    }

    private bool ShouldAddRealHomeFallback()
    {
        if (!_settings.UsePortableHomeInAppImage)
            return false;

        return !string.IsNullOrWhiteSpace(
                   Environment.GetEnvironmentVariable("APPIMAGE")) ||
               !string.IsNullOrWhiteSpace(
                   Environment.GetEnvironmentVariable("APPDIR"));
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first : second;
    }

    internal static IReadOnlyList<SteamAccountInfo> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var accounts = new Dictionary<string, AccountBuilder>(StringComparer.Ordinal);
        string? currentSteamId64 = null;

        foreach (var line in content.Split('\n'))
        {
            var match = QuotedEntryRegex.Match(line.TrimEnd('\r'));
            if (!match.Success)
                continue;

            var key = Unescape(match.Groups["key"].Value);
            var valueGroup = match.Groups["value"];

            // A line containing only a 17-digit key begins a Steam account block.
            if (!valueGroup.Success)
            {
                currentSteamId64 = IsSteamId64(key) ? key : null;

                if (currentSteamId64 != null)
                {
                    accounts.TryAdd(currentSteamId64, new AccountBuilder(currentSteamId64));
                }

                continue;
            }

            if (currentSteamId64 == null ||
                !accounts.TryGetValue(currentSteamId64, out var account))
            {
                continue;
            }

            var value = Unescape(valueGroup.Value);

            switch (key)
            {
                case "AccountName":
                    account.AccountName = value;
                    break;

                case "PersonaName":
                    account.PersonaName = value;
                    break;

                case "MostRecent":
                    account.IsMostRecent = value == "1";
                    break;
            }
        }

        return accounts.Values
            .Select(account => new SteamAccountInfo(account.SteamId64, account.AccountName, account.PersonaName, account.IsMostRecent))
            .OrderByDescending(account => account.IsMostRecent)
            .ThenBy(account => account.PersonaName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSteamId64(string value)
    {
        return value.Length == 17 &&
               value.All(character => character is >= '0' and <= '9');
    }

    private static string Unescape(string value)
    {
        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private sealed class AccountBuilder(string steamId64)
    {
        public string SteamId64 { get; } = steamId64;

        public string AccountName { get; set; } = string.Empty;

        public string PersonaName { get; set; } = string.Empty;

        public bool IsMostRecent { get; set; }
    }
}
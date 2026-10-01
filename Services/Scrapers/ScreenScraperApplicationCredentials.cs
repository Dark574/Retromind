using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Retromind.Services.Scrapers;

internal sealed record ScreenScraperApplicationCredentials(
    string DeveloperId,
    string DeveloperPassword)
{
    private const string DeveloperIdEnvironmentVariable = "RETROMIND_SCREENSCRAPER_DEVELOPER_ID";
    private const string DeveloperPasswordEnvironmentVariable = "RETROMIND_SCREENSCRAPER_DEVELOPER_PASSWORD";
    private const string DeveloperIdMetadataName = "Retromind.ScreenScraper.DeveloperId";
    private const string DeveloperPasswordMetadataName = "Retromind.ScreenScraper.DeveloperPassword";

    public static ScreenScraperApplicationCredentials? Resolve()
    {
        var assembly = typeof(ScreenScraperApplicationCredentials).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToArray();
        var embeddedDeveloperId = metadata
            .FirstOrDefault(attribute => attribute.Key == DeveloperIdMetadataName)?.Value;
        var embeddedDeveloperPassword = metadata
            .FirstOrDefault(attribute => attribute.Key == DeveloperPasswordMetadataName)?.Value;

        string? localDeveloperId = null;
        string? localDeveloperPassword = null;
#if DEBUG
        localDeveloperId = ReadLocalDevelopmentSecret(DeveloperIdEnvironmentVariable);
        localDeveloperPassword = ReadLocalDevelopmentSecret(DeveloperPasswordEnvironmentVariable);
#endif

        var developerId = FirstNonEmpty(
            embeddedDeveloperId,
            Environment.GetEnvironmentVariable(DeveloperIdEnvironmentVariable),
            localDeveloperId);
        var developerPassword = FirstNonEmpty(
            embeddedDeveloperPassword,
            Environment.GetEnvironmentVariable(DeveloperPasswordEnvironmentVariable),
            localDeveloperPassword);

        return string.IsNullOrWhiteSpace(developerId) || string.IsNullOrWhiteSpace(developerPassword)
            ? null
            : new ScreenScraperApplicationCredentials(developerId.Trim(), developerPassword);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

#if DEBUG
    private static string? ReadLocalDevelopmentSecret(string name)
    {
        try
        {
            var path = Path.Combine(Environment.CurrentDirectory, ".build-secrets", name);
            return File.Exists(path)
                ? File.ReadAllText(path).TrimEnd('\r', '\n')
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
#endif
}

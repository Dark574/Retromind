using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Services;

public sealed record GeProtonRelease(
    string TagName,
    string AssetName,
    string DownloadUrl)
{
    public string DisplayName => string.IsNullOrWhiteSpace(AssetName)
        ? TagName
        : $"{TagName} ({AssetName})";
}

/// <summary>
/// Owns external runner discovery and managed runner filesystem operations.
/// Settings UI state and assignment changes remain in <c>SettingsViewModel</c>.
/// </summary>
public sealed class RunnerVersionService : IDisposable
{
    private const string GeProtonReleasesApiUrl =
        "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases";
    private const int GeProtonPerPage = 100;
    private const int GeProtonMaxPages = 6;
    private const int GeProtonMaxItems = 300;
    private const string ManagedRunnerRelativeRoot = "Emulators/ProtonVersions";

    private readonly string _dataRoot;
    private readonly string _managedRunnerRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public RunnerVersionService(string dataRoot)
        : this(dataRoot, CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal RunnerVersionService(string dataRoot, HttpClient httpClient)
        : this(dataRoot, httpClient, ownsHttpClient: false)
    {
    }

    private RunnerVersionService(string dataRoot, HttpClient httpClient, bool ownsHttpClient)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("A data root is required.", nameof(dataRoot));

        _dataRoot = Path.GetFullPath(dataRoot);
        _managedRunnerRoot = Path.GetFullPath(Path.Combine(_dataRoot, "Emulators", "ProtonVersions"));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<IReadOnlyList<GeProtonRelease>> GetGeProtonReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<GeProtonRelease>();

        for (var page = 1; page <= GeProtonMaxPages; page++)
        {
            var url = $"{GeProtonReleasesApiUrl}?per_page={GeProtonPerPage}&page={page}";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (json.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var releaseCountOnPage = json.RootElement.GetArrayLength();
            if (releaseCountOnPage == 0)
                break;

            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (!TryGetStringProperty(release, "tag_name", out var tagName))
                    continue;

                if (release.TryGetProperty("draft", out var draftProp) &&
                    draftProp.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!TryGetStringProperty(asset, "name", out var assetName) ||
                        !assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                        !TryGetStringProperty(asset, "browser_download_url", out var downloadUrl))
                    {
                        continue;
                    }

                    if (assetName.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                        assetName.Contains("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new GeProtonRelease(tagName, assetName, downloadUrl));
                    break;
                }

                if (result.Count >= GeProtonMaxItems)
                    return result;
            }

            if (releaseCountOnPage < GeProtonPerPage)
                break;
        }

        return result;
    }

    public async Task<string> DownloadAndInstallGeProtonAsync(
        GeProtonRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        Directory.CreateDirectory(_managedRunnerRoot);
        var tempArchivePath = Path.Combine(Path.GetTempPath(), $"retromind_ge_{Guid.NewGuid():N}.tar.gz");

        try
        {
            using (var response = await _httpClient.GetAsync(
                       release.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var local = File.Create(tempArchivePath);
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var rootFolder = DetectArchiveRootFolderName(tempArchivePath);
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                rootFolder = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(release.AssetName));
            }

            rootFolder = SanitizeFolderName(rootFolder);
            if (string.IsNullOrWhiteSpace(rootFolder))
                throw new InvalidOperationException("Unable to determine installation folder name.");

            var targetDir = Path.Combine(_managedRunnerRoot, rootFolder);
            var relativeInstalledPath = NormalizeRelativePath(
                Path.Combine(ManagedRunnerRelativeRoot, rootFolder));

            if (Directory.Exists(targetDir))
            {
                EnsureCompleteProtonRunner(targetDir);
                return relativeInstalledPath;
            }

            var stagingDir = Path.Combine(_managedRunnerRoot, $".tmp_ge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                await using var archiveStream = File.OpenRead(tempArchivePath);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, stagingDir, overwriteFiles: false);
                cancellationToken.ThrowIfCancellationRequested();

                var expectedRoot = Path.Combine(stagingDir, rootFolder);
                if (Directory.Exists(expectedRoot))
                {
                    EnsureCompleteProtonRunner(expectedRoot);
                    Directory.Move(expectedRoot, targetDir);
                }
                else
                {
                    var extractedDirs = Directory.GetDirectories(stagingDir);
                    if (extractedDirs.Length == 1)
                    {
                        EnsureCompleteProtonRunner(extractedDirs[0]);
                        Directory.Move(extractedDirs[0], targetDir);
                    }
                    else
                    {
                        EnsureCompleteProtonRunner(stagingDir);
                        // Publish the complete tree with a same-filesystem rename so
                        // the final runner directory can never be only half populated.
                        Directory.Move(stagingDir, targetDir);
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }

            return relativeInstalledPath;
        }
        finally
        {
            TryDeleteFile(tempArchivePath);
        }
    }

    public async Task<bool> DeleteManagedRunnerAsync(
        string storedPath,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveManagedRunnerDirectory(storedPath, out var runnerDirectory))
            return false;

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(runnerDirectory))
                Directory.Delete(runnerDirectory, recursive: true);
        }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public RunnerVersionKind DetectRunnerKind(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RunnerVersionKind.Proton;

        var trimmed = path.Trim();
        var normalized = trimmed.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("/wine") || lower.Contains("wine64"))
            return RunnerVersionKind.Wine;

        if (lower.Contains("proton"))
            return RunnerVersionKind.Proton;

        try
        {
            var candidateDir = Directory.Exists(trimmed)
                ? trimmed
                : File.Exists(trimmed) ? Path.GetDirectoryName(trimmed) : null;

            if (!string.IsNullOrWhiteSpace(candidateDir))
            {
                var wineBin = Path.Combine(candidateDir, "bin", "wine");
                var wineBin64 = Path.Combine(candidateDir, "bin", "wine64");
                if (File.Exists(wineBin) || File.Exists(wineBin64))
                    return RunnerVersionKind.Wine;

                var protonScript = Path.Combine(candidateDir, "proton");
                var protonFixes = Path.Combine(candidateDir, "protonfixes");
                if (File.Exists(protonScript) || Directory.Exists(protonFixes))
                    return RunnerVersionKind.Proton;
            }
        }
        catch
        {
            // Detection is best effort; the editable UI selection remains authoritative.
        }

        return RunnerVersionKind.Proton;
    }

    private bool TryResolveManagedRunnerDirectory(string storedPath, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath))
            return false;

        try
        {
            var candidate = Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.GetFullPath(Path.Combine(_dataRoot, storedPath));
            var parentDirectory = Path.GetDirectoryName(
                candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (!string.Equals(parentDirectory, _managedRunnerRoot, StringComparison.Ordinal))
                return false;

            directory = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCompleteProtonRunner(string runnerDirectory)
    {
        if (RunnerVersionPathHelper.ResolveExecutablePath(RunnerVersionKind.Proton, runnerDirectory) != null)
            return;

        var format = Strings.ResourceManager.GetString(
                         "Settings_GeProtonIncompleteFormat",
                         Strings.Culture)
                     ?? "The Proton archive is incomplete. Expected 'proton' and 'toolmanifest.vdf' in '{0}'.";
        throw new InvalidOperationException(string.Format(format, runnerDirectory));
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string DetectArchiveRootFolderName(string tarGzPath)
    {
        if (string.IsNullOrWhiteSpace(tarGzPath) || !File.Exists(tarGzPath))
            return string.Empty;

        using var fileStream = File.OpenRead(tarGzPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) != null)
        {
            var name = entry.Name?.Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var firstSegment = name.Split(new[] { '/', '\\' }, 2)[0];
            if (!string.IsNullOrWhiteSpace(firstSegment))
                return firstSegment;
        }

        return string.Empty;
    }

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars())
            result = result.Replace(character.ToString(), string.Empty, StringComparison.Ordinal);

        return result.Trim();
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary staging cleanup is best effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temporary download cleanup is best effort.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Retromind", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    string DownloadUrl,
    string ChecksumUrl)
{
    public string DisplayName => string.IsNullOrWhiteSpace(AssetName)
        ? TagName
        : $"{TagName} ({AssetName})";
}

public enum GeProtonInstallStage
{
    Downloading,
    Installing
}

public readonly record struct GeProtonInstallProgress(
    GeProtonInstallStage Stage,
    long DownloadedBytes,
    long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(DownloadedBytes * 100d / TotalBytes.Value, 0d, 100d)
        : null;
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
    private const int GeProtonMaxRedirects = 5;
    private const int GeProtonMaxChecksumBytes = 16 * 1024;
    private const string ManagedRunnerRelativeRoot = "Emulators/ProtonVersions";
    private const string GithubApiHost = "api.github.com";
    private const string GithubHost = "github.com";
    private const string GithubReleaseAssetHost = "release-assets.githubusercontent.com";
    private const string GeProtonApiPath = "/repos/GloriousEggroll/proton-ge-custom/releases";
    private const string GeProtonReleasePathPrefix =
        "/GloriousEggroll/proton-ge-custom/releases/download/";
    internal const long MaxGeProtonDownloadBytes = 1024L * 1024 * 1024;
    internal const long MaxGeProtonExtractedBytes = 4L * 1024 * 1024 * 1024;
    internal const int MaxGeProtonArchiveEntries = 100_000;

    private readonly string _dataRoot;
    private readonly string _managedRunnerRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly long _maxDownloadBytes;
    private readonly long _maxExtractedBytes;
    private readonly int _maxArchiveEntries;

    public RunnerVersionService(string dataRoot)
        : this(
            dataRoot,
            CreateHttpClient(),
            ownsHttpClient: true,
            MaxGeProtonDownloadBytes,
            MaxGeProtonExtractedBytes,
            MaxGeProtonArchiveEntries)
    {
    }

    internal RunnerVersionService(string dataRoot, HttpClient httpClient)
        : this(
            dataRoot,
            httpClient,
            ownsHttpClient: false,
            MaxGeProtonDownloadBytes,
            MaxGeProtonExtractedBytes,
            MaxGeProtonArchiveEntries)
    {
    }

    internal RunnerVersionService(
        string dataRoot,
        HttpClient httpClient,
        long maxDownloadBytes,
        long maxExtractedBytes,
        int maxArchiveEntries)
        : this(
            dataRoot,
            httpClient,
            ownsHttpClient: false,
            maxDownloadBytes,
            maxExtractedBytes,
            maxArchiveEntries)
    {
    }

    private RunnerVersionService(
        string dataRoot,
        HttpClient httpClient,
        bool ownsHttpClient,
        long maxDownloadBytes,
        long maxExtractedBytes,
        int maxArchiveEntries)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("A data root is required.", nameof(dataRoot));
        if (maxDownloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        if (maxExtractedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExtractedBytes));
        if (maxArchiveEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxArchiveEntries));

        _dataRoot = Path.GetFullPath(dataRoot);
        _managedRunnerRoot = Path.GetFullPath(Path.Combine(_dataRoot, "Emulators", "ProtonVersions"));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _maxDownloadBytes = maxDownloadBytes;
        _maxExtractedBytes = maxExtractedBytes;
        _maxArchiveEntries = maxArchiveEntries;
    }

    public async Task<IReadOnlyList<GeProtonRelease>> GetGeProtonReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<GeProtonRelease>();

        for (var page = 1; page <= GeProtonMaxPages; page++)
        {
            var url = $"{GeProtonReleasesApiUrl}?per_page={GeProtonPerPage}&page={page}";
            using var response = await SendTrustedGetAsync(
                    new Uri(url),
                    TrustedRequestKind.ReleaseApi,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken)
                .ConfigureAwait(false);
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

                    var checksumName = assetName[..^".tar.gz".Length] + ".sha512sum";
                    if (!TryFindReleaseAsset(assets, checksumName, out var checksumUrl) ||
                        !IsExpectedReleaseAssetUrl(downloadUrl, tagName, assetName) ||
                        !IsExpectedReleaseAssetUrl(checksumUrl, tagName, checksumName))
                    {
                        continue;
                    }

                    result.Add(new GeProtonRelease(
                        tagName,
                        assetName,
                        downloadUrl,
                        checksumUrl));
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
        CancellationToken cancellationToken = default,
        IProgress<GeProtonInstallProgress>? progress = null,
        bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateReleaseSource(release);

        EnsureManagedRunnerRootForMutation();
        var tempArchivePath = Path.Combine(Path.GetTempPath(), $"retromind_ge_{Guid.NewGuid():N}.tar.gz");

        try
        {
            var expectedChecksum = await DownloadExpectedChecksumAsync(release, cancellationToken)
                .ConfigureAwait(false);

            using (var response = await SendTrustedGetAsync(
                       new Uri(release.DownloadUrl),
                       TrustedRequestKind.ReleaseAsset,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                EnsureDownloadSizeAllowed(response.Content.Headers.ContentLength);
                await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var local = File.Create(tempArchivePath);
                await CopyDownloadAsync(
                        remote,
                        local,
                        response.Content.Headers.ContentLength,
                        _maxDownloadBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await VerifyArchiveChecksumAsync(tempArchivePath, expectedChecksum, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new GeProtonInstallProgress(
                GeProtonInstallStage.Installing,
                0,
                null));

            var rootFolder = DetectArchiveRootFolderName(tempArchivePath);
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                rootFolder = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(release.AssetName));
            }

            rootFolder = SanitizeFolderName(rootFolder);
            if (string.IsNullOrWhiteSpace(rootFolder))
                throw new InvalidOperationException("Unable to determine installation folder name.");

            if (!TryResolveManagedDirectChild(rootFolder, out var targetDir))
                throw new InvalidOperationException("Unable to determine a safe installation folder name.");

            var relativeInstalledPath = NormalizeRelativePath(
                Path.Combine(ManagedRunnerRelativeRoot, rootFolder));

            if (Directory.Exists(targetDir) && !replaceExisting)
            {
                EnsureCompleteProtonRunner(targetDir);
                return relativeInstalledPath;
            }

            ValidateArchiveForExtraction(tempArchivePath, cancellationToken);

            var stagingDir = Path.Combine(_managedRunnerRoot, $".tmp_ge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);
            EnsureSafeManagedPath(stagingDir, requireDirectChild: true);

            try
            {
                await using var archiveStream = File.OpenRead(tempArchivePath);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, stagingDir, overwriteFiles: false);
                cancellationToken.ThrowIfCancellationRequested();

                var extractedRunnerDir = ResolveExtractedRunnerDirectory(stagingDir, rootFolder);
                EnsureSafeManagedPath(extractedRunnerDir, requireDirectChild: false);
                EnsureCompleteProtonRunner(extractedRunnerDir);
                PublishRunnerDirectory(extractedRunnerDir, stagingDir, targetDir, replaceExisting);
            }
            finally
            {
                TryDeleteManagedDirectory(stagingDir);
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

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveManagedRunnerDirectory(runnerDirectory, out var revalidatedDirectory))
                return false;

            if (Directory.Exists(revalidatedDirectory))
                Directory.Delete(revalidatedDirectory, recursive: true);

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public bool IsManagedRunnerPath(string storedPath)
        => TryResolveManagedRunnerDirectory(storedPath, out _);

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

            if (!FileSystemPathIdentity.Equals(parentDirectory, _managedRunnerRoot) ||
                !IsSafeManagedPath(candidate, requireDirectChild: true))
            {
                return false;
            }

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

    private static bool TryFindReleaseAsset(JsonElement assets, string assetName, out string downloadUrl)
    {
        downloadUrl = string.Empty;
        foreach (var candidate in assets.EnumerateArray())
        {
            if (TryGetStringProperty(candidate, "name", out var candidateName) &&
                string.Equals(candidateName, assetName, StringComparison.Ordinal) &&
                TryGetStringProperty(candidate, "browser_download_url", out downloadUrl))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateReleaseSource(GeProtonRelease release)
    {
        if (string.IsNullOrWhiteSpace(release.TagName) ||
            string.IsNullOrWhiteSpace(release.AssetName) ||
            release.TagName.IndexOfAny(['/', '\\']) >= 0 ||
            release.AssetName.IndexOfAny(['/', '\\']) >= 0 ||
            !release.AssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateUntrustedSourceException();
        }

        var checksumName = release.AssetName[..^".tar.gz".Length] + ".sha512sum";
        if (!IsExpectedReleaseAssetUrl(release.DownloadUrl, release.TagName, release.AssetName) ||
            !IsExpectedReleaseAssetUrl(release.ChecksumUrl, release.TagName, checksumName))
        {
            throw CreateUntrustedSourceException();
        }
    }

    private async Task<byte[]> DownloadExpectedChecksumAsync(
        GeProtonRelease release,
        CancellationToken cancellationToken)
    {
        using var response = await SendTrustedGetAsync(
                new Uri(release.ChecksumUrl),
                TrustedRequestKind.ReleaseAsset,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var checksumBytes = await ReadLimitedContentAsync(
                response.Content,
                GeProtonMaxChecksumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseSha512Checksum(Encoding.UTF8.GetString(checksumBytes), release.AssetName);
    }

    private static byte[] ParseSha512Checksum(string checksumContent, string assetName)
    {
        foreach (var rawLine in checksumContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimStart('\uFEFF');
            if (line.Length < 128)
                continue;

            var hashText = line[..128];
            var referencedName = line[128..].TrimStart();
            if (referencedName.StartsWith('*'))
                referencedName = referencedName[1..];

            if (!string.Equals(referencedName, assetName, StringComparison.Ordinal))
                continue;

            try
            {
                var hash = Convert.FromHexString(hashText);
                if (hash.Length == SHA512.HashSizeInBytes)
                    return hash;
            }
            catch (FormatException)
            {
                break;
            }
        }

        throw new InvalidDataException(GetMessage(
            "Settings_GeProtonChecksumInvalid",
            "The GE-Proton checksum file is invalid or does not match the selected archive name."));
    }

    private static async Task VerifyArchiveChecksumAsync(
        string archivePath,
        byte[] expectedChecksum,
        CancellationToken cancellationToken)
    {
        await using var archive = File.OpenRead(archivePath);
        var actualChecksum = await SHA512.HashDataAsync(archive, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
        {
            throw new InvalidDataException(GetMessage(
                "Settings_GeProtonChecksumMismatch",
                "The GE-Proton download failed SHA-512 verification and was not installed."));
        }
    }

    private static async Task<byte[]> ReadLimitedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength.HasValue &&
            content.Headers.ContentLength.Value > maximumBytes)
        {
            throw CreateInvalidChecksumException();
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return destination.ToArray();
            if (bytesRead > maximumBytes - destination.Length)
                throw CreateInvalidChecksumException();

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendTrustedGetAsync(
        Uri initialUri,
        TrustedRequestKind requestKind,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedInitialUri(initialUri, requestKind))
            throw CreateUntrustedSourceException();

        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= GeProtonMaxRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(request, completionOption, cancellationToken)
                .ConfigureAwait(false);
            var effectiveUri = response.RequestMessage?.RequestUri ?? currentUri;

            if (!IsAllowedResponseUri(effectiveUri, initialUri, requestKind))
            {
                response.Dispose();
                throw CreateUntrustedSourceException();
            }

            if (!IsRedirectStatusCode(response.StatusCode))
                return response;

            if (redirectCount == GeProtonMaxRedirects || response.Headers.Location == null)
            {
                response.Dispose();
                throw new HttpRequestException("The GE-Proton download returned too many redirects.");
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(currentUri, response.Headers.Location);
            response.Dispose();

            if (!IsAllowedResponseUri(nextUri, initialUri, requestKind))
                throw CreateUntrustedSourceException();

            currentUri = nextUri;
        }

        throw new HttpRequestException("The GE-Proton download returned too many redirects.");
    }

    private static bool IsAllowedInitialUri(Uri uri, TrustedRequestKind requestKind) =>
        requestKind == TrustedRequestKind.ReleaseApi
            ? IsTrustedApiUri(uri)
            : IsOfficialReleaseAssetUri(uri);

    private static bool IsAllowedResponseUri(
        Uri uri,
        Uri initialUri,
        TrustedRequestKind requestKind)
    {
        if (requestKind == TrustedRequestKind.ReleaseApi)
            return IsTrustedApiUri(uri);

        if (!IsSecureHttpsUri(uri))
            return false;

        if (string.Equals(uri.IdnHost, GithubReleaseAssetHost, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(uri.Fragment);

        return string.Equals(uri.IdnHost, GithubHost, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, initialUri.AbsolutePath, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsTrustedApiUri(Uri uri) =>
        IsSecureHttpsUri(uri) &&
        string.Equals(uri.IdnHost, GithubApiHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, GeProtonApiPath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsOfficialReleaseAssetUri(Uri uri) =>
        IsSecureHttpsUri(uri) &&
        string.Equals(uri.IdnHost, GithubHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(GeProtonReleasePathPrefix, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSecureHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsExpectedReleaseAssetUrl(string url, string tagName, string assetName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsOfficialReleaseAssetUri(uri))
            return false;

        try
        {
            var decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
            var expectedPath = GeProtonReleasePathPrefix + tagName + "/" + assetName;
            return string.Equals(decodedPath, expectedPath, StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static InvalidDataException CreateUntrustedSourceException() =>
        new(GetMessage(
            "Settings_GeProtonUntrustedSource",
            "The GE-Proton download source is not an approved official GitHub release location."));

    private static InvalidDataException CreateInvalidChecksumException() =>
        new(GetMessage(
            "Settings_GeProtonChecksumInvalid",
            "The GE-Proton checksum file is invalid or does not match the selected archive name."));

    private static string GetMessage(string resourceName, string fallback) =>
        Strings.ResourceManager.GetString(resourceName, Strings.Culture) ?? fallback;

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

    private void EnsureDownloadSizeAllowed(long? contentLength)
    {
        if (!contentLength.HasValue || contentLength.Value <= _maxDownloadBytes)
            return;

        throw new InvalidDataException(GetLimitMessage(
            "Settings_GeProtonDownloadTooLargeFormat",
            "The GE-Proton download exceeds the allowed size of {0} MiB.",
            _maxDownloadBytes / (1024 * 1024)));
    }

    private void ValidateArchiveForExtraction(string tarGzPath, CancellationToken cancellationToken)
    {
        using var fileStream = File.OpenRead(tarGzPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        long extractedBytes = 0;
        var entryCount = 0;

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            entryCount++;
            if (entryCount > _maxArchiveEntries)
            {
                throw new InvalidDataException(GetLimitMessage(
                    "Settings_GeProtonArchiveEntryLimitFormat",
                    "The GE-Proton archive contains more than the allowed {0} entries.",
                    _maxArchiveEntries));
            }

            if (entry.Length < 0 || entry.Length > _maxExtractedBytes - extractedBytes)
            {
                throw new InvalidDataException(GetLimitMessage(
                    "Settings_GeProtonArchiveTooLargeFormat",
                    "The GE-Proton archive expands beyond the allowed size of {0} MiB.",
                    _maxExtractedBytes / (1024 * 1024)));
            }

            extractedBytes += entry.Length;
        }
    }

    private static string GetLimitMessage(string resourceName, string fallbackFormat, object value)
    {
        var format = Strings.ResourceManager.GetString(resourceName, Strings.Culture) ?? fallbackFormat;
        return string.Format(format, value);
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

    private static string ResolveExtractedRunnerDirectory(string stagingDir, string rootFolder)
    {
        var expectedRoot = Path.Combine(stagingDir, rootFolder);
        if (Directory.Exists(expectedRoot))
            return expectedRoot;

        var extractedDirs = Directory.GetDirectories(stagingDir);
        return extractedDirs.Length == 1 ? extractedDirs[0] : stagingDir;
    }

    private void PublishRunnerDirectory(
        string sourceDir,
        string stagingDir,
        string targetDir,
        bool replaceExisting)
    {
        EnsureManagedRunnerRootForMutation();
        EnsureSafeManagedPath(stagingDir, requireDirectChild: true);
        EnsureSafeManagedPath(sourceDir, requireDirectChild: false);
        EnsureSafeManagedPath(targetDir, requireDirectChild: true);

        if (!IsPathEqualToOrBelow(sourceDir, stagingDir))
            throw new IOException($"Refusing to publish runner directory outside staging: {sourceDir}");

        if (!Directory.Exists(targetDir))
        {
            Directory.Move(sourceDir, targetDir);
            return;
        }

        if (!replaceExisting)
            throw new IOException($"The runner directory already exists: {targetDir}");

        var backupDir = Path.Combine(
            Path.GetDirectoryName(targetDir)!,
            $".backup_ge_{Guid.NewGuid():N}");
        EnsureSafeManagedPath(backupDir, requireDirectChild: true);
        Directory.Move(targetDir, backupDir);

        try
        {
            // Both directories are below the managed runner root, so publishing
            // remains an atomic same-filesystem rename.
            Directory.Move(sourceDir, targetDir);
            TryDeleteManagedDirectory(backupDir);
        }
        catch
        {
            if (IsSafeManagedPath(targetDir, requireDirectChild: true) &&
                IsSafeManagedPath(backupDir, requireDirectChild: true) &&
                !Directory.Exists(targetDir) &&
                Directory.Exists(backupDir))
            {
                Directory.Move(backupDir, targetDir);
            }

            throw;
        }
    }

    private void EnsureManagedRunnerRootForMutation()
    {
        EnsureSafeManagedPath(_managedRunnerRoot, requireDirectChild: false);
        Directory.CreateDirectory(_managedRunnerRoot);
        EnsureSafeManagedPath(_managedRunnerRoot, requireDirectChild: false);
    }

    private bool TryResolveManagedDirectChild(string directoryName, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryName))
            return false;

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_managedRunnerRoot, directoryName));
            if (!IsSafeManagedPath(candidate, requireDirectChild: true))
                return false;

            directory = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureSafeManagedPath(string path, bool requireDirectChild)
    {
        if (!IsSafeManagedPath(path, requireDirectChild))
        {
            throw new IOException(
                $"Refusing to modify unsafe managed runner path '{path}'.");
        }
    }

    private bool IsSafeManagedPath(string path, bool requireDirectChild)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var relativeToManagedRoot = Path.GetRelativePath(_managedRunnerRoot, candidate);

            if (Path.IsPathRooted(relativeToManagedRoot) ||
                relativeToManagedRoot == ".." ||
                relativeToManagedRoot.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    FileSystemPathIdentity.Comparison))
            {
                return false;
            }

            if (requireDirectChild &&
                (relativeToManagedRoot == "." ||
                 relativeToManagedRoot.IndexOfAny(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
            {
                return false;
            }

            return !ContainsSymbolicLinkBelowDataRoot(candidate);
        }
        catch
        {
            return false;
        }
    }

    private bool ContainsSymbolicLinkBelowDataRoot(string path)
    {
        var relativePath = Path.GetRelativePath(_dataRoot, path);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                FileSystemPathIdentity.Comparison))
        {
            return true;
        }

        var currentPath = _dataRoot;
        foreach (var component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);

            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsPathEqualToOrBelow(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (FileSystemPathIdentity.Equals(fullPath, fullRoot))
            return true;

        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, FileSystemPathIdentity.Comparison);
    }

    private static async Task CopyDownloadAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        long maximumBytes,
        IProgress<GeProtonInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long downloadedBytes = 0;
        var lastReportedPercentage = -1;

        progress?.Report(new GeProtonInstallProgress(
            GeProtonInstallStage.Downloading,
            downloadedBytes,
            totalBytes));

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            if (bytesRead > maximumBytes - downloadedBytes)
            {
                throw new InvalidDataException(GetLimitMessage(
                    "Settings_GeProtonDownloadTooLargeFormat",
                    "The GE-Proton download exceeds the allowed size of {0} MiB.",
                    maximumBytes / (1024 * 1024)));
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloadedBytes += bytesRead;

            if (totalBytes is not > 0)
                continue;

            var percentage = (int)Math.Clamp(downloadedBytes * 100L / totalBytes.Value, 0L, 100L);
            if (percentage == lastReportedPercentage)
                continue;

            lastReportedPercentage = percentage;
            progress?.Report(new GeProtonInstallProgress(
                GeProtonInstallStage.Downloading,
                downloadedBytes,
                totalBytes));
        }

        if (totalBytes is not > 0)
            return;

        progress?.Report(new GeProtonInstallProgress(
            GeProtonInstallStage.Downloading,
            downloadedBytes,
            totalBytes));
    }

    private void TryDeleteManagedDirectory(string path)
    {
        try
        {
            if (IsSafeManagedPath(path, requireDirectChild: true) && Directory.Exists(path))
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
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
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

    private enum TrustedRequestKind
    {
        ReleaseApi,
        ReleaseAsset
    }
}

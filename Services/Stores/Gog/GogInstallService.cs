using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog;

public enum GogInstallPlatform
{
    Linux = 0,
    Windows = 1
}

public sealed record GogInstallerDownloadFile(
    string Url,
    string FileName,
    long? Size,
    string? ChecksumUrl = null);

internal sealed record GogResolvedDownloadLink(
    string DownloadUrl,
    string? ChecksumUrl);

internal sealed record GogDownloadChecksum(
    long? TotalSize,
    string? Md5);

public sealed record GogInstallerPackage(
    string GameId,
    GogInstallPlatform Platform,
    string InstallerName,
    string Version,
    IReadOnlyList<GogInstallerDownloadFile> Files)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Version) ? InstallerName : $"{InstallerName} ({Version})";
}

public sealed record GogDlcCatalogItem(
    string ProductId,
    string Title,
    IReadOnlyList<GogInstallPlatform> AvailableInstallerPlatforms,
    IReadOnlyList<GogDlcInstallerMetadata>? InstallerMetadata = null)
{
    public bool HasInstaller => AvailableInstallerPlatforms.Count > 0;

    public GogDlcInstallerMetadata? GetInstallerMetadata(GogInstallPlatform platform) =>
        InstallerMetadata?.FirstOrDefault(metadata => metadata.Platform == platform);
}

public sealed record GogDlcInstallerMetadata(
    GogInstallPlatform Platform,
    string Version,
    string Signature);

public sealed record GogDownloadedInstallerPackage(
    string StagingDirectory,
    string EntryFilePath,
    IReadOnlyList<string> DownloadedFiles);

public sealed record GogInstallerDownloadProgress(
    string FileName,
    int FileIndex,
    int FileCount,
    long BytesDownloadedCurrentFile,
    long? BytesTotalCurrentFile,
    long BytesDownloadedOverall,
    long? BytesTotalOverall);

public sealed record GogPlayTaskInfo(
    string Path,
    string? Arguments,
    string? WorkingDirectory,
    bool IsPrimary);

public sealed class GogInstallService
{
    private static readonly Uri ApiBaseUri = new("https://api.gog.com/");
    private static readonly Uri EmbedBaseUri = new("https://embed.gog.com/");

    private readonly GogAuthService _authService;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;

    public GogInstallService(
        GogAuthService authService,
        HttpClient httpClient,
        HttpClient? downloadHttpClient = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadHttpClient = downloadHttpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };
        if (downloadHttpClient == null)
        {
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count > 0)
            {
                foreach (var userAgent in _httpClient.DefaultRequestHeaders.UserAgent)
                    _downloadHttpClient.DefaultRequestHeaders.UserAgent.Add(userAgent);
            }
            else
            {
                _downloadHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Retromind/1.0 (Linux Portable Media Manager)");
            }
        }
    }

    public async Task<GogInstallerPackage?> ResolveInstallerPackageAsync(
        string gameId,
        GogInstallPlatform platform,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        var accessToken = await _authService.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("GOG authentication is required.");

        var productUri = new Uri(ApiBaseUri, $"products/{Uri.EscapeDataString(gameId)}?expand=downloads");
        using var productRequest = CreateAuthorizedRequest(productUri, accessToken);
        using var productResponse = await _httpClient.SendAsync(productRequest, ct).ConfigureAwait(false);
        var productBody = await productResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!productResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GOG product request failed ({(int)productResponse.StatusCode} {productResponse.ReasonPhrase}): {ExtractErrorDetail(productBody)}");

        using var productJson = JsonDocument.Parse(productBody);
        if (!TrySelectInstaller(productJson.RootElement, platform, out var selectedInstaller))
            return null;

        var installerName = GetString(selectedInstaller, "name");
        if (string.IsNullOrWhiteSpace(installerName))
            installerName = $"GOG {gameId}";

        var installerVersion = GetString(selectedInstaller, "version") ?? string.Empty;

        if (!selectedInstaller.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            return null;

        var resolvedFiles = new List<GogInstallerDownloadFile>();
        var index = 0;
        foreach (var file in filesElement.EnumerateArray())
        {
            index++;
            var downlinkEndpoint = GetString(file, "downlink");
            if (string.IsNullOrWhiteSpace(downlinkEndpoint))
                continue;

            var resolvedLink = await ResolveDownlinkAsync(downlinkEndpoint, accessToken, ct).ConfigureAwait(false);
            if (resolvedLink == null)
                continue;

            var fallbackName = $"installer_part_{index:D2}";
            var fileName = ResolveFileName(resolvedLink.DownloadUrl, fallbackName);
            var size = GetLong(file, "size");

            resolvedFiles.Add(new GogInstallerDownloadFile(
                resolvedLink.DownloadUrl,
                fileName,
                size,
                resolvedLink.ChecksumUrl));
        }

        if (resolvedFiles.Count == 0)
            return null;

        return new GogInstallerPackage(gameId, platform, installerName, installerVersion, resolvedFiles);
    }

    /// <summary>
    /// Validates whether an install path is safe for uninstall deletion.
    /// Rules:
    /// - Block dangerous targets (root, home, common system paths).
    /// - Never allow deleting DataRoot or LibraryRoot themselves.
    /// - Require a valid ownership marker (.retromind-install.json) for deletable directories.
    /// - If the install directory is already missing, allow metadata-only cleanup.
    /// </summary>
    private static bool IsSafeToDeletePath(string installPath, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(installPath);
        }
        catch
        {
            return false;
        }

        if (GogInstallDirectorySafety.IsDangerousPath(fullPath))
            return false;

        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal))
            return false;

        var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
        if (string.Equals(fullPath, libraryRoot, StringComparison.Ordinal))
            return false;

        try
        {
            if (GogInstallDirectorySafety.ContainsSymbolicLinkInPath(fullPath))
                return false;
        }
        catch
        {
            // If the complete path cannot be inspected, it is not safe to delete.
            return false;
        }

        // If the folder no longer exists, no physical deletion can happen.
        // Allow metadata cleanup to proceed.
        if (!Directory.Exists(fullPath))
            return true;

        if (AppPaths.IsPathInsideDataRoot(fullPath))
            return GogInstallDirectorySafety.HasValidMarker(fullPath, item);

        var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? libraryRoot
            : libraryRoot + Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(libraryRootWithSep, StringComparison.Ordinal))
            return GogInstallDirectorySafety.HasValidMarker(fullPath, item);

        return GogInstallDirectorySafety.HasValidMarker(fullPath, item);
    }
    
    public async Task<IReadOnlyList<GogInstallPlatform>> GetAvailableInstallerPlatformsAsync(
        string gameId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        var accessToken = await _authService.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("GOG authentication is required.");

        var productUri = new Uri(ApiBaseUri, $"products/{Uri.EscapeDataString(gameId)}?expand=downloads");
        using var productRequest = CreateAuthorizedRequest(productUri, accessToken);
        using var productResponse = await _httpClient.SendAsync(productRequest, ct).ConfigureAwait(false);
        var productBody = await productResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!productResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GOG product request failed ({(int)productResponse.StatusCode} {productResponse.ReasonPhrase}): {ExtractErrorDetail(productBody)}");

        using var productJson = JsonDocument.Parse(productBody);
        return ExtractAvailableInstallerPlatforms(productJson.RootElement);
    }

    public async Task<IReadOnlyList<GogDlcCatalogItem>> GetOwnedDlcCatalogAsync(
        string gameId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        var accessToken = await _authService.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("GOG authentication is required.");

        var productUri = new Uri(
            ApiBaseUri,
            $"products/{Uri.EscapeDataString(gameId)}?expand=downloads,expanded_dlcs");
        using var productRequest = CreateAuthorizedRequest(productUri, accessToken);
        using var productResponse = await _httpClient.SendAsync(productRequest, ct).ConfigureAwait(false);
        var productBody = await productResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!productResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GOG product request failed ({(int)productResponse.StatusCode} {productResponse.ReasonPhrase}): {ExtractErrorDetail(productBody)}");
        }

        var ownedProductsUri = new Uri(EmbedBaseUri, "user/data/games");
        using var ownedProductsRequest = CreateAuthorizedRequest(ownedProductsUri, accessToken);
        using var ownedProductsResponse = await _httpClient.SendAsync(ownedProductsRequest, ct).ConfigureAwait(false);
        var ownedProductsBody = await ownedProductsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!ownedProductsResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GOG owned-products request failed ({(int)ownedProductsResponse.StatusCode} {ownedProductsResponse.ReasonPhrase}): {ExtractErrorDetail(ownedProductsBody)}");
        }

        using var productJson = JsonDocument.Parse(productBody);
        using var ownedProductsJson = JsonDocument.Parse(ownedProductsBody);
        return ParseOwnedDlcCatalog(productJson.RootElement, ownedProductsJson.RootElement);
    }

    public async Task UninstallGogGameAsync(
        MediaItem item,
        CancellationToken ct = default,
        bool deletePrefix = true)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        // --- Extract paths from CustomFields ---
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            string.IsNullOrWhiteSpace(storedInstallPath))
        {
            throw new InvalidOperationException("Cannot uninstall: install path is not set.");
        }

        if (!GogInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            throw new InvalidOperationException(
                $"Cannot uninstall: install path '{storedInstallPath}' is invalid or escapes the portable data root.");
        }
        
        var prefixPath = item.PrefixPath;
        bool prefixSkippedForSafety = false;

        if (!deletePrefix && !string.IsNullOrWhiteSpace(prefixPath))
        {
            prefixPath = null;
            prefixSkippedForSafety = true;
        }
        
        if (!string.IsNullOrWhiteSpace(prefixPath))
        {
            // 1. Normalize path (handles ../ sequences and redundant separators)
            var resolvedPrefix = PrefixPathHelper.ResolveAbsolutePrefixPath(
                prefixPath,
                AppPaths.LibraryRoot);
            
            // 2. Safety Check: Ensure resolved path is inside LibraryRoot
            var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
            var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? libraryRoot
                : libraryRoot + Path.DirectorySeparatorChar;

            var protectedLibraryDirectories = new[]
            {
                Path.Combine(libraryRoot, "Prefixes"),
                Path.Combine(libraryRoot, "Games")
            };
            var isProtectedLibraryDirectory = protectedLibraryDirectories.Any(path =>
                FileSystemPathIdentity.Equals(resolvedPrefix, path));

            // CRITICAL: resolvedPrefix must be a STRICT subdirectory of LibraryRoot.
            // Never allow deleting LibraryRoot itself or shared library roots such as Prefixes/Games.
            var isSafePrefix =
                !isProtectedLibraryDirectory &&
                resolvedPrefix.StartsWith(libraryRootWithSep, FileSystemPathIdentity.Comparison);

            if (isSafePrefix)
            {
                try
                {
                    isSafePrefix = !GogInstallDirectorySafety.ContainsSymbolicLinkInPath(resolvedPrefix);
                }
                catch
                {
                    isSafePrefix = false;
                }
            }

            if (!isSafePrefix)
            {
                Debug.WriteLine($"[Warning] Prefix path '{prefixPath}' is outside LibraryRoot, is a protected library directory, or contains a symbolic link ('{resolvedPrefix}'). Skipping prefix deletion for safety.");
                prefixPath = null; // Abort prefix deletion
                prefixSkippedForSafety = true;
            }
            else
            {
                prefixPath = resolvedPrefix;
            }
        }

        // --- Phase B: Physical deletion ---
        // Delete install directory first, then prefix.
        // If any deletion fails, we abort and do NOT touch metadata (Phase C).

        // --- Safety validation before deletion ---
        if (!IsSafeToDeletePath(installPath, item))
        {
            throw new InvalidOperationException(
                $"Refusing to delete install path '{installPath}': path is outside allowed boundaries.");
        }

        try
        {
            await DeleteDirectoryAsync(installPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to delete install directory '{installPath}'. The game may still be running or files are locked.",
                ex);
        }

        if (!string.IsNullOrWhiteSpace(prefixPath) && Directory.Exists(prefixPath))
        {
            try
            {
                await DeleteDirectoryAsync(prefixPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Install directory is already gone, but prefix deletion failed.
                // This is a partial failure – throw to prevent Phase C.
                throw new InvalidOperationException(
                    $"Install directory deleted, but failed to delete prefix directory '{prefixPath}'. " +
                    "The prefix may still be in use or files are locked.",
                    ex);
            }
        }

        // --- Phase C: Logical cleanup (metadata) ---
        // Only reached if Phase B succeeded completely.
        // Remove all Store.* custom fields related to the installation.
        var fieldsToRemove = new[]
        {
            CustomFieldKeyHelper.StoreInstallPath,
            CustomFieldKeyHelper.StoreInstallPlatform,
            CustomFieldKeyHelper.StoreInstallRunnerVersionId,
            CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference,
            CustomFieldKeyHelper.StoreUpdateAvailable,
            CustomFieldKeyHelper.StoreDlcUpdateAvailable,
            CustomFieldKeyHelper.StoreInstalledVersion,
            CustomFieldKeyHelper.StoreInstalledInstallerSignature,
            CustomFieldKeyHelper.StoreUpdateLastStatus,
            CustomFieldKeyHelper.StoreUpdateLastCheckedUtc
        };

        // Reassign the dictionary to trigger INotifyPropertyChanged.
        // In-place modifications (Remove) do not notify the UI, causing stale metadata display.
        item.CustomFields = item.CustomFields
            .Where(kv => !fieldsToRemove.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        item.GogDlcInstallations = null;

        // Only clear PrefixPath metadata if we actually deleted the prefix safely.
        // If prefix was skipped for safety, preserve the metadata so the user can clean up manually.
        if (!prefixSkippedForSafety)
        {
            item.PrefixPath = null;
        }
        else
        {
            Debug.WriteLine($"[Info] Prefix path metadata preserved for manual cleanup: '{item.PrefixPath}'");
        }

        GogLaunchConfigurationHelper.ClearAfterUninstall(item);
    }

    /// <summary>
    /// Recursively deletes a directory and all its contents.
    /// Uses retry logic for files that may be temporarily locked.
    /// </summary>
    private static async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        // Retry up to 3 times with short delays for locked files.
        var maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Run the synchronous deletion on a background thread to avoid blocking UI
                await Task.Run(() =>
                {
                    GogInstallDirectorySafety.DeleteDirectoryTreeWithoutFollowingLinks(path, ct);
                }, ct);
                return; // Success
            }
            catch (IOException ex) when (ex.HResult == -2147024864)
            {
                // HResult 0x80070020 = Sharing violation (file in use)
                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                throw; // Re-throw on last attempt
            }
            catch (UnauthorizedAccessException)
            {
                // Permission denied – no point retrying
                throw;
            }
        }
    }

    public async Task<GogDownloadedInstallerPackage> DownloadInstallerPackageAsync(
        GogInstallerPackage package,
        string stagingDirectory,
        IProgress<GogInstallerDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (package == null)
            throw new ArgumentNullException(nameof(package));

        if (string.IsNullOrWhiteSpace(stagingDirectory))
            throw new ArgumentException("Staging directory is required.", nameof(stagingDirectory));

        Directory.CreateDirectory(stagingDirectory);

        var downloadedFiles = new List<string>(package.Files.Count);
        var perFileDownloaded = new long[package.Files.Count];
        var hasKnownOverallTotal = package.Files.Count > 0 && package.Files.All(f => f.Size.HasValue && f.Size.Value > 0);
        long? overallTotalBytes = hasKnownOverallTotal
            ? package.Files.Sum(f => f.Size!.Value)
            : null;

        for (var index = 0; index < package.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var file = package.Files[index];
            var safeName = SanitizeFileName(file.FileName);
            var targetPath = Path.Combine(stagingDirectory, safeName);
            await DownloadFileWithResumeAsync(
                file,
                targetPath,
                bytesDownloaded =>
                {
                    perFileDownloaded[index] = Math.Max(0, bytesDownloaded);
                    var overallDownloadedBytes = perFileDownloaded.Sum();

                    progress?.Report(new GogInstallerDownloadProgress(
                        safeName,
                        index + 1,
                        package.Files.Count,
                        perFileDownloaded[index],
                        file.Size,
                        overallDownloadedBytes,
                        overallTotalBytes));
                },
                ct).ConfigureAwait(false);
            downloadedFiles.Add(targetPath);
        }

        var entryFile = SelectPrimaryInstallerFile(downloadedFiles, package.Platform);
        return new GogDownloadedInstallerPackage(stagingDirectory, entryFile, downloadedFiles);
    }

    public async Task<IReadOnlyList<GogPlayTaskInfo>> GetGameDetailsPlayTasksAsync(
        string gameId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        var accessToken = await _authService.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("GOG authentication is required.");

        var gameDetailsUri = new Uri(EmbedBaseUri, $"account/gameDetails/{Uri.EscapeDataString(gameId)}.json");
        using var request = CreateAuthorizedRequest(gameDetailsUri, accessToken);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return Array.Empty<GogPlayTaskInfo>();

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<GogPlayTaskInfo>();

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("playTasks", out var playTasks) || playTasks.ValueKind != JsonValueKind.Array)
            return Array.Empty<GogPlayTaskInfo>();

        return GogPlayTaskParser.Parse(playTasks);
    }

    private async Task<GogResolvedDownloadLink?> ResolveDownlinkAsync(
        string downlinkEndpoint,
        string accessToken,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(downlinkEndpoint, UriKind.Absolute, out var downlinkUri))
            return null;

        using var request = CreateAuthorizedRequest(downlinkUri, accessToken);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var looksLikeJson = mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!looksLikeJson)
        {
            var redirectedUrl = response.RequestMessage?.RequestUri?.ToString();
            return string.IsNullOrWhiteSpace(redirectedUrl)
                ? null
                : new GogResolvedDownloadLink(redirectedUrl, null);
        }

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        return ParseResolvedDownlink(payload);
    }

    internal static GogResolvedDownloadLink? ParseResolvedDownlink(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        if (TryGetString(root, out var direct))
            return new GogResolvedDownloadLink(direct, null);

        if (root.ValueKind == JsonValueKind.Object)
        {
            var checksumUrl = root.TryGetProperty("checksum", out var checksum) &&
                              TryGetString(checksum, out var parsedChecksumUrl)
                ? parsedChecksumUrl
                : null;

            if (root.TryGetProperty("downlink", out var downlink) &&
                TryGetString(downlink, out var downlinkUrl))
            {
                return new GogResolvedDownloadLink(downlinkUrl, checksumUrl);
            }

            if (root.TryGetProperty("url", out var url) && TryGetString(url, out var directUrl))
                return new GogResolvedDownloadLink(directUrl, checksumUrl);

            if (root.TryGetProperty("urls", out var urls))
            {
                if (urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidate in urls.EnumerateArray())
                    {
                        if (TryGetString(candidate, out var candidateUrl))
                            return new GogResolvedDownloadLink(candidateUrl, checksumUrl);

                        if (candidate.ValueKind == JsonValueKind.Object &&
                            candidate.TryGetProperty("url", out var nestedUrl) &&
                            TryGetString(nestedUrl, out candidateUrl))
                        {
                            return new GogResolvedDownloadLink(candidateUrl, checksumUrl);
                        }
                    }
                }
                else if (TryGetString(urls, out var urlsValue))
                {
                    return new GogResolvedDownloadLink(urlsValue, checksumUrl);
                }
            }
        }

        return null;
    }

    private async Task DownloadFileWithResumeAsync(
        GogInstallerDownloadFile file,
        string targetPath,
        Action<long>? reportProgress,
        CancellationToken ct)
    {
        var hasChecksumReference = !string.IsNullOrWhiteSpace(file.ChecksumUrl);
        var checksum = await TryGetDownloadChecksumAsync(file.ChecksumUrl, ct).ConfigureAwait(false);
        var catalogSize = file.Size is > 0 ? file.Size : null;
        var expectedSize = checksum != null ? checksum.TotalSize : catalogSize;
        var expectedMd5 = checksum?.Md5;

        if (File.Exists(targetPath))
        {
            var finalLength = new FileInfo(targetPath).Length;
            if ((!hasChecksumReference || checksum != null) &&
                await IsDownloadedFileValidAsync(
                    targetPath,
                    expectedSize,
                    expectedMd5,
                    ct).ConfigureAwait(false))
            {
                reportProgress?.Invoke(finalLength);
                return;
            }

            File.Delete(targetPath);
        }

        var partPath = targetPath + ".part";
        var resumeOffset = 0L;

        if (File.Exists(partPath))
        {
            var partialLength = new FileInfo(partPath).Length;
            var checksumTotalSize = checksum?.TotalSize;
            if (checksum != null &&
                await IsDownloadedFileValidAsync(
                    partPath,
                    checksum.TotalSize,
                    checksum.Md5,
                    ct).ConfigureAwait(false))
            {
                File.Move(partPath, targetPath, overwrite: true);
                reportProgress?.Invoke(partialLength);
                return;
            }

            if (partialLength > 0 &&
                (!checksumTotalSize.HasValue || partialLength < checksumTotalSize.Value))
            {
                resumeOffset = partialLength;
            }
            else
            {
                File.Delete(partPath);
            }
        }

        var attemptedFreshRetryAfterMismatch = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (resumeOffset > 0)
                request.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            using var response = await _downloadHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (resumeOffset > 0 &&
                response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (!attemptedFreshRetryAfterMismatch)
                {
                    attemptedFreshRetryAfterMismatch = true;
                    resumeOffset = 0;
                    if (File.Exists(partPath))
                        File.Delete(partPath);
                    continue;
                }
            }

            response.EnsureSuccessStatusCode();

            // If the server honors Range, it must return the exact requested start.
            var append = response.StatusCode == System.Net.HttpStatusCode.PartialContent && resumeOffset > 0;
            if (append && response.Content.Headers.ContentRange?.From != resumeOffset)
            {
                if (!attemptedFreshRetryAfterMismatch)
                {
                    attemptedFreshRetryAfterMismatch = true;
                    resumeOffset = 0;
                    if (File.Exists(partPath))
                        File.Delete(partPath);
                    continue;
                }

                throw new InvalidOperationException(
                    $"GOG returned an invalid resume range for '{file.FileName}'.");
            }

            // A successful full response means the server ignored Range. Restart cleanly.
            if (!append && resumeOffset > 0 && File.Exists(partPath))
            {
                File.Delete(partPath);
                resumeOffset = 0;
            }

            var responseStart = append ? resumeOffset : 0L;
            var responseLength = response.Content.Headers.ContentLength;
            long? responseTotalSize = response.StatusCode == System.Net.HttpStatusCode.PartialContent
                ? response.Content.Headers.ContentRange?.Length
                : responseLength;
            if (!responseTotalSize.HasValue && responseLength.HasValue)
                responseTotalSize = responseStart + responseLength.Value;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytesWritten = responseStart;
            reportProgress?.Invoke(bytesWritten);

            await using (var destination = new FileStream(
                             partPath,
                             append ? FileMode.Append : FileMode.Create,
                             FileAccess.Write,
                             FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (bytesRead <= 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    bytesWritten += bytesRead;
                    reportProgress?.Invoke(bytesWritten);
                }
            }

            var responseBytesRead = bytesWritten - responseStart;
            var transferLengthMatches = !responseLength.HasValue || responseBytesRead == responseLength.Value;
            var validationSize = checksum != null
                ? checksum.TotalSize
                : responseTotalSize ?? catalogSize;
            var downloadIsValid = transferLengthMatches &&
                                  await IsDownloadedFileValidAsync(
                                      partPath,
                                      validationSize,
                                      expectedMd5,
                                      ct).ConfigureAwait(false);
            if (!downloadIsValid)
            {
                if (!attemptedFreshRetryAfterMismatch)
                {
                    attemptedFreshRetryAfterMismatch = true;
                    resumeOffset = 0;
                    if (File.Exists(partPath))
                        File.Delete(partPath);
                    continue;
                }

                if (File.Exists(partPath))
                    File.Delete(partPath);

                throw new InvalidOperationException(
                    $"Downloaded installer file '{file.FileName}' failed its integrity check.");
            }

            File.Move(partPath, targetPath, overwrite: true);
            return;
        }
    }

    private async Task<GogDownloadChecksum?> TryGetDownloadChecksumAsync(
        string? checksumUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(checksumUrl) ||
            !Uri.TryCreate(checksumUrl, UriKind.Absolute, out var checksumUri))
        {
            return null;
        }

        try
        {
            using var response = await _downloadHttpClient.GetAsync(
                    checksumUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseDownloadChecksum(xml, out var checksum) ? checksum : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Installer checksum metadata could not be loaded: {ex.Message}");
            return null;
        }
    }

    internal static bool TryParseDownloadChecksum(string? xml, out GogDownloadChecksum checksum)
    {
        checksum = new GogDownloadChecksum(null, null);
        if (string.IsNullOrWhiteSpace(xml))
            return false;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 2 * 1024 * 1024
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            var root = XDocument.Load(reader, LoadOptions.None).Root;
            if (root == null || !root.Name.LocalName.Equals("file", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(root.Attribute("available")?.Value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.Attribute("available")?.Value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            long? totalSize = null;
            if (long.TryParse(
                    root.Attribute("total_size")?.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedSize) &&
                parsedSize > 0)
            {
                totalSize = parsedSize;
            }

            var md5 = NormalizeMd5(root.Attribute("md5")?.Value);
            if (!totalSize.HasValue && md5 == null)
                return false;

            checksum = new GogDownloadChecksum(totalSize, md5);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsDownloadedFileValidAsync(
        string path,
        long? expectedSize,
        string? expectedMd5,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        var actualSize = new FileInfo(path).Length;
        if (actualSize <= 0 || (expectedSize.HasValue && actualSize != expectedSize.Value))
            return false;

        var normalizedExpectedMd5 = NormalizeMd5(expectedMd5);
        if (normalizedExpectedMd5 == null)
            return true;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var md5 = MD5.Create();
        var actualHash = await md5.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(actualHash),
            normalizedExpectedMd5,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeMd5(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length != 32 || normalized.Any(character => !Uri.IsHexDigit(character)))
            return null;

        return normalized.ToUpperInvariant();
    }

    private static HttpRequestMessage CreateAuthorizedRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static bool TrySelectInstaller(JsonElement productRoot, GogInstallPlatform platform, out JsonElement installer)
    {
        installer = default;

        if (!productRoot.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object)
            return false;

        if (!downloads.TryGetProperty("installers", out var installers) || installers.ValueKind != JsonValueKind.Array)
            return false;

        var osName = platform == GogInstallPlatform.Linux ? "linux" : "windows";
        var candidates = new List<JsonElement>();
        foreach (var candidate in installers.EnumerateArray())
        {
            if (!string.Equals(GetString(candidate, "os"), osName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!HasInstallerFiles(candidate))
                continue;

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return false;

        installer = candidates
            .OrderByDescending(static c => IsPreferredLanguage(GetString(c, "language")))
            .First();
        return true;
    }

    private static IReadOnlyList<GogInstallPlatform> ExtractAvailableInstallerPlatforms(JsonElement productRoot)
    {
        var availablePlatforms = new HashSet<GogInstallPlatform>();

        if (!productRoot.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object)
            return Array.Empty<GogInstallPlatform>();

        if (!downloads.TryGetProperty("installers", out var installers) || installers.ValueKind != JsonValueKind.Array)
            return Array.Empty<GogInstallPlatform>();

        foreach (var candidate in installers.EnumerateArray())
        {
            if (!HasInstallerFiles(candidate))
                continue;

            var os = GetString(candidate, "os");
            if (string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase))
                availablePlatforms.Add(GogInstallPlatform.Linux);
            else if (string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                availablePlatforms.Add(GogInstallPlatform.Windows);
        }

        return availablePlatforms
            .OrderBy(static p => p == GogInstallPlatform.Linux ? 0 : 1)
            .ToArray();
    }

    internal static IReadOnlyList<GogDlcCatalogItem> ParseOwnedDlcCatalog(
        JsonElement productRoot,
        JsonElement ownedProductsRoot)
    {
        var ownedProductIds = ExtractOwnedProductIds(ownedProductsRoot);
        if (ownedProductIds.Count == 0 ||
            !productRoot.TryGetProperty("expanded_dlcs", out var dlcs) ||
            dlcs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GogDlcCatalogItem>();
        }

        var catalog = new List<GogDlcCatalogItem>();
        foreach (var dlc in dlcs.EnumerateArray())
        {
            var productId = GetString(dlc, "id");
            if (string.IsNullOrWhiteSpace(productId) || !ownedProductIds.Contains(productId))
                continue;

            var title = GetString(dlc, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = $"GOG DLC {productId}";

            var installerMetadata = ExtractDlcInstallerMetadata(dlc);
            catalog.Add(new GogDlcCatalogItem(
                productId,
                title,
                installerMetadata.Select(static metadata => metadata.Platform).ToArray(),
                installerMetadata));
        }

        return catalog;
    }

    private static IReadOnlyList<GogDlcInstallerMetadata> ExtractDlcInstallerMetadata(JsonElement productRoot)
    {
        var result = new List<GogDlcInstallerMetadata>(2);
        foreach (var platform in new[] { GogInstallPlatform.Linux, GogInstallPlatform.Windows })
        {
            if (!TrySelectInstaller(productRoot, platform, out var installer))
                continue;

            result.Add(new GogDlcInstallerMetadata(
                platform,
                GetString(installer, "version")?.Trim() ?? string.Empty,
                BuildDlcCatalogInstallerSignature(installer)));
        }

        return result;
    }

    private static string BuildDlcCatalogInstallerSignature(JsonElement installer)
    {
        if (!installer.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var file in files.EnumerateArray())
        {
            var downlink = GetString(file, "downlink")?.Trim() ?? string.Empty;
            var size = GetLong(file, "size")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (downlink.Length > 0)
                parts.Add($"{downlink}|{size}");
        }

        if (parts.Count == 0)
            return string.Empty;

        var payload = string.Join('\n', parts.OrderBy(static part => part, StringComparer.OrdinalIgnoreCase));
        return GogDlcUpdateComparer.CatalogSignaturePrefix +
               Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static HashSet<string> ExtractOwnedProductIds(JsonElement ownedProductsRoot)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!ownedProductsRoot.TryGetProperty("owned", out var owned) || owned.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var productIdElement in owned.EnumerateArray())
        {
            if (TryGetString(productIdElement, out var productId))
                result.Add(productId);
        }

        return result;
    }

    private static bool HasInstallerFiles(JsonElement installer)
    {
        if (!installer.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var file in filesElement.EnumerateArray())
        {
            var downlink = GetString(file, "downlink");
            if (!string.IsNullOrWhiteSpace(downlink))
                return true;
        }

        return false;
    }

    private static bool IsPreferredLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;

        return string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase);
    }

    private static string SelectPrimaryInstallerFile(IReadOnlyList<string> files, GogInstallPlatform platform)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("No installer files were downloaded.");

        if (platform == GogInstallPlatform.Windows)
        {
            var exe = files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exe))
                return exe;
        }
        else
        {
            var shell = files.FirstOrDefault(f =>
                f.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".run", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(shell))
                return shell;
        }

        return files[0];
    }

    private static string ResolveFileName(string downloadUrl, string fallback)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            return fallback;

        var name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        return Uri.UnescapeDataString(name);
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "installer.bin";

        var sanitized = fileName;
        foreach (var c in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(c, '_');

        return string.IsNullOrWhiteSpace(sanitized) ? "installer.bin" : sanitized;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return GetString(property);
    }

    private static string? GetString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    private static long? GetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), out value))
        {
            return value;
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        value = string.Empty;
        var parsed = GetString(element);
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static string ExtractErrorDetail(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
            return "No response body.";

        try
        {
            using var json = JsonDocument.Parse(jsonOrText);
            var root = json.RootElement;
            var error = GetString(root, "error");
            var message = GetString(root, "message");
            var description = GetString(root, "error_description");

            if (!string.IsNullOrWhiteSpace(error) ||
                !string.IsNullOrWhiteSpace(message) ||
                !string.IsNullOrWhiteSpace(description))
            {
                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(error))
                    parts.Add(error);
                if (!string.IsNullOrWhiteSpace(message))
                    parts.Add(message);
                if (!string.IsNullOrWhiteSpace(description))
                    parts.Add(description);
                return string.Join(" | ", parts);
            }
        }
        catch
        {
            // Ignore parse failures and fall back to raw text.
        }

        var trimmed = jsonOrText.Trim();
        return trimmed.Length <= 260 ? trimmed : trimmed[..260] + "...";
    }
    
}

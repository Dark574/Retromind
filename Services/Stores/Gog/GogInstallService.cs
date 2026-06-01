using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    long? Size);

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

    public GogInstallService(GogAuthService authService, HttpClient httpClient)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2)
        };
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

            var downloadUrl = await ResolveDownlinkAsync(downlinkEndpoint, accessToken, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            var fallbackName = $"installer_part_{index:D2}";
            var fileName = ResolveFileName(downloadUrl, fallbackName);
            var size = GetLong(file, "size");

            resolvedFiles.Add(new GogInstallerDownloadFile(downloadUrl, fileName, size));
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

        if (IsDangerousPath(fullPath))
            return false;

        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal))
            return false;

        var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
        if (string.Equals(fullPath, libraryRoot, StringComparison.Ordinal))
            return false;

        // If the folder no longer exists, no physical deletion can happen.
        // Allow metadata cleanup to proceed.
        if (!Directory.Exists(fullPath))
            return true;

        if (AppPaths.IsPathInsideDataRoot(fullPath))
            return HasValidInstallMarker(fullPath, item);

        var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? libraryRoot
            : libraryRoot + Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(libraryRootWithSep, StringComparison.Ordinal))
            return HasValidInstallMarker(fullPath, item);

        return HasValidInstallMarker(fullPath, item);
    }

    private static bool HasValidInstallMarker(string fullPath, MediaItem item)
    {
        var markerPath = Path.Combine(fullPath, ".retromind-install.json");
        if (!File.Exists(markerPath))
        {
            Debug.WriteLine($"[Warning] No install marker found at '{markerPath}'. Refusing to delete path '{fullPath}'.");
            return false;
        }

        InstallMarker? marker;
        try
        {
            var json = File.ReadAllText(markerPath);
            marker = JsonSerializer.Deserialize<InstallMarker>(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Warning] Failed to read install marker at '{markerPath}': {ex.Message}");
            return false;
        }

        if (marker == null)
            return false;

        if (!string.Equals(marker.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!item.CustomFields.TryGetValue("Store.GameId", out var gameId) ||
            !string.Equals(marker.StoreGameId, gameId, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[Warning] Install marker StoreGameId mismatch: expected '{gameId}', got '{marker.StoreGameId}'.");
            return false;
        }

        if (!string.Equals(marker.MediaItemId, item.Id, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[Warning] Install marker MediaItemId mismatch: expected '{item.Id}', got '{marker.MediaItemId}'.");
            return false;
        }

        return true;
    }
    
    private static bool IsDangerousPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return true;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return true;

        if (string.Equals(fullPath, root, StringComparison.Ordinal))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) &&
            string.Equals(fullPath, Path.GetFullPath(home), StringComparison.Ordinal))
        {
            return true;
        }

        var blockedPaths = new[] { "/usr", "/bin", "/sbin", "/etc", "/var", "/boot", "/dev", "/proc", "/sys" };
        foreach (var blocked in blockedPaths)
        {
            if (fullPath.StartsWith(blocked + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                string.Equals(fullPath, blocked, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    public async Task UninstallGogGameAsync(
        MediaItem item,
        CancellationToken ct = default)
    {
        if (item == null)
            throw new ArgumentNullException(nameof(item));

        // --- Extract paths from CustomFields ---
        if (!item.CustomFields.TryGetValue("Store.InstallPath", out var installPath) ||
            string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException("Cannot uninstall: install path is not set.");
        }
        
        var prefixPath = item.PrefixPath;
        bool prefixSkippedForSafety = false;
        
        if (!string.IsNullOrWhiteSpace(prefixPath))
        {
            string resolvedPrefix;
            
            // 1. Normalize path (handles ../ sequences)
            if (Path.IsPathRooted(prefixPath))
            {
                resolvedPrefix = Path.GetFullPath(prefixPath);
            }
            else
            {
                // Resolve relative to LibraryRoot
                resolvedPrefix = Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, prefixPath));
            }
            
            // 2. Safety Check: Ensure resolved path is inside LibraryRoot
            var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
            var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? libraryRoot
                : libraryRoot + Path.DirectorySeparatorChar;

            // CRITICAL: resolvedPrefix must be a STRICT subdirectory of LibraryRoot.
            // Never allow deleting LibraryRoot itself (e.g., if PrefixPath is "." or empty-normalized).
            var isSafePrefix = resolvedPrefix.StartsWith(libraryRootWithSep, StringComparison.Ordinal);

            if (!isSafePrefix)
            {
                Debug.WriteLine($"[Warning] Prefix path '{prefixPath}' resolves outside LibraryRoot ('{resolvedPrefix}'). Skipping prefix deletion for safety.");
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
            "Store.InstallPath",
            "Store.InstallPlatform",
            "Store.InstallRunnerVersionId",
            "Store.InstallWindowsInstallerPreference",
            "Store.UpdateAvailable",
            "Store.InstalledVersion",
            "Store.InstalledInstallerSignature",
            "Store.LastUpdateCheckStatus",
            "Store.LastUpdateCheckUtc"
        };

        // Reassign the dictionary to trigger INotifyPropertyChanged.
        // In-place modifications (Remove) do not notify the UI, causing stale metadata display.
        item.CustomFields = item.CustomFields
            .Where(kv => !fieldsToRemove.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

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

        // Clear launcher path/args since the executable is gone.
        item.LauncherPath = null;
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
                    Directory.Delete(path, recursive: true);
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

        var result = new List<GogPlayTaskInfo>();
        foreach (var task in playTasks.EnumerateArray())
        {
            var path = GetString(task, "path");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var arguments = ParseTaskArguments(task);
            var workingDir = GetString(task, "workingDir");
            var isPrimary = task.TryGetProperty("isPrimary", out var isPrimaryElement) &&
                            isPrimaryElement.ValueKind == JsonValueKind.True;

            result.Add(new GogPlayTaskInfo(path, arguments, workingDir, isPrimary));
        }

        return result;
    }

    private async Task<string?> ResolveDownlinkAsync(string downlinkEndpoint, string accessToken, CancellationToken ct)
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
            return response.RequestMessage?.RequestUri?.ToString();

        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
            return response.RequestMessage?.RequestUri?.ToString();

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        if (TryGetString(root, out var direct))
            return direct;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("downlink", out var downlink) && TryGetString(downlink, out var downlinkUrl))
                return downlinkUrl;

            if (root.TryGetProperty("url", out var url) && TryGetString(url, out var directUrl))
                return directUrl;

            if (root.TryGetProperty("urls", out var urls))
            {
                if (urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidate in urls.EnumerateArray())
                    {
                        if (TryGetString(candidate, out var candidateUrl))
                            return candidateUrl;

                        if (candidate.ValueKind == JsonValueKind.Object &&
                            candidate.TryGetProperty("url", out var nestedUrl) &&
                            TryGetString(nestedUrl, out candidateUrl))
                        {
                            return candidateUrl;
                        }
                    }
                }
                else if (TryGetString(urls, out var urlsValue))
                {
                    return urlsValue;
                }
            }
        }

        return response.RequestMessage?.RequestUri?.ToString();
    }

    private async Task DownloadFileWithResumeAsync(
        GogInstallerDownloadFile file,
        string targetPath,
        Action<long>? reportProgress,
        CancellationToken ct)
    {
        var expectedSize = file.Size.GetValueOrDefault(0);
        var hasExpectedSize = file.Size.HasValue && expectedSize > 0;

        if (File.Exists(targetPath))
        {
            var finalLength = new FileInfo(targetPath).Length;
            if ((hasExpectedSize && (finalLength == expectedSize || !IsObviouslyInvalidDownload(finalLength, expectedSize))) ||
                (!hasExpectedSize && finalLength > 0))
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
            if (hasExpectedSize && (partialLength == expectedSize || !IsObviouslyInvalidDownload(partialLength, expectedSize)))
            {
                File.Move(partPath, targetPath, overwrite: true);
                reportProgress?.Invoke(partialLength);
                return;
            }

            if (partialLength > 0 && (!hasExpectedSize || partialLength < expectedSize))
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

            // If server ignores range and returns full content, restart from scratch.
            var append = response.StatusCode == System.Net.HttpStatusCode.PartialContent && resumeOffset > 0;
            if (!append && resumeOffset > 0 && File.Exists(partPath))
                File.Delete(partPath);

            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytesWritten = append ? resumeOffset : 0L;
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

            if (hasExpectedSize)
            {
                var finalSize = new FileInfo(partPath).Length;
                if (finalSize != expectedSize)
                {
                    if (!IsObviouslyInvalidDownload(finalSize, expectedSize))
                    {
                        Debug.WriteLine(
                            $"[GOG] Installer size mismatch tolerated for '{file.FileName}' ({finalSize} != {expectedSize}).");
                    }
                    else if (!attemptedFreshRetryAfterMismatch)
                    {
                        attemptedFreshRetryAfterMismatch = true;
                        resumeOffset = 0;
                        if (File.Exists(partPath))
                            File.Delete(partPath);
                        continue;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Downloaded installer part has unexpected size ({finalSize} != {expectedSize}).");
                    }
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            return;
        }
    }

    private static bool IsObviouslyInvalidDownload(long actualSize, long expectedSize)
    {
        if (actualSize <= 0)
            return true;

        var absoluteDelta = Math.Abs(actualSize - expectedSize);
        if (absoluteDelta <= 64L * 1024 * 1024)
            return false;

        var relativeDelta = expectedSize > 0 ? absoluteDelta / (double)expectedSize : 1d;
        return relativeDelta > 0.2d;
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

    private static string? ParseTaskArguments(JsonElement task)
    {
        if (!task.TryGetProperty("arguments", out var args))
            return null;

        if (args.ValueKind == JsonValueKind.String)
            return args.GetString();

        if (args.ValueKind != JsonValueKind.Array)
            return null;

        var builder = new StringBuilder();
        foreach (var value in args.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var arg = value.GetString();
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(QuoteArgumentIfNeeded(arg));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string QuoteArgumentIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;

        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
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
    
    private sealed record InstallMarker(
        string ProviderId,
        string StoreGameId,
        string MediaItemId);
}

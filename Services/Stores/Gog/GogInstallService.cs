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
}

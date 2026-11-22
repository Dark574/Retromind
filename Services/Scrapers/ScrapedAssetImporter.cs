using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Downloads scraper assets and adds them to the item using the library's asset storage.
/// </summary>
public sealed class ScrapedAssetImporter
{
    internal const int MaxVideoBytes = 512 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly FileManagementService _fileService;
    private readonly IErrorService _errorService;
    private readonly ScrapedDocumentImporter _documentImporter;

    public ScrapedAssetImporter(HttpClient httpClient, FileManagementService fileService, IErrorService errorService)
    {
        _httpClient = httpClient;
        _fileService = fileService;
        _errorService = errorService;
        _documentImporter = new ScrapedDocumentImporter(httpClient, fileService);
    }

    internal async Task<(bool Changed, string? DocumentImportError)> ImportAsync(
        MediaItem item,
        ScraperSearchResult result,
        List<string> nodePath,
        ScraperImportSettings settings,
        bool appendAssetsWhenTypeExists,
        Action<AssetType>? onAssetImportFailed = null)
    {
        var changed = false;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Cover, result.CoverUrl, settings.ImportCover, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Wallpaper, result.WallpaperUrl, settings.ImportWallpaper, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Screenshot, result.ScreenshotUrl, settings.ImportScreenshot, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Logo, result.LogoUrl, settings.ImportLogo, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Video, result.VideoUrl, settings.ImportVideo, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Marquee, result.MarqueeUrl, settings.ImportMarquee, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.Bezel, result.BezelUrl, settings.ImportBezel, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        if (await TryImportScrapedAssetAsync(item, nodePath, AssetType.ControlPanel, result.ControlPanelUrl, settings.ImportControlPanel, appendAssetsWhenTypeExists, onAssetImportFailed))
            changed = true;

        string? documentImportError = null;
        if (settings.ImportManuals)
        {
            foreach (var document in result.Documents)
            {
                try
                {
                    var existing = await UiThreadHelper.InvokeAsync(() => item.Assets.ToList());
                    var imported = await _documentImporter.ImportAsync(document,
                        document.GetDisplayLabel(GetText("ScrapeDialog.Manual", "Manual")), item, nodePath, existing);
                    if (imported.Asset != null)
                    {
                        await UiThreadHelper.InvokeAsync(() => item.Assets.Add(imported.Asset));
                        changed = true;
                    }
                    else if (!imported.IsDuplicate)
                    {
                        // FileManagementService has already diagnosed the failed publication.
                        documentImportError ??= GetText("ScrapeDialog.DocumentSaveFailed", "The document could not be saved.");
                    }
                }
                catch (Exception ex)
                {
                    documentImportError ??= DescribeDocumentImportFailure(ex);
                    try
                    {
                        // HTTP exceptions can contain credential-bearing download URLs.
                        _errorService.Report(new ErrorReport("Scraper.DocumentImportFailed", ErrorSeverity.Warning,
                            nameof(ScrapedAssetImporter), "A scraper PDF document could not be downloaded or imported.",
                            Context: ScrapedDocumentImporter.GetFailureContext(ex)));
                    }
                    catch { /* Diagnostics must not interrupt remaining imports or saving. */ }
                }
            }
        }

        return (changed, documentImportError);
    }

    internal static string DescribeDocumentImportFailure(Exception exception)
    {
        return exception switch
        {
            ScrapedDocumentDownloadException { Reason: "NotPdf" } =>
                GetText("ScrapeDialog.DocumentNotPdf", "The provider returned no valid PDF file."),
            ScrapedDocumentDownloadException { Reason: "SizeLimitExceeded" } =>
                GetText("ScrapeDialog.DocumentTooLarge", "The document exceeds the 100 MiB limit."),
            ScrapedDocumentDownloadException { Reason: "IncompleteDownload" } =>
                GetText("ScrapeDialog.DocumentIncomplete", "The document download is incomplete."),
            HttpRequestException { StatusCode: { } status } =>
                string.Format(GetText("ScrapeDialog.DocumentHttpFailedFormat", "The server returned HTTP {0}."), (int)status),
            _ => GetText("ScrapeDialog.DocumentDownloadFailed", "The document could not be downloaded or processed.")
        };
    }

    private async Task<bool> TryImportScrapedAssetAsync(
        MediaItem item,
        List<string> nodePath,
        AssetType type,
        string? url,
        bool isEnabled,
        bool appendAssetsWhenTypeExists,
        Action<AssetType>? onAssetImportFailed = null)
    {
        if (!isEnabled || string.IsNullOrWhiteSpace(url))
            return false;

        var hasExisting = item.Assets.Any(a => a.Type == type);
        if (hasExisting && !appendAssetsWhenTypeExists)
            return false;

        var imported = await DownloadAndSetAssetAsync(url, item, nodePath, type);
        if (!imported) onAssetImportFailed?.Invoke(type);
        return imported;
    }

    private static string GetScrapedAssetExtension(string url, AssetType type)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url.Split('?', '#')[0];

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (type != AssetType.Video)
            return string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;

        return extension switch
        {
            ".mp4" or
            ".mkv" or
            ".avi" or
            ".mov" or
            ".wmv" or
            ".webm" or
            ".m4v" or
            ".mpg" or
            ".mpeg" => extension,

            _ => ".mp4"
        };
    }

    private async Task DownloadScrapedAssetAsync(
        string url,
        string destinationPath,
        AssetType type)
    {
        var isVideo = type == AssetType.Video;
        var maximumBytes = isVideo ? MaxVideoBytes : AsyncImageHelper.MaximumHttpImageBytes;
        using var timeout = new CancellationTokenSource(isVideo ? TimeSpan.FromMinutes(5) : _httpClient.Timeout);

        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        response.EnsureSuccessStatusCode();
        var expectedBytes = response.Content.Headers.ContentLength;
        if (expectedBytes > maximumBytes)
            throw new InvalidDataException("The scraper asset exceeds the download size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var count = Math.Min(buffer.Length, maximumBytes - total + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, count), timeout.Token);
            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("The scraper asset exceeds the download size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }

        if (expectedBytes.HasValue && expectedBytes.Value != total)
            throw new InvalidDataException("The scraper asset download is incomplete.");
    }

    private async Task<bool> DownloadAndSetAssetAsync(string url, MediaItem item, List<string> nodePath, AssetType type)
    {
        DirectoryInfo? tempDirectory = null;
        try
        {
            tempDirectory = Directory.CreateTempSubdirectory("retromind-scraped-asset-");
            var tempPathWithExt = Path.Combine(tempDirectory.FullName, "asset" + GetScrapedAssetExtension(url, type));
            // Preview bitmaps may have been resized. Persist the original response.
            await DownloadScrapedAssetAsync(url, tempPathWithExt, type);

            var imported = await _fileService.ImportAssetAsync(tempPathWithExt, item, nodePath, type);
            if (imported != null)
            {
                await UiThreadHelper.InvokeAsync(() => item.Assets.Add(imported));
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Scraper] Could not import {type}: {ex.GetType().Name}");
        }
        finally
        {
            try
            {
                tempDirectory?.Delete(recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }

        return false;
    }

    private static string GetText(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

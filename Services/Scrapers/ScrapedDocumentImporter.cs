using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Downloads bounded PDF documents, then publishes them through the existing safe asset importer.
/// Does not modify observable collections or persist provider download URLs.
/// </summary>
internal sealed class ScrapedDocumentImporter(HttpClient httpClient, FileManagementService fileService)
{
    internal const long MaxDocumentBytes = 100 * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    public async Task<(MediaAsset? Asset, bool IsDuplicate)> ImportAsync(
        ScraperDocument document,
        string displayName,
        MediaItem item,
        List<string> nodePath,
        IReadOnlyList<MediaAsset> existingDocuments,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(document.Format, "pdf", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(document.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidDataException("Unsupported scraper document format or URL.");

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Directory.CreateTempSubdirectory("retromind-scraped-document-");
        try
        {
            // The source stem becomes the Manual asset's display label. Never use a URL,
            // a random temporary name, or an unsanitized provider title for this filename.
            var name = new StringBuilder();
            foreach (var rune in PathHelper.SanitizePathSegment(displayName).EnumerateRunes())
            {
                if (Encoding.UTF8.GetByteCount(name.ToString()) + rune.Utf8SequenceLength > 80)
                    break;
                name.Append(rune.ToString());
            }
            var path = Path.Combine(directory.FullName, name + ".pdf");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            await DownloadAsync(document.Url, path, timeout.Token).ConfigureAwait(false);

            if (await HasIdenticalDocumentAsync(path, existingDocuments, cancellationToken).ConfigureAwait(false))
                return (null, true);

            cancellationToken.ThrowIfCancellationRequested();
            // Once publication begins, let the existing atomic copy complete.
            var asset = await fileService.ImportAssetAsync(path, item, nodePath, AssetType.Manual).ConfigureAwait(false);
            return (asset, false);
        }
        finally
        {
            try { directory.Delete(recursive: true); }
            catch { /* Best effort; cleanup must not hide the download/import outcome. */ }
        }
    }

    private async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength > MaxDocumentBytes)
            throw new ScrapedDocumentDownloadException("SizeLimitExceeded", (int)response.StatusCode, expectedLength);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var header = new byte[5];
        var headerLength = await source.ReadAtLeastAsync(header, header.Length,
            throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (headerLength != header.Length || !header.AsSpan().SequenceEqual("%PDF-"u8))
            throw new ScrapedDocumentDownloadException("NotPdf", (int)response.StatusCode, expectedLength,
                responseKind: Encoding.ASCII.GetString(header, 0, headerLength).TrimStart().StartsWith('<')
                    ? "Markup" : "Other");

        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        var total = (long)header.Length;
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > MaxDocumentBytes)
                throw new ScrapedDocumentDownloadException("SizeLimitExceeded", (int)response.StatusCode, expectedLength, total);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (expectedLength.HasValue && total != expectedLength.Value)
            throw new ScrapedDocumentDownloadException("IncompleteDownload", (int)response.StatusCode, expectedLength, total);
    }

    internal static Dictionary<string, string> GetFailureContext(Exception exception)
    {
        var context = new Dictionary<string, string>
        {
            ["Operation"] = "ImportScrapedDocument",
            ["Format"] = "PDF",
            ["ExceptionType"] = exception.GetType().Name
        };
        if (exception is ScrapedDocumentDownloadException download)
        {
            context["FailureReason"] = download.Reason;
            context["HttpStatus"] = download.HttpStatus.ToString(CultureInfo.InvariantCulture);
            if (download.ExpectedBytes is { } expected) context["ExpectedBytes"] = expected.ToString(CultureInfo.InvariantCulture);
            if (download.ActualBytes is { } actual) context["ActualBytes"] = actual.ToString(CultureInfo.InvariantCulture);
            if (download.ResponseKind is { } kind) context["ResponseKind"] = kind;
        }
        else if (exception is HttpRequestException { StatusCode: { } status })
        {
            context["HttpStatus"] = ((int)status).ToString(CultureInfo.InvariantCulture);
        }
        return context;
    }

    private static async Task<bool> HasIdenticalDocumentAsync(
        string downloadedPath, IReadOnlyList<MediaAsset> existingDocuments, CancellationToken cancellationToken)
    {
        await using var downloaded = File.OpenRead(downloadedPath);
        var hash = await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false);
        foreach (var asset in existingDocuments)
        {
            if (asset.Type != AssetType.Manual ||
                !AppPaths.TryResolveDataPathForMutation(asset.RelativePath, out var path))
                continue;

            try
            {
                await using var existing = File.OpenRead(path);
                if (existing.Length != downloaded.Length)
                    continue;

                var existingHash = await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false);
                if (hash.AsSpan().SequenceEqual(existingHash))
                    return true;
            }
            catch (IOException) { /* Missing/unreadable existing documents do not block new imports. */ }
            catch (UnauthorizedAccessException) { }
        }

        return false;
    }
}

/// <summary>Contains only caller-defined reasons and numeric metadata, never server text or URLs.</summary>
internal sealed class ScrapedDocumentDownloadException(
    string reason, int httpStatus, long? expectedBytes = null, long? actualBytes = null, string? responseKind = null)
    : IOException($"Scraper document download failed: {reason}.")
{
    public string Reason { get; } = reason;
    public int HttpStatus { get; } = httpStatus;
    public long? ExpectedBytes { get; } = expectedBytes;
    public long? ActualBytes { get; } = actualBytes;
    public string? ResponseKind { get; } = responseKind;
}

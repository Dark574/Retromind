using System.Net;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Scrapers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ScrapedDocumentImporterTests
{
    private const string Pdf = "%PDF-1.4\nExample document\n%%EOF\n";

    [Fact]
    public async Task Import_Http200ForceDownloadHtmlHasSafeSpecificDiagnosticsAndPublishesNothing()
    {
        const string body = "<!DOCTYPE html><html><title>Download</title><body>password=server-secret</body></html>";
        using var state = new State(_ =>
        {
            var response = Response(body);
            response.Content.Headers.ContentType = new("application/force-download");
            return response;
        });

        var error = await Assert.ThrowsAsync<ScrapedDocumentDownloadException>(() => state.Import());
        var context = ScrapedDocumentImporter.GetFailureContext(error);

        Assert.Equal("NotPdf", context["FailureReason"]);
        Assert.Equal("Markup", context["ResponseKind"]);
        Assert.Equal("200", context["HttpStatus"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(body).ToString(), context["ExpectedBytes"]);
        Assert.DoesNotContain("server-secret", error.ToString());
        Assert.DoesNotContain("server-secret", string.Join(" ", context.Values));
        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public void HttpFailureDiagnosticsIncludeStatusButNeverRawExceptionOrRequestUrl()
    {
        var error = new HttpRequestException("Failed https://example.test?password=private-secret", null, HttpStatusCode.Forbidden);

        var context = ScrapedDocumentImporter.GetFailureContext(error);

        Assert.Equal("403", context["HttpStatus"]);
        Assert.Equal(nameof(HttpRequestException), context["ExceptionType"]);
        Assert.DoesNotContain("private-secret", string.Join(" ", context.Values));
        Assert.DoesNotContain("https://", string.Join(" ", context.Values));
    }

    [Fact]
    public async Task Import_PhpDownloadUsesPdfExtensionReadableNameAndPortablePath()
    {
        using var state = new State(_ =>
        {
            var response = Response(Pdf);
            response.Content.Headers.ContentType = new("application/force-download");
            return response;
        });
        var document = new ScraperDocument
        {
            Url = "https://api.example/mediaManuelJeu.php?devpassword=private&media=manuel(eu)",
            Title = "Quick Guide", Region = "eu"
        };

        var result = await state.Import(document);

        var asset = Assert.IsType<MediaAsset>(result.Asset);
        Assert.False(result.IsDuplicate);
        Assert.Equal(AssetType.Manual, asset.Type);
        Assert.False(Path.IsPathRooted(asset.RelativePath));
        Assert.EndsWith("_Manual_01.pdf", asset.RelativePath);
        Assert.StartsWith("Quick_Guide_(EU,_PDF)", asset.DisplayLabel);
        Assert.DoesNotContain("private", asset.RelativePath);
        Assert.DoesNotContain(".php", asset.RelativePath);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(asset.AbsolutePath));
        Assert.Empty(state.Errors.Reports);
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_AddsDistinctDocumentsAndSkipsIdenticalContentsAcrossUrlsAndNames()
    {
        var responses = new Queue<string>([Pdf, Pdf + "different", Pdf]);
        using var state = new State(_ => Response(responses.Dequeue()));
        var manual = await state.Import();
        state.Item.Assets.Add(Assert.IsType<MediaAsset>(manual.Asset));

        var keychart = await state.Import(new ScraperDocument { Url = "https://example.test/keys.pdf", Title = "Keychart" });
        state.Item.Assets.Add(Assert.IsType<MediaAsset>(keychart.Asset));
        var duplicate = await state.Import(new ScraperDocument { Url = "https://example.test/other.pdf", Title = "Other name" });

        Assert.Null(duplicate.Asset);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(2, state.Item.Assets.Count);
        Assert.Equal(2, Directory.GetFiles(state.Temp.GetPath("Library", "Games", "Manual")).Length);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(manual.Asset!.AbsolutePath));
        state.AssertTemporaryFilesRemoved();
    }

    [Theory]
    [InlineData("NOMEDIA")]
    [InlineData("<html>API error</html>")]
    [InlineData("")]
    [InlineData("%PDF")]
    public async Task Import_RejectsNonPdfResponsesWithoutPublishing(string content)
    {
        using var state = new State(_ => Response(content));

        var error = await Assert.ThrowsAsync<ScrapedDocumentDownloadException>(() => state.Import());
        Assert.Equal("NotPdf", error.Reason);

        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_RejectsOversizedContentLengthBeforeReadingBody()
    {
        using var source = new GeneratedPdfStream(10);
        using var state = new State(_ =>
        {
            var response = StreamResponse(source);
            response.Content.Headers.ContentLength = ScrapedDocumentImporter.MaxDocumentBytes + 1;
            return response;
        });

        var error = await Assert.ThrowsAsync<ScrapedDocumentDownloadException>(() => state.Import());
        Assert.Equal("SizeLimitExceeded", error.Reason);

        Assert.Equal(0, source.BytesRead);
        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_EnforcesSizeLimitWithoutContentLengthAndRemovesPartialDownload()
    {
        using var state = new State(_ => StreamResponse(new GeneratedPdfStream(ScrapedDocumentImporter.MaxDocumentBytes + 1)));

        var error = await Assert.ThrowsAsync<ScrapedDocumentDownloadException>(() => state.Import());
        Assert.Equal("SizeLimitExceeded", error.Reason);

        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_DetectsIncompleteResponseDespiteValidPdfHeader()
    {
        using var state = new State(_ =>
        {
            var response = Response(Pdf);
            response.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(Pdf) + 10;
            return response;
        });

        var error = await Assert.ThrowsAsync<ScrapedDocumentDownloadException>(() => state.Import());
        Assert.Equal("IncompleteDownload", error.Reason);

        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_ReadFailureAfterPartialDownloadDoesNotPublish()
    {
        using var state = new State(_ => StreamResponse(new GeneratedPdfStream(500, failAfter: 50)));

        await Assert.ThrowsAsync<IOException>(() => state.Import());

        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_CancellationDuringDownloadRemovesTemporaryFile()
    {
        using var cancellation = new CancellationTokenSource();
        using var state = new State(_ => StreamResponse(new GeneratedPdfStream(500, afterRead: bytes =>
        {
            if (bytes > 5) cancellation.Cancel();
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Import(cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(AppPaths.LibraryRoot));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_FailedPublicationReturnsFailureAndIsReportedOnlyByFileService()
    {
        using var state = new State(_ => Response(Pdf));
        state.Temp.CreateFile("Library/Games", "blocks directory creation");

        var result = await state.Import();

        Assert.Null(result.Asset);
        Assert.False(result.IsDuplicate);
        Assert.Equal("FileManagement.AssetImportFailed", Assert.Single(state.Errors.Reports).Code);
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_DuplicateDetectionDoesNotFollowSymlinksOrEscapingPaths()
    {
        using var outside = new TemporaryDirectory();
        var original = outside.CreateFile("outside.pdf", Pdf);
        using var state = new State(_ => Response(Pdf));
        var link = state.Temp.GetPath("linked.pdf");
        File.CreateSymbolicLink(link, original);
        state.Item.Assets.Add(new MediaAsset { Type = AssetType.Manual, RelativePath = "linked.pdf" });
        state.Item.Assets.Add(new MediaAsset { Type = AssetType.Manual, RelativePath = original });

        var result = await state.Import();

        Assert.NotNull(result.Asset);
        Assert.False(result.IsDuplicate);
        Assert.Equal(Pdf, await File.ReadAllTextAsync(original));
        state.AssertTemporaryFilesRemoved();
    }

    [Fact]
    public async Task Import_SanitizesProviderTitleWithoutEscapingTemporaryDirectory()
    {
        using var state = new State(_ => Response(Pdf));
        var result = await state.Import(new ScraperDocument
        {
            Url = "https://example.test/manual.pdf", Title = "../../" + new string('日', 300) + "\\manual"
        });

        Assert.NotNull(result.Asset);
        Assert.True(AppPaths.TryResolveDataPathForMutation(result.Asset.RelativePath, out _));
        Assert.Equal(Pdf, await File.ReadAllTextAsync(result.Asset.AbsolutePath));
        state.AssertTemporaryFilesRemoved();
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
    };

    private static HttpResponseMessage StreamResponse(Stream stream) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream)
    };

    private sealed class State : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        public RecordingErrorService Errors { get; } = new();
        public MediaItem Item { get; } = new("Game") { Id = "12345678" };
        private readonly EnvironmentVariableScope _environment;
        private readonly HttpClient _client;
        private readonly ScrapedDocumentImporter _importer;
        private readonly HashSet<string> _existingTemporaryDirectories = TemporaryDirectories();

        public State(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _environment = new EnvironmentVariableScope(("APPIMAGE", Temp.GetPath("Retromind.AppImage")));
            _client = new HttpClient(new StubHandler(handler));
            _importer = new ScrapedDocumentImporter(_client, new FileManagementService(AppPaths.LibraryRoot, Errors));
        }

        public Task<(MediaAsset? Asset, bool IsDuplicate)> Import(ScraperDocument? document = null, CancellationToken cancellationToken = default)
        {
            document ??= new ScraperDocument { Url = "https://example.test/manual.php", Number = 1 };
            return _importer.ImportAsync(document, document.GetDisplayLabel("Manual"), Item, ["Games"],
                Item.Assets.ToList(), cancellationToken);
        }

        public void AssertTemporaryFilesRemoved() => Assert.Empty(TemporaryDirectories().Except(_existingTemporaryDirectories));

        private static HashSet<string> TemporaryDirectories() =>
            Directory.GetDirectories(Path.GetTempPath(), "retromind-scraped-document-*").ToHashSet();

        public void Dispose()
        {
            _client.Dispose();
            _environment.Dispose();
            Temp.Dispose();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class GeneratedPdfStream(long length, long? failAfter = null, Action<long>? afterRead = null) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter.HasValue && BytesRead >= failAfter) throw new IOException("Download interrupted.");
            var read = (int)Math.Min(count, (failAfter ?? length) - BytesRead);
            var target = buffer.AsSpan(offset, read);
            target.Fill((byte)' ');
            for (var index = 0; index < read && BytesRead + index < 5; index++)
                target[index] = "%PDF-"u8[(int)(BytesRead + index)];
            BytesRead += read;
            afterRead?.Invoke(BytesRead);
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new byte[buffer.Length];
            var read = Read(bytes, 0, bytes.Length);
            bytes.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using SkiaSharp;

namespace Retromind.Tests.Helpers;

public sealed class AsyncImageHelperTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(null)]
    [InlineData(1L)] // A misleading small header does not replace the actual streaming limit.
    [InlineData(1024L)]
    public async Task LoadBitmapAsync_LoadsImageWithOptionalContentLengthAndDownsampling(long? contentLength)
    {
        var stream = new TrackingStream(CreatePng(16, 8));
        using var client = CreateClient(stream, contentLength);
        var errors = new RecordingErrorService();

        using var bitmap = await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), 8, false, default, client, errors);

        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(8, 4), bitmap.PixelSize);
        Assert.True(stream.IsDisposed);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task LoadBitmapAsync_RejectsLargeContentLengthBeforeReadingBody()
    {
        var stream = new TrackingStream(CreatePng());
        using var client = CreateClient(stream, AsyncImageHelper.MaximumHttpImageBytes + 1L);
        var errors = new RecordingErrorService();

        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, default, client, errors));

        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.IsDisposed);
        AssertLimitReport(errors, "Image.DownloadTooLarge");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task LoadBitmapAsync_EnforcesStreamingLimitWithoutTrustingHeaders(long? contentLength)
    {
        var stream = new TrackingStream(length: AsyncImageHelper.MaximumHttpImageBytes + 128L * 1024);
        using var client = CreateClient(stream, contentLength);
        var errors = new RecordingErrorService();

        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, default, client, errors));

        Assert.Equal(AsyncImageHelper.MaximumHttpImageBytes + 1L, stream.BytesRead);
        Assert.InRange(stream.LargestReadRequest, 1, 64 * 1024);
        Assert.True(stream.IsDisposed);
        AssertLimitReport(errors, "Image.DownloadTooLarge");
    }

    [Fact]
    public async Task LoadBitmapAsync_AcceptsExactlyTheByteLimit()
    {
        // PNG tolerates trailing bytes; this exercises the successful one-byte EOF probe at the limit.
        var stream = new TrackingStream(CreatePng(), AsyncImageHelper.MaximumHttpImageBytes);
        using var client = CreateClient(stream, AsyncImageHelper.MaximumHttpImageBytes);
        var errors = new RecordingErrorService();

        using var bitmap = await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, default, client, errors);

        Assert.NotNull(bitmap);
        Assert.Equal(AsyncImageHelper.MaximumHttpImageBytes, stream.BytesRead);
        Assert.True(stream.IsDisposed);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task LoadBitmapAsync_CancellationDuringStreamingDisposesResponse()
    {
        var stream = new BlockingStream(CreatePng(), blockImmediately: false);
        using var client = CreateClient(stream);
        using var cancellation = new CancellationTokenSource();
        var errors = new RecordingErrorService();
        var loading = AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, cancellation.Token, client, errors);
        await stream.Blocked.Task.WaitAsync(TestTimeout);
        Assert.True(stream.BytesRead > 0);

        cancellation.Cancel();

        Assert.Null(await loading.WaitAsync(TestTimeout));
        Assert.True(stream.IsDisposed);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task LoadBitmapAsync_CancellationWhileWaitingForDecodeReleasesDownloadResources()
    {
        var gate = (SemaphoreSlim)typeof(AsyncImageHelper)
            .GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        await gate.WaitAsync();
        var stream = new TrackingStream(CreatePng());
        using var client = CreateClient(stream);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var errors = new RecordingErrorService();
        var loading = AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), 1, true, cancellation.Token, client, errors);
        try
        {
            Assert.False(loading.IsCompleted);
            Assert.True(stream.IsDisposed); // HTTP resources close before waiting for the existing decode lane.
            cancellation.Cancel();
            Assert.Null(await loading.WaitAsync(TestTimeout));
            Assert.Empty(errors.Reports);
        }
        finally
        {
            cancellation.Cancel();
            gate.Release();
            (await loading.WaitAsync(TestTimeout))?.Dispose();
        }
        var retryStream = new TrackingStream(CreatePng());
        using var retryClient = CreateClient(retryStream);
        using var retry = await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), 1, true, default, retryClient, errors);
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task LoadBitmapAsync_DoesNotIntroduceAnEightDownloadLimit()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var streams = Enumerable.Range(0, 9).Select(_ => new BlockingStream(CreatePng(), true)).ToArray();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(Response(streams[Interlocked.Increment(ref requests) - 1]))));
        var errors = new RecordingErrorService();
        var tasks = streams.Select(_ => AsyncImageHelper.LoadBitmapAsync(
            UniqueUrl(), null, false, cancellation.Token, client, errors)).ToArray();
        try
        {
            await Task.WhenAll(streams.Select(stream => stream.Blocked.Task)).WaitAsync(TestTimeout);
            Assert.Equal(9, requests);
        }
        finally
        {
            cancellation.Cancel();
            Assert.All(await Task.WhenAll(tasks).WaitAsync(TestTimeout), Assert.Null);
        }
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("read")]
    [InlineData("invalid-image")]
    [InlineData("request")]
    [InlineData("timeout")]
    public async Task LoadBitmapAsync_FailuresReleaseResourcesAndAllowLaterLoads(string failure)
    {
        var streams = new List<TrackingStream>();
        var errors = new RecordingErrorService { ThrowOnReport = true };
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            if (failure == "request") throw new HttpRequestException("Request failed with secret URL");
            if (failure == "timeout") throw new TaskCanceledException("HTTP timeout without caller cancellation");
            var stream = new TrackingStream(failure == "invalid-image" ? "not an image"u8.ToArray() : CreatePng())
            {
                ReadFailure = failure == "read" ? new IOException("Simulated stream failure") : null
            };
            streams.Add(stream);
            return Task.FromResult(Response(stream, status: failure == "status" ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
        }));

        for (var index = 0; index < 9; index++)
            Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, default, client, errors)
                .WaitAsync(TestTimeout));

        Assert.Equal(9, errors.Reports.Count);
        Assert.All(streams, stream => Assert.True(stream.IsDisposed));
        Assert.All(errors.Reports, report => Assert.Equal("Image.LoadFailed", report.Code));
        var validStream = new TrackingStream(CreatePng());
        using var validClient = CreateClient(validStream);
        using var bitmap = await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, default, validClient, errors);
        Assert.NotNull(bitmap);
        Assert.True(validStream.IsDisposed);
    }

    [Fact]
    public async Task LoadBitmapAsync_PreCanceledRequestDoesNoWorkOrDiagnostics()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            throw new InvalidOperationException("Should not be called");
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var errors = new RecordingErrorService();

        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), null, false, cancellation.Token, client, errors));

        Assert.Equal(0, requests);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task LoadBitmapAsync_SuppressesRepeatedFailureAndNeverReportsRawUrlOrException()
    {
        var url = UniqueUrl() + "?token=private-query-value&custom=unlabeled-sensitive-value";
        var errors = new RecordingErrorService();
        using var client = new HttpClient(new StubHandler((_, _) =>
            throw new HttpRequestException("Could not fetch " + url)));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            AsyncImageHelper.LoadBitmapAsync(url, null, false, default, client, errors)));

        var report = Assert.Single(errors.Reports);
        Assert.Null(report.Exception);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
        Assert.Equal(64, report.Context!["SourceId"].Length);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-query-value", serialized);
        Assert.DoesNotContain("unlabeled-sensitive-value", serialized);
        Assert.DoesNotContain("https://", serialized);
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Theory]
    [InlineData(20_000, 1)]
    [InlineData(1, 20_000)]
    [InlineData(8192, 8192)]
    public async Task LoadBitmapAsync_RejectsExtremeSourceDimensionsBeforePixelDecode(int width, int height)
    {
        var bytes = PngWithDimensions(width, height);
        // The fixture has a valid codec header but deliberately tiny IDAT data.
        using (var headerStream = new MemoryStream(bytes))
        using (var codec = SKCodec.Create(headerStream))
        {
            Assert.NotNull(codec);
            Assert.Equal(width, codec.Info.Width);
            Assert.Equal(height, codec.Info.Height);
        }
        var stream = new TrackingStream(bytes);
        using var client = CreateClient(stream);
        var errors = new RecordingErrorService();

        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), 8, false, default, client, errors));

        AssertLimitReport(errors, "Image.DimensionsExceeded");
        Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData(16_385)]
    [InlineData(int.MaxValue)]
    [InlineData(8192)] // A thin source can exceed the pixel budget even with a permitted target width.
    public async Task LoadBitmapAsync_RejectsExcessiveUpscalingWithWideArithmetic(int decodeWidth)
    {
        var stream = new TrackingStream(CreatePng(1, 2));
        using var client = CreateClient(stream);
        var errors = new RecordingErrorService();

        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(UniqueUrl(), decodeWidth, false, default, client, errors));

        AssertLimitReport(errors, "Image.DimensionsExceeded");
    }

    [Fact]
    public async Task LoadBitmapAsync_LocalFileKeepsDownsamplingAndReleasesFile()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("local image.png");
        await File.WriteAllBytesAsync(path, CreatePng(16, 8));
        var errors = new RecordingErrorService();

        using var bitmap = await AsyncImageHelper.LoadBitmapAsync(path, 8, true, default, errorService: errors);

        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(8, 4), bitmap.PixelSize);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(errors.Reports);
        Assert.Null(await AsyncImageHelper.LoadBitmapAsync(temp.GetPath("missing.png"), null, false, default,
            errorService: errors));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task PreloadLocalAsync_RetainsCacheAndInvalidationSemantics()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("cached.png");
        var saved = temp.GetPath("saved.png");
        await File.WriteAllBytesAsync(path, CreatePng(16, 8));
        try
        {
            await AsyncImageHelper.PreloadLocalAsync(path, 8, useSerialDecode: true);
            File.Delete(path);
            await AsyncImageHelper.PreloadLocalAsync(path, 8, useSerialDecode: true);
            Assert.True(await AsyncImageHelper.SaveCachedImageAsync(path, saved));
            using (var bitmap = new Bitmap(saved))
                Assert.Equal(new PixelSize(8, 4), bitmap.PixelSize);

            AsyncImageHelper.InvalidateCache(path);
            Assert.False(await AsyncImageHelper.SaveCachedImageAsync(path, saved));
            await File.WriteAllBytesAsync(path, CreatePng(8, 16));
            await AsyncImageHelper.PreloadLocalAsync(path, 8);
            Assert.True(await AsyncImageHelper.SaveCachedImageAsync(path, saved));
            using var updated = new Bitmap(saved);
            Assert.Equal(new PixelSize(8, 16), updated.PixelSize);
        }
        finally
        {
            AsyncImageHelper.InvalidateCache(path);
        }
    }

    private static void AssertLimitReport(RecordingErrorService errors, string code)
    {
        var report = Assert.Single(errors.Reports);
        Assert.Equal(code, report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal(nameof(AsyncImageHelper), report.Component);
        Assert.Null(report.Exception);
        Assert.All(errors.Notifications, Assert.Null);
    }

    private static string UniqueUrl() => $"https://images.example/{Guid.NewGuid():N}.png";

    private static HttpClient CreateClient(TrackingStream stream, long? contentLength = null) =>
        new(new StubHandler((_, _) => Task.FromResult(Response(stream, contentLength))));

    private static HttpResponseMessage Response(TrackingStream stream, long? contentLength = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(status) { Content = content };
    }

    private static byte[] CreatePng(int width = 1, int height = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] PngWithDimensions(int width, int height)
    {
        var png = CreatePng();
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        uint crc = uint.MaxValue;
        foreach (var value in png.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), ~crc);
        return png;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            send(request, token);
    }

    private class TrackingStream(byte[]? data = null, long? length = null) : Stream
    {
        public long BytesRead { get; private set; }
        public int LargestReadRequest { get; private set; }
        public bool IsDisposed { get; private set; }
        public Exception? ReadFailure { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => false; // StreamContent cannot synthesize Content-Length.
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (ReadFailure != null) throw ReadFailure;
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            var read = (int)Math.Min(buffer.Length, (length ?? data?.Length ?? 0) - BytesRead);
            buffer[..read].Clear();
            if (data != null && BytesRead < data.Length)
            {
                var copied = Math.Min(read, data.Length - (int)BytesRead);
                data.AsSpan((int)BytesRead, copied).CopyTo(buffer);
            }
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
            }
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream(byte[] data, bool blockImmediately, TaskCompletionSource? release = null)
        : TrackingStream(data)
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = release ?? new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (blockImmediately || BytesRead > 0)
            {
                Blocked.TrySetResult();
                await _release.Task.WaitAsync(token);
            }
            return await base.ReadAsync(buffer, token);
        }
    }
}

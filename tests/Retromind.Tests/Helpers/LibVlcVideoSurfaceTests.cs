using System.Runtime.InteropServices;
using Retromind.Helpers.Video;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class LibVlcVideoSurfaceTests
{
    [Theory]
    [InlineData(0U, 1080U)]
    [InlineData(1920U, 0U)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(65_536U, 65_536U)] // Previously wrapped the allocation size to zero.
    [InlineData(0x40000001U, 1U)] // Previously wrapped the BGRA pitch to four bytes.
    [InlineData(16_385U, 1U)]
    [InlineData(16_384U, 16_384U)] // Fits neither the supported frame budget nor a preview workload.
    [InlineData(8192U, 8193U)]
    public void VideoFormat_InvalidDimensionsReturnFailureWithoutAllocating(uint width, uint height)
    {
        var reports = new List<ErrorReport>();
        var allocations = 0;
        using var surface = new LibVlcVideoSurface(reports.Add, 1,
            _ => { allocations++; return IntPtr.Zero; }) { VideoPath = "preview.mp4" };

        var result = Format(surface, width, height);

        Assert.Equal(0U, result.Result);
        Assert.Equal(0U, result.Pitch);
        Assert.Equal(0U, result.Lines);
        Assert.Equal(0, allocations);
        AssertEmptyFrame(surface);
        var report = Assert.Single(reports);
        Assert.Equal("Video.PlaybackFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal(nameof(LibVlcVideoSurface), report.Component);
        Assert.Equal("VideoFormat", report.Context!["Operation"]);
        Assert.Equal("1", report.Context["SurfaceIndex"]);
        Assert.Equal("preview.mp4", report.Context["VideoPath"]);
        Assert.Equal(width.ToString(), report.Context["Width"]);
        Assert.Equal(height.ToString(), report.Context["Height"]);
    }

    [Theory]
    [InlineData(1U, 1U, 4, 4)]
    [InlineData(7U, 3U, 28, 84)]
    [InlineData(1920U, 1080U, 7680, 8_294_400)]
    [InlineData(7680U, 4320U, 30720, 132_710_400)]
    [InlineData(8192U, 8192U, 32768, 268_435_456)]
    [InlineData(16_384U, 1U, 65536, 65536)]
    public void TryGetFrameLayout_UsesSafeByteCountsWithoutAllocating(uint width, uint height, int pitch, int size)
    {
        Assert.True(LibVlcVideoSurface.TryGetFrameLayout(width, height, out var actualPitch, out var actualSize));
        Assert.Equal(pitch, actualPitch);
        Assert.Equal(size, actualSize);
    }

    [Fact]
    public void VideoFormat_AllocatesAlignedPlaneAndFreesTheOriginalAllocation()
    {
        var reports = new List<ErrorReport>();
        IntPtr allocation = IntPtr.Zero;
        var freed = new List<IntPtr>();
        using var surface = new LibVlcVideoSurface(reports.Add, 0,
            size => allocation = Marshal.AllocHGlobal(size),
            pointer => { freed.Add(pointer); Marshal.FreeHGlobal(pointer); });
        var result = Format(surface, 7, 3);
        Assert.Equal(1U, result.Result);
        Assert.Equal(28U, result.Pitch);
        Assert.Equal(3U, result.Lines);
        Assert.True(surface.TryGetFrameSize(out var frameWidth, out var frameHeight));
        Assert.Equal(7, frameWidth);
        Assert.Equal(3, frameHeight);
        Assert.Equal("RV32", System.Text.Encoding.ASCII.GetString(result.Chroma));
        Assert.Equal(0UL, (ulong)(nuint)surface.GetCurrentFrame() % 32);

        var planes = Marshal.AllocHGlobal(IntPtr.Size);
        var destination = Marshal.AllocHGlobal(84);
        try
        {
            Assert.Equal(IntPtr.Zero, surface.VideoLock(IntPtr.Zero, planes));
            Assert.Equal(surface.GetCurrentFrame(), Marshal.ReadIntPtr(planes));
            var pixels = Enumerable.Range(0, 84).Select(i => (byte)i).ToArray();
            Marshal.Copy(pixels, 0, surface.GetCurrentFrame(), pixels.Length);
            Assert.True(surface.CopyFrameTo(destination, pixels.Length));
            var copy = new byte[pixels.Length];
            Marshal.Copy(destination, copy, 0, copy.Length);
            Assert.Equal(pixels, copy);
        }
        finally
        {
            Marshal.FreeHGlobal(planes);
            Marshal.FreeHGlobal(destination);
        }

        surface.Dispose();
        surface.Dispose();
        Assert.Equal([allocation], freed);
        AssertEmptyFrame(surface);
        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VideoFormat_AllocationFailureReturnsZeroAndContainsReporterFailure(bool throws)
    {
        var reportCount = 0;
        using var surface = new LibVlcVideoSurface(_ =>
        {
            reportCount++;
            throw new IOException("Diagnostic output failed");
        }, 0, _ => throws ? throw new OutOfMemoryException("Test allocation failure") : IntPtr.Zero);

        var result = Format(surface, 16, 16);

        Assert.Equal(0U, result.Result);
        Assert.Equal(0U, result.Pitch);
        Assert.Equal(0U, result.Lines);
        AssertEmptyFrame(surface);
        Assert.Equal(1, reportCount);
    }

    [Fact]
    public void VideoCleanup_NormalStopAndReformatReleaseEachBufferWithoutErrors()
    {
        var reports = new List<ErrorReport>();
        var frees = 0;
        using var surface = new LibVlcVideoSurface(reports.Add, 0,
            free: pointer => { frees++; Marshal.FreeHGlobal(pointer); });
        Assert.Equal(1U, Format(surface, 16, 16).Result);
        Assert.Equal(1U, Format(surface, 8, 8).Result);
        Assert.Equal(1, frees);
        var opaque = IntPtr.Zero;
        surface.VideoCleanup(ref opaque);
        surface.VideoCleanup(ref opaque);
        Assert.Equal(2, frees);
        AssertEmptyFrame(surface);
        Assert.Equal(1U, Format(surface, 4, 4).Result);
        surface.Dispose();
        Assert.Equal(3, frees);
        Assert.Empty(reports);
    }

    [Fact]
    public void VideoFormat_InvalidReformatClearsPreviousFrame()
    {
        using var surface = new LibVlcVideoSurface();
        Assert.Equal(1U, Format(surface, 16, 16).Result);
        Assert.Equal(0U, Format(surface, uint.MaxValue, 16).Result);
        AssertEmptyFrame(surface);
        Assert.False(surface.CopyFrameTo(IntPtr.Zero, 0));
    }

    [Fact]
    public void VideoFormat_WaitingForBufferLockCannotAllocateAfterDispose()
    {
        var allocations = 0;
        var reports = new List<ErrorReport>();
        using var surface = new LibVlcVideoSurface(reports.Add, 0,
            allocate: size => { allocations++; return Marshal.AllocHGlobal(size); });
        var sync = typeof(LibVlcVideoSurface).GetField("_sync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        uint? result = null;
        Exception? threadFailure = null;
        var worker = new Thread(() =>
        {
            try { result = Format(surface, 16, 16).Result; }
            catch (Exception ex) { threadFailure = ex; }
        }) { IsBackground = true };

        lock (sync)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(
                () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            // Dispose re-enters this lock before the waiting native-format callback can acquire it.
            surface.Dispose();
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(threadFailure);
        Assert.Equal(0U, result);
        Assert.Equal(0, allocations);
        AssertEmptyFrame(surface);
        Assert.Empty(reports);
    }

    [Fact]
    public void VideoFormat_NullChromaReturnsFailure()
    {
        using var surface = new LibVlcVideoSurface();
        var opaque = IntPtr.Zero;
        uint width = 16, height = 16, pitch = 7, lines = 7;
        Assert.Equal(0U, surface.VideoFormat(ref opaque, IntPtr.Zero, ref width, ref height, ref pitch, ref lines));
        Assert.Equal(0U, pitch);
        Assert.Equal(0U, lines);
        AssertEmptyFrame(surface);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureReportsDiagnosticWithoutEscapingOrDoubleFree(bool dispose)
    {
        var reports = new List<ErrorReport>();
        var frees = 0;
        using var surface = new LibVlcVideoSurface(reports.Add, 2,
            free: pointer =>
            {
                frees++;
                Marshal.FreeHGlobal(pointer);
                throw new IOException("Test cleanup failure");
            });
        Assert.Equal(1U, Format(surface, 16, 16).Result);
        if (dispose)
            surface.Dispose();
        else
        {
            var opaque = IntPtr.Zero;
            surface.VideoCleanup(ref opaque);
        }
        var report = Assert.Single(reports);
        Assert.Equal("Video.ResourceCleanupFailed", report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
        Assert.Equal(dispose ? "DisposeSurface" : "VideoCleanup", report.Context!["Operation"]);
        AssertEmptyFrame(surface);
        surface.Dispose();
        Assert.Equal(1, frees);
    }

    [Fact]
    public void VideoDisplay_ContainsListenerExceptionsAndIgnoresDisposedSurface()
    {
        var reports = new List<ErrorReport>();
        using var surface = new LibVlcVideoSurface(reports.Add, 0);
        var frames = 0;
        surface.FrameReady += () => { frames++; throw new IOException("Frame listener failed"); };
        surface.VideoDisplay(IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(0, frames);
        Format(surface, 16, 16);
        surface.VideoDisplay(IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(1, frames);
        Assert.Equal("VideoDisplay", Assert.Single(reports).Context!["Operation"]);
        surface.Dispose();
        surface.VideoDisplay(IntPtr.Zero, IntPtr.Zero);
        Assert.Equal(1, frames);
        Assert.Equal(0U, Format(surface, 16, 16).Result);
        Assert.Single(reports);
    }

    private static (uint Result, uint Pitch, uint Lines, byte[] Chroma) Format(LibVlcVideoSurface surface, uint width, uint height)
    {
        var chroma = Marshal.AllocHGlobal(4);
        try
        {
            var opaque = IntPtr.Zero;
            uint pitch = 77, lines = 77;
            var result = surface.VideoFormat(ref opaque, chroma, ref width, ref height, ref pitch, ref lines);
            var bytes = new byte[4];
            Marshal.Copy(chroma, bytes, 0, 4);
            return (result, pitch, lines, bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(chroma);
        }
    }

    private static void AssertEmptyFrame(LibVlcVideoSurface surface)
    {
        Assert.Equal(IntPtr.Zero, surface.GetCurrentFrame());
        Assert.Equal(0, surface.Width);
        Assert.Equal(0, surface.Height);
        Assert.False(surface.TryGetFrameSize(out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }
}

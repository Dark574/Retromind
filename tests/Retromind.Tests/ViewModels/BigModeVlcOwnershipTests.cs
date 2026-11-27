using System.Reflection;
using System.Runtime.CompilerServices;
using LibVLCSharp.Shared;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeVlcOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_AlwaysAttemptsPlayerAndMediaDisposeAfterStopAndDetach(bool playerDisposeFails)
    {
        var operations = new List<string>();
        var media = new OwnedReference(() => operations.Add("release media"));
        var errors = new List<string>();

        var quiescent = VlcPlayerCleanup.Release(
            () => { operations.Add("stop"); throw new IOException(); },
            () => { operations.Add("detach"); throw new IOException(); },
            () => { operations.Add("dispose player"); if (playerDisposeFails) throw new IOException(); },
            media,
            (operation, _) => errors.Add(operation));

        Assert.Equal(["stop", "detach", "dispose player", "release media"], operations);
        Assert.Contains("Stop", errors);
        Assert.Contains("Detach", errors);
        Assert.Equal(playerDisposeFails, errors.Contains("DisposePlayer"));
        Assert.Equal(!playerDisposeFails, quiescent);
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("late")]
    [InlineData("missing")]
    public void StopReleasesOnlyItsOwnedReferenceRegardlessOfStoppedTiming(string delivery)
    {
        var nativeHandle = new IntPtr(42);
        var owner = MediaReference.Create(nativeHandle);
        var observer = MediaReference.Create(nativeHandle);
        Assert.NotSame(owner, observer);
        Assert.Equal(owner.NativeReference, observer.NativeReference);

        // The player has its own native retain. A Stopped observer can hold a
        // separate wrapper over that same native object.
        bool playerRetain = true;
        var stopped = 0;
        void ObserveStopped() => stopped++;
        Assert.True(VlcPlayerCleanup.Release(
            () => { if (delivery == "inline") ObserveStopped(); },
            () => playerRetain = false,
            disposePlayer: null, owner, (_, ex) => throw ex));
        if (delivery == "late") ObserveStopped();

        Assert.False(playerRetain);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(0, observer.DisposeCount);
        Assert.Equal(delivery == "missing" ? 0 : 1, stopped);
        observer.Dispose();
    }

    [Fact]
    public async Task ObsoleteStopAndQueuedFrame_CannotChangeTheReplacementPreview() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = new BigModeNavigationFixture([new MediaNode("Games")]);
            var owned = MediaReference.Create(new IntPtr(1));
            fixture.Set("_isViewReady", true);
            fixture.Set("_currentPreviewMediaA", owned);
            fixture.Set("_previewPlayGeneration", 7);
            fixture.Set("_expectedPreviewFrameGeneration", 7);
            fixture.Set("_expectedPreviewSurfaceIndex", 0);
            fixture.Call("BeginVideoPlayback", 0, "previous.mp4", 7, true);
            fixture.Call("OnMainVideoFrameReady", 0);

            fixture.Set("_previewPlayGeneration", 8);
            fixture.Call("BeginVideoPlayback", 0, "replacement.mp4", 8, true);
            fixture.Call("CompleteDelayedPreviewStop", 0, 7, 1);
            dispatcher.RunJobs();

            Assert.Same(owned, fixture.Get<Media>("_currentPreviewMediaA"));
            Assert.Equal(0, owned.DisposeCount);
            Assert.False(fixture.Vm.IsVideoVisible);

            fixture.Call("StopPreviewPlayer", 0);
            fixture.Call("StopPreviewPlayer", 0);
            Assert.Equal(1, owned.DisposeCount);
        });

    [Fact]
    public async Task LoopCallbackMustStillOwnItsPlayerAndPlaybackGeneration() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = new BigModeNavigationFixture([new MediaNode("Games")]);
            var retiredPlayer = (MediaPlayer)RuntimeHelpers.GetUninitializedObject(typeof(MediaPlayer));
            var replacementPlayer = (MediaPlayer)RuntimeHelpers.GetUninitializedObject(typeof(MediaPlayer));
            fixture.Set("_isViewReady", true);
            fixture.Set("_mediaPlayerA", replacementPlayer);
            fixture.Call("BeginVideoPlayback", 0, "replacement.mp4", 8, true);

            var check = typeof(BigModeViewModel).GetMethod("IsCurrentVideoCallback", BindingFlags.NonPublic | BindingFlags.Instance)!;
            bool IsCurrent(MediaPlayer player, int generation) => (bool)check.Invoke(fixture.Vm, [0, player, generation])!;

            Assert.False(IsCurrent(retiredPlayer, 8));
            Assert.False(IsCurrent(replacementPlayer, 7));
            Assert.True(IsCurrent(replacementPlayer, 8));
            fixture.Call("ExpectVideoStop", 0);
            Assert.False(IsCurrent(replacementPlayer, 8));
        });

    [Fact]
    public async Task AttractStop_ReleasesMediaAndPreservesTheAppImageRecreationRule()
    {
        var appImage = (bool)typeof(BigModeViewModel)
            .GetField("IsAppImage", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        BigModeNavigationFixture? fixture = null;
        Task? spin = null;
        var owned = MediaReference.Create(new IntPtr(1));
        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                fixture = new BigModeNavigationFixture([new MediaNode("Games") { Items = [new MediaItem("Game")] }]);
                fixture.Session.ActivateSelected();
                fixture.Set("_currentPreviewMediaA", owned);
                spin = fixture.CallAsync("PerformAttractModeStepAnimatedAsync");
            });
            await spin!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(appImage, fixture!.Get<bool>("_forceRecreateMediaPlayerNextTime"));
            Assert.Equal(1, owned.DisposeCount);
            Assert.False(fixture.Vm.IsInAttractMode);
        }
        finally
        {
            if (fixture != null)
                await AvaloniaTestDispatcher.InvokeAsync(_ => fixture.Dispose());
        }
    }

    private sealed class OwnedReference(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    // Supplies a native identity without loading VLC on the CI runner.
    private sealed class MediaReference : Media
    {
        private MediaReference() : base(null!, "unused") { }

        public int DisposeCount { get; private set; }

        internal static MediaReference Create(IntPtr handle)
        {
            var reference = (MediaReference)RuntimeHelpers.GetUninitializedObject(typeof(MediaReference));
            typeof(Internal).GetProperty(nameof(NativeReference), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(reference, handle);
            return reference;
        }

        protected override void Dispose(bool disposing) => DisposeCount++;
    }
}

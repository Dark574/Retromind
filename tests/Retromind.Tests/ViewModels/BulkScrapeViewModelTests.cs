using System.Net;
using Retromind.Models;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BulkScrapeViewModelTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task StartCommand_AfterBackup_ConnectsOnlyWhileDialogIsOpen(
        bool closeDuringBackup,
        int expectedRequests)
    {
        using var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        var settings = new AppSettings();
        settings.Scrapers.Add(new ScraperConfig
        {
            Type = ScraperType.IGDB,
            ClientId = "test-client",
            ClientSecret = "test-secret"
        });
        var service = new MetadataService(settings, client);
        using var viewModel = new BulkScrapeViewModel(
            new MediaNode("Games", NodeType.Area), settings, service);
        var backupCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.OnBeforeStartAsync = () => backupCompleted.Task;

        var start = viewModel.StartCommand.ExecuteAsync(null);
        Assert.False(start.IsCompleted);
        Assert.Equal(0, handler.RequestCount);

        // Closing BulkScrapeView disposes its view model while the backup is pending.
        if (closeDuringBackup)
            viewModel.Dispose();

        backupCompleted.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expectedRequests, handler.RequestCount);
        Assert.False(viewModel.IsBusy);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            // Stop at authentication so this test needs neither network nor an Avalonia event loop.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}

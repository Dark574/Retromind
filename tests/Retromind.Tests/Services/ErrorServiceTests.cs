using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ErrorServiceTests
{
    [Theory]
    [InlineData(ErrorSeverity.Diagnostic)]
    [InlineData(ErrorSeverity.Warning)]
    [InlineData(ErrorSeverity.Error)]
    [InlineData(ErrorSeverity.Critical)]
    public void Report_PersistsStructuredClassificationWithoutAutomaticNotification(ErrorSeverity severity)
    {
        using var temp = new TemporaryDirectory();
        var posts = 0;
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"), 4096, 3, _ => posts++);
        var exception = new IOException("Disk full", new InvalidOperationException("Inner detail"));

        service.Report(new ErrorReport("Persistence.SaveFailed", severity, "Test", "Could not save",
            exception, "save-42", new Dictionary<string, string> { ["Operation"] = "Save" }));

        using var json = JsonDocument.Parse(File.ReadAllText(LogPath(temp)));
        var entry = json.RootElement;
        Assert.Equal("Persistence.SaveFailed", entry.GetProperty("Code").GetString());
        Assert.Equal(severity.ToString(), entry.GetProperty("Severity").GetString());
        Assert.Equal("Test", entry.GetProperty("Component").GetString());
        Assert.Equal("Could not save", entry.GetProperty("Message").GetString());
        Assert.Equal("save-42", entry.GetProperty("CorrelationId").GetString());
        Assert.Equal("Save", entry.GetProperty("Context").GetProperty("Operation").GetString());
        Assert.Contains("Inner detail", entry.GetProperty("Exception").GetString());
        Assert.True(entry.GetProperty("TimestampUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal(0, posts);
    }

    [Fact]
    public void Report_RotatesByUtf8ByteSizeAndRetainsNewestArchivesAcrossInstances()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.GetPath("Logs", "Exceptions");
        var service = new ErrorService(directory);
        service.Report(Error("first"));
        var size = new FileInfo(LogPath(temp)).Length;

        // A fresh service must honor the size of an existing log, including non-ASCII text.
        service = new ErrorService(directory, size + 1, archiveCount: 2);
        foreach (var message in new[] { "second", "third", "fourth" })
            service.Report(Error(message));

        Assert.Equal(3, Directory.GetFiles(directory).Length);
        Assert.Equal("fourth", ReadMessage(LogPath(temp)));
        Assert.Equal("third", ReadMessage(LogPath(temp) + ".1"));
        Assert.Equal("second", ReadMessage(LogPath(temp) + ".2"));
    }

    [Fact]
    public void Report_AppendsUntilSizeLimitAndSupportsNoArchives()
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        service.Report(Error("Größe 日本語"));
        var size = new FileInfo(LogPath(temp)).Length;
        service = new ErrorService(temp.GetPath("Logs", "Exceptions"), size * 2, archiveCount: 0);
        service.Report(Error("Größe 日本語"));
        Assert.Equal(2, File.ReadAllLines(LogPath(temp)).Length);
        service.Report(Error("Größe 日本語"));
        Assert.Single(File.ReadAllLines(LogPath(temp)));
        Assert.Single(Directory.GetFiles(temp.GetPath("Logs", "Exceptions")));
    }

    [Theory]
    [InlineData("--password \"two word secret\"", "two word secret")]
    [InlineData("{\"client_secret\":\"two word secret\"}", "two word secret")]
    [InlineData("{\"password\":\"escaped\\\"secret\"}", "secret")]
    [InlineData("https://example.test/?refresh_token=query-secret&item=42", "query-secret")]
    [InlineData("https://user:url-password@example.test/path", "url-password")]
    [InlineData("Authorization: Bearer header-secret", "header-secret")]
    [InlineData("Proxy-Authorization: Basic basic-secret", "basic-secret")]
    [InlineData("Cookie: session=cookie-one; other=cookie-two", "cookie-two")]
    [InlineData("api_key=api-secret", "api-secret")]
    [InlineData("pwd=pwd-secret", "pwd-secret")]
    [InlineData("authorization_code=oauth-secret", "oauth-secret")]
    public void Report_RedactsCredentialsInEveryTextFieldAndExceptionChain(string text, string secret)
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        service.Report(new ErrorReport(text, ErrorSeverity.Error, text, text,
            new IOException(text, new Exception(text)), text,
            new Dictionary<string, string> { ["Detail"] = text }));

        var output = File.ReadAllText(LogPath(temp));
        Assert.DoesNotContain(secret, output);
        using var json = JsonDocument.Parse(output);
        Assert.Contains("<redacted>", json.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public void Report_RedactsSensitiveContextValuesAndExplicitSecretsInFreeText()
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        service.Report(new ErrorReport("Test", ErrorSeverity.Error, "Test", "raw hidden-value and known-value",
            new Exception("hidden-value known-value"),
            Context: new Dictionary<string, string>
            {
                ["AccessToken"] = "hidden-value",
                ["Detail"] = "hidden-value known-value",
                ["Operation"] = "Save"
            }, SensitiveValues: ["known-value"]));

        var output = File.ReadAllText(LogPath(temp));
        Assert.DoesNotContain("hidden-value", output);
        Assert.DoesNotContain("known-value", output);
        Assert.Contains("Save", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Report_WriteOrRotationFailureDoesNotThrowAndStillPostsExactlyOneNotification(bool failRotation)
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.GetPath("Logs", "Exceptions");
        if (failRotation)
        {
            temp.CreateFile("Logs/Exceptions/errors.jsonl", "existing log");
            temp.CreateDirectory("Logs", "Exceptions", "errors.jsonl.1");
        }
        else
        {
            temp.CreateFile("Logs", "blocks directory creation");
        }

        var queue = new Queue<Action>();
        var notifications = 0;
        var service = new ErrorService(directory, 1, 1, queue.Enqueue);
        service.Report(Error("failure"), () => { notifications++; return Task.CompletedTask; });

        Assert.Equal(0, notifications);
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, notifications);
        Assert.Empty(queue);
    }

    [Fact]
    public void Report_NotificationAndDispatcherFailuresDoNotEscapeOrRecursivelyReport()
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"), 4096, 3, action => action());
        service.Report(Error("notification"), () => throw new IOException("notification failure"));
        service = new ErrorService(temp.GetPath("Logs", "Exceptions"), 4096, 3,
            _ => throw new InvalidOperationException("dispatcher stopped"));
        service.Report(Error("dispatch"), () => Task.CompletedTask);
        Assert.Equal(2, File.ReadAllLines(LogPath(temp)).Length);
    }

    [Fact]
    public async Task Report_ConcurrentReportsRemainCompleteJsonLines()
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        await Task.WhenAll(Enumerable.Range(0, 50).Select(index => Task.Run(() => service.Report(Error($"{index}")))));
        var lines = File.ReadAllLines(LogPath(temp));
        Assert.Equal(50, lines.Length);
        Assert.Equal(50, lines.Select(line => ReadMessageFromJson(line)).Distinct().Count());
    }

    [Fact]
    public void Report_LimitsOversizedRecordsAfterRedactionAndKeepsValidJson()
    {
        using var temp = new TemporaryDirectory();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        service.Report(new ErrorReport("Test", ErrorSeverity.Error, "Test",
            "password=hidden-secret " + new string('日', 100_000),
            new IOException(new string('日', 100_000))));
        Assert.InRange(new FileInfo(LogPath(temp)).Length, 1, 64 * 1024);
        var output = File.ReadAllText(LogPath(temp));
        Assert.DoesNotContain("hidden-secret", output);
        using var json = JsonDocument.Parse(output);
        Assert.True(json.RootElement.GetProperty("Truncated").GetBoolean());
    }

    [Fact]
    public async Task Report_FromWorkerRoutesNotificationThroughUiScheduler()
    {
        using var temp = new TemporaryDirectory();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var service = new ErrorService(temp.GetPath("Logs", "Exceptions"), 4096, 3, queue.Enqueue);
        int? notifiedOn = null;
        await Task.Run(() => service.Report(Error("background"), () =>
        {
            notifiedOn = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }));
        Assert.Null(notifiedOn);
        Assert.True(queue.TryDequeue(out var notification));
        var uiSchedulerThread = Environment.CurrentManagedThreadId;
        notification();
        Assert.Equal(uiSchedulerThread, notifiedOn);
        Assert.Empty(queue);
    }

    private static ErrorReport Error(string message) => new("Test.Code", ErrorSeverity.Error, "Test", message);
    private static string LogPath(TemporaryDirectory temp) => temp.GetPath("Logs", "Exceptions", "errors.jsonl");
    private static string ReadMessage(string path) => ReadMessageFromJson(File.ReadAllText(path));
    private static string ReadMessageFromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Message").GetString()!;
    }
}

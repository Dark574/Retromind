using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class WinetricksProcessLogTests
{
    [Fact]
    public async Task FailedProcess_PersistsBothStreamsAndCorrelatesBoundedStderrWithCentralReport()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp,
                "printf 'normal output\\n'\nprintf 'wine: Call from unsupported function\\nTraceback: setup could not continue\\n' >&2\nexit 1"));
        var live = new ConcurrentQueue<string>();
        var request = WinetricksServiceTests.Request(temp);

        var result = await service.RunAsync(request, live.Enqueue);

        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.ProcessFailed", report.Code);
        Assert.True(Guid.TryParseExact(report.CorrelationId, "N", out _));
        Assert.Equal("1", report.Context!["ExitCode"]);
        Assert.Equal("Wine", report.Context["RunnerType"]);
        Assert.Equal(request.Verbs, report.Context["Verbs"]);
        Assert.Equal(request.PrefixRoot, report.Context["PrefixPath"]);
        Assert.Contains("unsupported function", report.Context["StderrSummary"]);
        Assert.Contains("Traceback", report.Context["StderrSummary"]);
        Assert.DoesNotContain("normal output", report.Context["StderrSummary"]);
        Assert.Equal("Written", report.Context["ProcessLogStatus"]);
        var path = Path.Combine(AppPaths.DataRoot, report.Context["ProcessLogPath"]);
        Assert.Equal(temp.GetPath("Logs", "Winetricks", $"winetricks-{report.CorrelationId}.jsonl"), path);
        var records = ReadRecords(path);
        Assert.Equal(report.CorrelationId, records[0].GetProperty("CorrelationId").GetString());
        Assert.Equal(request.Verbs, records[0].GetProperty("Verbs").GetString());
        Assert.Equal(request.PrefixRoot, records[0].GetProperty("PrefixPath").GetString());
        Assert.Equal("Wine", records[0].GetProperty("RunnerType").GetString());
        Assert.Contains(records, record => IsOutput(record, "stdout", "normal output"));
        Assert.Contains(records, record => IsOutput(record, "stderr", "wine: Call from unsupported function"));
        Assert.Equal(report.CorrelationId, records[^1].GetProperty("CorrelationId").GetString());
        Assert.Equal(1, records[^1].GetProperty("ExitCode").GetInt32());
        Assert.Equal("ProcessExecution", records[^1].GetProperty("Status").GetString());
        Assert.Contains("normal output", live);
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task SuccessfulProcess_WritesTranscriptWithoutExecutionError()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.GetPath("Logs", "Winetricks");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'stdout\\n'\nprintf 'stderr\\n' >&2\nexit 0"),
            processLogDirectory: directory);
        var result = await service.RunAsync(WinetricksServiceTests.Request(temp), _ => { });
        Assert.True(result.Success);
        Assert.Empty(errors.Reports);
        var records = ReadRecords(Assert.Single(Directory.GetFiles(directory)));
        Assert.Contains(records, record => IsOutput(record, "stdout", "stdout"));
        Assert.Contains(records, record => IsOutput(record, "stderr", "stderr"));
        Assert.Equal("Succeeded", records[^1].GetProperty("Status").GetString());
        Assert.Equal(0, records[^1].GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task LargeParallelOutput_StaysBoundedAndStillSummarizesFinalStderrAfterTranscriptFills()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.GetPath("Logs", "Winetricks");
        var errors = new RecordingErrorService();
        var body = "i=0\nwhile [ \"$i\" -lt 5000 ]; do\n" +
            $"printf 'stdout %s {new string('x', 256)}\\n' \"$i\"\n" +
            $"printf 'stderr %s {new string('y', 256)}\\n' \"$i\" >&2\n" +
            "i=$((i+1))\ndone\nprintf 'wine: final failure detail after all output\\n' >&2\nexit 1";
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, body), processLogDirectory: directory);
        var liveLines = 0;

        var result = await service.RunAsync(WinetricksServiceTests.Request(temp), _ => Interlocked.Increment(ref liveLines));

        Assert.Equal(1, result.ExitCode);
        Assert.True(liveLines >= 10_001); // Size limits never cut off the live output.
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Truncated", report.Context!["ProcessLogStatus"]);
        var summary = report.Context["StderrSummary"];
        Assert.Contains("wine: final failure detail", summary);
        Assert.InRange(Encoding.UTF8.GetByteCount(summary), 1, WinetricksProcessLog.MaximumSummaryBytes);
        Assert.InRange(summary.Split('\n').Length, 1, WinetricksProcessLog.MaximumSummaryLines);
        var path = Assert.Single(Directory.GetFiles(directory));
        Assert.InRange(new FileInfo(path).Length, 1, WinetricksProcessLog.DefaultMaximumBytes);
        var records = ReadRecords(path); // Every retained line remains complete JSON despite interleaved callbacks.
        Assert.Contains(records, record => record.GetProperty("Kind").GetString() == "Truncated");
        Assert.Equal("End", records[^1].GetProperty("Kind").GetString());
        Assert.Equal(1, records[^1].GetProperty("ExitCode").GetInt32());
        Assert.True(records[^1].GetProperty("Truncated").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentRuns_UseDistinctFilesAndCorrelationIds()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.GetPath("Logs", "Winetricks");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'diagnostic stderr\\n' >&2\nexit 1"), processLogDirectory: directory);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => service.RunAsync(
            WinetricksServiceTests.Request(temp) with { PrefixRoot = temp.GetPath("Library", "Prefixes", $"Game{index}") }, _ => { })));
        Assert.Equal(4, errors.Reports.Count);
        Assert.Equal(4, errors.Reports.Select(report => report.CorrelationId).Distinct().Count());
        Assert.Equal(4, Directory.GetFiles(directory).Length);
        foreach (var report in errors.Reports)
        {
            var path = Path.Combine(directory, Path.GetFileName(report.Context!["ProcessLogPath"]));
            Assert.Equal(report.CorrelationId, ReadRecords(path)[0].GetProperty("CorrelationId").GetString());
        }
    }

    [Fact]
    public async Task Cancellation_ClosesTranscriptWithCancelledStatusWithoutExecutionError()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var directory = temp.GetPath("Logs", "Winetricks");
        var errors = new RecordingErrorService();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'READY\\n'\nprintf 'before cancellation\\n' >&2\nexec /bin/sleep 60"),
            processLogDirectory: directory);
        var running = service.RunAsync(WinetricksServiceTests.Request(temp), line => { if (line == "READY") ready.TrySetResult(); }, cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsCancelled);
            Assert.Empty(errors.Reports);
            var records = ReadRecords(Assert.Single(Directory.GetFiles(directory)));
            Assert.Equal("Cancelled", records[^1].GetProperty("Status").GetString());
            Assert.Contains(records, record => IsOutput(record, "stdout", "READY"));
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UnwritableLogDirectory_DoesNotChangeResultOrLoseLiveOutputOrStderrSummary(int exitCode)
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.CreateFile("Logs/Winetricks", "blocked");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, $"printf 'live stdout\\n'\nprintf 'diagnostic stderr\\n' >&2\nexit {exitCode}"),
            processLogDirectory: directory);
        var live = new ConcurrentQueue<string>();
        var result = await service.RunAsync(WinetricksServiceTests.Request(temp), live.Enqueue);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(exitCode == 0, result.Success);
        Assert.Contains("live stdout", live);
        var logFailure = Assert.Single(errors.Reports, report => report.Code == "Winetricks.LogWriteFailed");
        Assert.Equal(ErrorSeverity.Diagnostic, logFailure.Severity);
        Assert.Equal("Unavailable", logFailure.Context!["ProcessLogStatus"]);
        Assert.Contains("diagnostic stderr", logFailure.Context["StderrSummary"]);
        Assert.Equal(exitCode == 0 ? 0 : 1, errors.Reports.Count(report => report.Code == "Winetricks.ProcessFailed"));
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task WriteFailureAfterHeader_IsDiagnosticAndNeverAnExecutionFailure()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'one\\ntwo\\nthree\\n'\nprintf 'stderr tail\\n' >&2\nexit 0"),
            processLogDirectory: temp.GetPath("Logs", "Winetricks"),
            openProcessLog: path => new ThrowingWriteStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write)));
        var result = await service.RunAsync(WinetricksServiceTests.Request(temp), _ => { });
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.LogWriteFailed", report.Code);
        Assert.Equal("Incomplete", report.Context!["ProcessLogStatus"]);
        Assert.Contains("stderr tail", report.Context["StderrSummary"]);
    }

    [Fact]
    public async Task TranscriptsAndSummary_RedactUrlsArgumentsAndKnownSecretsInFreeText()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("RETROMIND_TEST_API_TOKEN", "host-free-secret"));
        var errors = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        var body = "printf '%s\\n' 'https://url-user:url-password@example.test/path?access_token=query-token' >&2\n" +
            "printf '%s\\n' 'Authorization: Bearer bearer-secret' >&2\n" +
            "printf '%s\\n' '{\"password\":\"json-secret\"}' >&2\n" +
            "printf '%s\\n' 'multiline-known-part-one' 'multiline-known-part-two' >&2\n" +
            "printf '%s\\n' 'free text: host-free-secret override-free-secret quoted argument secret'\nexit 1";
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, body));
        var request = WinetricksServiceTests.Request(temp) with
        {
            Verbs = "corefonts --password 'quoted argument secret'",
            EnvironmentOverrides = new Dictionary<string, string>
            {
                ["TOKEN"] = "override-free-secret",
                ["SESSION_TOKEN"] = "multiline-known-part-one\nmultiline-known-part-two"
            }
        };
        var live = new ConcurrentQueue<string>();
        await service.RunAsync(request, live.Enqueue);
        var transcript = File.ReadAllText(Assert.Single(Directory.GetFiles(temp.GetPath("Logs", "Winetricks"))));
        var central = File.ReadAllText(temp.GetPath("Logs", "Exceptions", "errors.jsonl"));
        var all = transcript + central + string.Join('\n', live);
        foreach (var secret in new[] { "url-password", "query-token", "bearer-secret", "json-secret", "host-free-secret", "override-free-secret", "quoted argument secret", "multiline-known-part-one", "multiline-known-part-two" })
            Assert.DoesNotContain(secret, all);
        using var report = JsonDocument.Parse(central);
        var context = report.RootElement.GetProperty("Context");
        Assert.Contains("redacted", context.GetProperty("StderrSummary").GetString());
        Assert.DoesNotContain("EnvironmentOverrides", transcript);
        Assert.DoesNotContain("RETROMIND_TEST_API_TOKEN", transcript);
        Assert.False(context.TryGetProperty("Environment", out _));
    }

    [Fact]
    public void Retention_BoundsOwnedFilesAcrossRestartsAndLeavesUnrelatedFilesAlone()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("notes.txt", "unrelated");
        temp.CreateFile("winetricks-user.jsonl", "unrelated");
        var redactor = Redactor();
        for (var index = 0; index < 8; index++)
        {
            using var log = new WinetricksProcessLog(temp.RootPath, "Wine", "corefonts", "prefix", redactor, retainedFiles: 3);
            log.AppendOutput("stderr", $"line {index}");
            log.Complete(new WinetricksResult(true, 0));
            Assert.Null(log.WriteFailure);
            Assert.InRange(Directory.GetFiles(temp.RootPath).Length, 3, 5);
        }
        Assert.Equal(5, Directory.GetFiles(temp.RootPath).Length);
        Assert.Equal("unrelated", File.ReadAllText(temp.GetPath("notes.txt")));
        Assert.Equal("unrelated", File.ReadAllText(temp.GetPath("winetricks-user.jsonl")));
    }

    [Fact]
    public void Retention_DoesNotDeleteActiveLogsAndFailsClosedAtLimit()
    {
        using var temp = new TemporaryDirectory();
        using var first = new WinetricksProcessLog(temp.RootPath, "Wine", "one", "prefix", Redactor(), retainedFiles: 2);
        using var second = new WinetricksProcessLog(temp.RootPath, "Wine", "two", "prefix", Redactor(), retainedFiles: 2);
        using var third = new WinetricksProcessLog(temp.RootPath, "Wine", "three", "prefix", Redactor(), retainedFiles: 2);
        Assert.NotNull(third.WriteFailure);
        Assert.Equal(2, Directory.GetFiles(temp.RootPath).Length);
        first.Complete(new WinetricksResult(true, 0));
        using var fourth = new WinetricksProcessLog(temp.RootPath, "Wine", "four", "prefix", Redactor(), retainedFiles: 2);
        Assert.Null(fourth.WriteFailure);
        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
        second.AppendOutput("stdout", "still active");
        second.Complete(new WinetricksResult(true, 0));
        Assert.Contains(ReadRecords(second.FilePath), record => IsOutput(record, "stdout", "still active"));
    }

    [Fact]
    public void HugeUnicodeLine_IsSanitizedBeforeTruncationAndStaysWithinByteBudgets()
    {
        using var temp = new TemporaryDirectory();
        var redactor = new ErrorReportRedactor(new ErrorReport("Test", ErrorSeverity.Error, "Test", "Test",
            SensitiveValues: ["hidden-secret"]));
        using var log = new WinetricksProcessLog(temp.RootPath, "Wine", "corefonts", "prefix", redactor, maximumBytes: 32 * 1024);
        log.AppendOutput("stderr", new string('日', 30_000) + " hidden-secret final context");
        log.Complete(new WinetricksResult(false, 1, WinetricksFailureCategory.ProcessExecution));
        Assert.InRange(new FileInfo(log.FilePath).Length, 1, 32 * 1024);
        var summary = log.GetDiagnosticContext()["StderrSummary"];
        Assert.Contains("final context", summary);
        Assert.DoesNotContain("hidden-secret", summary + File.ReadAllText(log.FilePath));
        Assert.InRange(Encoding.UTF8.GetByteCount(summary), 1, WinetricksProcessLog.MaximumSummaryBytes);
        Assert.Equal(1, ReadRecords(log.FilePath)[^1].GetProperty("ExitCode").GetInt32());
    }

    private static ErrorReportRedactor Redactor() => new(new ErrorReport("Test", ErrorSeverity.Diagnostic, "Test", "Test"));

    private static List<JsonElement> ReadRecords(string path) => File.ReadAllLines(path).Select(line =>
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }).ToList();

    private static bool IsOutput(JsonElement record, string stream, string text) =>
        record.GetProperty("Kind").GetString() == "Output" &&
        record.GetProperty("Stream").GetString() == stream && record.GetProperty("Text").GetString() == text;

    private sealed class ThrowingWriteStream(Stream inner) : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (++_writes > 2) throw new IOException("Simulated transcript write failure");
            inner.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value) => inner.WriteByte(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>Bounded, best-effort diagnostic output; never handles the failed operation.</summary>
public sealed class ErrorService : IErrorService
{
    private readonly object _writeLock = new();
    private readonly string _logDirectory;
    private readonly long _maximumFileBytes;
    private readonly int _archiveCount;
    private readonly Action<Action> _postToUi;

    public ErrorService(string logDirectory, long maximumFileBytes = 1024 * 1024, int archiveCount = 3)
        : this(logDirectory, maximumFileBytes, archiveCount, UiThreadHelper.Post)
    {
    }

    internal ErrorService(string logDirectory, long maximumFileBytes, int archiveCount, Action<Action> postToUi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(archiveCount);
        _logDirectory = logDirectory;
        _maximumFileBytes = maximumFileBytes;
        _archiveCount = archiveCount;
        _postToUi = postToUi;
    }

    public void Report(ErrorReport error, Func<Task>? notifyUser = null)
    {
        try
        {
            var redactor = new ErrorReportRedactor(error);
            var entry = new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Code = redactor.Redact(error.Code),
                Severity = error.Severity.ToString(),
                Component = redactor.Redact(error.Component),
                Message = redactor.Redact(error.Message),
                Exception = redactor.Redact(error.Exception?.ToString()),
                CorrelationId = redactor.Redact(error.CorrelationId),
                Context = redactor.RedactContext(error.Context)
            };
            var json = JsonSerializer.Serialize(entry);
            if (Encoding.UTF8.GetByteCount(json) > 64 * 1024 - 1)
            {
                // Keep a single enormous exception/context from defeating bounded retention.
                // Redact fully before truncating so a cut cannot leave a partial credential.
                json = JsonSerializer.Serialize(new
                {
                    entry.TimestampUtc,
                    Code = Truncate(entry.Code, 128),
                    entry.Severity,
                    Component = Truncate(entry.Component, 128),
                    Message = Truncate(entry.Message, 2048),
                    Exception = Truncate(entry.Exception, 4096),
                    CorrelationId = Truncate(entry.CorrelationId, 128),
                    Truncated = true
                });
            }
            var bytes = Encoding.UTF8.GetBytes(json + "\n");

            lock (_writeLock)
            {
                Directory.CreateDirectory(_logDirectory);
                var path = Path.Combine(_logDirectory, "errors.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 0 &&
                    new FileInfo(path).Length + bytes.Length > _maximumFileBytes)
                {
                    Rotate(path);
                }

                using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    UnixCreateMode = OperatingSystem.IsWindows()
                        ? null
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                stream.Write(bytes);
            }
        }
        catch
        {
            // Never report logging failures through this service, Debug, or Console:
            // even a secondary exception message could contain an unredacted secret.
        }

        if (notifyUser == null)
            return;

        try
        {
            _postToUi(async () =>
            {
                try
                {
                    await notifyUser();
                }
                catch
                {
                    // Notification failure cannot affect the originating operation either.
                }
            });
        }
        catch
        {
            // Dispatcher may already be shutting down. No recursive notification.
        }
    }

    private static string? Truncate(string? value, int maximumCharacters) =>
        value?.Length > maximumCharacters ? value[..maximumCharacters] + "…" : value;

    private void Rotate(string path)
    {
        if (_archiveCount == 0)
        {
            File.Delete(path);
            return;
        }

        for (var index = _archiveCount - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            if (File.Exists(source))
                File.Move(source, path + "." + (index + 1), overwrite: true);
        }

        File.Move(path, path + ".1", overwrite: true);
    }
}

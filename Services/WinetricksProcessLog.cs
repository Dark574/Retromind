using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>One bounded, sanitized diagnostic transcript; never controls Winetricks execution.</summary>
internal sealed class WinetricksProcessLog : IDisposable
{
    internal const int DefaultMaximumBytes = 1024 * 1024;
    internal const int DefaultRetainedFiles = 20;
    internal const int MaximumSummaryBytes = 4096;
    internal const int MaximumSummaryLines = 16;
    private const int FooterReserveBytes = 2048;
    private static readonly object RetentionLock = new();
    private static readonly HashSet<string> ActivePaths = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly ErrorReportRedactor _redactor;
    private readonly int _maximumBytes;
    private readonly Queue<string> _stderrTail = new();
    private int _stderrBytes;
    private Stream? _stream;
    private int _writtenBytes;
    private bool _created;
    private bool _completed;
    private bool _outputFull;
    private bool _truncated;
    private bool _registered;
    private Exception? _writeFailure;

    internal string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    internal string FilePath { get; }
    internal string RelativePath { get; }
    internal string Verbs { get; }
    internal int? ExitCode { get; private set; }
    internal Exception? WriteFailure { get { lock (_sync) return _writeFailure; } }

    internal WinetricksProcessLog(string directory, string runnerType, string verbs, string prefix,
        ErrorReportRedactor redactor, int maximumBytes = DefaultMaximumBytes,
        int retainedFiles = DefaultRetainedFiles, Func<string, Stream>? openFile = null)
    {
        _redactor = redactor;
        _maximumBytes = maximumBytes;
        var name = $"winetricks-{CorrelationId}.jsonl";
        FilePath = name;
        RelativePath = Path.Combine("Logs", "Winetricks", name);
        Verbs = LimitUtf8(Sanitize(verbs), 1024);
        var safePrefix = LimitUtf8(Sanitize(prefix), 2048);
        var safeRunner = LimitUtf8(Sanitize(runnerType), 64);
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 32 * 1024);
            ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
            directory = Path.GetFullPath(directory);
            FilePath = Path.Combine(directory, name);
            lock (RetentionLock)
            {
                Directory.CreateDirectory(directory);
                var files = Directory.GetFiles(directory, "winetricks-*.jsonl")
                    .Where(IsOwnedLogFile)
                    .OrderBy(path => File.GetLastWriteTimeUtc(path))
                    .ThenBy(path => path, StringComparer.Ordinal).ToList();
                while (files.Count >= retainedFiles)
                {
                    var oldest = files.FirstOrDefault(path => !ActivePaths.Contains(path));
                    if (oldest == null)
                        throw new IOException("The Winetricks log retention limit is occupied by active runs.");
                    // If retention cannot delete a file, fail closed instead of growing without a bound.
                    File.Delete(oldest);
                    files.Remove(oldest);
                }
                _stream = openFile != null ? openFile(FilePath) : new FileStream(FilePath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                _created = true;
                ActivePaths.Add(FilePath);
                _registered = true;
            }
            WriteRecord(new
            {
                Kind = "Start", TimestampUtc = DateTimeOffset.UtcNow, CorrelationId,
                RunnerType = safeRunner, Verbs, PrefixPath = safePrefix
            }, _maximumBytes - FooterReserveBytes);
        }
        catch (Exception ex)
        {
            FailWriting(ex);
        }
    }

    internal void AppendOutput(string channel, string text, Action<string>? appendSanitizedLiveOutput = null)
    {
        // The redactor is immutable/thread-safe. Do not serialize stdout/stderr
        // redaction under the writer lock or redact the same live line a second time.
        var sanitized = Sanitize(text);
        try
        {
            lock (_sync)
            {
                if (_completed)
                    return;
                // stderr keeps a bounded tail even when the transcript is full/unwritable.
                if (channel != "stderr" && (_outputFull || _writeFailure != null))
                    return;
                if (channel == "stderr" && !string.IsNullOrWhiteSpace(sanitized))
                {
                    var line = LimitUtf8(sanitized, MaximumSummaryBytes - 1, keepEnd: true, markTruncated: false);
                    _stderrTail.Enqueue(line);
                    _stderrBytes += Encoding.UTF8.GetByteCount(line) + 1;
                    while (_stderrTail.Count > MaximumSummaryLines || _stderrBytes > MaximumSummaryBytes)
                        _stderrBytes -= Encoding.UTF8.GetByteCount(_stderrTail.Dequeue()) + 1;
                }
                if (_outputFull || _writeFailure != null)
                    return;
                WriteRecord(new
                {
                    Kind = "Output", TimestampUtc = DateTimeOffset.UtcNow, Stream = channel,
                    Text = LimitUtf8(sanitized, 8192)
                }, _maximumBytes - FooterReserveBytes);
            }
        }
        catch (Exception ex)
        {
            lock (_sync) FailWriting(ex);
        }
        finally
        {
            // Neither a full/failed file nor an observer failure changes live output or execution.
            // Invoke the observer only after releasing the transcript lock.
            try { appendSanitizedLiveOutput?.Invoke(sanitized); }
            catch { /* A closed process window cannot break a native output callback. */ }
        }
    }

    internal void Complete(WinetricksResult result)
    {
        lock (_sync)
        {
            if (_completed)
                return;
            ExitCode = result.ExitCode;
            Finish(result.IsCancelled ? "Cancelled" : result.Success ? "Succeeded" : result.FailureCategory.ToString());
        }
    }

    internal IReadOnlyDictionary<string, string> GetDiagnosticContext()
    {
        lock (_sync)
        {
            return new Dictionary<string, string>
            {
                ["Verbs"] = Verbs,
                ["ProcessLogPath"] = RelativePath,
                ["ProcessLogStatus"] = !_created ? "Unavailable" : _writeFailure != null ? "Incomplete" : _truncated ? "Truncated" : "Written",
                ["StderrSummary"] = _stderrTail.Count == 0 ? "No non-empty stderr output was captured." : string.Join('\n', _stderrTail)
            };
        }
    }

    public void Dispose()
    {
        lock (_sync)
            if (!_completed)
                Finish("Incomplete");
    }

    private void Finish(string status)
    {
        _completed = true;
        try
        {
            WriteRecord(new { Kind = "End", TimestampUtc = DateTimeOffset.UtcNow, CorrelationId, Status = status, ExitCode, Truncated = _truncated },
                _maximumBytes, isFooter: true);
            _stream?.Dispose();
            _stream = null;
        }
        catch (Exception ex)
        {
            FailWriting(ex);
        }
        finally
        {
            if (_registered)
            {
                lock (RetentionLock) ActivePaths.Remove(FilePath);
                _registered = false;
            }
        }
    }

    private void WriteRecord(object record, int budget, bool isFooter = false)
    {
        if (_stream == null || _writeFailure != null)
            return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if ((long)_writtenBytes + bytes.Length + 1 > budget)
        {
            _outputFull = true;
            _truncated = true;
            // Footer space is reserved even if the next complete output record does not fit.
            if (!isFooter)
                WriteRecord(new { Kind = "Truncated", Reason = "Process output exceeded the transcript size limit." }, _maximumBytes, isFooter: true);
            return;
        }
        _stream.Write(bytes, 0, bytes.Length);
        _stream.WriteByte((byte)'\n');
        _writtenBytes += bytes.Length + 1;
    }

    private void FailWriting(Exception exception)
    {
        _writeFailure ??= exception;
        try { _stream?.Dispose(); }
        catch { /* The original diagnostic write failure remains authoritative. */ }
        _stream = null;
    }

    private string Sanitize(string value)
    {
        try { return _redactor.Redact(value) ?? string.Empty; }
        catch { return "[Content omitted because redaction failed.]"; }
    }

    private string LimitUtf8(string value, int maximumBytes, bool keepEnd = false, bool markTruncated = true)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
            return value;
        if (markTruncated)
            _truncated = true;
        var bytes = 0;
        if (keepEnd)
        {
            var start = value.Length;
            while (start > 0)
            {
                var status = Rune.DecodeLastFromUtf16(value.AsSpan(0, start), out var rune, out var consumed);
                if (status != System.Buffers.OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                    consumed = 1;
                }
                if (bytes + rune.Utf8SequenceLength > maximumBytes)
                    break;
                bytes += rune.Utf8SequenceLength;
                start -= consumed;
            }
            return value[start..];
        }
        var characters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }
        return value[..characters];
    }

    private static bool IsOwnedLogFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == "winetricks-".Length + 32 && Guid.TryParseExact(name["winetricks-".Length..], "N", out _);
    }
}

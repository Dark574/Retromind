using System.Collections.Concurrent;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.TestInfrastructure;

internal sealed class RecordingErrorService : IErrorService
{
    public ConcurrentQueue<ErrorReport> Reports { get; } = new();
    public ConcurrentQueue<Func<Task>?> Notifications { get; } = new();
    public bool ThrowOnReport { get; init; }

    public void Report(ErrorReport error, Func<Task>? notifyUser = null)
    {
        Reports.Enqueue(error);
        Notifications.Enqueue(notifyUser);
        if (ThrowOnReport)
            throw new IOException("Diagnostic output unavailable");
    }
}

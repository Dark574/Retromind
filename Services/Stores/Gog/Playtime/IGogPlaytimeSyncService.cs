using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog.Playtime;

internal interface IGogPlaytimeSyncService
{
    /// <summary>
    /// Completes after persistence for an eligible session. Queue/account/capacity failures fault the task;
    /// the launcher already treats these as a best-effort integration failure without changing game results.
    /// </summary>
    Task EnqueueSessionAsync(
        MediaItem item,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    void RequestPendingSync();

    Task TrySyncPendingAsync(CancellationToken cancellationToken = default);
}

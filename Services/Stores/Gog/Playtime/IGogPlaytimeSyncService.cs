using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog.Playtime;

internal interface IGogPlaytimeSyncService
{
    Task EnqueueSessionAsync(
        MediaItem item,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    void RequestPendingSync();

    Task TrySyncPendingAsync(CancellationToken cancellationToken = default);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog.Playtime;

internal interface IGogPlaytimeSyncService
{
    Task ReportSessionAsync(
        MediaItem item,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task TrySyncPendingAsync(CancellationToken cancellationToken = default);
}

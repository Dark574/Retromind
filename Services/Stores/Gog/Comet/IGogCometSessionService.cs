using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog.Comet;

internal interface IGogCometGameSession : IAsyncDisposable
{
    Task CompleteAsync();
}

internal interface IGogCometSessionService
{
    Task<IGogCometGameSession?> TryStartAsync(
        MediaItem item,
        CancellationToken ct = default);
}

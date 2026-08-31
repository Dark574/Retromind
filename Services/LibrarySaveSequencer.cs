using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services;

/// <summary>
/// Ensures that complete library-save pipelines run one at a time.
/// </summary>
internal sealed class LibrarySaveSequencer
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

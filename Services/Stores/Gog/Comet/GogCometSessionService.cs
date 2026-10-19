using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed class GogCometSessionService : IGogCometSessionService
{
    private static readonly TimeSpan CompletionGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    private readonly CometExecutableResolver _executableResolver;
    private readonly GogCometCredentialProvider _credentialProvider;
    private readonly CometProcessRunner _processRunner;

    internal GogCometSessionService(
        CometExecutableResolver executableResolver,
        GogCometCredentialProvider credentialProvider,
        CometProcessRunner processRunner)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<IGogCometGameSession?> TryStartAsync(
        MediaItem item,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsEligible(item) || _executableResolver.Resolve() == null)
            return null;

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(StartupTimeout);
        try
        {
            var credentials = await _credentialProvider
                .GetCredentialsAsync(startupCts.Token)
                .ConfigureAwait(false);
            if (credentials == null)
                return null;

            var processSession = await _processRunner
                .StartAsync(credentials, startupCts.Token)
                .ConfigureAwait(false);
            return new GogCometGameSession(processSession);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CometProcessException(
                $"Comet did not start within {StartupTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal static bool IsEligible(MediaItem item) =>
        GogMediaItemStateHelper.TryGetGameId(item) != null;

    private sealed class GogCometGameSession(
        CometProcessRunner.CometProcessSession processSession) : IGogCometGameSession
    {
        private int _disposed;

        public async Task CompleteAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await processSession
                    .WaitForExitAsync()
                    .WaitAsync(CompletionGracePeriod)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Games without a compatible Galaxy SDK never connect, so Comet's
                // --quit mode cannot observe a client disconnect on its own.
            }
            finally
            {
                await processSession.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await processSession.DisposeAsync().ConfigureAwait(false);
        }
    }
}

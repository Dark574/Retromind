using System;

namespace Retromind.Helpers.Video;

internal static class VlcPlayerCleanup
{
    // Called by the player owner, outside VLC callbacks. The player and the
    // application each own their own native reference to the assigned medium.
    internal static bool Release(Action? stop, Action? detach, Action? disposePlayer,
        IDisposable? media, Action<string, Exception> report)
    {
        var stopped = Try(stop, "Stop", report);
        var detached = Try(detach, "Detach", report);
        var disposed = Try(disposePlayer, "DisposePlayer", report);
        if (media != null)
            Try(media.Dispose, "DisposeMedia", report);

        // Any successful native operation above can quiesce the input.
        return stopped || detached || disposed || (stop == null && detach == null && disposePlayer == null);
    }

    private static bool Try(Action? action, string operation, Action<string, Exception> report)
    {
        if (action == null)
            return false;
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            report(operation, ex);
            return false;
        }
    }
}

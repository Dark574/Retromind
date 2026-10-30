using System;
using System.IO;

namespace Retromind.Services.Stores.Gog.Playtime;

internal enum GogPlaytimeQueueFailure
{
    Read,
    Write,
    Capacity,
    AccountUnavailable,
    SessionChangedAfterUpload
}

/// <summary>A failed local persistence operation; messages deliberately contain no account or credential data.</summary>
internal sealed class GogPlaytimeQueueException(GogPlaytimeQueueFailure failure, Exception? innerException = null)
    : IOException(failure switch
    {
        GogPlaytimeQueueFailure.Read => "The existing GOG playtime queue could not be read or validated and was preserved.",
        GogPlaytimeQueueFailure.Write => "The GOG playtime session could not be durably saved; the previous queue was preserved.",
        GogPlaytimeQueueFailure.Capacity => "The GOG playtime queue is full; the new session was not queued and existing sessions were preserved.",
        GogPlaytimeQueueFailure.AccountUnavailable => "The GOG playtime session could not be queued without a gameplay account.",
        _ => "GOG confirmed the upload, but the queued session changed and cannot safely be removed or uploaded again."
    }, innerException)
{
    internal GogPlaytimeQueueFailure Failure { get; } = failure;
}

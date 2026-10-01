using System;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Indicates that a provider-reported request allowance has been exhausted.
/// Bulk operations use this signal to stop instead of repeating doomed calls.
/// </summary>
public sealed class MetadataQuotaExceededException : Exception
{
    public MetadataQuotaExceededException(string message)
        : base(message)
    {
    }
}

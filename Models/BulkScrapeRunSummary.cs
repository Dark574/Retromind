namespace Retromind.Models;

/// <summary>Result of applying one safe scraper match, independently of later library persistence.</summary>
public enum BulkScrapeImportOutcome
{
    Imported,
    Skipped,
    Failed
}

public enum BulkScrapeRunStatus
{
    NotStarted,
    Running,
    Completed,
    CompletedWithErrors,
    Cancelled,
    ProviderLimitReached,
    Failed
}

/// <summary>A snapshot of mutually exclusive terminal item outcomes; unfinished items remain unprocessed.</summary>
public sealed record BulkScrapeRunSummary(
    int Total, int Completed, int Imported, int NoSafeMatch, int Skipped, int Failed, int NotProcessed,
    bool CollectionComplete);

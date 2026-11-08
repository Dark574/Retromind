using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.ViewModels;

internal enum BulkScrapeItemOutcome { Imported, NoSafeMatch, Skipped, Failed }

/// <summary>Small run-local accumulator shared by parallel workers, never by UI bindings.</summary>
internal sealed class BulkScrapeRunProgress
{
    private readonly object _gate = new();
    private readonly HashSet<MediaItem> _completed = new(ReferenceEqualityComparer.Instance);
    private readonly int[] _counts = new int[4];

    public int Completed { get { lock (_gate) return _completed.Count; } }

    public int Complete(MediaItem item, BulkScrapeItemOutcome outcome)
    {
        lock (_gate)
        {
            if (_completed.Add(item))
                _counts[(int)outcome]++;
            return _completed.Count;
        }
    }

    public BulkScrapeRunSummary Snapshot(int total, bool collectionComplete)
    {
        lock (_gate)
            return new(total, _completed.Count, _counts[0], _counts[1], _counts[2], _counts[3],
                Math.Max(0, total - _completed.Count), collectionComplete);
    }
}

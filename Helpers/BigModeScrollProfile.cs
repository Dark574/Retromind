using System;

namespace Retromind.Helpers;

/// <summary>Shared controller timing and library-only step profiles; no persisted settings.</summary>
internal static class BigModeScrollProfile
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(350);
    internal static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(80);
    internal const int PageSize = 10;

    private static readonly (TimeSpan From, int Step)[] DirectionSteps =
    [
        (TimeSpan.Zero, 1), (TimeSpan.FromSeconds(1), 2),
        (TimeSpan.FromSeconds(2), 5), (TimeSpan.FromSeconds(3), 10),
        (TimeSpan.FromSeconds(5), 20), (TimeSpan.FromSeconds(8), 40)
    ];

    private static readonly (TimeSpan From, int Step)[] PageSteps =
    [
        (TimeSpan.Zero, PageSize), (TimeSpan.FromSeconds(2), 20),
        (TimeSpan.FromSeconds(3), 30), (TimeSpan.FromSeconds(5), 40),
        (TimeSpan.FromSeconds(7), 50)
    ];

    internal static int GetStep(TimeSpan held, int count, bool page)
    {
        var profile = page ? PageSteps : DirectionSteps;
        var step = profile[0].Step;
        foreach (var stage in profile)
        {
            if (held < stage.From) break;
            step = stage.Step;
        }
        // Keep repeated direction jumps within a tenth of a small list. Pages
        // retain their established ten-entry base step and clamp at the edge.
        return Math.Min(step, Math.Max(page ? PageSize : 1, count / 10));
    }
}

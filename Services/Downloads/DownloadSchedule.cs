using System;
using Retromind.Models;

namespace Retromind.Services.Downloads;

internal static class DownloadSchedule
{
    internal static bool IsAllowed(DownloadSettings settings, DateTimeOffset localTime)
    {
        if (!settings.LimitToTimeWindow || settings.WindowStart == settings.WindowEnd)
            return true;

        var now = TimeOnly.FromDateTime(localTime.DateTime);
        return settings.WindowStart < settings.WindowEnd
            ? now >= settings.WindowStart && now < settings.WindowEnd
            : now >= settings.WindowStart || now < settings.WindowEnd;
    }

    internal static TimeSpan? UntilTransition(DownloadSettings settings, DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        if (!settings.LimitToTimeWindow || settings.WindowStart == settings.WindowEnd)
            return null;

        var localNow = TimeZoneInfo.ConvertTime(utcNow, zone);
        var allowed = IsAllowed(settings, localNow);
        DateTimeOffset? next = null;
        for (var days = 0; days <= 2; days++)
        {
            var date = DateOnly.FromDateTime(localNow.DateTime).AddDays(days);
            foreach (var time in new[] { settings.WindowStart, settings.WindowEnd })
            {
                var local = date.ToDateTime(time);
                // A boundary in the spring gap starts at the first real local minute.
                while (zone.IsInvalidTime(local))
                    local = local.AddMinutes(1);
                var offsets = zone.IsAmbiguousTime(local)
                    ? zone.GetAmbiguousTimeOffsets(local)
                    : [zone.GetUtcOffset(local)];
                foreach (var offset in offsets)
                {
                    var candidate = new DateTimeOffset(local, offset);
                    if (candidate > utcNow && (!next.HasValue || candidate < next))
                        next = candidate;
                }
            }
        }

        // The clock can itself cross a window boundary when a DST hour is repeated.
        var limit = next ?? utcNow.AddDays(1);
        var nextMinute = utcNow.UtcTicks - utcNow.UtcTicks % TimeSpan.TicksPerMinute + TimeSpan.TicksPerMinute;
        for (var instant = new DateTimeOffset(nextMinute, TimeSpan.Zero); instant < limit; instant = instant.AddMinutes(1))
        {
            if (IsAllowed(settings, TimeZoneInfo.ConvertTime(instant, zone)) != allowed)
                return instant - utcNow;
        }

        return limit - utcNow;
    }
}

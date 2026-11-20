using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Retromind.Services.Downloads;

namespace Retromind.Services.Stores.Epic.Legendary;

// Only numeric counters from Legendary's known download output reach the queue.
internal sealed partial class LegendaryDownloadProgress(Action<DownloadProgress> publish)
{
    private DownloadProgress _progress;

    internal void Read(string line)
    {
        var match = Metrics().Match(line);
        if (!match.Success || !double.TryParse(match.Groups["value"].ValueSpan, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) || value > long.MaxValue / 1_048_576d)
            return;

        _progress = match.Groups["kind"].Value switch
        {
            "Progress:" => _progress with { Fraction = Math.Clamp(value / 100, 0, 1) },
            "Downloaded:" => _progress with { Bytes = (long)(value * 1_048_576) },
            "Download size:" => _progress with { Total = (long)(value * 1_048_576) },
            _ => _progress with { BytesPerSecond = value * 1_048_576 }
        };
        publish(_progress);
    }

    [GeneratedRegex(@"\b(?<kind>Progress:|Downloaded:|Download\s+-|Download size:)\s*(?<value>[0-9]{1,12}(?:\.[0-9]{1,4})?)(?:\s*%|\s+MiB(?:/s\s+\(raw\))?)", RegexOptions.CultureInvariant)]
    private static partial Regex Metrics();
}

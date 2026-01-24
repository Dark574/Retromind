using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.ViewModels;

public sealed class StatusFilterOption
{
    public string Label { get; }
    public PlayStatus? Value { get; }

    public StatusFilterOption(string label, PlayStatus? value)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Value = value;
    }

    public static IReadOnlyList<StatusFilterOption> CreateDefault(string allLabel = "All")
    {
        var options = new List<StatusFilterOption>
        {
            new StatusFilterOption(allLabel, null)
        };

        foreach (var status in Enum.GetValues<PlayStatus>())
            options.Add(new StatusFilterOption(status.ToString(), status));

        return options;
    }
}

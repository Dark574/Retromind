using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Retromind.Helpers;

namespace Retromind.Services.BigMode;

public sealed record BigModeAlphabetGroup(string Label, int TargetIndex, BigModeNavigationKey? TargetKey)
{
    public bool IsAvailable => TargetIndex >= 0;
}

/// <summary>A bounded grid and first visible target per group, built from one frame only.</summary>
internal sealed class BigModeAlphabetIndex
{
    internal const int Columns = 7;
    private static readonly string[] Labels =
        ["0–9", .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), "#",
         "Α–Ω", "А–Я", "漢字", "かな", "한글", "ع", "א", "अ", "…"];
    internal IReadOnlyList<BigModeAlphabetGroup> Groups { get; }

    internal BigModeAlphabetIndex(IReadOnlyList<BigModeNavigationEntry> entries)
    {
        var groups = Labels.Select(label => new BigModeAlphabetGroup(label, -1, null)).ToArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var group = GetGroupIndex(entries[i]);
            if (!groups[group].IsAvailable) groups[group] = new(Labels[group], i, entries[i].Key);
        }
        Groups = groups;
    }

    internal static int GetGroupIndex(BigModeNavigationEntry entry) => GetGroupIndex(
        entry is BigModeMediaNavigationEntry media ? MediaSortHelper.GetPrimarySortKey(media.Item) : entry.Title);

    internal static int GetGroupIndex(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key) || !Rune.TryGetRuneAt(key, 0, out var rune)) return 27;
        if (Rune.IsNumber(rune)) return 0;
        if (!Rune.IsLetter(rune)) return 27;
        // Normalize only the first valid rune, never the rest of a possibly malformed title.
        var folded = rune.ToString().Normalize(NormalizationForm.FormD).ToUpperInvariant();
        var c = folded[0];
        if (c is >= 'A' and <= 'Z') return c - 'A' + 1;
        var latin = rune.Value switch
        {
            0xDF or 0x1E9E => 'S', 0xC6 or 0xE6 => 'A', 0x152 or 0x153 or 0xD8 or 0xF8 => 'O',
            0x141 or 0x142 => 'L', 0xD0 or 0xF0 or 0x110 or 0x111 => 'D', 0xDE or 0xFE => 'T',
            _ => '\0'
        };
        if (latin != '\0') return latin - 'A' + 1;
        return rune.Value switch
        {
            >= 0x370 and <= 0x3FF or >= 0x1F00 and <= 0x1FFF => 28,
            >= 0x400 and <= 0x52F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F => 29,
            >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF => 30,
            >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9D => 31,
            >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or
                >= 0xAC00 and <= 0xD7FF => 32,
            >= 0x600 and <= 0x6FF or >= 0x750 and <= 0x77F or >= 0x8A0 and <= 0x8FF or
                >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF => 33,
            >= 0x590 and <= 0x5FF or >= 0xFB1D and <= 0xFB4F => 34,
            >= 0x900 and <= 0x97F => 35,
            _ => 36
        };
    }

    internal int FirstAvailable => FindAvailable(0, 1);

    private int FindAvailable(int start, int delta)
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            var index = (start + delta * i + Groups.Count) % Groups.Count;
            if (Groups[index].IsAvailable) return index;
        }
        return -1;
    }

    internal int Move(int current, int dx, int dy)
    {
        if (current < 0 || current >= Groups.Count) return FirstAvailable;
        if (dy == 0) return FindAvailable((current + dx + Groups.Count) % Groups.Count, dx < 0 ? -1 : 1);
        var rows = (Groups.Count + Columns - 1) / Columns;
        for (var distance = 1; distance < rows; distance++)
        {
            var row = (current / Columns + dy * distance + rows) % rows;
            var best = -1;
            var nearest = int.MaxValue;
            for (var col = 0; col < Columns; col++)
            {
                var index = row * Columns + col;
                if (index >= Groups.Count || !Groups[index].IsAvailable) continue;
                var offset = Math.Abs(col - current % Columns);
                if (offset < nearest) { best = index; nearest = offset; }
            }
            if (best >= 0) return best;
        }
        return current;
    }
}

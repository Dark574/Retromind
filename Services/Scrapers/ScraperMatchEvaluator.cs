using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public enum ScraperMatchStatus
{
    Match,
    Ambiguous,
    NoMatch
}

public sealed record ScraperMatchDecision(
    ScraperMatchStatus Status,
    ScraperSearchResult? BestCandidate,
    double Score,
    ScraperSearchResult? RunnerUpCandidate,
    double? RunnerUpScore);

/// <summary>
/// Selects only sufficiently reliable automatic matches from scraper search results.
/// Provider ordering is retained as a final tie-breaker, but never establishes confidence by itself.
/// </summary>
public static class ScraperMatchEvaluator
{
    private const double MinimumAcceptedScore = 0.88;
    private const double MinimumLeadOverRunnerUp = 0.06;
    private const int ShortTitleLength = 4;

    private static readonly string[] EditionSuffixes =
    [
        " game of the year edition",
        " game of the year",
        " collectors edition",
        " collector edition",
        " definitive edition",
        " complete edition",
        " enhanced edition",
        " special edition",
        " deluxe edition",
        " gold edition",
        " goty edition",
        " directors cut",
        " director cut",
        " remastered",
        " goty"
    ];

    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["ii"] = "2",
        ["iii"] = "3",
        ["iv"] = "4",
        ["v"] = "5",
        ["vi"] = "6",
        ["vii"] = "7",
        ["viii"] = "8",
        ["ix"] = "9",
        ["x"] = "10"
    };

    public static ScraperMatchDecision SelectBestMatch(
        string title,
        IReadOnlyList<ScraperSearchResult> results,
        string? platform = null,
        DateTime? releaseDate = null)
    {
        if (string.IsNullOrWhiteSpace(title) || results.Count == 0)
            return new ScraperMatchDecision(ScraperMatchStatus.NoMatch, null, 0, null, null);

        var normalizedTitle = NormalizeTitle(title);
        if (normalizedTitle.Length == 0)
            return new ScraperMatchDecision(ScraperMatchStatus.NoMatch, null, 0, null, null);

        var scored = results
            .Select((result, index) => new ScoredResult(
                result,
                ScoreCandidate(normalizedTitle, result, platform, releaseDate),
                index))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.ProviderOrder)
            .ToList();

        var best = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1] : null;
        var runnerUpScore = runnerUp?.Score;
        if (best.Score < MinimumAcceptedScore)
        {
            return new ScraperMatchDecision(
                ScraperMatchStatus.NoMatch,
                best.Result,
                best.Score,
                runnerUp?.Result,
                runnerUpScore);
        }

        if (runnerUpScore.HasValue && best.Score - runnerUpScore.Value < MinimumLeadOverRunnerUp)
        {
            return new ScraperMatchDecision(
                ScraperMatchStatus.Ambiguous,
                best.Result,
                best.Score,
                runnerUp?.Result,
                runnerUpScore);
        }

        return new ScraperMatchDecision(
            ScraperMatchStatus.Match,
            best.Result,
            best.Score,
            runnerUp?.Result,
            runnerUpScore);
    }

    private static double ScoreCandidate(
        string normalizedTitle,
        ScraperSearchResult result,
        string? platform,
        DateTime? releaseDate)
    {
        var normalizedCandidate = NormalizeTitle(result.Title);
        if (normalizedCandidate.Length == 0)
            return 0;

        var titleScore = ScoreTitles(normalizedTitle, normalizedCandidate);
        if (titleScore <= 0)
            return 0;

        var score = titleScore;

        if (PlatformsMatch(platform, result.Platform))
            score += 0.04;

        if (releaseDate.HasValue && result.ReleaseDate.HasValue)
        {
            var yearDifference = Math.Abs(releaseDate.Value.Year - result.ReleaseDate.Value.Year);
            score += yearDifference switch
            {
                0 => 0.04,
                1 => 0.02,
                > 2 => -0.04,
                _ => 0
            };
        }

        return score;
    }

    private static double ScoreTitles(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1.0;

        var leftCore = StripEditionSuffix(left);
        var rightCore = StripEditionSuffix(right);
        if (string.Equals(leftCore, rightCore, StringComparison.Ordinal))
            return 0.97;

        // Very short titles are too ambiguous for fuzzy automatic matching.
        if (leftCore.Replace(" ", string.Empty, StringComparison.Ordinal).Length <= ShortTitleLength ||
            rightCore.Replace(" ", string.Empty, StringComparison.Ordinal).Length <= ShortTitleLength)
        {
            return 0;
        }

        var leftTokens = leftCore.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = rightCore.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var editSimilarity = CalculateEditSimilarity(leftCore, rightCore);
        var tokenSimilarity = CalculateTokenDiceCoefficient(leftTokens, rightTokens);
        var score = editSimilarity * 0.55 + tokenSimilarity * 0.45;
        var leftNumbers = GetNumericTokens(leftTokens);
        var rightNumbers = GetNumericTokens(rightTokens);

        if (Math.Min(leftTokens.Length, rightTokens.Length) >= 2 &&
            leftNumbers.Count > 0 &&
            leftNumbers.SetEquals(rightNumbers) &&
            IsTokenSubset(leftTokens, rightTokens))
        {
            // Covers numbered titles with subtitle differences such as
            // "The Witcher 3" and "The Witcher 3 Wild Hunt". An unrestricted
            // subset rule would incorrectly equate titles such as "Super Mario"
            // and "Super Mario Kart".
            score = Math.Max(score, 0.90);
        }

        if (!leftNumbers.SetEquals(rightNumbers))
        {
            // A differing sequel number is a strong signal that this is another game.
            score -= leftNumbers.Count > 0 && rightNumbers.Count > 0 ? 0.35 : 0.22;
        }

        return Math.Max(0, score);
    }

    private static string NormalizeTitle(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character == '&')
            {
                builder.Append(" and ");
            }
            else if (character is '\'' or '\u2019')
            {
                // Possessive apostrophes should not split a word: "Meier's" -> "meiers".
            }
            else
            {
                builder.Append(' ');
            }
        }

        var tokens = builder
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => RomanNumerals.TryGetValue(token, out var number) ? number : token);

        return string.Join(' ', tokens);
    }

    private static string StripEditionSuffix(string title)
    {
        var result = title;
        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in EditionSuffixes)
            {
                if (!result.EndsWith(suffix, StringComparison.Ordinal) || result.Length <= suffix.Length)
                    continue;

                result = result[..^suffix.Length].TrimEnd();
                stripped = true;
                break;
            }
        }
        while (stripped);

        return result;
    }

    private static bool IsTokenSubset(string[] left, string[] right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        return leftSet.IsSubsetOf(rightSet) || rightSet.IsSubsetOf(leftSet);
    }

    private static HashSet<string> GetNumericTokens(IEnumerable<string> tokens)
    {
        return tokens
            .Where(token => token.All(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double CalculateTokenDiceCoefficient(string[] left, string[] right)
    {
        var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
        var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
        if (leftSet.Count == 0 || rightSet.Count == 0)
            return 0;

        var intersectionCount = leftSet.Count(rightSet.Contains);
        return 2d * intersectionCount / (leftSet.Count + rightSet.Count);
    }

    private static double CalculateEditSimilarity(string left, string right)
    {
        var distance = CalculateLevenshteinDistance(left, right);
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }

    private static int CalculateLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool PlatformsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftTokens = NormalizeTitle(left).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = NormalizeTitle(right).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
            return false;

        var leftSet = new HashSet<string>(leftTokens, StringComparer.Ordinal);
        return rightTokens.Any(leftSet.Contains);
    }

    private sealed record ScoredResult(ScraperSearchResult Result, double Score, int ProviderOrder);
}

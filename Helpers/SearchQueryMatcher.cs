using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Retromind.Models;

namespace Retromind.Helpers;

public sealed class SearchQueryMatcher
{
    private readonly QueryNode? _root;

    private SearchQueryMatcher(bool isActive, QueryNode? root)
    {
        IsActive = isActive;
        _root = root;
    }

    public bool IsActive { get; }

    public static SearchQueryMatcher Create(string? query)
    {
        var normalized = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new SearchQueryMatcher(false, null);

        var tokens = Tokenize(normalized);
        if (tokens.Count == 0)
            return new SearchQueryMatcher(false, null);

        var expanded = InsertImplicitAnd(tokens);
        if (!TryParseExpression(expanded, out var root))
        {
            // Keep the legacy fallback robust for malformed expressions.
            return new SearchQueryMatcher(true, new TermNode(new QueryTerm(QueryTermKind.Contains, QueryField.Title, normalized)));
        }

        return new SearchQueryMatcher(true, root);
    }

    public bool Matches(MediaItem item)
    {
        if (!IsActive || _root == null)
            return true;

        return _root.Evaluate(item);
    }

    private enum QueryTokenType
    {
        Term,
        And,
        Or,
        Not,
        LParen,
        RParen
    }

    private enum QueryTermKind
    {
        Contains,
        Has,
        Missing,
        AlwaysFalse
    }

    private enum QueryField
    {
        Title,
        SortTitle,
        Description,
        Developer,
        Publisher,
        Platform,
        Source,
        Genre,
        Series,
        ReleaseType,
        PlayMode,
        MaxPlayers,
        Status,
        Year,
        ReleaseDate,
        Tag,
        CustomAny,
        CustomKey,
        CustomValue,
        CustomNamedKey,
        Id,
        Favorite,
        Rating
    }

    private sealed record QueryToken(QueryTokenType Type, string? Text = null, bool Quoted = false);
    private sealed record QueryTerm(QueryTermKind Kind, QueryField Field, string? Value = null, string? CustomKey = null);

    private abstract record QueryNode
    {
        public abstract bool Evaluate(MediaItem item);
    }

    private sealed record TermNode(QueryTerm Term) : QueryNode
    {
        public override bool Evaluate(MediaItem item) => EvaluateTerm(item, Term);
    }

    private sealed record NotNode(QueryNode Inner) : QueryNode
    {
        public override bool Evaluate(MediaItem item) => !Inner.Evaluate(item);
    }

    private sealed record AndNode(QueryNode Left, QueryNode Right) : QueryNode
    {
        public override bool Evaluate(MediaItem item) => Left.Evaluate(item) && Right.Evaluate(item);
    }

    private sealed record OrNode(QueryNode Left, QueryNode Right) : QueryNode
    {
        public override bool Evaluate(MediaItem item) => Left.Evaluate(item) || Right.Evaluate(item);
    }

    private static bool TryParseExpression(IReadOnlyList<QueryToken> tokens, out QueryNode root)
    {
        var parser = new Parser(tokens);
        return parser.TryParse(out root);
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<QueryToken> _tokens;
        private int _index;

        public Parser(IReadOnlyList<QueryToken> tokens)
        {
            _tokens = tokens;
        }

        public bool TryParse(out QueryNode root)
        {
            root = null!;
            if (!TryParseOr(out root))
                return false;

            return _index == _tokens.Count;
        }

        private bool TryParseOr(out QueryNode node)
        {
            if (!TryParseAnd(out node))
                return false;

            while (Match(QueryTokenType.Or))
            {
                if (!TryParseAnd(out var right))
                    return false;

                node = new OrNode(node, right);
            }

            return true;
        }

        private bool TryParseAnd(out QueryNode node)
        {
            if (!TryParseUnary(out node))
                return false;

            while (Match(QueryTokenType.And))
            {
                if (!TryParseUnary(out var right))
                    return false;

                node = new AndNode(node, right);
            }

            return true;
        }

        private bool TryParseUnary(out QueryNode node)
        {
            if (Match(QueryTokenType.Not))
            {
                if (!TryParseUnary(out var inner))
                {
                    node = null!;
                    return false;
                }

                node = new NotNode(inner);
                return true;
            }

            return TryParsePrimary(out node);
        }

        private bool TryParsePrimary(out QueryNode node)
        {
            if (Match(QueryTokenType.LParen))
            {
                if (!TryParseOr(out node))
                    return false;

                return Match(QueryTokenType.RParen);
            }

            if (!TryReadTerm(out var term))
            {
                node = null!;
                return false;
            }

            node = new TermNode(term);
            return true;
        }

        private bool TryReadTerm(out QueryTerm term)
        {
            term = null!;
            if (_index >= _tokens.Count)
                return false;

            var token = _tokens[_index];
            if (token.Type != QueryTokenType.Term || string.IsNullOrWhiteSpace(token.Text))
                return false;

            _index++;
            term = ParseTerm(token.Text);
            return true;
        }

        private bool Match(QueryTokenType type)
        {
            if (_index >= _tokens.Count || _tokens[_index].Type != type)
                return false;

            _index++;
            return true;
        }
    }

    private static QueryTerm ParseTerm(string tokenText)
    {
        if (!TrySplitKeyValue(tokenText, out var rawKey, out var rawValue))
        {
            return new QueryTerm(QueryTermKind.Contains, QueryField.Title, tokenText);
        }

        if (rawKey.Equals("has", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseField(rawValue, out var hasField, out var hasCustomKey))
                return new QueryTerm(QueryTermKind.AlwaysFalse, QueryField.Title);

            return new QueryTerm(QueryTermKind.Has, hasField, null, hasCustomKey);
        }

        if (rawKey.Equals("missing", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseField(rawValue, out var missingField, out var missingCustomKey))
                return new QueryTerm(QueryTermKind.AlwaysFalse, QueryField.Title);

            return new QueryTerm(QueryTermKind.Missing, missingField, null, missingCustomKey);
        }

        if (TryParseField(rawKey, out var field, out var customKey))
            return new QueryTerm(QueryTermKind.Contains, field, rawValue, customKey);

        // Unknown key:value pair -> keep robust behavior by treating it as a title token.
        return new QueryTerm(QueryTermKind.Contains, QueryField.Title, tokenText);
    }

    private static bool TrySplitKeyValue(string token, out string rawKey, out string rawValue)
    {
        rawKey = string.Empty;
        rawValue = string.Empty;

        var separator = token.IndexOfAny([':', '=']);
        if (separator <= 0 || separator >= token.Length - 1)
            return false;

        rawKey = token[..separator].Trim();
        rawValue = token[(separator + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(rawKey) && !string.IsNullOrWhiteSpace(rawValue);
    }

    private static List<QueryToken> Tokenize(string query)
    {
        var tokens = new List<QueryToken>();
        var current = new StringBuilder();
        var inQuotes = false;
        var currentContainsQuotedText = false;

        void FlushCurrent()
        {
            if (current.Length == 0)
                return;

            var text = current.ToString();
            tokens.Add(ToToken(text, currentContainsQuotedText));
            current.Clear();
            currentContainsQuotedText = false;
        }

        foreach (var ch in query)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                currentContainsQuotedText = true;
                continue;
            }

            if (!inQuotes && (ch == '(' || ch == ')'))
            {
                FlushCurrent();
                tokens.Add(new QueryToken(ch == '(' ? QueryTokenType.LParen : QueryTokenType.RParen));
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                FlushCurrent();
                continue;
            }

            current.Append(ch);
        }

        FlushCurrent();
        return tokens;
    }

    private static QueryToken ToToken(string text, bool quoted)
    {
        if (!quoted)
        {
            if (text.Equals("AND", StringComparison.OrdinalIgnoreCase))
                return new QueryToken(QueryTokenType.And);
            if (text.Equals("OR", StringComparison.OrdinalIgnoreCase))
                return new QueryToken(QueryTokenType.Or);
            if (text.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                return new QueryToken(QueryTokenType.Not);
        }

        return new QueryToken(QueryTokenType.Term, text, quoted);
    }

    private static List<QueryToken> InsertImplicitAnd(IReadOnlyList<QueryToken> tokens)
    {
        var result = new List<QueryToken>(tokens.Count + 8);
        for (var i = 0; i < tokens.Count; i++)
        {
            var current = tokens[i];
            result.Add(current);

            if (i == tokens.Count - 1)
                continue;

            var next = tokens[i + 1];
            if (CanEndOperand(current) && CanStartOperand(next))
                result.Add(new QueryToken(QueryTokenType.And));
        }

        return result;
    }

    private static bool CanEndOperand(QueryToken token)
        => token.Type is QueryTokenType.Term or QueryTokenType.RParen;

    private static bool CanStartOperand(QueryToken token)
        => token.Type is QueryTokenType.Term or QueryTokenType.LParen or QueryTokenType.Not;

    private static bool TryParseField(string rawKey, out QueryField field, out string? customKey)
    {
        field = default;
        customKey = null;

        if (string.IsNullOrWhiteSpace(rawKey))
            return false;

        var key = rawKey.Trim();
        if (key.StartsWith("cf.", StringComparison.OrdinalIgnoreCase) && key.Length > 3)
        {
            field = QueryField.CustomNamedKey;
            customKey = key[3..];
            return !string.IsNullOrWhiteSpace(customKey);
        }

        switch (key.ToLowerInvariant())
        {
            case "title":
            case "t":
                field = QueryField.Title;
                return true;
            case "sort":
            case "sorttitle":
            case "st":
                field = QueryField.SortTitle;
                return true;
            case "desc":
            case "description":
            case "notes":
                field = QueryField.Description;
                return true;
            case "dev":
            case "developer":
                field = QueryField.Developer;
                return true;
            case "pub":
            case "publisher":
                field = QueryField.Publisher;
                return true;
            case "platform":
            case "plat":
                field = QueryField.Platform;
                return true;
            case "source":
            case "src":
                field = QueryField.Source;
                return true;
            case "genre":
                field = QueryField.Genre;
                return true;
            case "series":
                field = QueryField.Series;
                return true;
            case "release":
            case "releasetype":
            case "rt":
                field = QueryField.ReleaseType;
                return true;
            case "mode":
            case "playmode":
                field = QueryField.PlayMode;
                return true;
            case "max":
            case "players":
            case "maxplayers":
                field = QueryField.MaxPlayers;
                return true;
            case "status":
            case "state":
                field = QueryField.Status;
                return true;
            case "year":
                field = QueryField.Year;
                return true;
            case "date":
            case "released":
                field = QueryField.ReleaseDate;
                return true;
            case "tag":
            case "tags":
                field = QueryField.Tag;
                return true;
            case "cf":
            case "custom":
            case "customfield":
                field = QueryField.CustomAny;
                return true;
            case "cfk":
            case "customkey":
                field = QueryField.CustomKey;
                return true;
            case "cfv":
            case "customvalue":
                field = QueryField.CustomValue;
                return true;
            case "id":
                field = QueryField.Id;
                return true;
            case "fav":
            case "favorite":
            case "favourite":
                field = QueryField.Favorite;
                return true;
            case "rating":
            case "score":
                field = QueryField.Rating;
                return true;
            default:
                return false;
        }
    }

    private static bool EvaluateTerm(MediaItem item, QueryTerm term)
    {
        return term.Kind switch
        {
            QueryTermKind.Contains => MatchesContains(item, term),
            QueryTermKind.Has => !IsMissing(item, term.Field, term.CustomKey),
            QueryTermKind.Missing => IsMissing(item, term.Field, term.CustomKey),
            QueryTermKind.AlwaysFalse => false,
            _ => false
        };
    }

    private static bool MatchesContains(MediaItem item, QueryTerm term)
    {
        var value = term.Value ?? string.Empty;
        switch (term.Field)
        {
            case QueryField.Title:
                return ContainsIgnoreCase(item.Title, value);
            case QueryField.SortTitle:
                return ContainsIgnoreCase(item.SortTitle, value);
            case QueryField.Description:
                return ContainsIgnoreCase(item.Description, value);
            case QueryField.Developer:
                return ContainsIgnoreCase(item.Developer, value);
            case QueryField.Publisher:
                return ContainsIgnoreCase(item.Publisher, value);
            case QueryField.Platform:
                return ContainsIgnoreCase(item.Platform, value);
            case QueryField.Source:
                return ContainsIgnoreCase(item.Source, value);
            case QueryField.Genre:
                return ContainsIgnoreCase(item.Genre, value);
            case QueryField.Series:
                return ContainsIgnoreCase(item.Series, value);
            case QueryField.ReleaseType:
                return ContainsIgnoreCase(item.ReleaseType, value);
            case QueryField.PlayMode:
                return ContainsIgnoreCase(item.PlayMode, value);
            case QueryField.MaxPlayers:
                return MatchesMaxPlayers(item.MaxPlayers, value);
            case QueryField.Status:
                return MatchesStatus(item.Status, value);
            case QueryField.Year:
                return MatchesYear(item.ReleaseDate, value);
            case QueryField.ReleaseDate:
                return item.ReleaseDate.HasValue &&
                       ContainsIgnoreCase(item.ReleaseDate.Value.ToString("yyyy-MM-dd"), value);
            case QueryField.Tag:
                return item.Tags.Any(tag => ContainsIgnoreCase(tag, value));
            case QueryField.CustomAny:
                return MatchesCustomAny(item.CustomFields, value);
            case QueryField.CustomKey:
                return item.CustomFields.Keys.Any(key => ContainsIgnoreCase(key, value));
            case QueryField.CustomValue:
                return item.CustomFields.Values.Any(v => ContainsIgnoreCase(v, value));
            case QueryField.CustomNamedKey:
                return MatchesCustomNamedKey(item.CustomFields, term.CustomKey, value);
            case QueryField.Id:
                return ContainsIgnoreCase(item.Id, value);
            case QueryField.Favorite:
                return MatchesFavorite(item.IsFavorite, value);
            case QueryField.Rating:
                return MatchesRating(item.Rating, value);
            default:
                return false;
        }
    }

    private static bool IsMissing(MediaItem item, QueryField field, string? customKey)
    {
        switch (field)
        {
            case QueryField.Title:
                return string.IsNullOrWhiteSpace(item.Title);
            case QueryField.SortTitle:
                return string.IsNullOrWhiteSpace(item.SortTitle);
            case QueryField.Description:
                return string.IsNullOrWhiteSpace(item.Description);
            case QueryField.Developer:
                return string.IsNullOrWhiteSpace(item.Developer);
            case QueryField.Publisher:
                return string.IsNullOrWhiteSpace(item.Publisher);
            case QueryField.Platform:
                return string.IsNullOrWhiteSpace(item.Platform);
            case QueryField.Source:
                return string.IsNullOrWhiteSpace(item.Source);
            case QueryField.Genre:
                return string.IsNullOrWhiteSpace(item.Genre);
            case QueryField.Series:
                return string.IsNullOrWhiteSpace(item.Series);
            case QueryField.ReleaseType:
                return string.IsNullOrWhiteSpace(item.ReleaseType);
            case QueryField.PlayMode:
                return string.IsNullOrWhiteSpace(item.PlayMode);
            case QueryField.MaxPlayers:
                return string.IsNullOrWhiteSpace(item.MaxPlayers);
            case QueryField.Status:
                return false;
            case QueryField.Year:
            case QueryField.ReleaseDate:
                return !item.ReleaseDate.HasValue;
            case QueryField.Tag:
                return item.Tags.Count == 0 || !item.Tags.Any(v => !string.IsNullOrWhiteSpace(v));
            case QueryField.CustomAny:
                return item.CustomFields.Count == 0;
            case QueryField.CustomKey:
                return item.CustomFields.Count == 0 || !item.CustomFields.Keys.Any(v => !string.IsNullOrWhiteSpace(v));
            case QueryField.CustomValue:
                return item.CustomFields.Count == 0 || !item.CustomFields.Values.Any(v => !string.IsNullOrWhiteSpace(v));
            case QueryField.CustomNamedKey:
                return IsCustomNamedValueMissing(item.CustomFields, customKey);
            case QueryField.Id:
                return string.IsNullOrWhiteSpace(item.Id);
            case QueryField.Favorite:
                return false;
            case QueryField.Rating:
                return item.Rating <= 0d;
            default:
                return false;
        }
    }

    private static bool IsCustomNamedValueMissing(Dictionary<string, string> customFields, string? customKey)
    {
        if (customFields.Count == 0 || string.IsNullOrWhiteSpace(customKey))
            return true;

        foreach (var pair in customFields)
        {
            if (!string.Equals(pair.Key, customKey, StringComparison.OrdinalIgnoreCase))
                continue;

            return string.IsNullOrWhiteSpace(pair.Value);
        }

        return true;
    }

    private static bool MatchesStatus(PlayStatus status, string rawValue)
    {
        if (Enum.TryParse<PlayStatus>(rawValue, ignoreCase: true, out var parsed))
            return status == parsed;

        return ContainsIgnoreCase(status.ToString(), rawValue);
    }

    private static bool MatchesYear(DateTime? releaseDate, string rawValue)
    {
        if (!releaseDate.HasValue)
            return false;

        if (int.TryParse(rawValue, out var year))
            return releaseDate.Value.Year == year;

        return ContainsIgnoreCase(releaseDate.Value.ToString("yyyy-MM-dd"), rawValue);
    }

    private static bool MatchesFavorite(bool isFavorite, string rawValue)
    {
        var normalized = rawValue.Trim().ToLowerInvariant();
        if (normalized is "1" or "true" or "yes" or "y")
            return isFavorite;
        if (normalized is "0" or "false" or "no" or "n")
            return !isFavorite;

        var favoriteText = isFavorite ? "true" : "false";
        return ContainsIgnoreCase(favoriteText, rawValue);
    }

    private static bool MatchesMaxPlayers(string? maxPlayers, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(maxPlayers))
            return false;

        if (int.TryParse(rawValue, out var expected) &&
            int.TryParse(maxPlayers.Trim(), out var actual))
        {
            return actual == expected;
        }

        return ContainsIgnoreCase(maxPlayers, rawValue);
    }

    private static bool MatchesCustomAny(Dictionary<string, string>? customFields, string rawValue)
    {
        if (customFields == null || customFields.Count == 0)
            return false;

        foreach (var kv in customFields)
        {
            if (ContainsIgnoreCase(kv.Key, rawValue) || ContainsIgnoreCase(kv.Value, rawValue))
                return true;
        }

        return false;
    }

    private static bool MatchesCustomNamedKey(Dictionary<string, string>? customFields, string? customKey, string rawValue)
    {
        if (customFields == null || customFields.Count == 0 || string.IsNullOrWhiteSpace(customKey))
            return false;

        foreach (var kv in customFields)
        {
            if (!string.Equals(kv.Key, customKey, StringComparison.OrdinalIgnoreCase))
                continue;

            return ContainsIgnoreCase(kv.Value, rawValue);
        }

        return false;
    }

    private static bool MatchesRating(double rating, string rawValue)
    {
        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
            return Math.Abs(rating - expected) < 0.0001d;

        return ContainsIgnoreCase(rating.ToString("0.##", CultureInfo.InvariantCulture), rawValue);
    }

    private static bool ContainsIgnoreCase(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

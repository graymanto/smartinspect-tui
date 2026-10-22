using System.Text;
using FuzzySharp;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Packets;

namespace SmartInspectTui.Filtering;

/// <summary>
/// Evaluates <see cref="LogEntry"/> instances against a <see cref="LogFilterQuery"/>,
/// supporting multiple field-specific filter clauses, inline property tags, negation,
/// level badges (e.g. DBG, INF, WRN, ERR, FTL), minimum severity levels, and fuzzy matching.
/// </summary>
public sealed class LogFilterEngine
{
    private const int MaxPayloadFuzzyLength = 4096;

    private readonly LogFilterQuery _query;
    private readonly string[] _appFilterValues;
    private readonly string[] _sessionFilterValues;
    private readonly List<ParsedSearchToken> _andTokens = [];
    private readonly Dictionary<LogFilterField, List<ParsedSearchToken>> _orFieldGroups = [];

    public LogFilterEngine(LogFilterQuery query)
    {
        _query = query ?? new LogFilterQuery();
        _appFilterValues = SplitCsv(_query.AppNameFilter);
        _sessionFilterValues = SplitCsv(_query.SessionFilter);

        var allTokens = new List<ParsedSearchToken>();

        // 1. Add pinned filter clauses
        foreach (var clause in _query.Clauses)
        {
            if (string.IsNullOrWhiteSpace(clause.Value))
            {
                continue;
            }

            allTokens.AddRange(ParseTokensForField(clause.Field, clause.Value, clause.Negate));
        }

        // 2. Add live search text for the currently selected ActiveField
        if (!string.IsNullOrWhiteSpace(_query.SearchText))
        {
            allTokens.AddRange(
                ParseTokensForField(_query.ActiveField, _query.SearchText, forceNegate: false)
            );
        }

        // Group positive discrete-field tokens by field so adding e.g. [Lvl: DBG] and [Lvl: ERR]
        // acts like an Excel multi-select (DBG OR ERR), while different fields and Any/Title/Data
        // or negated tokens combine with AND.
        foreach (var token in allTokens)
        {
            if (!token.Negate && token.Field.SupportsAutocomplete())
            {
                if (!_orFieldGroups.TryGetValue(token.Field, out var group))
                {
                    group = [];
                    _orFieldGroups[token.Field] = group;
                }

                group.Add(token);
            }
            else
            {
                _andTokens.Add(token);
            }
        }
    }

    public LogFilterQuery Query => _query;

    // FTS is only a candidate accelerator. The existing evaluator remains authoritative.
    // Non-ASCII titles bypass the accelerator to preserve OrdinalIgnoreCase semantics.
    internal string? GetIndexedTitleMatch()
    {
        if (_query.EnableFuzzy)
            return null;
        var groups = _andTokens
            .Where(t =>
                t.Field == LogFilterField.Title
                && !t.Negate
                && t.Values.Length > 0
                && t.Values.All(v => v.Length >= 3 && v.All(c => c is >= ' ' and <= '~'))
            )
            .Select(t =>
                "("
                + string.Join(" OR ", t.Values.Select(v => "\"" + v.Replace("\"", "\"\"") + "\""))
                + ")"
            );
        var match = string.Join(" AND ", groups);
        return match.Length == 0 ? null : match;
    }

    public bool Matches(LogEntry entry)
    {
        if (
            _query.MinLogLevel.HasValue
            && !PassesLogLevelFilter(entry.LogEntryType, _query.MinLogLevel.Value)
        )
        {
            return false;
        }

        if (_appFilterValues.Length > 0 && !MatchesMultiValueField(_appFilterValues, entry.AppName))
        {
            return false;
        }

        if (
            _sessionFilterValues.Length > 0
            && !MatchesMultiValueField(_sessionFilterValues, entry.SessionName)
        )
        {
            return false;
        }

        foreach (var group in _orFieldGroups.Values)
        {
            var anyInFieldMatched = false;
            foreach (var token in group)
            {
                if (EvaluateToken(entry, token))
                {
                    anyInFieldMatched = true;
                    break;
                }
            }

            if (!anyInFieldMatched)
            {
                return false;
            }
        }

        foreach (var token in _andTokens)
        {
            var matched = EvaluateToken(entry, token);
            if (token.Negate)
            {
                matched = !matched;
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    public List<LogEntry> FilterEntries(IEnumerable<LogEntry> entries)
    {
        if (_query.IsEmpty)
        {
            return entries is List<LogEntry> list ? [.. list] : entries.ToList();
        }

        var result = new List<LogEntry>();
        foreach (var entry in entries)
        {
            if (Matches(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private bool EvaluateToken(LogEntry entry, ParsedSearchToken token)
    {
        if (string.IsNullOrEmpty(token.Value))
        {
            return true;
        }

        var values = token.Values;

        return token.Field switch
        {
            LogFilterField.Any => MatchesAnyProperty(entry, token.Value),
            LogFilterField.App => MatchesMultiValueField(values, entry.AppName),
            LogFilterField.Session => MatchesMultiValueField(values, entry.SessionName),
            LogFilterField.Host => MatchesMultiValueField(values, entry.HostName),
            LogFilterField.Title => MatchesMultiValueField(values, entry.Title),
            LogFilterField.Data => entry.DataAsString is { Length: > 0 } data
                && MatchesMultiValueField(values, data),
            LogFilterField.ProcessId => MatchesExactOrContains(values, entry.ProcessId.ToString()),
            LogFilterField.ThreadId => MatchesExactOrContains(values, entry.ThreadId.ToString()),
            LogFilterField.Level => MatchesLevelToken(entry.LogEntryType, values),
            _ => false,
        };
    }

    private bool MatchesAnyProperty(LogEntry entry, string term)
    {
        var levelBadge = LogFilterFieldExtensions.GetLevelBadge(entry.LogEntryType);

        // 1. Fast case-insensitive substring check across all fields (including Lvl badge like DBG)
        if (
            entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.AppName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.SessionName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.HostName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || levelBadge.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.LogEntryType.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.ProcessId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.ThreadId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        var dataText = entry.DataAsString;
        if (
            !string.IsNullOrEmpty(dataText)
            && dataText.Contains(term, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (!_query.EnableFuzzy || term.Length < 2)
        {
            return false;
        }

        // 2. Fuzzy match across primary text properties
        return MatchesFuzzyText(entry.Title, term)
            || MatchesFuzzyText(entry.AppName, term)
            || MatchesFuzzyText(entry.SessionName, term)
            || MatchesFuzzyText(entry.HostName, term)
            || (!string.IsNullOrEmpty(dataText) && MatchesFuzzyPayload(dataText, term));
    }

    private bool MatchesMultiValueField(string[] filterValues, string candidate)
    {
        if (filterValues.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        foreach (var val in filterValues)
        {
            if (candidate.Contains(val, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_query.EnableFuzzy && val.Length >= 2 && MatchesFuzzyText(candidate, val))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesExactOrContains(string[] filterValues, string candidate)
    {
        foreach (var val in filterValues)
        {
            if (candidate.Contains(val, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesLevelToken(LogEntryType entryType, string[] values)
    {
        var typeName = entryType.ToString();
        var badge = LogFilterFieldExtensions.GetLevelBadge(entryType);

        foreach (var raw in values)
        {
            if (raw.EndsWith('+'))
            {
                var minLevelName = raw[..^1];
                if (
                    TryParseLevelAlias(minLevelName, out var minLevel)
                    && PassesLogLevelFilter(entryType, minLevel)
                )
                {
                    return true;
                }
            }
            else if (TryParseLevelAlias(raw, out var exactLevel) && entryType == exactLevel)
            {
                return true;
            }
            else if (
                badge.Contains(raw, StringComparison.OrdinalIgnoreCase)
                || typeName.Contains(raw, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseLevelAlias(string text, out LogEntryType level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "dbg":
            case "debug":
                level = LogEntryType.Debug;
                return true;
            case "vrb":
            case "verbose":
            case "trace":
                level = LogEntryType.Verbose;
                return true;
            case "inf":
            case "info":
            case "msg":
            case "message":
                level = LogEntryType.Message;
                return true;
            case "wrn":
            case "warn":
            case "warning":
                level = LogEntryType.Warning;
                return true;
            case "err":
            case "error":
                level = LogEntryType.Error;
                return true;
            case "ftl":
            case "fatal":
            case "crit":
            case "critical":
                level = LogEntryType.Fatal;
                return true;
            case ">>":
            case "enter":
            case "entermethod":
                level = LogEntryType.EnterMethod;
                return true;
            case "<<":
            case "leave":
            case "leavemethod":
                level = LogEntryType.LeaveMethod;
                return true;
            case "---":
            case "sep":
            case "separator":
                level = LogEntryType.Separator;
                return true;
            case "chk":
            case "checkpoint":
                level = LogEntryType.Checkpoint;
                return true;
            case "ast":
            case "assert":
                level = LogEntryType.Assert;
                return true;
            case "cmt":
            case "comment":
                level = LogEntryType.Comment;
                return true;
            default:
                return Enum.TryParse(text, ignoreCase: true, out level);
        }
    }

    private bool MatchesFuzzyText(string candidate, string queryTerm)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (IsOrderedSubsequence(candidate, queryTerm))
        {
            return true;
        }

        var lowerCandidate = candidate.ToLowerInvariant();
        var lowerQuery = queryTerm.ToLowerInvariant();
        var score = Fuzz.PartialRatio(lowerQuery, lowerCandidate);
        return score >= _query.FuzzyThreshold;
    }

    private bool MatchesFuzzyPayload(string payload, string queryTerm)
    {
        var slice =
            payload.Length > MaxPayloadFuzzyLength ? payload[..MaxPayloadFuzzyLength] : payload;

        return MatchesFuzzyText(slice, queryTerm);
    }

    private static bool IsOrderedSubsequence(string candidate, string pattern)
    {
        if (pattern.Length > candidate.Length)
        {
            return false;
        }

        var patternIdx = 0;
        var matchedWordStartOrAdjacent = 0;
        var lastMatchIdx = -2;

        for (var i = 0; i < candidate.Length && patternIdx < pattern.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(pattern[patternIdx]))
            {
                if (
                    i == 0
                    || i == lastMatchIdx + 1
                    || !char.IsLetterOrDigit(candidate[i - 1])
                    || (char.IsUpper(candidate[i]) && char.IsLower(candidate[i - 1]))
                )
                {
                    matchedWordStartOrAdjacent++;
                }

                lastMatchIdx = i;
                patternIdx++;
            }
        }

        if (patternIdx < pattern.Length)
        {
            return false;
        }

        return matchedWordStartOrAdjacent * 10 >= pattern.Length * 6;
    }

    public static bool PassesLogLevelFilter(LogEntryType entryType, LogEntryType minLevel)
    {
        static int GetSeverity(LogEntryType type) =>
            type switch
            {
                LogEntryType.Debug => 1,
                LogEntryType.Verbose => 2,
                LogEntryType.Message => 3,
                LogEntryType.Warning => 4,
                LogEntryType.Error => 5,
                LogEntryType.Fatal => 6,
                LogEntryType.EnterMethod or LogEntryType.LeaveMethod => 3,
                LogEntryType.Separator => 3,
                _ => 3,
            };

        return GetSeverity(entryType) >= GetSeverity(minLevel);
    }

    private static string[] SplitCsv(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    }

    internal static List<ParsedSearchToken> ParseTokensForField(
        LogFilterField defaultField,
        string? rawText,
        bool forceNegate
    )
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return [];
        }

        // If a specific field is selected (not Any), treat the entire input (or comma-separated values)
        // as targeting that field directly, while still honoring a leading '-' or '!' for negation.
        if (defaultField != LogFilterField.Any)
        {
            var trimmed = rawText.Trim();
            var negate = forceNegate;
            if ((trimmed.StartsWith('-') || trimmed.StartsWith('!')) && trimmed.Length > 1)
            {
                negate = !negate;
                trimmed = trimmed[1..].TrimStart();
            }

            var unquoted = Unquote(trimmed);
            return [new ParsedSearchToken(defaultField, unquoted, SplitCsv(unquoted), negate)];
        }

        var result = new List<ParsedSearchToken>();
        var rawTokens = TokenizeRespectingQuotes(rawText);
        foreach (var raw in rawTokens)
        {
            var current = raw.Trim();
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            var negate = forceNegate;
            if ((current.StartsWith('-') || current.StartsWith('!')) && current.Length > 1)
            {
                negate = !negate;
                current = current[1..];
            }

            var colonIdx = current.IndexOf(':');
            if (colonIdx > 0 && colonIdx < current.Length - 1)
            {
                var prefix = current[..colonIdx];
                var value = Unquote(current[(colonIdx + 1)..]);
                if (
                    LogFilterFieldExtensions.TryParseFieldPrefix(prefix, out var parsedField)
                    && parsedField != LogFilterField.Any
                )
                {
                    result.Add(new ParsedSearchToken(parsedField, value, SplitCsv(value), negate));
                    continue;
                }
            }

            var unquoted = Unquote(current);
            result.Add(new ParsedSearchToken(LogFilterField.Any, unquoted, [unquoted], negate));
        }

        return result;
    }

    private static List<string> TokenizeRespectingQuotes(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        return value;
    }

    internal sealed record ParsedSearchToken(
        LogFilterField Field,
        string Value,
        string[] Values,
        bool Negate
    );
}

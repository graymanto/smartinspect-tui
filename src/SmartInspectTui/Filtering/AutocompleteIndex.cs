using SmartInspectConsole.Core.Packets;

namespace SmartInspectTui.Filtering;

/// <summary>
/// Maintains a fast, bounded, in-memory index of distinct property values seen across
/// ingested log entries to power instantaneous Tab autocomplete without scanning the log store.
/// </summary>
public sealed class AutocompleteIndex
{
    private const int MaxDistinctValuesPerField = 2000;

    private static readonly string[] BuiltInLevels =
    [
        "DBG",
        "INF",
        "WRN",
        "ERR",
        "FTL",
        "VRB",
        "Debug+",
        "Verbose+",
        "Message+",
        "Warning+",
        "Error+",
        "Debug",
        "Verbose",
        "Message",
        "Warning",
        "Error",
        "Fatal",
        "EnterMethod",
        "LeaveMethod",
        "Separator",
        "Checkpoint",
        "Assert",
        "Comment",
    ];

    private readonly object _sync = new();
    private readonly HashSet<string> _levels = new(BuiltInLevels, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public void IndexEntry(LogEntry entry)
    {
        lock (_sync)
        {
            var badge = LogFilterFieldExtensions.GetLevelBadge(entry.LogEntryType);
            _levels.Add(badge);
            _levels.Add(entry.LogEntryType.ToString());

            AddBounded(_apps, entry.AppName);
            AddBounded(_sessions, entry.SessionName);
            AddBounded(_hosts, entry.HostName);
            AddBounded(_tids, entry.ThreadId.ToString());
            AddBounded(_pids, entry.ProcessId.ToString());
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _levels.Clear();
            foreach (var lvl in BuiltInLevels)
            {
                _levels.Add(lvl);
            }

            _apps.Clear();
            _sessions.Clear();
            _tids.Clear();
            _pids.Clear();
            _hosts.Clear();
        }
    }

    /// <summary>
    /// Computes autocomplete suggestions for the selected <paramref name="selectedField"/> and
    /// raw <paramref name="inputText"/>. Also supports inline <c>field:value</c> syntax when
    /// <paramref name="selectedField"/> is <see cref="LogFilterField.Any"/>.
    /// </summary>
    public IReadOnlyList<AutocompleteSuggestion> GetSuggestions(
        LogFilterField selectedField,
        string? inputText,
        int maxResults = 8
    )
    {
        var raw = inputText ?? string.Empty;

        // If the user is in "Any" mode and typing a field prefix like "lvl:d" or "app:Au",
        // offer completions for the last token!
        if (selectedField == LogFilterField.Any)
        {
            return GetInlineTokenSuggestions(raw, maxResults);
        }

        if (!selectedField.SupportsAutocomplete())
        {
            return [];
        }

        // Support comma-separated values or leading negation '-' / '!' in a dedicated field
        var lastComma = raw.LastIndexOf(',');
        var prefixBeforeValue = lastComma >= 0 ? raw[..(lastComma + 1)] : string.Empty;
        var currentSegment = lastComma >= 0 ? raw[(lastComma + 1)..].TrimStart() : raw.TrimStart();

        var negationPrefix = string.Empty;
        if (currentSegment.StartsWith('-') || currentSegment.StartsWith('!'))
        {
            negationPrefix = currentSegment[..1];
            currentSegment = currentSegment[1..];
        }

        var candidates = MatchFieldCandidates(selectedField, currentSegment, maxResults);
        return candidates
            .Select(c => new AutocompleteSuggestion(
                DisplayText: c,
                CompletedInput: $"{prefixBeforeValue}{negationPrefix}{c}"
            ))
            .ToList();
    }

    private IReadOnlyList<AutocompleteSuggestion> GetInlineTokenSuggestions(
        string rawInput,
        int maxResults
    )
    {
        if (string.IsNullOrWhiteSpace(rawInput) || rawInput.EndsWith(' '))
        {
            return [];
        }

        var lastSpace = rawInput.LastIndexOf(' ');
        var leadingText = lastSpace >= 0 ? rawInput[..(lastSpace + 1)] : string.Empty;
        var lastToken = lastSpace >= 0 ? rawInput[(lastSpace + 1)..] : rawInput;

        var negation = string.Empty;
        var coreToken = lastToken;
        if ((coreToken.StartsWith('-') || coreToken.StartsWith('!')) && coreToken.Length > 1)
        {
            negation = coreToken[..1];
            coreToken = coreToken[1..];
        }

        var colonIdx = coreToken.IndexOf(':');
        if (colonIdx <= 0)
        {
            return [];
        }

        var fieldPrefix = coreToken[..colonIdx];
        var partialValue = coreToken[(colonIdx + 1)..];

        if (
            !LogFilterFieldExtensions.TryParseFieldPrefix(fieldPrefix, out var targetField)
            || !targetField.SupportsAutocomplete()
        )
        {
            return [];
        }

        var candidates = MatchFieldCandidates(targetField, partialValue, maxResults);
        return candidates
            .Select(c => new AutocompleteSuggestion(
                DisplayText: $"{fieldPrefix}:{c}",
                CompletedInput: $"{leadingText}{negation}{fieldPrefix}:{c}"
            ))
            .ToList();
    }

    private List<string> MatchFieldCandidates(LogFilterField field, string partial, int maxResults)
    {
        List<string> snapshot;
        lock (_sync)
        {
            var source = GetSetForFieldUnsafe(field);
            if (source == null || source.Count == 0)
            {
                return [];
            }

            snapshot = [.. source];
        }

        var term = partial.Trim().Trim('"');
        if (string.IsNullOrEmpty(term))
        {
            return snapshot
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();
        }

        var prefixMatches = new List<string>();
        var containsMatches = new List<string>();

        foreach (var item in snapshot)
        {
            if (item.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                prefixMatches.Add(item);
            }
            else if (item.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                containsMatches.Add(item);
            }
        }

        prefixMatches.Sort(StringComparer.OrdinalIgnoreCase);
        containsMatches.Sort(StringComparer.OrdinalIgnoreCase);

        var combined = new List<string>(maxResults);
        foreach (var item in prefixMatches)
        {
            combined.Add(item);
            if (combined.Count >= maxResults)
            {
                return combined;
            }
        }

        foreach (var item in containsMatches)
        {
            combined.Add(item);
            if (combined.Count >= maxResults)
            {
                return combined;
            }
        }

        return combined;
    }

    private HashSet<string>? GetSetForFieldUnsafe(LogFilterField field) =>
        field switch
        {
            LogFilterField.Level => _levels,
            LogFilterField.App => _apps,
            LogFilterField.Session => _sessions,
            LogFilterField.ThreadId => _tids,
            LogFilterField.ProcessId => _pids,
            LogFilterField.Host => _hosts,
            _ => null,
        };

    private static void AddBounded(HashSet<string> set, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (set.Count < MaxDistinctValuesPerField)
        {
            set.Add(value);
        }
    }
}

public sealed record AutocompleteSuggestion(string DisplayText, string CompletedInput);

using SmartInspectConsole.Core.Enums;

namespace SmartInspectTui.Filtering;

/// <summary>
/// Represents a single active filter rule on a specific field or across all fields.
/// </summary>
public sealed record LogFilterClause(LogFilterField Field, string Value, bool Negate = false)
{
    public string ToDisplayChip()
    {
        var prefix = Negate ? "-" : string.Empty;
        return $"{prefix}{Field.GetDisplayName()}: {Value} ×";
    }
}

/// <summary>
/// Represents the active filter state combining multiple pinned <see cref="LogFilterClause"/> rules,
/// live input field/text, and fuzzy matching settings.
/// </summary>
public sealed record LogFilterQuery
{
    public IReadOnlyList<LogFilterClause> Clauses { get; init; } = [];
    public LogFilterField ActiveField { get; init; } = LogFilterField.Any;
    public string SearchText { get; init; } = string.Empty;
    public string AppNameFilter { get; init; } = string.Empty;
    public string SessionFilter { get; init; } = string.Empty;
    public LogEntryType? MinLogLevel { get; init; }
    public bool EnableFuzzy { get; init; } = true;
    public int FuzzyThreshold { get; init; } = 75;

    public bool IsEmpty =>
        Clauses.Count == 0
        && string.IsNullOrWhiteSpace(SearchText)
        && string.IsNullOrWhiteSpace(AppNameFilter)
        && string.IsNullOrWhiteSpace(SessionFilter)
        && !MinLogLevel.HasValue;
}

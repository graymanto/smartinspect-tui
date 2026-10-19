using SmartInspectConsole.Core.Enums;

namespace SmartInspectTui.Filtering;

/// <summary>
/// Identifies which log entry property a filter targets.
/// </summary>
public enum LogFilterField
{
    Any,
    Level,
    App,
    Session,
    ThreadId,
    ProcessId,
    Host,
    Title,
    Data,
}

public static class LogFilterFieldExtensions
{
    public static readonly LogFilterField[] AllFields =
    [
        LogFilterField.Any,
        LogFilterField.Level,
        LogFilterField.App,
        LogFilterField.Session,
        LogFilterField.ThreadId,
        LogFilterField.ProcessId,
        LogFilterField.Host,
        LogFilterField.Title,
        LogFilterField.Data,
    ];

    public static string GetDisplayName(this LogFilterField field) =>
        field switch
        {
            LogFilterField.Any => "Any",
            LogFilterField.Level => "Lvl",
            LogFilterField.App => "App",
            LogFilterField.Session => "Session",
            LogFilterField.ThreadId => "TID",
            LogFilterField.ProcessId => "PID",
            LogFilterField.Host => "Host",
            LogFilterField.Title => "Title",
            LogFilterField.Data => "Data",
            _ => "Any",
        };

    public static string GetPrefixName(this LogFilterField field) =>
        field switch
        {
            LogFilterField.Level => "lvl",
            LogFilterField.App => "app",
            LogFilterField.Session => "session",
            LogFilterField.ThreadId => "tid",
            LogFilterField.ProcessId => "pid",
            LogFilterField.Host => "host",
            LogFilterField.Title => "title",
            LogFilterField.Data => "data",
            _ => string.Empty,
        };

    /// <summary>
    /// Returns true for discrete fields where Excel-style autocomplete suggestions are useful.
    /// </summary>
    public static bool SupportsAutocomplete(this LogFilterField field) =>
        field
            is LogFilterField.Level
                or LogFilterField.App
                or LogFilterField.Session
                or LogFilterField.ThreadId
                or LogFilterField.ProcessId
                or LogFilterField.Host;

    public static bool TryParseFieldPrefix(string prefix, out LogFilterField field)
    {
        switch (prefix.Trim().ToLowerInvariant())
        {
            case "any":
            case "all":
                field = LogFilterField.Any;
                return true;
            case "lvl":
            case "level":
            case "type":
                field = LogFilterField.Level;
                return true;
            case "app":
            case "application":
                field = LogFilterField.App;
                return true;
            case "sess":
            case "session":
                field = LogFilterField.Session;
                return true;
            case "tid":
            case "thread":
                field = LogFilterField.ThreadId;
                return true;
            case "pid":
            case "process":
                field = LogFilterField.ProcessId;
                return true;
            case "host":
            case "hostname":
                field = LogFilterField.Host;
                return true;
            case "title":
            case "msg":
            case "message":
                field = LogFilterField.Title;
                return true;
            case "data":
            case "payload":
                field = LogFilterField.Data;
                return true;
            default:
                field = LogFilterField.Any;
                return false;
        }
    }

    public static string GetLevelBadge(LogEntryType type) =>
        type switch
        {
            LogEntryType.Debug => "DBG",
            LogEntryType.Verbose => "VRB",
            LogEntryType.Message => "INF",
            LogEntryType.Warning => "WRN",
            LogEntryType.Error => "ERR",
            LogEntryType.Fatal => "FTL",
            LogEntryType.EnterMethod => ">>",
            LogEntryType.LeaveMethod => "<<",
            LogEntryType.Separator => "---",
            LogEntryType.Checkpoint => "CHK",
            LogEntryType.Assert => "AST",
            LogEntryType.Comment => "CMT",
            _ => "LOG",
        };
}

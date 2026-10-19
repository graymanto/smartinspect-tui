using System.Data;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Packets;
using Terminal.Gui;

namespace SmartInspectTui.UI;

/// <summary>
/// Manages the <see cref="DataTable"/> backing the <see cref="TableView"/> for fast incremental
/// batch appends, retention trimming, and full filter refreshes.
/// </summary>
public sealed class LogTableManager
{
    public const int ColLevel = 0;
    public const int ColTime = 1;
    public const int ColElapsed = 2;
    public const int ColApp = 3;
    public const int ColSession = 4;
    public const int ColTitle = 5;
    public const int ColThread = 6;
    public const int ColProcess = 7;
    public const int ColHost = 8;

    private readonly DataTable _dataTable;
    private readonly List<LogEntry> _entries = [];

    private readonly ColorScheme _errorScheme;
    private readonly ColorScheme _fatalScheme;
    private readonly ColorScheme _warningScheme;
    private readonly ColorScheme _debugScheme;
    private readonly ColorScheme _flowScheme;
    private readonly ColorScheme _separatorScheme;
    private readonly ColorScheme _defaultScheme;

    public LogTableManager()
    {
        _dataTable = new DataTable("Logs");
        _dataTable.Columns.Add("Lvl", typeof(string));
        _dataTable.Columns.Add("Time", typeof(string));
        _dataTable.Columns.Add("Elapsed", typeof(string));
        _dataTable.Columns.Add("App", typeof(string));
        _dataTable.Columns.Add("Session", typeof(string));
        _dataTable.Columns.Add("Title", typeof(string));
        _dataTable.Columns.Add("TID", typeof(int));
        _dataTable.Columns.Add("PID", typeof(int));
        _dataTable.Columns.Add("Host", typeof(string));

        _defaultScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.Gray, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.White, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.Cyan, Color.Black) ?? default,
            HotFocus =
                Application.Driver?.MakeAttribute(Color.BrightCyan, Color.DarkGray) ?? default,
        };

        _errorScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.Red, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.BrightRed, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.Red, Color.Black) ?? default,
            HotFocus =
                Application.Driver?.MakeAttribute(Color.BrightRed, Color.DarkGray) ?? default,
        };

        _fatalScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.BrightRed, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.White, Color.Red) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.BrightRed, Color.Black) ?? default,
            HotFocus = Application.Driver?.MakeAttribute(Color.White, Color.Red) ?? default,
        };

        _warningScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.Brown, Color.Black) ?? default,
            Focus =
                Application.Driver?.MakeAttribute(Color.BrightYellow, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.Brown, Color.Black) ?? default,
            HotFocus =
                Application.Driver?.MakeAttribute(Color.BrightYellow, Color.DarkGray) ?? default,
        };

        _debugScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.DarkGray, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.Gray, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.DarkGray, Color.Black) ?? default,
            HotFocus = Application.Driver?.MakeAttribute(Color.Gray, Color.DarkGray) ?? default,
        };

        _flowScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.Green, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.BrightGreen, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.Green, Color.Black) ?? default,
            HotFocus =
                Application.Driver?.MakeAttribute(Color.BrightGreen, Color.DarkGray) ?? default,
        };

        _separatorScheme = new ColorScheme
        {
            Normal = Application.Driver?.MakeAttribute(Color.DarkGray, Color.Black) ?? default,
            Focus = Application.Driver?.MakeAttribute(Color.Gray, Color.DarkGray) ?? default,
            HotNormal = Application.Driver?.MakeAttribute(Color.DarkGray, Color.Black) ?? default,
            HotFocus = Application.Driver?.MakeAttribute(Color.Gray, Color.DarkGray) ?? default,
        };
    }

    public DataTable DataTable => _dataTable;

    public int RowCount => _entries.Count;

    public void ConfigureTableView(TableView tableView)
    {
        tableView.Table = _dataTable;
        tableView.ColorScheme = _defaultScheme;
        tableView.FullRowSelect = true;
        tableView.MultiSelect = false;
        tableView.Style.AlwaysShowHeaders = true;
        tableView.Style.ShowHorizontalHeaderOverline = false;
        tableView.Style.ShowVerticalCellLines = true;
        tableView.Style.ShowHorizontalScrollIndicators = true;
        tableView.Style.RowColorGetter = GetRowColorScheme;

        SetColumnWidth(tableView, ColLevel, 5, 5);
        SetColumnWidth(tableView, ColTime, 12, 12);
        SetColumnWidth(tableView, ColElapsed, 8, 10);
        SetColumnWidth(tableView, ColApp, 10, 20);
        SetColumnWidth(tableView, ColSession, 8, 16);
        SetColumnWidth(tableView, ColTitle, 30, 120);
        SetColumnWidth(tableView, ColThread, 4, 7);
        SetColumnWidth(tableView, ColProcess, 4, 8);
        SetColumnWidth(tableView, ColHost, 8, 18);
    }

    public void RebuildFromSnapshot(IReadOnlyList<LogEntry> snapshot)
    {
        _entries.Clear();
        _dataTable.BeginLoadData();
        try
        {
            _dataTable.Rows.Clear();
            foreach (var entry in snapshot)
            {
                _entries.Add(entry);
                AddRowUnsafe(entry);
            }
        }
        finally
        {
            _dataTable.EndLoadData();
        }
    }

    public LogEntry? GetEntryAt(int rowIndex)
    {
        return rowIndex >= 0 && rowIndex < _entries.Count ? _entries[rowIndex] : null;
    }

    public static string FormatLevelBadge(LogEntryType type) =>
        type switch
        {
            LogEntryType.Debug => "DBG",
            LogEntryType.Verbose => "VRB",
            LogEntryType.Message => "INF",
            LogEntryType.Warning => "WRN",
            LogEntryType.Error => "ERR",
            LogEntryType.Fatal => "FTL",
            LogEntryType.EnterMethod => ">> ",
            LogEntryType.LeaveMethod => "<< ",
            LogEntryType.Separator => "---",
            LogEntryType.Checkpoint => "CHK",
            LogEntryType.Assert => "AST",
            LogEntryType.Comment => "CMT",
            _ => "LOG",
        };

    private ColorScheme? GetRowColorScheme(TableView.RowColorGetterArgs args)
    {
        var entry = GetEntryAt(args.RowIndex);
        if (entry == null)
        {
            return _defaultScheme;
        }

        return entry.LogEntryType switch
        {
            LogEntryType.Fatal => _fatalScheme,
            LogEntryType.Error or LogEntryType.Assert => _errorScheme,
            LogEntryType.Warning => _warningScheme,
            LogEntryType.Debug or LogEntryType.Verbose or LogEntryType.Comment => _debugScheme,
            LogEntryType.EnterMethod or LogEntryType.LeaveMethod => _flowScheme,
            LogEntryType.Separator => _separatorScheme,
            _ => _defaultScheme,
        };
    }

    private void AddRowUnsafe(LogEntry entry)
    {
        var title =
            entry.LogEntryType == LogEntryType.Separator
                ? $"──── {entry.SingleLineTitle} ────"
                : entry.SingleLineTitle;

        _dataTable.Rows.Add(
            FormatLevelBadge(entry.LogEntryType),
            entry.Timestamp.ToString("HH:mm:ss.fff"),
            entry.ElapsedTimeFormatted,
            entry.AppName,
            entry.SessionName,
            title,
            entry.ThreadId,
            entry.ProcessId,
            entry.HostName
        );
    }

    private static void SetColumnWidth(
        TableView tableView,
        int columnIndex,
        int minWidth,
        int maxWidth
    )
    {
        var col = tableView.Table.Columns[columnIndex];
        var style = tableView.Style.GetOrCreateColumnStyle(col);
        style.MinWidth = minWidth;
        style.MaxWidth = maxWidth;
    }
}

using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Configuration;
using SmartInspectTui.Filtering;
using SmartInspectTui.Formatting;
using SmartInspectTui.Services;
using Terminal.Gui;

namespace SmartInspectTui.UI;

public sealed class MainWindow : Toplevel
{
    private static readonly PayloadFormatMode[] FormatModes =
    [
        PayloadFormatMode.Auto,
        PayloadFormatMode.Json,
        PayloadFormatMode.Xml,
        PayloadFormatMode.KeyValue,
        PayloadFormatMode.BinaryHex,
        PayloadFormatMode.Text,
    ];

    private readonly TuiSettings _settings;
    private readonly LogIngestionService _ingestionService;
    private readonly LogTableManager _tableManager;

    private readonly List<LogFilterClause> _activeClauses = [];
    private LogFilterField _selectedField = LogFilterField.Any;
    private IReadOnlyList<AutocompleteSuggestion> _currentSuggestions = [];

    private readonly Window _mainWindow;
    private readonly Button _fieldSelectorButton;
    private readonly TextField _searchField;
    private readonly View _activeFiltersContainer;
    private readonly Button _pauseButton;
    private readonly Button _autoScrollButton;
    private readonly Button _fuzzyButton;
    private readonly Button _detailsButton;
    private readonly FrameView _logFrame;
    private readonly TableView _logTableView;
    private readonly FrameView _detailFrame;
    private readonly Label _detailMetaLabel;
    private readonly Button _detailFormatButton;
    private readonly TextView _detailTextView;
    private readonly FrameView _autocompleteFrame;
    private readonly ListView _autocompleteListView;
    private readonly StatusItem _listenerStatusItem;
    private readonly StatusItem _stateStatusItem;

    private int _pageOffset;
    private long _pageAnchor;
    private long _displayedRevision = -1;
    private bool _refreshBusy;
    private bool _refreshAgain;
    private bool _uiStopped;
    private int _pageRequest;
    private int _detailRequest;
    private bool _detailBusy;
    private bool _detailAgain;
    private CancellationTokenSource? _filterCancellation;
    private int _selectedFormatIndex;
    private bool _autoScroll;
    private bool _fuzzyEnabled;
    private bool _showDetailsPane;
    private bool _suppressFilterEvents;
    private object? _timerToken;

    public MainWindow(TuiSettings settings, LogIngestionService ingestionService)
    {
        DarkTheme.Apply();
        ColorScheme = DarkTheme.BaseScheme;

        _settings = settings;
        _ingestionService = ingestionService;
        _tableManager = new LogTableManager();

        _autoScroll = _settings.AutoScroll;
        _fuzzyEnabled = _settings.EnableFuzzySearch;
        _showDetailsPane = _settings.ShowDetailsPane;

        var menuBar = BuildMenuBar();
        menuBar.ColorScheme = DarkTheme.MenuScheme;

        _mainWindow = new Window("SmartInspect TUI")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ColorScheme = DarkTheme.BaseScheme,
        };

        // Row 0: Field Selector + Search/Filter Input + [+ Add] + [Clear Filters]
        _fieldSelectorButton = new Button(GetFieldButtonText())
        {
            X = 1,
            Y = 0,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _fieldSelectorButton.Clicked += ShowFieldSelectorDialog;

        var filterLabel = new Label("Filter (/):")
        {
            X = Pos.Right(_fieldSelectorButton) + 1,
            Y = 0,
        };

        var clearFiltersButton = new Button("Clear Filters (^R)")
        {
            X = Pos.AnchorEnd(23),
            Y = 0,
            Width = 22,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        clearFiltersButton.Clicked += ResetFilters;

        var addFilterButton = new Button("+ Add (Enter)")
        {
            X = Pos.Left(clearFiltersButton) - 18,
            Y = 0,
            Width = 17,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        addFilterButton.Clicked += AddCurrentFilterClause;

        _searchField = new TextField(string.Empty)
        {
            X = Pos.Right(filterLabel) + 1,
            Y = 0,
            Width = Dim.Fill(42),
            ColorScheme = DarkTheme.InputScheme,
        };

        _searchField.TextChanged += _ => OnSearchTextChanged();
        _searchField.KeyPress += OnSearchFieldKeyPress;
        _searchField.Enter += _ => RefreshAutocompletePopup();
        _searchField.Leave += _ =>
        {
            if (_autocompleteListView?.HasFocus != true)
            {
                HideAutocompletePopup();
            }
        };

        // Row 1: Active Filters Bar (interactive removable filter chips)
        var activeLabel = new Label("Active:") { X = 1, Y = 1 };
        _activeFiltersContainer = new View
        {
            X = Pos.Right(activeLabel) + 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1,
            ColorScheme = DarkTheme.BaseScheme,
        };
        RebuildActiveFilterChips();

        // Row 2: Action / Toggle Bar
        _pauseButton = new Button("Pause (F6)")
        {
            X = 1,
            Y = 2,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _pauseButton.Clicked += TogglePause;

        _autoScrollButton = new Button(GetAutoScrollButtonText())
        {
            X = Pos.Right(_pauseButton) + 1,
            Y = 2,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _autoScrollButton.Clicked += ToggleAutoScroll;

        _fuzzyButton = new Button(GetFuzzyButtonText())
        {
            X = Pos.Right(_autoScrollButton) + 1,
            Y = 2,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _fuzzyButton.Clicked += ToggleFuzzy;

        var clearButton = new Button("Clear Logs (Ctrl+L)")
        {
            X = Pos.Right(_fuzzyButton) + 1,
            Y = 2,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        clearButton.Clicked += ClearLogs;

        _detailsButton = new Button(GetDetailsButtonText())
        {
            X = Pos.Right(clearButton) + 1,
            Y = 2,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _detailsButton.Clicked += ToggleDetailsPane;

        // Row 3+: Log Frame & TableView
        _logFrame = new FrameView("Logs (0 / 0)")
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = _showDetailsPane ? Dim.Percent(60) : Dim.Fill(),
            ColorScheme = DarkTheme.BaseScheme,
        };

        _logTableView = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _tableManager.ConfigureTableView(_logTableView);
        _logTableView.SelectedCellChanged += OnSelectedCellChanged;
        _logTableView.KeyPress += OnTableKeyPress;
        _logFrame.Add(_logTableView);

        // Detail Inspector Frame
        _detailFrame = new FrameView("Detail Inspector (F4 to toggle)")
        {
            X = 0,
            Y = Pos.Bottom(_logFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = _showDetailsPane,
            ColorScheme = DarkTheme.BaseScheme,
        };

        _detailMetaLabel = new Label("Select a log entry to inspect its payload.")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(18),
        };

        _detailFormatButton = new Button("Fmt: Auto (F7)")
        {
            X = Pos.AnchorEnd(17),
            Y = 0,
            Width = 16,
            ColorScheme = DarkTheme.ButtonScheme,
        };
        _detailFormatButton.Clicked += CyclePayloadFormat;

        _detailTextView = new TextView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            ColorScheme = DarkTheme.BaseScheme,
        };

        _detailFrame.Add(_detailMetaLabel, _detailFormatButton, _detailTextView);

        // Floating Autocomplete Popup (overlaid below _searchField when active)
        _autocompleteFrame = new FrameView("Autocomplete (Tab: complete, Enter: add)")
        {
            X = Pos.Left(_searchField),
            Y = 1,
            Width = 44,
            Height = 8,
            Visible = false,
            ColorScheme = DarkTheme.BaseScheme,
        };

        _autocompleteListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            ColorScheme = DarkTheme.InputScheme,
        };
        _autocompleteListView.OpenSelectedItem += _ =>
        {
            AcceptSelectedAutocompleteSuggestion(pinImmediately: true);
        };
        _autocompleteFrame.Add(_autocompleteListView);

        _mainWindow.Add(
            _fieldSelectorButton,
            filterLabel,
            _searchField,
            addFilterButton,
            clearFiltersButton,
            activeLabel,
            _activeFiltersContainer,
            _pauseButton,
            _autoScrollButton,
            _fuzzyButton,
            clearButton,
            _detailsButton,
            _logFrame,
            _detailFrame,
            _autocompleteFrame
        );

        _listenerStatusItem = new StatusItem(Key.Null, "Starting...", null);
        _stateStatusItem = new StatusItem(Key.Null, "Ready", null);

        var statusBar = new StatusBar([
            new StatusItem(Key.F1, "~F1~ Help", ShowHelpDialog),
            new StatusItem(Key.F6, "~F6~ Pause", TogglePause),
            new StatusItem(Key.F8, "~F8~ AutoScroll", ToggleAutoScroll),
            new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", () => Application.RequestStop()),
            _stateStatusItem,
            _listenerStatusItem,
        ])
        {
            ColorScheme = DarkTheme.MenuScheme,
        };

        Add(menuBar, _mainWindow, statusBar);

        KeyPress += OnGlobalKeyPress;
    }

    public void StartUiTimer()
    {
        _ingestionService.StartProcessing();
        RefreshFullTable();
        UpdateStatusAndHeaders();
        _timerToken = Application.MainLoop.AddTimeout(
            TimeSpan.FromMilliseconds(50),
            _ =>
            {
                ProcessBatchTick();
                return true;
            }
        );
    }

    public void StopUiTimer()
    {
        _uiStopped = true;
        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        if (_timerToken != null && Application.MainLoop != null)
        {
            Application.MainLoop.RemoveTimeout(_timerToken);
            _timerToken = null;
        }
    }

    private MenuBar BuildMenuBar()
    {
        return new MenuBar([
            new MenuBarItem(
                "_File",
                [
                    new MenuItem("_Open .sil File...", "Ctrl+O", OpenSilFileDialog),
                    new MenuItem("_Save .sil File...", "Ctrl+S", SaveSilFileDialog),
                    null!,
                    new MenuItem("_Settings...", "F9", OpenSettingsDialog),
                    null!,
                    new MenuItem("_Quit", "Ctrl+Q", () => Application.RequestStop()),
                ]
            ),
            new MenuBarItem(
                "_View",
                [
                    new MenuItem("_Select Filter Field...", "F2", ShowFieldSelectorDialog),
                    new MenuItem("_Toggle Detail Inspector", "F4", ToggleDetailsPane),
                    new MenuItem("_Cycle Payload Format", "F7", CyclePayloadFormat),
                    new MenuItem("_Toggle Fuzzy Search", "F3", ToggleFuzzy),
                    new MenuItem("_Toggle Auto-Scroll", "F8", ToggleAutoScroll),
                    new MenuItem("_Previous History Page", "[", () => ChangePage(-1)),
                    new MenuItem("_Next History Page", "]", () => ChangePage(1)),
                    null!,
                    new MenuItem("_Clear Logs", "Ctrl+L", ClearLogs),
                    new MenuItem("_Clear All Filters", "Ctrl+R", ResetFilters),
                ]
            ),
            new MenuBarItem(
                "_Listeners",
                [
                    new MenuItem("_Pause / Resume Screen", "F6", TogglePause),
                    new MenuItem("_Start / Stop Listeners", "F5", ToggleListeners),
                ]
            ),
            new MenuBarItem(
                "_Help",
                [new MenuItem("_Search Syntax & Shortcuts", "F1", ShowHelpDialog)]
            ),
        ]);
    }

    private void ProcessBatchTick()
    {
        if (_displayedRevision != _ingestionService.Revision && !_refreshBusy)
            RefreshFullTable();
        UpdateStatusAndHeaders();
    }

    private void RefreshFullTable()
    {
        _pageRequest++;
        if (_refreshBusy)
        {
            _refreshAgain = true;
            return;
        }
        _refreshBusy = true;
        _refreshAgain = false;
        var request = _pageRequest;
        var previousRow = _logTableView.SelectedRow;
        var previousRowOffset = _logTableView.RowOffset;
        var selectedId = _tableManager.GetEntryAt(previousRow)?.SequenceId ?? 0;
        var offset = _pageOffset;
        var anchor = _pageAnchor;
        var tail = _autoScroll && !_ingestionService.IsPaused;
        _ = Task.Run(() =>
        {
            LogPage? page = null;
            string? error = null;
            try
            {
                page = _ingestionService.GetPage(offset, tail, anchor);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            Application.MainLoop?.Invoke(() =>
            {
                _refreshBusy = false;
                if (_uiStopped)
                    return;
                if (request == _pageRequest && page != null)
                {
                    _pageOffset = page.Offset;
                    _pageAnchor = page.Entries.FirstOrDefault()?.SequenceId ?? 0;
                    _displayedRevision = page.Revision;
                    _tableManager.RebuildFromSnapshot(page.Entries);
                    _logTableView.Update();
                    // TableView can retain a scroll offset beyond a shorter replacement page.
                    var visibleRows = Math.Max(1, _logTableView.Bounds.Height - 2);
                    _logTableView.RowOffset = Math.Clamp(
                        previousRowOffset,
                        0,
                        Math.Max(0, page.Entries.Count - visibleRows)
                    );
                    if (page.Entries.Count > 0)
                    {
                        var selected = page
                            .Entries.ToList()
                            .FindIndex(e => e.SequenceId == selectedId);
                        _logTableView.SelectedRow =
                            tail ? page.Entries.Count - 1
                            : selected >= 0 ? selected
                            : Math.Clamp(previousRow, 0, page.Entries.Count - 1);
                        _logTableView.EnsureSelectedCellIsVisible();
                    }
                    UpdateDetailViewForSelectedRow();
                    if (_searchField.HasFocus && _autocompleteFrame.Visible)
                        RefreshAutocompletePopup();
                }
                if (error != null)
                    _stateStatusItem.Title = $"Storage error: {error}";
                if (_refreshAgain)
                    RefreshFullTable();
            });
        });
    }

    private void ChangePage(int direction)
    {
        _autoScroll = false;
        _settings.AutoScroll = false;
        _autoScrollButton.Text = GetAutoScrollButtonText();
        _pageAnchor = 0;
        _pageOffset = Math.Max(
            0,
            _pageOffset + direction * Math.Clamp(_settings.PageSize, 1, 1000)
        );
        RefreshFullTable();
    }

    private void UpdateStatusAndHeaders()
    {
        var filtered = _ingestionService.FilteredCount;
        var retained = _ingestionService.RetainedCount;
        var stateTag = _ingestionService.IsPaused
            ? $"PAUSED (+{_ingestionService.PausedBufferedCount:N0} buffered)"
            : "LIVE";

        var filterSummary =
            _activeClauses.Count > 0 ? $" | {_activeClauses.Count} filter(s)" : string.Empty;
        var first = _tableManager.RowCount == 0 ? 0 : _pageOffset + 1;
        var last = Math.Min(filtered, _pageOffset + _tableManager.RowCount);
        var frameTitle =
            $"Logs {first:N0}–{last:N0} of {filtered:N0} / {retained:N0} [{stateTag}{filterSummary}] ([/]: pages)";
        if (_logFrame.Title != frameTitle)
        {
            _logFrame.Title = frameTitle;
        }

        var tcpPart = _ingestionService.IsTcpListening
            ? $"TCP:{_settings.TcpPort}({_ingestionService.TcpClientCount})"
            : "TCP:off";
        var pipePart = _ingestionService.IsPipeListening
            ? $"Pipe:{_settings.PipeName}({_ingestionService.PipeClientCount})"
            : "Pipe:off";
        var wsPart = _ingestionService.IsWsListening
            ? $"WS:{_settings.WebSocketPort}({_ingestionService.WsClientCount})"
            : "WS:off";

        _listenerStatusItem.Title = $"{tcpPart} {pipePart} {wsPart}";
        _stateStatusItem.Title =
            $"{_ingestionService.StatusMessage} | Dropped: {_ingestionService.DroppedCount:N0} | Queued: {_ingestionService.PendingBytes / 1048576.0:F1} MiB";
    }

    private void OnSearchTextChanged()
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        RefreshAutocompletePopup();
        ApplyFilterFromInputs();
    }

    private void RefreshAutocompletePopup()
    {
        if (!_searchField.HasFocus)
        {
            HideAutocompletePopup();
            return;
        }

        var text = _searchField.Text?.ToString() ?? string.Empty;
        _currentSuggestions = _ingestionService.GetAutocompleteSuggestions(
            _selectedField,
            text,
            maxResults: 8
        );

        // If the only suggestion is already identical to what the user typed, hide the popup
        if (
            _currentSuggestions.Count == 0
            || (
                _currentSuggestions.Count == 1
                && string.Equals(
                    _currentSuggestions[0].CompletedInput,
                    text.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            HideAutocompletePopup();
            return;
        }

        var items = _currentSuggestions.Select(s => s.DisplayText).ToList();
        _autocompleteListView.SetSource(items);
        _autocompleteListView.SelectedItem = 0;

        var desiredHeight = Math.Clamp(items.Count + 2, 3, 10);
        _autocompleteFrame.Height = desiredHeight;
        _autocompleteFrame.Visible = true;
        _mainWindow.BringSubviewToFront(_autocompleteFrame);
        _mainWindow.SetNeedsDisplay();
    }

    private void HideAutocompletePopup()
    {
        if (_autocompleteFrame.Visible)
        {
            _autocompleteFrame.Visible = false;
            _mainWindow.SetNeedsDisplay();
        }
    }

    private bool AcceptSelectedAutocompleteSuggestion(bool pinImmediately)
    {
        if (
            !_autocompleteFrame.Visible
            || _currentSuggestions.Count == 0
            || _autocompleteListView.SelectedItem < 0
            || _autocompleteListView.SelectedItem >= _currentSuggestions.Count
        )
        {
            return false;
        }

        var suggestion = _currentSuggestions[_autocompleteListView.SelectedItem];

        _suppressFilterEvents = true;
        try
        {
            _searchField.Text = suggestion.CompletedInput;
            _searchField.CursorPosition = suggestion.CompletedInput.Length;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        HideAutocompletePopup();

        if (pinImmediately)
        {
            AddCurrentFilterClause();
        }
        else
        {
            ApplyFilterFromInputs();
        }

        return true;
    }

    private void OnSearchFieldKeyPress(KeyEventEventArgs args)
    {
        var key = args.KeyEvent.Key;
        var currentText = _searchField.Text?.ToString() ?? string.Empty;

        // Backspace on an empty filter input removes the last pinned filter chip
        if (key == Key.Backspace && string.IsNullOrEmpty(currentText) && _activeClauses.Count > 0)
        {
            RemoveFilterClauseAt(_activeClauses.Count - 1, restoreFocusToSearch: true);
            args.Handled = true;
            return;
        }

        if (_autocompleteFrame.Visible && _currentSuggestions.Count > 0)
        {
            if (key == Key.Tab)
            {
                AcceptSelectedAutocompleteSuggestion(pinImmediately: false);
                args.Handled = true;
                return;
            }

            if (key == Key.CursorDown)
            {
                _autocompleteListView.SelectedItem =
                    (_autocompleteListView.SelectedItem + 1) % _currentSuggestions.Count;
                _autocompleteListView.SetNeedsDisplay();
                args.Handled = true;
                return;
            }

            if (key == Key.CursorUp)
            {
                _autocompleteListView.SelectedItem =
                    (_autocompleteListView.SelectedItem - 1 + _currentSuggestions.Count)
                    % _currentSuggestions.Count;
                _autocompleteListView.SetNeedsDisplay();
                args.Handled = true;
                return;
            }

            if (key == Key.Esc)
            {
                HideAutocompletePopup();
                args.Handled = true;
                return;
            }
        }

        if (key == Key.Enter)
        {
            var trimmedText = currentText.Trim();
            if (
                string.IsNullOrEmpty(trimmedText)
                && _autocompleteFrame.Visible
                && _currentSuggestions.Count > 0
            )
            {
                AcceptSelectedAutocompleteSuggestion(pinImmediately: true);
            }
            else if (!string.IsNullOrEmpty(trimmedText))
            {
                AddCurrentFilterClause();
            }
            else
            {
                // Pressing Enter on an empty search box when popup is closed jumps to the log table
                _logTableView.SetFocus();
            }

            args.Handled = true;
        }
        else if (key == Key.Esc)
        {
            _logTableView.SetFocus();
            args.Handled = true;
        }
    }

    private void AddCurrentFilterClause()
    {
        var rawText = _searchField.Text?.ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(rawText))
        {
            return;
        }

        var parsedTokens = LogFilterEngine.ParseTokensForField(
            _selectedField,
            rawText,
            forceNegate: false
        );

        foreach (var token in parsedTokens)
        {
            if (string.IsNullOrWhiteSpace(token.Value))
            {
                continue;
            }

            var clause = new LogFilterClause(token.Field, token.Value, token.Negate);
            if (!_activeClauses.Contains(clause))
            {
                _activeClauses.Add(clause);
            }
        }

        _suppressFilterEvents = true;
        try
        {
            _searchField.Text = string.Empty;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        HideAutocompletePopup();
        RebuildActiveFilterChips();
        ApplyFilterFromInputs();
    }

    private void RemoveFilterClauseAt(int index, bool restoreFocusToSearch = false)
    {
        if (index < 0 || index >= _activeClauses.Count)
        {
            return;
        }

        _activeClauses.RemoveAt(index);
        RebuildActiveFilterChips(
            focusChipIndex: restoreFocusToSearch ? null : Math.Min(index, _activeClauses.Count - 1)
        );

        if (restoreFocusToSearch || _activeClauses.Count == 0)
        {
            _searchField.SetFocus();
        }

        ApplyFilterFromInputs();
    }

    private void RebuildActiveFilterChips(int? focusChipIndex = null)
    {
        _activeFiltersContainer.RemoveAll();

        if (_activeClauses.Count == 0)
        {
            var emptyHint = new Label(
                "(none — F2: Field, /: Filter, Tab: Complete, Enter: Add, Backspace/^R: Clear)"
            )
            {
                X = 0,
                Y = 0,
            };
            _activeFiltersContainer.Add(emptyHint);
            _activeFiltersContainer.SetNeedsDisplay();
            return;
        }

        View? previousChip = null;
        Button? buttonToFocus = null;

        for (var i = 0; i < _activeClauses.Count; i++)
        {
            var capturedIndex = i;
            var clause = _activeClauses[i];
            var chipButton = new Button(clause.ToDisplayChip())
            {
                X = previousChip == null ? 0 : Pos.Right(previousChip) + 1,
                Y = 0,
                ColorScheme = DarkTheme.ButtonScheme,
            };
            chipButton.Clicked += () => RemoveFilterClauseAt(capturedIndex);
            chipButton.KeyPress += e =>
            {
                if (e.KeyEvent.Key is Key.Backspace or Key.DeleteChar or Key.Delete)
                {
                    RemoveFilterClauseAt(capturedIndex);
                    e.Handled = true;
                }
            };

            _activeFiltersContainer.Add(chipButton);
            previousChip = chipButton;

            if (focusChipIndex == i)
            {
                buttonToFocus = chipButton;
            }
        }

        _activeFiltersContainer.SetNeedsDisplay();
        buttonToFocus?.SetFocus();
    }

    private void ShowFieldSelectorDialog()
    {
        var fields = LogFilterFieldExtensions.AllFields;
        var labels = fields
            .Select(f =>
                f.SupportsAutocomplete() ? $"{f.GetDisplayName(), -8} (Tab Autocomplete)"
                : f == LogFilterField.Any ? "Any      (Global Fuzzy / Field Tags)"
                : $"{f.GetDisplayName(), -8} (Text / Fuzzy)"
            )
            .ToList();

        var dialog = new Dialog("Select Filter Field (F2)", 46, fields.Length + 6)
        {
            ColorScheme = DarkTheme.BaseScheme,
        };

        var listView = new ListView(labels)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = fields.Length,
            SelectedItem = Array.IndexOf(fields, _selectedField),
            ColorScheme = DarkTheme.InputScheme,
        };

        void ApplySelectedField()
        {
            if (listView.SelectedItem >= 0 && listView.SelectedItem < fields.Length)
            {
                _selectedField = fields[listView.SelectedItem];
                _fieldSelectorButton.Text = GetFieldButtonText();
            }

            Application.RequestStop();
            _searchField.SetFocus();
            RefreshAutocompletePopup();
            ApplyFilterFromInputs();
        }

        listView.OpenSelectedItem += _ => ApplySelectedField();

        var selectBtn = new Button("Select", is_default: true)
        {
            ColorScheme = DarkTheme.ButtonScheme,
        };
        selectBtn.Clicked += ApplySelectedField;

        var cancelBtn = new Button("Cancel") { ColorScheme = DarkTheme.ButtonScheme };
        cancelBtn.Clicked += () => Application.RequestStop();

        dialog.Add(listView);
        dialog.AddButton(selectBtn);
        dialog.AddButton(cancelBtn);

        Application.Run(dialog);
    }

    private void ApplyFilterFromInputs()
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        var query = new LogFilterQuery
        {
            Clauses = [.. _activeClauses],
            ActiveField = _selectedField,
            SearchText = _searchField.Text?.ToString() ?? string.Empty,
            EnableFuzzy = _fuzzyEnabled,
        };

        _filterCancellation?.Cancel();
        _filterCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        var token = cancellation.Token;
        _pageOffset = 0;
        _pageAnchor = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token);
                await _ingestionService.UpdateFilterAsync(query, token);
                Application.MainLoop?.Invoke(() =>
                {
                    if (!_uiStopped && !token.IsCancellationRequested)
                        RefreshFullTable();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    if (!_uiStopped)
                        MessageBox.ErrorQuery("Search failed", ex.Message, "OK");
                });
            }
        });
    }

    private void TogglePause()
    {
        var paused = _ingestionService.TogglePaused();
        _pauseButton.Text = paused ? "Resume (F6)" : "Pause (F6)";
        if (!paused)
        {
            RefreshFullTable();
        }

        UpdateStatusAndHeaders();
    }

    private void ToggleAutoScroll()
    {
        _autoScroll = !_autoScroll;
        _settings.AutoScroll = _autoScroll;
        _autoScrollButton.Text = GetAutoScrollButtonText();
        if (_autoScroll)
            RefreshFullTable();
    }

    private void ToggleFuzzy()
    {
        _fuzzyEnabled = !_fuzzyEnabled;
        _settings.EnableFuzzySearch = _fuzzyEnabled;
        _fuzzyButton.Text = GetFuzzyButtonText();
        ApplyFilterFromInputs();
    }

    private void ToggleDetailsPane()
    {
        _showDetailsPane = !_showDetailsPane;
        _settings.ShowDetailsPane = _showDetailsPane;
        _detailsButton.Text = GetDetailsButtonText();
        _detailFrame.Visible = _showDetailsPane;
        _logFrame.Height = _showDetailsPane ? Dim.Percent(60) : Dim.Fill();
        UpdateDetailViewForSelectedRow();
        _mainWindow.SetNeedsDisplay();
    }

    private void CyclePayloadFormat()
    {
        _selectedFormatIndex = (_selectedFormatIndex + 1) % FormatModes.Length;
        var mode = FormatModes[_selectedFormatIndex];
        _detailFormatButton.Text = $"Fmt: {mode} (F7)";
        UpdateDetailViewForSelectedRow();
    }

    private void ClearLogs()
    {
        _ingestionService.Clear();
        HideAutocompletePopup();
        RefreshFullTable();
        UpdateStatusAndHeaders();
    }

    private void ResetFilters()
    {
        _activeClauses.Clear();
        _suppressFilterEvents = true;
        try
        {
            _searchField.Text = string.Empty;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        HideAutocompletePopup();
        RebuildActiveFilterChips();
        ApplyFilterFromInputs();
    }

    private void ToggleListeners()
    {
        _ = Task.Run(async () =>
        {
            if (_ingestionService.IsListening)
            {
                await _ingestionService.StopListenersAsync();
            }
            else
            {
                await _ingestionService.StartListenersAsync();
            }

            Application.MainLoop?.Invoke(UpdateStatusAndHeaders);
        });
    }

    private void OnSelectedCellChanged(TableView.SelectedCellChangedEventArgs args)
    {
        UpdateDetailViewForSelectedRow();
    }

    private void UpdateDetailViewForSelectedRow()
    {
        if (!_showDetailsPane)
        {
            return;
        }

        var entry = _tableManager.GetEntryAt(_logTableView.SelectedRow);
        if (entry == null)
        {
            _detailRequest++;
            _detailFrame.Title = "Detail Inspector (F4 to toggle)";
            _detailMetaLabel.Text = "Select a log entry to inspect its payload.";
            _detailTextView.Text = string.Empty;
            return;
        }

        var mode = FormatModes[_selectedFormatIndex];
        var request = ++_detailRequest;
        if (_detailBusy)
        {
            _detailAgain = true;
            return;
        }
        _detailBusy = true;
        _detailAgain = false;
        _ = Task.Run(() =>
        {
            try
            {
                var fullEntry = _ingestionService.GetEntry(entry.SequenceId);
                var formatted =
                    fullEntry == null ? null : PayloadFormatter.FormatPreview(fullEntry, mode);
                Application.MainLoop?.Invoke(() =>
                {
                    if (_uiStopped || request != _detailRequest)
                        return;
                    if (fullEntry == null || formatted == null)
                    {
                        _detailTextView.Text = "Entry expired under the retention limit.";
                        return;
                    }
                    _detailFrame.Title =
                        $"Detail: [{LogTableManager.FormatLevelBadge(fullEntry.LogEntryType).Trim()}] {Truncate(fullEntry.SingleLineTitle, 50)} ({formatted.DetectedFormat}, {formatted.ByteSize:N0} B)";
                    _detailMetaLabel.Text =
                        $"App: {entry.AppName} | Sess: {entry.SessionName} | Host: {entry.HostName} | PID: {entry.ProcessId} | TID: {entry.ThreadId} | Time: {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}";
                    _detailTextView.Text =
                        $"Title: {Truncate(fullEntry.Title, 16384)}\n"
                        + "────────────────────────────────────────────────────────────────────────────\n"
                        + formatted.FormattedText;
                });
            }
            catch (Exception ex)
                when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException) { }
            finally
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _detailBusy = false;
                    if (!_uiStopped && _detailAgain)
                        UpdateDetailViewForSelectedRow();
                });
            }
        });
    }

    private void OnTableKeyPress(KeyEventEventArgs args)
    {
        var key = args.KeyEvent.Key;
        if (key == (Key)'[' || key == (Key.CtrlMask | Key.PageUp))
        {
            ChangePage(-1);
            args.Handled = true;
        }
        else if (key == (Key)']' || key == (Key.CtrlMask | Key.PageDown))
        {
            ChangePage(1);
            args.Handled = true;
        }
        else if (key == (Key)'/')
        {
            _searchField.SetFocus();
            RefreshAutocompletePopup();
            args.Handled = true;
        }
        else if (key == (Key)' ')
        {
            TogglePause();
            args.Handled = true;
        }
        else if (key == Key.Enter)
        {
            if (!_showDetailsPane)
            {
                ToggleDetailsPane();
            }

            _detailTextView.SetFocus();
            args.Handled = true;
        }
    }

    private void OnGlobalKeyPress(KeyEventEventArgs args)
    {
        switch (args.KeyEvent.Key)
        {
            case Key.F1:
                ShowHelpDialog();
                args.Handled = true;
                break;
            case Key.F2:
                ShowFieldSelectorDialog();
                args.Handled = true;
                break;
            case Key.F3:
                ToggleFuzzy();
                args.Handled = true;
                break;
            case Key.F4:
                ToggleDetailsPane();
                args.Handled = true;
                break;
            case Key.F5:
                ToggleListeners();
                args.Handled = true;
                break;
            case Key.F6:
                TogglePause();
                args.Handled = true;
                break;
            case Key.F7:
                CyclePayloadFormat();
                args.Handled = true;
                break;
            case Key.F8:
                ToggleAutoScroll();
                args.Handled = true;
                break;
            case Key.F9:
                OpenSettingsDialog();
                args.Handled = true;
                break;
            case Key.CtrlMask | Key.L:
                ClearLogs();
                args.Handled = true;
                break;
            case Key.CtrlMask | Key.R:
                ResetFilters();
                args.Handled = true;
                break;
            case Key.CtrlMask | Key.O:
                OpenSilFileDialog();
                args.Handled = true;
                break;
            case Key.CtrlMask | Key.S:
                SaveSilFileDialog();
                args.Handled = true;
                break;
        }
    }

    private void OpenSilFileDialog()
    {
        var dialog = new OpenDialog("Open SmartInspect Log (.sil)", "Select a .sil file to load")
        {
            CanChooseDirectories = false,
            AllowsMultipleSelection = false,
            AllowedFileTypes = [".sil"],
            ColorScheme = DarkTheme.BaseScheme,
        };

        Application.Run(dialog);
        if (dialog.Canceled || dialog.FilePaths.Count == 0)
        {
            return;
        }

        var path = dialog.FilePaths[0];
        _ = Task.Run(async () =>
        {
            try
            {
                await _ingestionService.LoadSilFileAsync(path);
                Application.MainLoop?.Invoke(() =>
                {
                    RefreshFullTable();
                    UpdateStatusAndHeaders();
                });
            }
            catch (Exception ex)
            {
                Application.MainLoop?.Invoke(() =>
                    MessageBox.ErrorQuery("Failed to Open .sil File", ex.Message, "OK")
                );
            }
        });
    }

    private void SaveSilFileDialog()
    {
        var dialog = new SaveDialog("Save SmartInspect Log (.sil)", "Save retained entries to .sil")
        {
            AllowedFileTypes = [".sil"],
            ColorScheme = DarkTheme.BaseScheme,
        };

        Application.Run(dialog);
        if (dialog.Canceled || string.IsNullOrWhiteSpace(dialog.FilePath?.ToString()))
        {
            return;
        }

        var path = dialog.FilePath.ToString()!;
        if (!path.EndsWith(".sil", StringComparison.OrdinalIgnoreCase))
        {
            path += ".sil";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _ingestionService.SaveSilFileAsync(path);
                Application.MainLoop?.Invoke(UpdateStatusAndHeaders);
            }
            catch (Exception ex)
            {
                Application.MainLoop?.Invoke(() =>
                    MessageBox.ErrorQuery("Failed to Save .sil File", ex.Message, "OK")
                );
            }
        });
    }

    private void OpenSettingsDialog()
    {
        var dialog = new Dialog("Settings (F9)", 68, 24) { ColorScheme = DarkTheme.BaseScheme };

        var tcpCheck = new CheckBox("Enable TCP Listener", _settings.EnableTcp) { X = 2, Y = 1 };
        var tcpPortLabel = new Label("Port:") { X = 32, Y = 1 };
        var tcpPortField = new TextField(_settings.TcpPort.ToString())
        {
            X = 38,
            Y = 1,
            Width = 10,
            ColorScheme = DarkTheme.InputScheme,
        };

        var pipeCheck = new CheckBox("Enable Named Pipe Listener", _settings.EnablePipe)
        {
            X = 2,
            Y = 3,
        };
        var pipeNameLabel = new Label("Name:") { X = 32, Y = 3 };
        var pipeNameField = new TextField(_settings.PipeName)
        {
            X = 38,
            Y = 3,
            Width = 22,
            ColorScheme = DarkTheme.InputScheme,
        };

        var wsCheck = new CheckBox("Enable WebSocket Listener", _settings.EnableWebSocket)
        {
            X = 2,
            Y = 5,
        };
        var wsPortLabel = new Label("Port:") { X = 32, Y = 5 };
        var wsPortField = new TextField(_settings.WebSocketPort.ToString())
        {
            X = 38,
            Y = 5,
            Width = 10,
            ColorScheme = DarkTheme.InputScheme,
        };

        var maxEntriesLabel = new Label("Max Retained Log Entries:") { X = 2, Y = 7 };
        var maxEntriesField = new TextField(_settings.MaxLogEntries.ToString())
        {
            X = 30,
            Y = 7,
            Width = 12,
            ColorScheme = DarkTheme.InputScheme,
        };

        var diskLabel = new Label("Disk quota MiB (restart):") { X = 2, Y = 9 };
        var diskField = new TextField((_settings.MaxDiskBytes / 1048576).ToString())
        {
            X = 30,
            Y = 9,
            Width = 12,
            ColorScheme = DarkTheme.InputScheme,
        };
        var queueLabel = new Label("Queue budget MiB:") { X = 2, Y = 10 };
        var queueField = new TextField((_settings.MaxPendingBytes / 1048576).ToString())
        {
            X = 30,
            Y = 10,
            Width = 12,
            ColorScheme = DarkTheme.InputScheme,
        };

        var otelFrame = new FrameView("OpenTelemetry (OTLP) Relay (Roadmap)")
        {
            X = 1,
            Y = 12,
            Width = Dim.Fill(1),
            Height = 6,
            ColorScheme = DarkTheme.BaseScheme,
        };

        var otelEnabledCheck = new CheckBox(
            "Relay logs to OTel Collector",
            _settings.OtelRelay.Enabled
        )
        {
            X = 1,
            Y = 0,
        };
        var otelEndpointLabel = new Label("Endpoint:") { X = 1, Y = 1 };
        var otelEndpointField = new TextField(_settings.OtelRelay.Endpoint)
        {
            X = 11,
            Y = 1,
            Width = 45,
            ColorScheme = DarkTheme.InputScheme,
        };
        var otelProtocolLabel = new Label("Protocol (grpc/http):") { X = 1, Y = 2 };
        var otelProtocolField = new TextField(_settings.OtelRelay.Protocol)
        {
            X = 23,
            Y = 2,
            Width = 12,
            ColorScheme = DarkTheme.InputScheme,
        };

        otelFrame.Add(
            otelEnabledCheck,
            otelEndpointLabel,
            otelEndpointField,
            otelProtocolLabel,
            otelProtocolField
        );

        var saveButton = new Button("Save", is_default: true)
        {
            ColorScheme = DarkTheme.ButtonScheme,
        };
        saveButton.Clicked += () =>
        {
            _settings.EnableTcp = tcpCheck.Checked;
            if (
                int.TryParse(tcpPortField.Text?.ToString(), out var tcpPort)
                && tcpPort is > 0 and <= 65535
            )
            {
                _settings.TcpPort = tcpPort;
            }

            _settings.EnablePipe = pipeCheck.Checked;
            _settings.PipeName = pipeNameField.Text?.ToString()?.Trim() ?? "smartinspect";

            _settings.EnableWebSocket = wsCheck.Checked;
            if (
                int.TryParse(wsPortField.Text?.ToString(), out var wsPort)
                && wsPort is > 0 and <= 65535
            )
            {
                _settings.WebSocketPort = wsPort;
            }

            if (
                int.TryParse(maxEntriesField.Text?.ToString(), out var maxEntries)
                && maxEntries >= 1000
            )
            {
                _settings.MaxLogEntries = maxEntries;
            }

            if (
                long.TryParse(diskField.Text?.ToString(), out var diskMiB)
                && diskMiB is >= 2 and <= 1048576
            )
                _settings.MaxDiskBytes = diskMiB * 1048576;
            if (
                long.TryParse(queueField.Text?.ToString(), out var queueMiB)
                && queueMiB is >= 1 and <= 1024
            )
                _settings.MaxPendingBytes = queueMiB * 1048576;
            _settings.OtelRelay.Enabled = otelEnabledCheck.Checked;
            _settings.OtelRelay.Endpoint =
                otelEndpointField.Text?.ToString()?.Trim() ?? "http://localhost:4317";
            _settings.OtelRelay.Protocol = otelProtocolField.Text?.ToString()?.Trim() ?? "grpc";

            _settings.Save();
            Application.RequestStop();

            _ = Task.Run(async () =>
            {
                await _ingestionService.StopListenersAsync();
                await _ingestionService.StartListenersAsync();
                Application.MainLoop?.Invoke(UpdateStatusAndHeaders);
            });
        };

        var cancelButton = new Button("Cancel") { ColorScheme = DarkTheme.ButtonScheme };
        cancelButton.Clicked += () => Application.RequestStop();

        dialog.Add(
            tcpCheck,
            tcpPortLabel,
            tcpPortField,
            pipeCheck,
            pipeNameLabel,
            pipeNameField,
            wsCheck,
            wsPortLabel,
            wsPortField,
            maxEntriesLabel,
            maxEntriesField,
            diskLabel,
            diskField,
            queueLabel,
            queueField,
            otelFrame
        );
        dialog.AddButton(saveButton);
        dialog.AddButton(cancelButton);

        Application.Run(dialog);
    }

    private static void ShowHelpDialog()
    {
        const string helpText =
            "Keyboard Shortcuts:\n"
            + "  /          Focus the Filter input\n"
            + "  F2         Select Filter Field (Any, Lvl, App, Session, TID, PID, Host, Title, Data)\n"
            + "  Tab        Complete highlighted suggestion in the Autocomplete popup\n"
            + "  Up / Down  Navigate suggestions in the Autocomplete popup\n"
            + "  Enter      Pin the current filter to the Active Filters bar\n"
            + "  Backspace  (When Filter input is empty) Remove the last pinned filter chip\n"
            + "  Ctrl+R     Clear all active filter chips and reset filter input\n"
            + "  Esc        Close Autocomplete popup or return focus to Log Table\n"
            + "  Space / F6 Pause or resume live log rendering\n"
            + "  F3         Toggle Fuzzy matching ON / OFF\n"
            + "  F4         Show / hide the Payload Detail Inspector pane\n"
            + "  F5         Start / stop network & pipe listeners\n"
            + "  F7         Cycle payload format (Auto, JSON, XML, KeyValue, Hex, Text)\n"
            + "  F8         Toggle Auto-Scroll to newest entries\n"
            + "  [ / ]      Previous / next history page (table focus)\n"
            + "  Ctrl+PgUp / Ctrl+PgDn  Page history if supported by your terminal\n"
            + "  F9         Open Settings dialog (Ports, Pipe, Retention, OTel Relay)\n"
            + "  Ctrl+L     Clear all retained and visible log entries\n"
            + "  Ctrl+O     Open a recorded SmartInspect (.sil) file\n"
            + "  Ctrl+S     Save current log entries to a (.sil) file\n"
            + "  Ctrl+Q     Quit SmartInspect TUI\n\n"
            + "Multi-Filter & Autocomplete Workflow (100% Keyboard Driven):\n"
            + "  1. Press / to focus the Filter input, and F2 to pick a field (or type 'lvl:DBG').\n"
            + "  2. Use Up/Down and Tab to complete from the Excel-style autocomplete popup.\n"
            + "  3. Press Enter to pin the filter to the 'Active:' bar.\n"
            + "  4. Press Backspace in an empty Filter input to pop the last filter chip,\n"
            + "     press Ctrl+R anywhere to clear all filters, or Tab to a chip and press\n"
            + "     Enter / Space / Delete / Backspace to remove that specific filter.";

        MessageBox.Query("SmartInspect TUI Help", helpText, "OK");
    }

    private string GetFieldButtonText() => $"Field: {_selectedField.GetDisplayName()} ▼ (F2)";

    private string GetAutoScrollButtonText() =>
        _autoScroll ? "AutoScroll: ON (F8)" : "AutoScroll: OFF (F8)";

    private string GetFuzzyButtonText() => _fuzzyEnabled ? "Fuzzy: ON (F3)" : "Fuzzy: OFF (F3)";

    private string GetDetailsButtonText() =>
        _showDetailsPane ? "Details: ON (F4)" : "Details: OFF (F4)";

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)] + "...";
    }
}

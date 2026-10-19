using System.Collections.Concurrent;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Events;
using SmartInspectConsole.Core.FileIO;
using SmartInspectConsole.Core.Listeners;
using SmartInspectConsole.Core.Packets;
using SmartInspectTui.Configuration;
using SmartInspectTui.Filtering;
using SmartInspectTui.Relaying;

namespace SmartInspectTui.Services;

public sealed record BatchDrainResult(
    int DrainedCount,
    int AddedToFilteredCount,
    int TrimmedFromFilteredCount,
    bool ClearedByCommand,
    bool HasVisibleChanges
);

/// <summary>
/// Coordinates SmartInspect listeners, background packet queuing, retention trimming,
/// pause/resume screen state, live filtering, and .sil file I/O.
/// </summary>
public sealed class LogIngestionService : IDisposable
{
    private const double TrimTargetRetentionRatio = 0.90;
    private const int MaxPendingQueueDepth = 100_000;

    private readonly object _sync = new();
    private readonly TuiSettings _settings;
    private readonly ILogRelaySink _relaySink;
    private readonly ConcurrentQueue<QueuedPacket> _pendingQueue = new();
    private readonly AutocompleteIndex _autocompleteIndex = new();

    private readonly List<LogEntry> _retainedEntries = [];
    private readonly List<LogEntry> _filteredEntries = [];

    private SmartInspectTcpListener? _tcpListener;
    private SmartInspectPipeListener? _pipeListener;
    private SmartInspectWebSocketListener? _wsListener;

    private LogFilterEngine _filterEngine;
    private DateTime? _lastTimestamp;
    private int _pendingCount;
    private long _totalReceived;
    private int _pausedBufferedCount;
    private string _statusMessage = "Ready";

    public LogIngestionService(
        TuiSettings settings,
        ILogRelaySink? relaySink = null,
        LogFilterQuery? initialFilter = null
    )
    {
        _settings = settings ?? new TuiSettings();
        _relaySink = relaySink ?? new NoOpOtelRelaySink();
        _filterEngine = new LogFilterEngine(
            initialFilter ?? new LogFilterQuery { EnableFuzzy = _settings.EnableFuzzySearch }
        );
    }

    public AutocompleteIndex Autocomplete => _autocompleteIndex;

    public IReadOnlyList<AutocompleteSuggestion> GetAutocompleteSuggestions(
        LogFilterField field,
        string? inputText,
        int maxResults = 8
    ) => _autocompleteIndex.GetSuggestions(field, inputText, maxResults);

    public bool IsListening { get; private set; }
    public bool IsPaused { get; private set; }
    public string StatusMessage
    {
        get
        {
            lock (_sync)
            {
                return _statusMessage;
            }
        }
        private set
        {
            lock (_sync)
            {
                _statusMessage = value;
            }
        }
    }

    public int RetainedCount
    {
        get
        {
            lock (_sync)
            {
                return _retainedEntries.Count;
            }
        }
    }

    public int FilteredCount
    {
        get
        {
            lock (_sync)
            {
                return _filteredEntries.Count;
            }
        }
    }

    public int PausedBufferedCount => Volatile.Read(ref _pausedBufferedCount);
    public long TotalReceived => Interlocked.Read(ref _totalReceived);
    public int PendingQueueDepth => Math.Max(0, Volatile.Read(ref _pendingCount));

    public int TcpClientCount => _tcpListener?.ClientCount ?? 0;
    public int PipeClientCount => _pipeListener?.ClientCount ?? 0;
    public int WsClientCount => _wsListener?.ClientCount ?? 0;

    public bool IsTcpListening => _tcpListener?.IsListening ?? false;
    public bool IsPipeListening => _pipeListener?.IsListening ?? false;
    public bool IsWsListening => _wsListener?.IsListening ?? false;

    public LogFilterQuery CurrentFilter
    {
        get
        {
            lock (_sync)
            {
                return _filterEngine.Query;
            }
        }
    }

    public async Task StartListenersAsync(CancellationToken cancellationToken = default)
    {
        if (IsListening)
        {
            return;
        }

        await StopListenersAsync();

        var errors = new List<string>();
        var startedAny = false;

        if (_settings.EnableTcp)
        {
            try
            {
                _tcpListener = new SmartInspectTcpListener(_settings.TcpPort);
                AttachListenerEvents(_tcpListener);
                await _tcpListener.StartAsync(cancellationToken);
                startedAny = true;
            }
            catch (Exception ex)
            {
                errors.Add($"TCP:{_settings.TcpPort} ({ex.Message})");
            }
        }

        if (_settings.EnablePipe && !string.IsNullOrWhiteSpace(_settings.PipeName))
        {
            try
            {
                _pipeListener = new SmartInspectPipeListener(_settings.PipeName);
                AttachListenerEvents(_pipeListener);
                await _pipeListener.StartAsync(cancellationToken);
                startedAny = true;
            }
            catch (Exception ex)
            {
                errors.Add($"Pipe:{_settings.PipeName} ({ex.Message})");
            }
        }

        if (_settings.EnableWebSocket)
        {
            try
            {
                _wsListener = new SmartInspectWebSocketListener(_settings.WebSocketPort);
                AttachListenerEvents(_wsListener);
                await _wsListener.StartAsync(cancellationToken);
                startedAny = true;
            }
            catch (Exception ex)
            {
                errors.Add($"WS:{_settings.WebSocketPort} ({ex.Message})");
            }
        }

        IsListening = startedAny;
        if (errors.Count > 0)
        {
            StatusMessage = startedAny
                ? $"Listening (partial errors: {string.Join("; ", errors)})"
                : $"Failed to start listeners: {string.Join("; ", errors)}";
        }
        else if (startedAny)
        {
            StatusMessage = "Listening for SmartInspect connections";
        }
        else
        {
            StatusMessage = "Listeners disabled";
        }
    }

    public async Task StopListenersAsync()
    {
        var tasks = new List<Task>();

        if (_tcpListener != null)
        {
            DetachListenerEvents(_tcpListener);
            tasks.Add(_tcpListener.StopAsync());
        }

        if (_pipeListener != null)
        {
            DetachListenerEvents(_pipeListener);
            tasks.Add(_pipeListener.StopAsync());
        }

        if (_wsListener != null)
        {
            DetachListenerEvents(_wsListener);
            tasks.Add(_wsListener.StopAsync());
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Ignore shutdown exceptions
            }
        }

        _tcpListener = null;
        _pipeListener = null;
        _wsListener = null;
        IsListening = false;
        StatusMessage = "Listeners stopped";
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            if (IsPaused == paused)
            {
                return;
            }

            IsPaused = paused;
            if (paused)
            {
                Interlocked.Exchange(ref _pausedBufferedCount, 0);
                _statusMessage = "Screen paused (incoming logs buffered in background)";
            }
            else
            {
                var buffered = Interlocked.Exchange(ref _pausedBufferedCount, 0);
                RebuildFilteredEntriesUnsafe();
                _statusMessage =
                    buffered > 0
                        ? $"Resumed live log view (+{buffered:N0} entries)"
                        : "Resumed live log view";
            }
        }
    }

    public bool TogglePaused()
    {
        SetPaused(!IsPaused);
        return IsPaused;
    }

    public void UpdateFilter(LogFilterQuery query)
    {
        lock (_sync)
        {
            _filterEngine = new LogFilterEngine(query);
            RebuildFilteredEntriesUnsafe();
        }
    }

    public void Clear()
    {
        while (_pendingQueue.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
        }

        Interlocked.Exchange(ref _pendingCount, 0);
        Interlocked.Exchange(ref _pausedBufferedCount, 0);

        lock (_sync)
        {
            _retainedEntries.Clear();
            _filteredEntries.Clear();
            _autocompleteIndex.Clear();
            _lastTimestamp = null;
            _statusMessage = "Cleared log entries";
        }
    }

    public void EnqueueLogEntry(LogEntry entry, string clientId = "local")
    {
        Interlocked.Increment(ref _totalReceived);

        if (Interlocked.Increment(ref _pendingCount) > MaxPendingQueueDepth)
        {
            if (_pendingQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }

        _pendingQueue.Enqueue(new QueuedPacket(entry, null, clientId));

        if (_relaySink.IsEnabled)
        {
            _ = _relaySink.RelayLogEntryAsync(entry, clientId);
        }
    }

    public BatchDrainResult DrainBatch(int maxBatchSize = 2000)
    {
        var drained = 0;
        var addedToFiltered = 0;
        var trimmedFromFiltered = 0;
        var clearedByCommand = false;

        lock (_sync)
        {
            while (drained < maxBatchSize && _pendingQueue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _pendingCount);
                drained++;

                if (item.ControlCommand != null)
                {
                    if (
                        item.ControlCommand.ControlCommandType
                        is ControlCommandType.ClearLog
                            or ControlCommandType.ClearAll
                    )
                    {
                        _retainedEntries.Clear();
                        _autocompleteIndex.Clear();
                        if (!IsPaused)
                        {
                            _filteredEntries.Clear();
                            clearedByCommand = true;
                        }
                    }

                    continue;
                }

                if (item.LogEntry == null)
                {
                    continue;
                }

                var entry = item.LogEntry;
                ComputeElapsedTimeUnsafe(entry);
                _retainedEntries.Add(entry);
                _autocompleteIndex.IndexEntry(entry);

                if (IsPaused)
                {
                    Interlocked.Increment(ref _pausedBufferedCount);
                }
                else if (_filterEngine.Matches(entry))
                {
                    _filteredEntries.Add(entry);
                    addedToFiltered++;
                }
            }

            if (_retainedEntries.Count > _settings.MaxLogEntries)
            {
                trimmedFromFiltered = TrimRetainedEntriesUnsafe();
            }
        }

        var hasVisibleChanges =
            !IsPaused && (addedToFiltered > 0 || trimmedFromFiltered > 0 || clearedByCommand);

        return new BatchDrainResult(
            drained,
            addedToFiltered,
            trimmedFromFiltered,
            clearedByCommand,
            hasVisibleChanges
        );
    }

    public IReadOnlyList<LogEntry> GetFilteredSnapshot()
    {
        lock (_sync)
        {
            return [.. _filteredEntries];
        }
    }

    public LogEntry? GetFilteredEntryAt(int index)
    {
        lock (_sync)
        {
            return index >= 0 && index < _filteredEntries.Count ? _filteredEntries[index] : null;
        }
    }

    public async Task<int> LoadSilFileAsync(string filePath)
    {
        var reader = new SilFileReader();
        var packets = await reader.ReadFileAsync(filePath);
        var loadedCount = 0;

        lock (_sync)
        {
            foreach (var packet in packets)
            {
                if (packet is LogEntry logEntry)
                {
                    ComputeElapsedTimeUnsafe(logEntry);
                    _retainedEntries.Add(logEntry);
                    _autocompleteIndex.IndexEntry(logEntry);
                    loadedCount++;
                }
            }

            if (_retainedEntries.Count > _settings.MaxLogEntries)
            {
                TrimRetainedEntriesUnsafe();
            }

            RebuildFilteredEntriesUnsafe();
            _statusMessage = $"Loaded {loadedCount:N0} entries from {Path.GetFileName(filePath)}";
        }

        return loadedCount;
    }

    public async Task<int> SaveSilFileAsync(string filePath, bool filteredOnly = false)
    {
        List<Packet> packetsToSave;
        lock (_sync)
        {
            var source = filteredOnly ? _filteredEntries : _retainedEntries;
            packetsToSave = source.Cast<Packet>().ToList();
        }

        var writer = new SilFileWriter();
        await writer.WriteFileAsync(filePath, packetsToSave);
        StatusMessage = $"Saved {packetsToSave.Count:N0} entries to {Path.GetFileName(filePath)}";
        return packetsToSave.Count;
    }

    private void ComputeElapsedTimeUnsafe(LogEntry entry)
    {
        if (_lastTimestamp.HasValue)
        {
            var elapsed = entry.Timestamp - _lastTimestamp.Value;
            entry.ElapsedTime = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        _lastTimestamp = entry.Timestamp;
    }

    private int TrimRetainedEntriesUnsafe()
    {
        var maxEntries = Math.Max(1_000, _settings.MaxLogEntries);
        if (_retainedEntries.Count <= maxEntries)
        {
            return 0;
        }

        var targetCount = Math.Max(1_000, (int)Math.Floor(maxEntries * TrimTargetRetentionRatio));
        var removeCount = _retainedEntries.Count - targetCount;
        if (removeCount <= 0)
        {
            return 0;
        }

        var removedSet = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < removeCount; i++)
        {
            removedSet.Add(_retainedEntries[i]);
        }

        _retainedEntries.RemoveRange(0, removeCount);

        if (IsPaused || _filteredEntries.Count == 0)
        {
            return 0;
        }

        return _filteredEntries.RemoveAll(removedSet.Contains);
    }

    private void RebuildFilteredEntriesUnsafe()
    {
        _filteredEntries.Clear();
        foreach (var entry in _retainedEntries)
        {
            if (_filterEngine.Matches(entry))
            {
                _filteredEntries.Add(entry);
            }
        }
    }

    private void AttachListenerEvents(IPacketListener listener)
    {
        listener.PacketReceived += OnPacketReceived;
        listener.ClientConnected += OnClientConnected;
        listener.ClientDisconnected += OnClientDisconnected;
        listener.Error += OnListenerError;
    }

    private void DetachListenerEvents(IPacketListener listener)
    {
        listener.PacketReceived -= OnPacketReceived;
        listener.ClientConnected -= OnClientConnected;
        listener.ClientDisconnected -= OnClientDisconnected;
        listener.Error -= OnListenerError;
    }

    private void OnPacketReceived(object? sender, PacketReceivedEventArgs e)
    {
        switch (e.Packet)
        {
            case LogEntry logEntry:
                EnqueueLogEntry(logEntry, e.ClientId);
                break;

            case ControlCommand controlCommand:
                Interlocked.Increment(ref _pendingCount);
                _pendingQueue.Enqueue(new QueuedPacket(null, controlCommand, e.ClientId));
                break;

            case LogHeader logHeader:
                logHeader.ParseContent();
                StatusMessage =
                    $"Client connected: {logHeader.AppName ?? e.ClientId}@{logHeader.HostName ?? "local"}";
                break;
        }
    }

    private void OnClientConnected(object? sender, ClientEventArgs e)
    {
        StatusMessage = $"Client connected ({e.ClientId})";
    }

    private void OnClientDisconnected(object? sender, ClientEventArgs e)
    {
        StatusMessage = $"Client disconnected ({e.ClientId})";
    }

    private void OnListenerError(object? sender, Exception e)
    {
        StatusMessage = $"Listener error: {e.Message}";
    }

    public void Dispose()
    {
        StopListenersAsync().GetAwaiter().GetResult();
    }

    private sealed record QueuedPacket(
        LogEntry? LogEntry,
        ControlCommand? ControlCommand,
        string ClientId
    );
}

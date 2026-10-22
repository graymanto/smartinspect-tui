using Microsoft.Data.Sqlite;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.Events;
using SmartInspectConsole.Core.FileIO;
using SmartInspectConsole.Core.Listeners;
using SmartInspectConsole.Core.Packets;
using SmartInspectConsole.Core.Parsing;
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

public sealed record LogPage(
    IReadOnlyList<LogEntry> Entries,
    int Offset,
    int TotalCount,
    long Revision
);

public sealed class LogIngestionService : IDisposable
{
    private readonly object _sync = new();
    private readonly object _queueSync = new();
    private readonly Queue<QueuedPacket> _pendingQueue = new();
    private readonly TuiSettings _settings;
    private readonly ILogRelaySink _relaySink;
    private readonly SqliteLogStore _store;
    private readonly AutocompleteIndex _autocompleteIndex = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private Task? _processingTask;
    private SmartInspectTcpListener? _tcpListener;
    private SmartInspectPipeListener? _pipeListener;
    private SmartInspectWebSocketListener? _wsListener;
    private LogFilterEngine _filterEngine;
    private LogFilterEngine? _searchEngine;
    private long _generationCounter;
    private long _viewGeneration;
    private long _searchGeneration;
    private long _searchRequest;
    private long _visibleUpperId;
    private DateTime? _lastTimestamp;
    private int _retainedCount;
    private int _filteredCount;
    private int _pendingCount;
    private long _pendingBytes;
    private long _totalReceived;
    private long _droppedCount;
    private long _revision;
    private int _pausedBufferedCount;
    private volatile bool _paused;
    private volatile bool _disposed;
    private string _statusMessage = "Ready";

    public LogIngestionService(
        TuiSettings settings,
        ILogRelaySink? relaySink = null,
        LogFilterQuery? initialFilter = null
    )
    {
        _settings = settings;
        _relaySink = relaySink ?? new NoOpOtelRelaySink();
        _filterEngine = new LogFilterEngine(
            initialFilter ?? new LogFilterQuery { EnableFuzzy = settings.EnableFuzzySearch }
        );
        _viewGeneration = _filterEngine.Query.IsEmpty ? 0 : ++_generationCounter;
        _store = new SqliteLogStore(settings);
    }

    public AutocompleteIndex Autocomplete => _autocompleteIndex;

    public IReadOnlyList<AutocompleteSuggestion> GetAutocompleteSuggestions(
        LogFilterField field,
        string? inputText,
        int maxResults = 8
    ) => _autocompleteIndex.GetSuggestions(field, inputText, maxResults);

    public bool IsListening { get; private set; }
    public bool IsPaused => _paused;
    public string StatusMessage
    {
        get => Volatile.Read(ref _statusMessage);
        private set => Volatile.Write(ref _statusMessage, value);
    }
    public int RetainedCount => Volatile.Read(ref _retainedCount);
    public int FilteredCount => Volatile.Read(ref _filteredCount);
    public int PausedBufferedCount => Volatile.Read(ref _pausedBufferedCount);
    public long TotalReceived => Interlocked.Read(ref _totalReceived);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long EvictedCount
    {
        get
        {
            lock (_sync)
                return _store.EvictedCount;
        }
    }
    public long Revision => Interlocked.Read(ref _revision);
    public int PendingQueueDepth => Volatile.Read(ref _pendingCount);
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public string DatabasePath => _store.DatabasePath;
    public int TcpClientCount => _tcpListener?.ClientCount ?? 0;
    public int PipeClientCount => _pipeListener?.ClientCount ?? 0;
    public int WsClientCount => _wsListener?.ClientCount ?? 0;
    public bool IsTcpListening => _tcpListener?.IsListening ?? false;
    public bool IsPipeListening => _pipeListener?.IsListening ?? false;
    public bool IsWsListening => _wsListener?.IsListening ?? false;
    public LogFilterQuery CurrentFilter => _filterEngine.Query;

    // The UI starts a single writer. Tests and noninteractive clients may drain explicitly.
    public void StartProcessing()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _processingTask ??= Task.Run(async () =>
            {
                try
                {
                    while (!_lifetime.IsCancellationRequested)
                    {
                        DrainBatch(256);
                        await Task.Delay(25, _lifetime.Token);
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            });
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
            if (_paused == paused)
                return;
            _paused = paused;
            _pausedBufferedCount = 0;
            if (!paused)
                _visibleUpperId = _store.LastId;
            UpdateCountsUnsafe();
            Interlocked.Increment(ref _revision);
            StatusMessage = paused
                ? "Screen paused; capture continues within retention limits"
                : "Resumed live log view";
        }
    }

    public bool TogglePaused()
    {
        SetPaused(!IsPaused);
        return IsPaused;
    }

    public void UpdateFilter(LogFilterQuery query) =>
        UpdateFilterAsync(query).GetAwaiter().GetResult();

    public async Task UpdateFilterAsync(
        LogFilterQuery query,
        CancellationToken cancellationToken = default
    )
    {
        var request = Interlocked.Increment(ref _searchRequest);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token
        );
        var token = linked.Token;
        await _searchGate.WaitAsync(token);
        try
        {
            await Task.Run(
                () =>
                {
                    var engine = new LogFilterEngine(query);
                    long generation;
                    long upper;
                    lock (_sync)
                    {
                        CheckSearch(request, token);
                        generation = query.IsEmpty ? 0 : ++_generationCounter;
                        _searchGeneration = generation;
                        _searchEngine = engine;
                        upper = _store.LastId;
                        _store.PruneGenerations(_viewGeneration, generation);
                        StatusMessage = "Searching history…";
                    }
                    try
                    {
                        if (generation != 0)
                        {
                            var after = 0L;
                            var titleMatch = engine.GetIndexedTitleMatch();
                            while (true)
                            {
                                CheckSearch(request, token);
                                IReadOnlyList<LogEntry> page;
                                lock (_sync)
                                {
                                    CheckSearch(request, token);
                                    page = _store.ScanPage(after, upper, titleMatch);
                                }
                                if (page.Count == 0)
                                    break;
                                var ids = new List<long>();
                                foreach (var entry in page)
                                {
                                    CheckSearch(request, token);
                                    if (engine.Matches(entry))
                                        ids.Add(entry.SequenceId);
                                }
                                lock (_sync)
                                {
                                    CheckSearch(request, token);
                                    if (ids.Count > 0)
                                        _store.AddMatches(generation, ids);
                                }
                                after = page[^1].SequenceId;
                            }
                        }
                        lock (_sync)
                        {
                            CheckSearch(request, token);
                            _filterEngine = engine;
                            _viewGeneration = generation;
                            _searchEngine = null;
                            _store.PruneGenerations(generation, generation);
                            UpdateCountsUnsafe();
                            Interlocked.Increment(ref _revision);
                            StatusMessage = "Search complete";
                        }
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            _searchEngine = null;
                            _store.PruneGenerations(_viewGeneration, _viewGeneration);
                        }
                    }
                },
                token
            );
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private void CheckSearch(long request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request != Interlocked.Read(ref _searchRequest))
            throw new OperationCanceledException();
    }

    public void Clear()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_queueSync)
            {
                _pendingQueue.Clear();
                _pendingCount = 0;
                _pendingBytes = 0;
            }
            ClearUnsafe();
        }
    }

    private void ClearUnsafe()
    {
        Interlocked.Increment(ref _searchRequest);
        _searchEngine = null;
        _store.Clear();
        _autocompleteIndex.Clear();
        _lastTimestamp = null;
        _pausedBufferedCount = 0;
        _visibleUpperId = _store.LastId;
        UpdateCountsUnsafe();
        Interlocked.Increment(ref _revision);
        StatusMessage = "Cleared log entries";
    }

    public void EnqueueLogEntry(LogEntry entry, string clientId = "local")
    {
        Interlocked.Increment(ref _totalReceived);
        // Includes space for UTF-16 decoding if the payload is searched or inspected.
        var bytes = entry.EstimatedMemoryBytes;
        if (bytes > 4L * BinaryPacketReader.MaxPacketPayloadSize)
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }
        Enqueue(new QueuedPacket(entry, null, clientId, bytes));
    }

    private void Enqueue(QueuedPacket item)
    {
        lock (_queueSync)
        {
            if (_disposed)
                return;
            var budget = Math.Max(1024, _settings.MaxPendingBytes);
            var countLimit = Math.Max(1, _settings.MaxPendingEntries);
            if (item.Bytes > budget)
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }
            while (
                _pendingQueue.Count > 0
                && (_pendingQueue.Count >= countLimit || _pendingBytes + item.Bytes > budget)
            )
            {
                _pendingBytes -= _pendingQueue.Dequeue().Bytes;
                Interlocked.Increment(ref _droppedCount);
            }
            _pendingQueue.Enqueue(item);
            _pendingBytes += item.Bytes;
            _pendingCount = _pendingQueue.Count;
        }
    }

    public BatchDrainResult DrainBatch(int maxBatchSize = 2000)
    {
        var drained = 0;
        var added = 0;
        var cleared = false;
        long bytes = 0;
        lock (_sync)
        {
            if (_disposed)
                return new(0, 0, 0, false, false);
            var previousCount = _filteredCount;
            var previousEvicted = _store.EvictedCount;
            var writes = new List<SqliteLogStore.Write>();
            var relays = new List<(LogEntry Entry, string ClientId)>();
            void Flush()
            {
                if (writes.Count == 0)
                    return;
                try
                {
                    _store.AddBatch(writes, _settings.MaxLogEntries);
                    foreach (var write in writes)
                    {
                        _autocompleteIndex.IndexEntry(write.Entry);
                        if (_paused)
                            _pausedBufferedCount++;
                        else if (write.Matches)
                            added++;
                    }
                    // Relay work is bounded by this batch rather than fire-and-forget tasks.
                    if (_relaySink.IsEnabled)
                        foreach (var relay in relays)
                            _relaySink
                                .RelayLogEntryAsync(relay.Entry, relay.ClientId, _lifetime.Token)
                                .GetAwaiter()
                                .GetResult();
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    Interlocked.Add(ref _droppedCount, writes.Count);
                    StatusMessage = $"Capture storage error (batch dropped): {ex.Message}";
                }
                writes.Clear();
                relays.Clear();
            }
            var batchBytes = Math.Clamp(_settings.MaxDiskBytes / 16, 64 * 1024, 1024 * 1024);
            while (drained < maxBatchSize && bytes < batchBytes)
            {
                QueuedPacket item;
                lock (_queueSync)
                {
                    if (!_pendingQueue.TryPeek(out var next))
                        break;
                    if (writes.Count > 0 && bytes + next.Bytes > batchBytes)
                        break;
                    item = _pendingQueue.Dequeue();
                    _pendingBytes -= item.Bytes;
                    _pendingCount = _pendingQueue.Count;
                }
                bytes += item.Bytes;
                drained++;
                if (item.ControlCommand != null)
                {
                    Flush();
                    ClearUnsafe();
                    cleared = true;
                    continue;
                }
                var entry = item.LogEntry!;
                ComputeElapsedTimeUnsafe(entry);
                writes.Add(
                    new(
                        entry,
                        _viewGeneration,
                        _filterEngine.Matches(entry),
                        _searchGeneration,
                        _searchEngine?.Matches(entry) ?? false
                    )
                );
                if (_relaySink.IsEnabled)
                    relays.Add((entry, item.ClientId));
            }
            Flush();
            if (!_paused)
                _visibleUpperId = _store.LastId;
            if (drained > 0)
            {
                _retainedCount = _store.Count;
                if (!_paused || cleared || _store.EvictedCount != previousEvicted)
                    UpdateCountsUnsafe();
                if (!_paused || cleared || _store.EvictedCount != previousEvicted)
                    Interlocked.Increment(ref _revision);
            }
            var trimmed = Math.Max(0, previousCount + added - _filteredCount);
            return new(
                drained,
                added,
                trimmed,
                cleared,
                cleared || (!_paused && (added > 0 || trimmed > 0))
            );
        }
    }

    private void UpdateCountsUnsafe()
    {
        _retainedCount = _store.Count;
        _filteredCount = _store.MatchCount(_viewGeneration, _visibleUpperId);
    }

    public LogPage GetPage(int offset = 0, bool tail = false, long anchorId = 0)
    {
        lock (_sync)
        {
            var size = Math.Clamp(_settings.PageSize, 1, 1000);
            var count = _filteredCount;
            if (tail)
                offset = Math.Max(0, count - size);
            else if (anchorId > 0)
                offset = _store.OffsetOf(anchorId, _viewGeneration, _visibleUpperId);
            else
                offset = Math.Clamp(offset, 0, Math.Max(0, (count - 1) / size * size));
            return new(
                _store.ReadPage(_viewGeneration, _visibleUpperId, offset, size, tail, anchorId),
                offset,
                count,
                Revision
            );
        }
    }

    // Compatibility helper is intentionally bounded; history consumers should page or stream.
    public IReadOnlyList<LogEntry> GetFilteredSnapshot()
    {
        lock (_sync)
        {
            var result = new List<LogEntry>();
            long bytes = 0;
            foreach (var summary in GetPage().Entries)
            {
                var entry = _store.ReadEntry(summary.SequenceId);
                if (entry == null)
                    continue;
                if (bytes + entry.EstimatedMemoryBytes > 16L * 1024 * 1024 && result.Count > 0)
                    break;
                result.Add(entry);
                bytes += entry.EstimatedMemoryBytes;
            }
            return result;
        }
    }

    public LogEntry? GetFilteredEntryAt(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _filteredCount)
                return null;
            var summary = _store
                .ReadPage(_viewGeneration, _visibleUpperId, index, 1)
                .FirstOrDefault();
            return summary == null ? null : _store.ReadEntry(summary.SequenceId);
        }
    }

    public LogEntry? GetEntry(long sequenceId)
    {
        lock (_sync)
            return _store.ReadEntry(sequenceId);
    }

    public async Task<int> LoadSilFileAsync(string filePath)
    {
        var loaded = 0;
        await foreach (
            var packet in new SilFileReader().ReadFileStreamingAsync(filePath, _lifetime.Token)
        )
        {
            if (packet is not LogEntry entry)
                continue;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ComputeElapsedTimeUnsafe(entry);
                _store.AddBatch(
                    [
                        new(
                            entry,
                            _viewGeneration,
                            _filterEngine.Matches(entry),
                            _searchGeneration,
                            _searchEngine?.Matches(entry) ?? false
                        ),
                    ],
                    _settings.MaxLogEntries
                );
                _autocompleteIndex.IndexEntry(entry);
                if (_paused)
                    _pausedBufferedCount++;
                else
                    _visibleUpperId = _store.LastId;
                loaded++;
                UpdateCountsUnsafe();
                Interlocked.Increment(ref _revision);
            }
        }
        StatusMessage =
            $"Loaded {loaded:N0} entries from {Path.GetFileName(filePath)} (retention applies)";
        return loaded;
    }

    public async Task<int> SaveSilFileAsync(string filePath, bool filteredOnly = false)
    {
        // A sequence upper bound excludes future arrivals. Retention may remove older entries
        // during export; never pin an unbounded snapshot in RAM or a long SQLite transaction.
        long upper;
        LogFilterEngine engine;
        lock (_sync)
        {
            upper = filteredOnly ? _visibleUpperId : _store.LastId;
            engine = _filterEngine;
        }
        var count = 0;
        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            65536,
            true
        );
        await stream.WriteAsync("SILF"u8.ToArray(), _lifetime.Token);
        var writer = new BinaryPacketWriter();
        long after = 0;
        while (true)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            LogEntry? entry;
            lock (_sync)
            {
                var id = _store.NextId(after, upper);
                if (id == 0)
                    break;
                entry = _store.ReadEntry(id);
                after = id;
            }
            if (entry == null || (filteredOnly && !engine.Matches(entry)))
                continue;
            await writer.WritePacketAsync(stream, entry, _lifetime.Token);
            count++;
        }
        StatusMessage = $"Saved {count:N0} entries to {Path.GetFileName(filePath)}";
        return count;
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
                // Only clear commands affect this view. Queue them under the same limits as logs.
                if (
                    controlCommand.ControlCommandType
                    is ControlCommandType.ClearLog
                        or ControlCommandType.ClearAll
                )
                    Enqueue(new QueuedPacket(null, controlCommand, e.ClientId, 128));
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
        _lifetime.Cancel();
        StopListenersAsync().GetAwaiter().GetResult();
        _processingTask?.GetAwaiter().GetResult();
        _searchGate.Wait();
        try
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                lock (_queueSync)
                {
                    _pendingQueue.Clear();
                    _pendingCount = 0;
                    _pendingBytes = 0;
                }
                _store.Dispose();
            }
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private sealed record QueuedPacket(
        LogEntry? LogEntry,
        ControlCommand? ControlCommand,
        string ClientId,
        long Bytes
    );
}

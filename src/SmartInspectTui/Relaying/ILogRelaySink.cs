using SmartInspectConsole.Core.Packets;

namespace SmartInspectTui.Relaying;

/// <summary>
/// Abstraction for relaying incoming SmartInspect log entries to external telemetry backends
/// such as an OpenTelemetry (OTLP) collector.
/// </summary>
public interface ILogRelaySink : IAsyncDisposable
{
    bool IsEnabled { get; }

    ValueTask RelayLogEntryAsync(
        LogEntry entry,
        string clientId,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Default no-op relay sink used until OpenTelemetry relaying is enabled.
/// </summary>
public sealed class NoOpOtelRelaySink : ILogRelaySink
{
    public bool IsEnabled { get; set; }

    public ValueTask RelayLogEntryAsync(
        LogEntry entry,
        string clientId,
        CancellationToken cancellationToken = default
    )
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

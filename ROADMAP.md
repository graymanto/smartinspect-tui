# SmartInspect TUI Roadmap

## Phase 1: Core Live TUI & Search (Completed)
- Cross-platform `SmartInspectConsole.Core` library (`net10.0`) supporting TCP (`4228`), Named Pipe (`smartinspect`), WebSocket (`4229`), and `.sil` file reading/writing.
- `Terminal.Gui` rolling log view with color-coded severity rows, Pause/Resume (`Space`/`F6`), Auto-Scroll (`F8`), and Clear (`Ctrl+L`).
- Fast full-text, property tag (`app:`, `session:`, `host:`, `level:`, `pid:`, `tid:`, `title:`, `data:`), and fuzzy (`FuzzySharp`) search engine.
- Split/toggleable Payload Detail Inspector (`F4`) with auto-detection for JSON, XML, Key-Value, Hex, and Text payloads.
- CLI and interactive (`Ctrl+O` / `Ctrl+S`) support for `.sil` recorded log files.

---

## Phase 2: OpenTelemetry (OTel) Log Relaying (Next Priority)
Relay incoming SmartInspect log entries directly to a local or remote OpenTelemetry (OTLP) listener/collector when enabled in Settings (`F9` or `~/.config/smartinspect-tui/settings.json`).

### Planned Implementation
1. **`OtlpLogRelaySink` implementing [`ILogRelaySink`](src/SmartInspectTui/Relaying/ILogRelaySink.cs)**:
   - Use `OpenTelemetry.Exporter.OpenTelemetryProtocol` (supporting both OTLP/gRPC on `http://localhost:4317` and OTLP/HTTP protobuf on `http://localhost:4318/v1/logs`).
   - Non-blocking bounded channel batching so slow or unreachable OTel collectors never stall SmartInspect ingestion.
2. **Attribute & Resource Mapping**:
   - `LogEntry.AppName` $\rightarrow$ OTel Resource `service.name` (falling back to `OtelRelayOptions.DefaultServiceName`)
   - `LogEntry.HostName` $\rightarrow$ OTel Resource `host.name`
   - `LogEntry.ProcessId` $\rightarrow$ OTel Resource `process.pid`
   - `LogEntry.Timestamp` $\rightarrow$ `LogRecord.Timestamp`
   - `LogEntry.LogEntryType` $\rightarrow$ `LogRecord.Severity` (`Debug`, `Trace`, `Info`, `Warn`, `Error`, `Fatal`)
   - `LogEntry.Title` $\rightarrow$ `LogRecord.Body`
   - `LogEntry.SessionName`, `ThreadId`, `ViewerId`, `Color`, and `DataAsString` $\rightarrow$ structured `smartinspect.*` attributes on the exported `LogRecord`.

---

## Phase 3: Multi-Tab Views, Watches & Process Flow
- Multiple saved/named filter tabs in the TUI (e.g., `F2` to add/switch tabs).
- Live **Watches** table view (`Watch` packets) and **Process Flow** call/thread tree view (`ProcessFlow` packets).
- Connected Applications drawer with per-application mute toggles.

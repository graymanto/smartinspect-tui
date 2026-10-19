# SmartInspect TUI (`smartinspect-tui`)

A fast, cross-platform Terminal User Interface (TUI) for receiving, inspecting, and searching **SmartInspect** logs on Linux, macOS, and Windows.

---

## Features

- **Live SmartInspect Listeners**:
  - **TCP** listener (default port `4228`)
  - **Named Pipe / Unix Domain Socket** listener (default pipe `smartinspect`)
  - **WebSocket** listener for browser/JS clients (default port `4229`)
- **Rolling Virtualized Log Screen**:
  - High-throughput lock-free packet queueing with 50ms batched UI draining and automatic retention trimming (default `20,000` entries).
  - Color-coded rows by severity (`Debug`, `Verbose`, `Message`, `Warning`, `Error`, `Fatal`, `EnterMethod`/`LeaveMethod`, `Separator`).
  - **Pause / Resume (`Space` or `F6`)**: Freeze the visible log screen for inspection while background listeners continue acknowledging and buffering incoming traffic so client applications never block.
  - **Auto-Scroll (`F8`)**: Automatically follow the tail of incoming logs.
  - **Clear (`Ctrl+L`)**: Clear all retained and displayed entries.
- **Fuzzy & Structured Property Search (`/`)**:
  - Dedicated quick-filter inputs for **Search**, **App**, **Session**, and **Min Level**.
  - Inline property tag syntax (`app:`, `session:`, `host:`, `level:`, `pid:`, `tid:`, `title:`, `data:`, and negation `-app:Noise`).
  - Fast substring and **FuzzySharp** partial/subsequence matching across all properties and message payloads.
- **Split Payload Detail Inspector (`F4`)**:
  - Automatically detects and pretty-prints `JSON`, `XML`, `Key-Value`, `Binary (Hex)`, and `Text` payloads (`F7` to cycle format mode).
- **`.sil` Log File Support**:
  - Open recorded `.sil` files from the command line (`smartinspect-tui file.sil`) or interactively via `Ctrl+O`.
  - Save retained entries to a `.sil` file via `Ctrl+S`.

---

## Quick Start

### Build & Run

```bash
dotnet build SmartInspectTui.sln
dotnet run --project src/SmartInspectTui
```

### Command-Line Options

```text
Usage:
  smartinspect-tui [options] [file.sil]

Options:
  -p, --port <port>       TCP listener port (default: 4228)
  --no-tcp                Disable TCP listener
  --pipe <name>           Named pipe listener name (default: smartinspect)
  --no-pipe               Disable named pipe listener
  --ws-port <port>        WebSocket listener port (default: 4229)
  --no-ws                 Disable WebSocket listener
  --max-entries <count>   Maximum retained log entries (default: 20000)
  --no-listen             Do not start live listeners automatically on launch
  -h, --help              Show help message
```

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `/` | Focus the Filter input |
| `F2` | Select Filter Field (`Any`, `Lvl`, `App`, `Session`, `TID`, `PID`, `Host`, `Title`, `Data`) |
| `Tab` / `Up` / `Down` | Navigate and complete from the Excel-style Autocomplete popup |
| `Enter` (in Filter input) | Pin the current filter to the `Active:` filter chips bar |
| `Backspace` (in empty Filter input) | Remove (pop) the last pinned filter chip |
| `Delete` / `Backspace` / `Enter` / `Space` (on focused chip) | Remove that specific filter chip |
| `Ctrl+R` | Clear all active filter chips and reset the filter input |
| `Esc` | Close Autocomplete popup or return focus from Filter input to the Log Table |
| `Space` or `F6` | Pause / Resume live log screen updates |
| `F3` | Toggle Fuzzy search matching (`ON` / `OFF`) |
| `F4` or `Enter` | Toggle / focus the Payload Detail Inspector pane |
| `F5` | Start / Stop live listeners |
| `F7` | Cycle Payload format (`Auto`, `JSON`, `XML`, `KeyValue`, `BinaryHex`, `Text`) |
| `F8` | Toggle Auto-Scroll to newest entries |
| `F9` | Open Settings dialog (Ports, Pipe, Retention, OTel Relay) |
| `Ctrl+L` | Clear all retained and visible log entries |
| `Ctrl+O` | Open a `.sil` log file |
| `Ctrl+S` | Save retained logs to a `.sil` file |
| `Ctrl+Q` | Quit `smartinspect-tui` |

---

## Search & Filter Syntax

Type into the **Search (`/`)** box to filter live in real time:

- **Free text / Fuzzy**: Any unprefixed words match across `Title`, `Payload Data`, `AppName`, `SessionName`, `HostName`, `LogEntryType`, `ProcessId`, and `ThreadId`.
- **Property prefixes**:
  - `app:AuthService` (or comma-separated `app:Auth,Gateway`)
  - `session:Main` or `sess:Database`
  - `host:prod-01`
  - `level:Warning+` or `level:Error` or `type:EnterMethod`
  - `pid:1234` / `tid:42`
  - `title:"user login"`
  - `data:NpgsqlException`
- **Negation**: Prefix any token with `-` or `!` (e.g., `-app:HealthCheck -level:Debug`).

---

## Development & CSharpier Formatting

This repository enforces [CSharpier](https://csharpier.com/) formatting enforced with a Git `pre-commit` hook (`.githooks/pre-commit`).

```bash
# Restore local tools (csharpier)
dotnet tool restore

# Format all files
dotnet csharpier format .

# Check formatting
dotnet csharpier check .

# Ensure Git uses the repository hooks directory
git config core.hooksPath .githooks
```

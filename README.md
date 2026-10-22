# SmartInspect TUI (`smartinspect-tui`)

A fast, cross-platform Terminal User Interface (TUI) for receiving, inspecting, and searching **SmartInspect** logs on Linux, macOS, and Windows.

---

## Features

- **Live SmartInspect Listeners**:
  - **TCP** listener (default port `4228`)
  - **Named Pipe / Unix Domain Socket** listener (default pipe `smartinspect`)
  - **WebSocket** listener for browser/JS clients (default port `4229`)
- **Rolling Virtualized Log Screen**:
  - Bounded packet queueing, background SQLite batch writes, and a paged log view (default disk history: up to `2,000,000` entries within a `2 GiB` budget).
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
  --max-entries <count>   Maximum disk history entries (default: 2000000)
  --disk-mb <MiB>         Disk budget including journal headroom (default: 2048)
  --queue-mb <MiB>        Incoming queue memory budget (default: 16)
  --storage-dir <path>    Directory for temporary disk history
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
| `[` / `]` | Browse previous / next history page (table focus); Ctrl+PageUp / Ctrl+PageDown also work where supported |
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

## Long-running capture and storage limits

Captured logs are stored in a temporary SQLite database. The UI keeps one page of
summaries (500 rows by default), loads payloads on selection, and limits payload
previews to 64 KiB. Full titles and payloads remain available in `.sil` exports.
Use **[ / ]** (or **Ctrl+PageUp / Ctrl+PageDown** where supported) with the table focused to browse history;
**F8** resumes following the newest entries. Selection follows stable entry IDs.

Default limits:

| Setting | Default | Behavior |
| --- | --- | --- |
| `MaxPendingBytes` / `--queue-mb` | 16 MiB | Estimated queued memory, including potential text decoding; oldest pending packets are dropped on overflow. |
| `MaxPendingEntries` | 10,000 | Secondary queue count limit. |
| `MaxDiskBytes` / `--disk-mb` | 2 GiB | Half is available to the SQLite database; half is reserved for rollback journal and maintenance headroom. Oldest history is evicted when full. |
| `MaxLogEntries` / `--max-entries` | 2,000,000 | Secondary disk history count limit. Existing configured limits are preserved. |
| `PageSize` | 500 | Display rows per page, clamped to 1–1,000. |
| SQLite page cache | 8 MiB | Separate from the incoming queue and .NET/runtime memory. |

TCP, pipe, file and WebSocket messages have a 16 MiB packet/message limit. An entry
larger than the queue budget or the per-entry allowance for the disk quota is dropped. Oversized or unwritable storage reports a
capture error; capture continues with bounded buffers. The status bar reports
queue memory and dropped entries. Pausing freezes the view while capture and
retention continue: expired entries are not pinned in memory or on disk.

Searches run in the background, are debounced, and can be superseded by another
query. Non-fuzzy positive title searches of at least three ASCII characters use
an FTS5 trigram index to narrow candidates. Other filters, including fuzzy,
payload, short text, and negation, use bounded scans with the same filter
semantics as live logs. These scans can take longer on large histories. Recent
logs continue to be captured during searches.

Import and export stream entries; they do not load all history into memory.
Export fixes the filter and upper entry ID at the start, but retention can still
expire older entries while an export is running. Stop listeners before exporting
if every currently retained entry must be included.

`--storage-dir` (or `StorageDirectory` in settings) selects the cache location;
use a filesystem with sufficient disk space rather than a RAM-backed temporary
filesystem. The default is `smartinspect-tui/captures` under the platform's local
application data directory. Each instance has its own cache and quota. Caches
are removed on normal exit; abandoned caches older than an hour are cleaned up
on a subsequent launch. **Export `.sil` files to keep captures after exit.**
Disk quota changes in F9 settings take effect after restarting the application.

These are component budgets, not a hard process RSS limit. Parsing, one bounded
search batch, payload inspection, SQLite indexing, and the runtime need additional
memory; memory use should plateau rather than grow with capture duration.

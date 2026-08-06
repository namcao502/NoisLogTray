# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 WinForms system-tray app (`net10.0-windows`, `WinExe`) that automates daily
work-log entries to two destinations from a single tray icon:

- **TSC**: a shared Excel workbook on SharePoint, written via the Microsoft Graph
  Excel API (`GraphTscClient`).
- **HRM**: `api-hrm.nois.vn` via its MCP server over Streamable HTTP (`HrmMcpClient`).

This started as a C# port of a TypeScript web app at `C:\Project\log-system`, so many
source files and tests still carry `// port of lib/*.ts` comments from that origin.
**That web app is being retired; this .NET app is now the source of truth.** Do not
treat parity with the old app as a constraint - change behavior on its own merits, and
feel free to drop the stale "port of ..." comments as you touch files.

## Build / run / test

`NoisLogTray.slnx` (the SDK's current XML solution format) ties the four projects
together: the main app, the test project, and the two smoke exes. All target
`net10.0` (`-windows` for the app and tests). Run from the repo root.

```sh
# Build everything (resolves NoisLogTray.slnx)
dotnet build

# ...or just the tray app
dotnet build NoisLogTray.csproj

# Run the tray app (starts the NotifyIcon; there is no console UI)
dotnet run --project NoisLogTray.csproj

# All unit tests (xUnit)
dotnet test tests/NoisLogTray.Tests

# A single test class / test
dotnet test tests/NoisLogTray.Tests --filter "FullyQualifiedName~TimeSlotsTests"
dotnet test tests/NoisLogTray.Tests --filter "DisplayName~SplitsTwoTicketsEvenly"
```

The main `.csproj` excludes `tests/**` and `smoke/**` from compilation, and exposes
internals to `NoisLogTray.Tests` via `InternalsVisibleTo`. Almost everything is
`internal`; tests reach internals directly rather than through a public surface.

### Distribute to other users

```sh
# Self-contained single-file build (no .NET install needed on the target PC)
dotnet publish NoisLogTray.csproj -p:PublishProfile=win-x64
```

Output: `bin/publish/win-x64/` - a single `NoisLogTray.exe` (runtime baked in) plus
the `.playwright/` driver folder and `.env.example`. Zip that folder and share it.
The csproj marks `.env` `CopyToPublishDirectory=Never`, so **no secrets ship**; each
recipient fills the first-run dialog (writes `%AppData%\NoisLogTray\.env`). Google
Chrome must be installed on the target PC (the TSC sniff/re-auth uses `channel=chrome`).

### Smoke projects (manual, not part of the build)

`smoke/SniffSmoke` and `smoke/HrmSmoke` are standalone `net10.0` console exes that
verify the two risky integrations against **real credentials** (read from the
project `.env`). Run them by hand when touching Playwright or MCP:

```sh
dotnet run --project smoke/SniffSmoke   # Playwright can launch Chrome + sniff a Graph token
dotnet run --project smoke/HrmSmoke     # C# MCP client connects and sees log_timesheet
```

Playwright needs a browser installed once. `channel=chrome` uses the system Chrome:
`pwsh bin/Debug/net10.0-windows/playwright.ps1 install chromium`.

## Architecture

Layered, with the UI at the top and two integration clients at the bottom. Pure
logic is isolated into small, unit-tested modules.

Folders follow the backend's WindowsApps convention (`Backend-DotNet/src/WindowsApps`)
and all share the flat `NoisLogTray` namespace (folder does not equal namespace):
- `Services/` - service + external-client classes: `LoggingService`, `SixPmScheduler`,
  `StartupService`, `GraphTscClient`, `HrmMcpClient`, `JiraClient`, `TscTokenSniffer`,
  `TicketQueue`.
- `Helpers/` - utilities: `TicketParser`, `TimeSlots`, `TscCells`, `Timesheet`, `Hcm`,
  `Env`, `AppPaths`, `AppLogger`, `BrowserLock`, `AppConfig`, `AppSettings`, `AppIcon`.
- `Models/` - record types only: `QueueEntry`, `JiraSuggestion`/`JiraVerifyResult`,
  `DrainResult`/`EntryLogResult`, `GraphTscOptions`, `TimeSlot`, `ToolEnvelope`.
- `Interface/` - abstractions (`IJiraClient`, implemented by `JiraClient`).
- `UI/` - WinForms. `Theme` is a central light/dark palette; controls read it at paint
  time and subscribe to `Theme.Changed` to repaint (the header's Dark/Light button calls
  `Theme.Toggle`; `MainForm.ApplyTheme` re-colors native controls). It defaults to dark
  and persists the choice through `AppSettings` (`Theme.Load` runs in `Program.Main`;
  `Theme.Toggle` does a read-modify-write so it keeps the window position). Also `TrayApp`,
  `MainForm`, `CredentialsForm` (the themed first-run / edit-credentials dialog), and
  owner-drawn controls: `MacButton`
  (rounded button), `Card` (rounded card surface), `RoundedHost` (rounded border
  around native controls like the list/textbox), `RoundedDatePicker` (rounded date
  field with a custom `ModernCalendar` popup in a rounded `CalendarPopupForm`, replacing
  the native `DateTimePicker`/`MonthCalendar`), `WillLogRow` (one owner-drawn Will Log
  line), and `MiniProgress` (thin rounded bar).
- `Program.cs` stays at the repo root.

- **`Program.cs`** - entry point. Single-instance `Global\` mutex, global exception
  handlers routed to `AppLogger`, then `Application.Run(new TrayApp())`.
- **`TrayApp` (ApplicationContext)** - owns the process lifetime, the `NotifyIcon`
  + context menu, and the scheduler. Background work (sniff, MCP, drain) runs off
  the UI thread and is marshaled back through a hidden `Control` (`RunOnUi`) for
  tooltip/balloon/log updates. `_draining` is an `Interlocked` guard so only one
  drain runs at a time. When config is missing at startup it shows `CredentialsForm`
  (first-run), and the "Edit credentials..." menu item reopens it; saving writes the
  per-user `.env` and rebuilds `_service`.
- **`MainForm`** - the capture window: a standard-chrome window structured like the
  old web app - a header (title + date), then stacked `Card`s: "Log entries" (a
  "My tickets" list from `LoggingService.GetMyTicketsAsync`, click a row to add it,
  + date/ticket), "Will log" (a live preview of the date + one `WillLogRow` per
  ticket - a status dot, the colored ticket key, and its time slots; the dot reflects
  Jira verification, green valid / red not found / amber error, via `VerifyTicketsAsync`
  debounced on typing and on blur, suggestions pre-marked valid. The card grows to fit
  all rows via `FitWillLogToContent` - no inner scroll), and "Actions" (Queue / Log now / Log TSC /
  Log HRM / Check TSC / Re-auth). A read-only "Queued for 6 PM" card shows the persisted
  queue (from `queue.json`) with a Clear button - refreshed on queue/clear, on window
  activate, and after a drain (`RefreshQueuedView`, also called by `TrayApp`). A large
  top status bar (hidden until used) shows
  every activity line live (`AppendLog` -> log file + status) and the green/red result
  of each action (`ShowStatus`), auto-cleared. Just below it, a log run shows TSC/HRM
  progress bars - `LoggingService` forwards `onProgress(done,total)` callbacks to
  `GraphTscClient` / `HrmMcpClient`. Ticket-dependent buttons are gated on valid input
  via `UpdateActionState`.
  `MacButton`, `Card`, and `RoundedHost` are owner-drawn
  (no third-party UI library). Closing (X) **hides to tray**; `TrayApp` owns exit.
  Icons come from the embedded `app.ico` via `AppIcon.Load(size)`. The window position
  is persisted via `AppSettings` (saved on move/close, restored on open only if it
  still lands on a connected monitor, else centered).
- **`LoggingService`** - orchestrator over both destinations plus Jira. `DrainQueueAsync`
  is the core: sniff one Graph token, log every queued entry to both destinations
  (`LogEntryAsync` runs TSC + HRM in parallel), and remove only entries that fully
  succeeded. All methods take an `Action<string>? onLog` callback so the UI streams
  progress. `AcquireGraphTokenAsync` **caches** the sniffed token (reused until ~2 min
  before its JWT `exp`, guarded by a `SemaphoreSlim`) so repeated TSC actions don't each
  relaunch Chrome; `InvalidateGraphToken` clears it and is called after a successful
  re-auth.

Bottom-layer clients:

- **`GraphTscClient`** - writes the ticket to the Excel workbook via Graph. Target
  `row = 2 + dayOfYear`; the columns default to `M` (primary) and `J` (mirror) but are
  per-user configurable via `GraphTscOptions.Columns` (`TSC_GRAPH_COLUMNS`), one
  worksheet per year. Uses a persistent workbook session (`persistChanges:true`).
  **Fail-closed date safety**: reads column B for the row and aborts if it does not
  match the expected `M/D/YYYY`, to avoid ever logging the wrong day.
- **`HrmMcpClient`** - calls the `log_timesheet` MCP tool (Bearer = `HRM_API_KEY`).
  A time slot straddling lunch becomes two calls (first creates the task, second
  appends).
- **`TscTokenSniffer`** - Playwright-driven token source for Graph. Runs **headless**
  to sniff a `Files.ReadWrite.All` Bearer off `office.com`/SharePoint from the saved
  session, and to check the session; **only re-auth opens a visible browser**. Uses a
  single on-disk Chrome profile at `~/.tsc-daily-log-browser`.
- **`JiraClient`** - Jira Cloud REST (basic auth): verify a ticket, list "my tickets".

Support: `AppConfig` + `Env` (config), `AppPaths` (per-user paths), `Hcm` (timezone),
`BrowserLock`, `AppLogger`, `StartupService` (Run-key logon registration),
`SixPmScheduler`. Pure/tested helpers: `TscCells`, `TimeSlots`, `Timesheet`,
`TicketParser`, `TicketQueue`.

## Cross-cutting rules to preserve

- **Idempotent re-runs.** Re-logging is safe: TSC skips a cell already equal to the
  ticket; HRM treats `LOGTIME_OVERLAP` as an already-done skip. `DrainQueueAsync`
  therefore only removes fully-succeeded entries and keeps the rest for retry. Do
  not change drain to remove partially-failed entries.
- **All time is Asia/Ho_Chi_Minh** and must go through `Hcm` (no DST; UTC+7 fallback
  if the tz lookup fails). Worksheet year, day-of-year row, and HRM `workDate` all
  derive from it. Consequence: HRM rejects future stop times, so **today's** queue
  only succeeds from 18:00 on; past dates work anytime. `SixPmScheduler` fires the
  auto-drain at 18:00 HCM in-process (replacing the old external Task Scheduler job).
  It **polls every 60s** rather than arming one long one-shot timer, so a sleep/wake or
  clock change can't silently skip the fire (a late wake still drains within ~1 min).
  Complementing it, `TrayApp.CatchUpIfDue` drains once on startup when the queue already
  has a due entry (a past date, or today once it's 18:00+) - covers the app not running
  at 18:00.
- **One browser at a time.** `LaunchPersistentContextAsync` locks the profile on
  disk, so every Playwright entry point goes through `BrowserLock.TryAcquire()` /
  `Release()` (reject-fast, not queued). HRM logging is browser-free and can run in
  parallel with a TSC sniff.
- **UI-thread marshaling.** Never touch WinForms controls from a background task
  directly - use `TrayApp.RunOnUi` or the `InvokeRequired`/`BeginInvoke` guards in
  `MainForm`. WinForms `async void` event handlers are the intended style here.
- **Config load never crashes startup.** `AppConfig.TryLoad` returns `null` + a
  message on a missing required key; on `null` the tray shows `CredentialsForm`
  (first-run) to collect the values, and if still missing surfaces a warning balloon
  and disables logging rather than throwing. Config is **layered** (`AppConfig.DefaultSources`,
  later wins): the app-directory `.env` (optional shared, non-secret defaults - the
  dev's project `.env` is copied here via the csproj and gitignored) then the per-user
  `.env` at `%AppData%\NoisLogTray\.env` (each user's secrets, written by the dialog via
  `AppConfig.SaveUserEnv`), then process environment. **Don't ship a build with the
  dev's app-dir `.env`** - distribute without it (see `.env.example`) so each user fills
  their own on first run.
  Required keys: `JIRA_BASE_URL`, `JIRA_EMAIL`, `JIRA_API_TOKEN`, `HRM_API_KEY`.
  Optional: `TSC_GRAPH_COLUMNS` (per-user columns in the shared workbook; default
  `M, J` via `TscCells.TargetColumns`, parsed by `TscCells.ParseColumns`),
  `HRM_PROJECT_ID`, `MS_GRAPH_TOKEN` (override that skips the sniff),
  `TSC_GRAPH_DRIVE_ID` / `_ITEM_ID` / `_SHARE_URL` / `_WORKSHEET`.

## Per-user data

Everything runtime lives under `%AppData%\NoisLogTray` (see `AppPaths`): `queue.json`
(the pending log queue), `settings.json` (theme + window position, via `AppSettings`),
`.env` (per-user config/secrets written by `CredentialsForm`, `AppPaths.EnvPath`), and
`logs/app.log`. A missing or malformed `queue.json` yields an empty queue by design
so the 18:00 runner never throws; `AppSettings` likewise falls back to defaults on a
missing/bad `settings.json`. The TSC Chrome profile (the saved Microsoft session) lives
separately at `%UserProfile%\.tsc-daily-log-browser` (`TscTokenSniffer.ProfileDir`).

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

**Releases + update check.** CI lives in `.github/workflows/`: `ci.yml` builds + tests
the solution on every push/PR; `release.yml` fires on a `v*` git tag, runs the publish
above with `-p:Version` derived from the tag, and attaches the zip to a GitHub Release.
On startup the app calls `UpdateService.CheckAsync` (anonymous GitHub Releases API for the
public `namcao502/NoisLogTray` repo) and, if the latest tag beats the running `<Version>`
(csproj, default `1.0.0`), reveals a tray "Download update..." item + a balloon that opens
the release page. Any failure/offline is silent. So a release = tag `vX.Y.Z` (numeric, so
the tag parses to a `System.Version`); users get notified on their next launch.

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
  `TicketQueue`, `UpdateService` (startup GitHub-Releases update check),
  `OffDaySync` (marks approved leave `OFF` in TSC; see "Days off" below).
- `Helpers/` - utilities: `TicketParser`, `TimeSlots`, `TscCells`, `Timesheet`, `Leave`,
  `Hcm`, `Env`, `AppPaths`, `AppLogger`, `BrowserLock`, `AppConfig`, `AppSettings`,
  `AppIcon`, `OffDayStore`,
  `Retry` (transient-transport-failure retry with backoff, used by the Jira/Graph/HRM clients).
- `Models/` - record types (+ the `CredentialCheck` enum): `QueueEntry`,
  `JiraSuggestion`/`JiraVerifyResult`, `DrainResult`/`EntryLogResult`/`OffWriteResult`,
  `GraphTscOptions`, `TimeSlot`, `ToolEnvelope`, `CredentialCheck`, `UpdateInfo`.
- `Interface/` - abstractions (`IJiraClient`, implemented by `JiraClient`).
- `UI/` - WinForms. `Theme` is a central light/dark palette; controls read it at paint
  time and subscribe to `Theme.Changed` to repaint (the header's Dark/Light button calls
  `Theme.Toggle`; `MainForm.ApplyTheme` re-colors native controls). It defaults to dark
  and persists the choice through `AppSettings` (`Theme.Load` runs in `Program.Main`;
  `Theme.Toggle` does a read-modify-write so it keeps the window position). Also `TrayApp`,
  `MainForm`, `CredentialsForm` (the themed first-run / edit-credentials dialog),
  `WeeklyCheckForm` (weekly coverage: HRM hours + TSC ticket per weekday, opened from the
  tray "Weekly check..." item; an under-logged past weekday is a clickable `ClickableRow`
  that raises `LogDayRequested` -> `TrayApp` opens `MainForm` on that date via
  `MainForm.PrepareForDate`. An approved day off (`DayCoverage.IsOff`) reads gray "off"
  for HRM and is judged only on the TSC marker - green `OFF`, else amber "off - not
  marked" and clickable, including when it is still in the future so planned leave can
  be marked ahead), and
  owner-drawn controls: `MacButton`
  (rounded button), `Card` (rounded card surface), `RoundedHost` (rounded border
  around native controls like the list/textbox), `RoundedDatePicker` (rounded date
  field with a custom `ModernCalendar` popup in a rounded `CalendarPopupForm`, replacing
  the native `DateTimePicker`/`MonthCalendar`), `WillLogRow` (one owner-drawn Will Log
  line), `ClickableRow` (a focusable/keyboard-activatable `Panel` for clickable list rows -
  My-tickets suggestions and actionable weekly days), `MiniProgress` (thin rounded bar), and
  `ActivityLogPanel` (the bottom activity
  block - a self-theming control owning the scrolling console + TSC/HRM progress bars;
  `MainForm` just delegates `AppendLog`/`ShowStatus`/`ShowProgress` to it).
  Owner-drawn interactive controls (`MacButton`, `RoundedDatePicker`, `ClickableRow`) paint
  a keyboard-focus ring and set `AccessibleName`/`AccessibleRole` so the app is operable by
  keyboard and screen reader.
- `Program.cs` stays at the repo root.

- **`Program.cs`** - entry point. Single-instance `Global\` mutex, global exception
  handlers routed to `AppLogger`, then `Application.Run(new TrayApp())`.
- **`TrayApp` (ApplicationContext)** - owns the process lifetime, the `NotifyIcon`
  + context menu, and the scheduler. Background work (sniff, MCP, drain) runs off
  the UI thread and is marshaled back through a hidden `Control` (`RunOnUi`) for
  tooltip/balloon/log updates. `_draining` is an `Interlocked` guard so only one
  drain runs at a time. When config is missing it runs `RunFirstRunSetup` (a
  `BeginInvoke` after the message loop starts, not a modal in the ctor) to show
  `CredentialsForm`; the "Edit credentials..." menu item reopens it. Saving writes the
  per-user config into `settings.json`, rebuilds `_service` via `ReloadServiceAndShow`,
  and opens the window so the user sees it worked. The dialog **verifies** the entered credentials before
  saving (Jira via `/myself`, HRM via an MCP connect) and rejects a bad token/key inline;
  an unreachable service falls back to a "save anyway?" prompt so offline setup isn't
  blocked. A successful TSC re-auth (tray or window, via the `ReauthSucceeded` event)
  calls `CatchUpIfDue` to retry any queue entries that were waiting on sign-in.
- **`MainForm`** - the capture window: a standard-chrome window structured like the
  old web app - a header (title + date), then stacked `Card`s: "Log entries" (a
  "My tickets" list from `LoggingService.GetMyTicketsAsync` - each row shows the key,
  summary, then a right-aligned due date - click a row to add it; an "Edit JQL" button
  opens `JqlForm` to customise the query driving that list (validated against Jira, then
  persisted + re-fetched), + date/ticket), "Will log" (one row per ticket - a
  status dot, the colored
  ticket key, and its time slots; the dot reflects Jira verification, green valid /
  red not found / amber error, via `VerifyTicketsAsync` debounced on typing and on
  blur, suggestions pre-marked valid. While typing it previews the typed tickets for
  the selected date, using an **editable `WillLogEditRow`** with an inline hours field
  per ticket: default is the even split (`TimeSlots.EvenSplit`), editing sets a custom
  per-ticket duration (partial day allowed, day capped at 8h; over-8h disables Queue /
  Log now / Log HRM with a header hint). Custom durations ride the queue as
  `QueueEntry.Minutes` (null = even split) and drive the HRM slots; TSC ignores time.
  With the input empty it falls back to the **whole persisted queue** grouped by date
  (each headed "(queued for 6 PM)"), shown read-only via `WillLogRow`, and shows a
  "Clear queue" button - this is the single view of what's scheduled (there is no
  separate queue card). The card is **fixed height and scrolls internally**
  (`WillLogHostH`) so the Actions card below stays visible with a long queue),
  and "Actions" (Queue / Log now, then a 5-up row: Log TSC /
  Log HRM / Log OFF / Check TSC / Re-auth). "Log OFF" needs no ticket and takes any date,
  confirms, then writes the `OFF` marker for the selected day and drops that day's queued
  entries; it is the only path that overwrites a cell holding real work.
  `RefreshQueuedView` (called on queue/clear, on window
  activate, and after a drain, including by `TrayApp`) just re-renders "Will log". At the
  bottom of the window a docked "Activity" card holds a scrolling console log that
  streams every activity line live (`AppendLog` -> log file + console) and the green/red
  result of each action (`ShowStatus`); it is persistent (no auto-clear) and capped to
  the last `ActivityCap` lines, re-rendered on a theme switch so old lines re-color. A
  log run shows TSC/HRM progress bars inside that same card (the console shrinks to make
  room) - `LoggingService` forwards `onProgress(done,total)` callbacks to
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
  re-auth. `CheckWeekAsync` is the read-back path (weekly coverage): one Graph token,
  then `GraphTscClient.ReadTicketsAsync` + `HrmMcpClient.GetDayHoursAsync` +
  `GetOffDatesAsync` in parallel, merged into `DayCoverage` per date (future dates
  skipped -> nulls, but leave is read for the whole week so a planned day off reads
  "off", not "pending"). `LogOffAsync` writes the `OFF` marker (see "Days off").

Bottom-layer clients:

- **`GraphTscClient`** - writes the ticket to the Excel workbook via Graph. Target
  `row = 2 + dayOfYear`; the columns default to `M` (primary) and `J` (mirror) but are
  per-user configurable via `GraphTscOptions.Columns` (`TSC_GRAPH_COLUMNS`), one
  worksheet per year. Uses a persistent workbook session (`persistChanges:true`).
  **Fail-closed date safety**: reads column B for the row and aborts if it does not
  match the expected `M/D/YYYY`, to avoid ever logging the wrong day. `ReadTicketsAsync`
  reads back the ticket cell per date for the weekly check (same B sanity, read-only).
  `WriteOffAsync` writes `TscCells.OffMarker` on `TscCells.OffFillColor`
  (`PATCH .../format/fill`); with `overwrite:false` it leaves a cell holding real work
  alone and reports it as skipped. Writing a real ticket over an `OFF` cell clears the
  fill (`POST .../format/fill/clear`) so no stray yellow is left behind.
- **`HrmMcpClient`** - calls the `log_timesheet` MCP tool (Bearer = `HRM_API_KEY`).
  A time slot straddling lunch becomes two calls (first creates the task, second
  appends); a hard-failing ticket is skipped so the rest still log (partial failure).
  `GetDayHoursAsync` reads back total hours per date via the `get_my_day_logs` tool
  (parsed by `Timesheet.ParseDayHours`) for the weekly check. `GetOffDatesAsync` reads
  approved leave via `find_my_requests` (parsed by `Leave.ParseOffDates`).
- **`TscTokenSniffer`** - Playwright-driven token source for Graph. Runs **headless**
  to sniff a `Files.ReadWrite.All` Bearer off the M365 shell/SharePoint from the saved
  session, and to check the session; **only re-auth opens a visible browser**. Uses a
  single on-disk Chrome profile at `~/.tsc-daily-log-browser`.
  The broad `.All` token must come from the M365 shell (app `M365ChatClient`); the
  OneDrive surfaces only ever mint own-file `Files.ReadWrite`, which 403s on the shared
  workbook (it lives in another user's OneDrive). `www.office.com` now 301s to
  `m365.cloud.microsoft`, whose **root is an anonymous marketing page** that renders a
  "Sign in" link and never auto-SSOs - so the sniff targets
  `m365.cloud.microsoft/login?ru=%2F` directly, which does the top-level auth redirect
  and completes silently against the saved session. If a future migration breaks this
  again the symptom is a narrow-scope token plus `shares/driveItem -> 403 accessDenied`,
  and `smoke/SniffSmoke` reproduces it in isolation.
- **`JiraClient`** - Jira Cloud REST (basic auth): verify a ticket, list "my tickets".

Support: `AppConfig` + `Env` (config), `AppPaths` (per-user paths), `Hcm` (timezone),
`BrowserLock`, `AppLogger`, `StartupService` (Run-key logon registration),
`SixPmScheduler`, `OffDaySync` (leave watcher). Pure/tested helpers: `TscCells`,
`TimeSlots`, `Timesheet`, `Leave`, `TicketParser`, `TicketQueue`, `OffDayStore`.

## Cross-cutting rules to preserve

- **Idempotent re-runs.** Re-logging is safe: TSC skips a cell already equal to the
  ticket; HRM treats `LOGTIME_OVERLAP` as an already-done skip (and continues past a
  hard-failing ticket so one bad ticket doesn't block the rest, reporting a partial
  failure). `DrainQueueAsync` therefore only removes fully-succeeded entries and keeps
  the rest for retry. Do not change drain to remove partially-failed entries. Removal
  goes through `TicketQueue.RemoveLogged`, a locked read-modify-write against the
  CURRENT queue (removing only the processed entries), so a ticket queued concurrently
  during a drain is never clobbered - do not revert this to overwriting with a snapshot.
- **All time is Asia/Ho_Chi_Minh** and must go through `Hcm` (no DST; UTC+7 fallback
  if the tz lookup fails). Worksheet year, day-of-year row, and HRM `workDate` all
  derive from it. Consequence: HRM rejects future stop times, so **today's** queue
  only succeeds from 18:00 on; past dates work anytime. `MainForm.HrmClosedForToday`
  blocks Log now / Log HRM for today before 18:00 with an explanatory status (Queue
  instead), rather than letting HRM fail confusingly. `SixPmScheduler` fires the
  daily run in-process at a **configurable** time (`LOG_TIME`, default 18:00 HCM;
  replacing the old external Task Scheduler job). Setting `LOG_TIME` earlier than 18:00
  makes today's HRM entries fail until 18:00 (the future-stop-time rule still applies).
  It **polls every 60s** rather than arming one long one-shot timer, so a sleep/wake or
  clock change can't silently skip the fire (a late wake still drains within ~1 min).
  `TrayApp.OnScheduledFireAsync` is the fire callback: if the queue has entries it
  drains; if the queue is **empty on a weekday** it opens the window as a reminder to
  log manually (silent on weekends). Editing `LOG_TIME` in `CredentialsForm` calls
  `SixPmScheduler.SetFireTime` to re-arm without a restart.
  Complementing it, `TrayApp.CatchUpIfDue` drains when the queue already has a due
  entry (a past date, or today once it's 18:00+) - run on startup and after a
  successful TSC re-auth, so entries kept by a logged-out drain retry automatically.
  Note: the date picker is in local time; for a user outside Vietnam a near-midnight
  pick can differ from the HCM day (a tooltip on the date field flags this).
- **Days off are marked eagerly, not at `LOG_TIME`.** The `OFF` cell is a signal to
  teammates, so its value is in arriving early - and leave is approved days ahead.
  `OffDaySync` therefore polls `find_my_requests` on startup and **every 2 hours**,
  looking ahead **today .. +60 days** (never backwards; a past day is only markable via
  the button). Only `status: Approved` **and** `periodType: AllDay` count - a half-day
  request is still half a working day and needs a real ticket. Detection is one
  browser-free MCP call but the TSC write needs a Graph token (= a headless Chrome
  sniff), so the two are split: `OffDayStore` (the `markedOffDates` list in
  `settings.json`) records what is already written, and Graph is only touched when a
  date is genuinely unmarked - do not remove that gate, or every app start relaunches
  Chrome. The automatic path passes `overwrite:false` and **never** replaces a real
  ticket; only the window's "Log OFF" button overwrites, behind a confirm, and it also
  drops that date's queued entries so the drain cannot undo it. A skipped date never
  reaches `OffDayStore` (it is not marked), so `OffDaySync` also holds a **session-scoped**
  skipped set - without it such a date stays pending forever and every poll reopens a
  Graph session just to skip it again. Session-scoped so a restart re-checks once and
  clearing the cell self-heals.
  `TrayApp.OnScheduledFireAsync` consults the same sync before the reminder: an approved
  day off suppresses the popup. A **failed** lookup returns empty and the reminder still
  opens - nagging is the safe direction when a day off cannot be told from a missed one.
  There is **no holiday source**: the HRM MCP server exposes no public-holiday calendar
  (verified across all 29 tools), and in practice this team files leave requests for
  company holidays too. An HRM key without leave scope gets `FORBIDDEN`, which must
  degrade to "no off days" plus one log line - the app ships to users whose key is
  timesheet-only.
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
  and disables logging rather than throwing. Config is read from the `Config` map in
  `settings.json` (`%AppData%\NoisLogTray\settings.json`, via `AppSettings`), written by
  the dialog through `AppConfig.SaveUserConfig` (read-modify-write so the theme/window
  keys survive); the process environment is a last-resort fallback per key (`Env.Get`).
  A legacy per-user `.env` is folded into `settings.json` once on load
  (`AppConfig.MigrateLegacyEnv`) then deleted, so config lives in one file. `settings.json`
  writes are **atomic** (temp + rename) and a corrupt file is preserved as
  `settings.json.bad` rather than silently reset (`AppSettings.LoadOrBackup`). The
  project `.env` is gitignored and never copied to a build. Secret keys
  (`JIRA_API_TOKEN`, `HRM_API_KEY`, see `Secrets.Keys`) are encrypted at rest with
  Windows DPAPI (CurrentUser) - stored `enc:`-prefixed, decrypted only when building
  the runtime config; `AppConfig` upgrades any leftover plaintext secret on load.
  Required keys: `JIRA_BASE_URL`, `JIRA_EMAIL`, `JIRA_API_TOKEN`, `HRM_API_KEY`.
  Optional: `TSC_GRAPH_COLUMNS` (per-user columns in the shared workbook; default
  `M, J` via `TscCells.TargetColumns`, parsed by `TscCells.ParseColumns`),
  `LOG_TIME` (daily auto-log + reminder time; the dialog uses 12-hour `h:mm tt`, e.g.
  `6:00 PM`, and `AppConfig.ParseLogTime` also accepts 24-hour `H:mm`; default 18:00 via
  `AppConfig.DefaultLogTime`),
  `JIRA_MY_TICKETS_JQL` (custom "My tickets" query, edited via `JqlForm`; default is the
  built-in MDP filter `JiraClient.DefaultMyTicketsJql`),
  `HRM_PROJECT_ID`, `MS_GRAPH_TOKEN` (override that skips the sniff),
  `TSC_GRAPH_DRIVE_ID` / `_ITEM_ID` / `_SHARE_URL` / `_WORKSHEET`.

## Per-user data

Everything runtime lives under `%AppData%\NoisLogTray` (see `AppPaths`): `queue.json`
(the pending log queue), `settings.json` (theme, window position, `markedOffDates` -
the dates already marked `OFF` in TSC, via `OffDayStore`, pruned of past dates on every
write - **and** the `Config` key/value map of secrets/settings written by
`CredentialsForm`, via `AppSettings`), and `logs/app.log`. A legacy `.env` (`AppPaths.EnvPath`) is migrated into `settings.json` on
first load and removed. A missing or malformed `queue.json` yields an empty queue by
design so the 18:00 runner never throws; `AppSettings` likewise falls back to defaults on
a missing/bad `settings.json` (preserving the bad copy as `settings.json.bad`). `app.log`
is size-capped and rolls to `app.log.1` (`AppLogger`). The TSC Chrome profile (the saved
Microsoft session) lives separately at
`%UserProfile%\.tsc-daily-log-browser` (`TscTokenSniffer.ProfileDir`).

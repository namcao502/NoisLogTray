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
The app is now for personal use only; the release/update pipeline is kept as is.

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
  `TicketQueue`, `UpdateService` (startup GitHub-Releases update check), and the Tasks
  board: `TicketStore` (JSON state files), `TicketBoard` (section logic), `TicketPoller` (Jira poll),
  `AzureDevOpsClient` + `PrTracker` + `PrUpdateChecker` (PR tracking), `TeamsNotifier`
  (Teams webhook cards), `JiraStatusFlow` (the walk to Ready to Test); the Claude terminal:
  `PseudoConsoleSession` (one child process on a Windows ConPTY, P/Invoke) and `ClaudeSessionHost`
  (owns that session for the tray's lifetime plus a ~1MB replay buffer).
- `Helpers/` - utilities: `TicketParser` (incl. `TryParseKey` for a key or Jira link), `TicketSearch`, `TimeSlots`, `TscCells`, `Timesheet`, `Leave`,
  `Hcm`, `Env`, `AppPaths`, `AppLogger`, `BrowserLock`, `AppConfig`, `AppSettings`,
  `AppIcon`, `TicketSort` (earliest due first, undated last, then key; Tasks + Dashboard),
  `PrCommentAlerts` (pure edits of the unseen-PR-comment map), `DashboardDataBuilder` (Dashboard data),
  `Retry` (transient-transport-failure retry with backoff, used by the Jira/Graph/HRM clients).
- `Models/` - record types (+ the `CredentialCheck` and `NoticeKind` enums): `QueueEntry`,
  `JiraSuggestion`/`JiraVerifyResult`, `DrainResult`/`EntryLogResult`/`OffWriteResult`,
  `GraphTscOptions`, `TimeSlot`, `ToolEnvelope`, `CredentialCheck`, `UpdateInfo`; Tasks: `JiraTicket`, `DismissedTicket`,
  `TicketSection`, `DueUrgency`, `DevPriorityLevel`, `TicketPollResult`, `PrStatus`, `TrackedPr`,
  `PrNotification`, `PrCheckResult`, `StartStage`, `QueueAddResult`, `PrLookupResult`, `PrCommentAlert`;
  Dashboard: `DashboardData`, `AttentionItem`, `AttentionKind`; coverage: `DayCoverage`, `TscReadState`;
  shell: `ShellPage`.
- `Interface/` - abstractions (`IJiraClient`, implemented by `JiraClient`; `IAzureDevOpsClient`, by
  `AzureDevOpsClient`; `IShellPage`, implemented by the shell's pages).
- `UI/` - WinForms. `Theme` is a central light/dark palette; controls read it at paint
  time and subscribe to `Theme.Changed` to repaint (the header's Dark/Light button calls
  `Theme.Toggle`; `ShellForm`'s and each page's `ApplyTheme` re-color native controls). It defaults to dark
  and persists the choice through `AppSettings` (`Theme.Load` runs in `Program.Main`;
  `Theme.Toggle` does a read-modify-write so it keeps the window position). `Lang` is the
  same pattern for the UI language (English default, or Vietnamese): `Lang.T("English",
  "Tiếng Việt")` picks the text at display time, `Lang.LongDate`/`ShortDate` format dates
  per language, the choice persists as `AppSettings.Language` (`Lang.Load` in
  `Program.Main`), and the header's EN/VI `LanguageToggleButton` calls `Lang.Toggle`.
  `ShellForm`, `SidebarNav`, the pages and `TrayApp.ApplyLanguage` re-label on `Lang.Changed`;
  other dialogs read the language when opened. Logs and code stay English. Also `TrayApp`,
  `ShellForm` + `SidebarNav` + `ShellLayout` (the one app window, see below), the pages
  `DashboardPage` (with `DashboardRow`, see below), `LogPage`, `TasksPage` (with `TaskRow`),
  `ClaudePage` (see below) and `WeekPage` (weekly coverage: HRM hours + TSC
  ticket per weekday; an under-logged past weekday is a clickable `ClickableRow` that raises
  `LogDayRequested` -> `TrayApp` shows the Log page on that date via `LogPage.PrepareForDate`.
  An approved day off (`DayCoverage.IsOff`) reads gray "off"
  for HRM and is judged only on the TSC marker - green `OFF`, else amber "off - not
  marked" and clickable, including when it is still in the future so planned leave can
  be marked ahead. A TSC cell that was not read (`DayCoverage.TscState` `NoToken` / `Failed`)
  reads gray "not read" and never makes a day clickable), `SettingsPanel` (the Settings body shared
  by the Settings page and the dialog), `SettingsPage` (the shell's Settings page: the panel on a
  `Card` sized to the page width, up to 1100px; from `SettingsPanel.WideMinWidth` the option groups
  and the Jira site/email share a row; a width change rebuilds the panel and keeps typed values;
  Dev repo path has a "Browse..." folder picker), `CredentialsForm` (a thin dialog around `SettingsPanel`, used only for first-run and
  while the config is invalid), and
  owner-drawn controls: `NotificationBell` + `NoticeToast` (see "Notices" under `ShellForm`),
  `WindowGlyphButton` (header menu / minimize / maximize-restore glyphs),
  `ThemedMenuRenderer` (themed `ContextMenuStrip`s, opened right-aligned under their button
  so they stay inside the window; the bell history also cuts each line to fit), `MacButton`
  (rounded button), `Card` (rounded card surface), `RoundedHost` (rounded border
  around native controls like the list/textbox), `RoundedDatePicker` (rounded date
  field with a custom `ModernCalendar` popup in a rounded `CalendarPopupForm`, replacing
  the native `DateTimePicker`/`MonthCalendar`), `WillLogRow` (one owner-drawn Will Log
  line), `ClickableRow` (a focusable/keyboard-activatable `Panel` for clickable list rows -
  My-tickets suggestions and actionable weekly days).
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
  `CredentialsForm`. Otherwise Settings is a page: the sidebar's ⚙ item or the tray
  "Settings..." item (`EditCredentials`) opens the shell on `ShellPage.Settings`; only while
  there is no working config (`_service == null`, so no window) does "Settings..." fall back to the
  dialog. Both host `SettingsPanel`: instant-apply pill groups (Language, Start with Windows via
  `StartupService`; selected pill = `MacButton.Selected`), the account fields verified on Save
  (Enter in a field = Save), and an "Open log folder" footer. No Quit button there on purpose -
  beside Settings it reads as "close this dialog"; quitting is the tray menu's "Quit".
  `SettingsPage` re-reads the saved values each time it is shown (unless a Save is verifying), so
  leaving the page or Esc (hide to tray) drops half-done edits; window activation does not reload. The tray menu is deliberately short - Open, Weekly
  check... (Week page), Tasks... (Tasks page), Settings..., Quit (plus "Download update..." when
  one exists). Open and a left click on the icon (`OpenShell`) show the **Dashboard** when the
  window is hidden or not built yet, and keep the current page when it is already on screen; the
  18:00 reminder and a Week day click still open the Log page. Logging and TSC session actions live
  only in the window, so they are not duplicated there. `ShowShell(page)` builds the shell and its
  pages lazily (`_shell`, `_dashboardPage`, `_logPage`, `_tasksPage`, `_weekPage`, `_claudePage`, `_settingsPage`) and shows the
  requested page. `_claudeHost` (the Claude session) is created once and outlives every shell
  rebuild; Quit and `Dispose` end it. Ticket state changes refresh both ticket views via `RefreshTicketViews`
  (Tasks + Dashboard); launch stages and the checking state are pushed to both pages.
  A page Save (`SettingsPage.SaveRequested` -> `OnSettingsSaved`) writes the per-user config into
  `settings.json` (an IO/permission failure is logged and noticed), then - deferred via
  `BeginInvoke`, since the panel's click is still on the stack - rebuilds `_service` via
  `ReloadServiceAndShow`, which closes and disposes the shell and reopens it on the same page (so
  back on Settings), then `NotifySettingsResult` (reopens the dialog if the config is still
  invalid). The panel **verifies** the entered credentials before
  saving (Jira via `/myself`, HRM via an MCP connect) and rejects a bad token/key inline;
  an unreachable service falls back to a "save anyway?" prompt so offline setup isn't
  blocked. A successful TSC re-auth (from the window, via the `ReauthSucceeded` event)
  calls `CatchUpIfDue` to retry any queue entries that were waiting on sign-in.
- **`ShellForm`** - the single app window (title "NOIS Workday"): a `SidebarNav` on the left
  (one row per `ShellPage` - Dashboard, Log, Tasks, Week, Claude, and Settings pinned at the bottom), and on the right a
  header (menu button that collapses the sidebar to glyphs, page title, bell, theme toggle, EN/VI
  language toggle, then minimize / maximize-restore / close) over the current page. Pages are
  `UserControl`s added with `AddPage`; they may implement `IShellPage` (`OnPageShown` when the page
  becomes visible or the window is shown on it, `OnActivatedRefresh` on window activate, `OnHidden`
  to every page on hide-to-tray). The shell's `MinimumSize` is the open sidebar plus the largest
  page `MinimumSize`, recomputed in `AddPage`. Showing a page that was never added (Tasks, when its state setup
  failed) raises `PageUnavailable` instead.
  **Window:** borderless (`FormBorderStyle.None`, `CS_DROPSHADOW`, `WS_MINIMIZEBOX` +
  `WS_MAXIMIZEBOX` so the taskbar, Win+Up and Aero Snap work). While Normal it is rounded (a
  `Region` plus a 1px painted border) with a 6px resize grip (`ResizeHitTest`); maximized it is
  edge to edge, and `WM_GETMINMAXINFO` pins the maximized rect to the monitor's work area
  (`ShellLayout.MaximizedRect`) so the borderless window does not cover the taskbar. It opens
  **maximized the first time** (`AppSettings.WindowMaximized` null -> `ShellLayout.StartMaximized`
  true) and afterwards remembers maximized vs Normal; the Normal bounds (`RestoreBounds` while
  maximized) are saved on resize-end, state change and close, and restored only if they still
  land on a connected monitor, else centered (1200x800 the first time). Dragging the header moves
  the window, double-clicking it toggles maximize, and dragging a maximized header restores it
  under the cursor. The sidebar's collapsed state persists as `AppSettings.SidebarCollapsed`. The
  Tasks row shows a red **badge** with `TasksPage.AttentionCount` (In progress / Done tickets with
  a Ready to Test button, or In progress ones already Done in Jira), raised via `AttentionChanged`
  on every `Reload`.
  **Keys:** Ctrl+1 .. Ctrl+5 switch to Dashboard / Log / Tasks / Week / Claude (Settings
  has no shortcut). Esc clears a non-empty input
  box (one with a placeholder, so not the inline hours field), otherwise hides to tray - except on
  the Claude page, where Esc goes to the CLI (it interrupts a running turn); the
  header's close button and Alt+F4 also **hide to tray**; `TrayApp` owns exit.
  **Notices:** every user-facing message goes through `TrayApp.Notify`, which records
  it in the bell history (`ShellForm.PostNotice`, last 20, session-only; the
  header bell shows a red dot until opened) and shows it as an in-window `NoticeToast`
  under the bell while the window is on screen (3s, errors 6s; capped at 2 lines). A Windows balloon is
  added unless the window is the **foreground** one (`IsForeground`), so a result that
  lands while the window is covered by another app is not missed. A notice arriving while the
  window is hidden clears any toast.
  **Size before Anchor:** a page builds its controls at a fixed design size and assigns `Size`
  (and `MinimumSize`) **before** setting any `Anchor`, because WinForms records each anchor's edge
  distances when `Anchor` is assigned; anchoring first records them against the default size and
  the layout breaks on the first resize. Lists whose rows are built at the list's width re-render
  (posted via `BeginInvoke`) only when that width really changes; My tickets also re-renders
  when a height change flips its scrollbar allowance, coalesced to one posted render per burst;
  the Tasks page re-renders on any card size change, since its kanban columns are placed by hand.
- **`DashboardPage`** (partial with `DashboardPage.Rows.cs`, rows are `DashboardRow`) - today at a
  glance, four cards placed by `ShellLayout.DashboardCardBounds` (one column below
  `TwoColumnMinWidth`, else Today + In progress left, Attention + New right). **Today**: the queue
  line with a "Log queue now" button (`LogQueueRequested` -> the tray's guarded drain; disabled while
  `SetDraining(true)`), and today's TSC + HRM coverage. **In progress**: Started tickets with due date
  and a PR summary, "+ queue" (`QueueTicketRequested` -> `TrayApp.QueueTicketForToday`, the same
  `AddStartedTicket` path Start uses) and Open. **Attention**: Ready to Test (dev PR merged), overdue,
  unseen PR comments ("Seen" -> `CommentsSeen`) and In progress tickets already Done in Jira
  ("-> Done" -> `MarkDoneRequested`). **New**: pending tickets with Start. Lists are sorted by
  `TicketSort.ByDue`; a "View Tasks" link raises `PageRequested(Tasks)`. The page only draws local
  data (`DashboardDataBuilder` over the ticket store + queue) and raises events; `TrayApp` does the
  work and re-seeds busy/checking state into a rebuilt page. Coverage is read once per window show
  with `allowSniff: false` (cached/override Graph token only, never Chrome), so TSC may read "not
  read (click Re-check)" (`TscReadState.NoToken`); Re-check reads with `allowSniff: true`. After a
  drain that had a today entry, `TrayApp` re-reads coverage without a sniff (the drain's token is
  still cached), but only if the page had already loaded it.
- **`LogPage`** - the capture page, structured like the old web app: `Card`s, input first:
  "New entry" (date + ticket, then one action row: the primary "Add to queue", a "Log now" dropdown
  with TSC + HRM / TSC only / HRM only, and a "more" dropdown with Log OFF... / Check TSC
  session / Re-authenticate TSC; the dropdowns are `ContextMenuStrip`s painted by
  `ThemedMenuRenderer`), "Queue" (titled QUEUE; called "Will log" in code, described
  below), and "My tickets" (a
  "My tickets" list from `LoggingService.GetMyTicketsAsync` - every match, paged via
  `nextPageToken` (capped at 500), in a list that scrolls - each row shows the key,
  summary, then a right-aligned due date - click a row to add it; an "Edit JQL" button
  opens `JqlForm` to customise the query driving that list (validated against Jira, then
  persisted + re-fetched)).
  **Layout:** `LayoutCards` (on every `OnSizeChanged`) places the three cards from
  `ShellLayout.LogCardBounds`. Below `ShellLayout.TwoColumnMinWidth` (1180px) they stack in one
  column (New entry, Queue at its built height, My tickets taking the rest); at or above it New
  entry + the Queue fill the left column and My tickets (the long list) takes the full-height right
  column. Only
  `Bounds` change, so switching columns keeps typed text. The cards carry no `Anchor`; the
  controls inside them do (the Queue and My tickets lists stretch on all four sides).
  `BuildLayout` stores the cards and their built heights before assigning `Size`, so that first
  `OnSizeChanged` reproduces the built bounds before `AnchorForResize` records the distances.
  "Will log" has one row per ticket - a
  status dot, the colored
  ticket key, and its time slots; the dot reflects Jira verification, green valid /
  red not found / amber error, via `VerifyTicketsAsync` debounced on typing and on
  blur, suggestions pre-marked valid. While typing it previews the typed tickets for
  the selected date, using an **editable `WillLogEditRow`** with an inline hours field
  per ticket: default is the even split (`TimeSlots.EvenSplit`), editing sets a custom
  per-ticket duration (partial day allowed, day capped at 8h; over-8h disables Add to queue
  and the TSC + HRM / HRM only log items, with a header hint). Custom durations ride the queue as
  `QueueEntry.Minutes` (null = even split) and drive the HRM slots; TSC ignores time. A resize
  or `RefreshQueuedView` while an hours field has focus defers the Queue re-render until focus leaves the list,
  so a half-typed value is not lost (not guaranteed if a drain lands while the window is
  in the background, since `Leave` does not fire on deactivation).
  With the input empty it falls back to the **whole persisted queue** grouped by date
  (each headed "(queued for <LOG_TIME>)"), shown read-only via `WillLogRow`, and shows a
  "Log queue now" (secondary; raises `DrainRequested` -> the tray's guarded drain) + "Clear queue"
  buttons - this is the single view of what's scheduled (there is no separate queue card). The
  list **scrolls internally** (`WillLogHostH` sizes it for a date header + 3 ticket rows in the
  one-column layout). "Log OFF" needs no ticket and takes any date,
  confirms, then writes the `OFF` marker for the selected day and drops that day's queued
  entries; it is the only path that overwrites a cell holding real work.
  `RefreshQueuedView` (called on queue/clear, on window
  activate, and after a drain, including by `TrayApp`) just re-renders "Will log". There
  is no in-window activity console: detailed progress (`AppendLog`) goes to the log file
  only. A page action's result (`ShowStatus`) reaches `TrayApp.Notify` via `StatusRaised`. While
  an action runs, `SetBusy` raises `BusyChanged` and the shell shows a sticky toast naming it
  ("Logging to TSC + HRM...") while the Log now button reads "Working..."; "Log queue now" shows
  "Logging the queue..." until the drain's result replaces it. Ticket-dependent buttons are gated
  on valid input via `UpdateActionState`.
  `MacButton`, `Card`, and `RoundedHost` are owner-drawn
  (no third-party UI library).
  Keyboard fast path: showing the page and picking a suggestion both focus the ticket
  box, and Enter in the ticket box queues (only when "Add to queue" is enabled). Enter in the
  search box instead adds the top match to the ticket box (never queues). Hiding the window clears
  the half-typed ticket text (`OnHidden`), because the date resets to today on reopen.
- **`TasksPage`** (with `TaskRow`) - Jira tickets in sections New / In
  progress / Done / Dismissed, a search box, "Add by key/link", and "Check now"; it re-renders on
  show only when data changed while hidden. When the list area (card width - 16, in logical
  `UiScale` pixels) reaches `ShellLayout.TaskBoardMinWidth` the sections become a **kanban**: three
  columns New / In progress / Done placed by `ShellLayout.TaskBoardColumnBounds` inside a host
  panel that scrolls sideways; a column with nothing to show (after search; a collapsed Dismissed
  alone counts as empty) shrinks to `TaskColumnCollapsedWidth` and the others share the rest, never
  below `TaskColumnMinWidth`. Each column scrolls on its own, with a 1px
  `Theme.Divider` line in each gap; Dismissed (collapsed by default) sits at the end of the Done
  column. No drag-and-drop - rows move via their buttons as before. Narrower, it is one vertical
  list. Search, collapse and focus work the same in both modes; any card resize re-renders once per
  burst (posted), keeping each list's scroll within the same mode. In a `TaskRow` the buttons move
  to their own line when they need more than half the row (`ButtonsOnOwnLine`) and wrap across
  lines by `ButtonLines`, so the larger buttons at high DPI are never clipped in a narrow column.
  `TrayApp` polls
  Jira every 5 min via `TicketPoller` and notifies new assignments and In-progress tickets that
  turned Done in Jira. A row's **Start** (`TrayApp.StartTicketAsync`) runs
  `TicketLauncher.StartNewTicket` off the UI thread: create the worktree
  `<DEV_REPO_PATH>\.claude\worktrees\<KEY>` (`WorktreeService`), drop a one-shot VS Code
  folderOpen task running `ship <KEY>`, and open VS Code (a worktree that already exists gets an
  idle Claude instead; a failed worktree opens the main repo without ship). It then moves the
  ticket to In progress and queues it for today via `TicketQueue.AddStartedTicket`. **Open**
  (`OpenExistingTicket`) reopens the worktree with an idle Claude and touches neither board nor
  queue. The row shows the `StartStage` while it runs; `TrayApp._launchStages` holds the in-flight
  stages (so a rebuilt `TasksPage` is seeded from it, and a second click on a busy ticket is
  ignored) and the `finally` always clears the stage.
  The same 5-min check also runs **PR tracking** when `AZURE_DEVOPS_PAT` is set, even if the Jira
  poll failed: `PrUpdateChecker` reads the Azure DevOps PRs of every Started ticket (and Done ones
  with an open PR) and notifies merges and new comments. The very first run only records a baseline
  (`.pr-baseline-done`, marked only after a complete pass with no network, per-ticket or per-branch
  failure; a failed main/-dev branch still reports the healthy one but counts the pass incomplete),
  so existing PRs do not flood notices. New tickets, PR merges and new PR
  comments are also posted to Teams via `TeamsNotifier` when `TEAMS_WEBHOOK_URL` is set (never
  throws; the URL is a secret and is never logged). **Ready to Test** (`TrayApp.MoveToReadyToTestAsync`)
  shows on an In progress / Done row once its `dev` PR merged: it walks the ticket through Jira's
  transitions (`JiraStatusFlow.GetStepsToReadyToTest`, from Jira's **live** status so it never walks
  backward), stops at the first missing transition, then re-reads the ticket. Deployed to Dev / Ready
  to Test stay a manual click because CI deploys after the merge; the merge notice only reminds.
  This replaces TaskWatcher: **shut TaskWatcher down and remove it from startup**, or both apps
  post the same events to Teams.
- **`WeekPage`** - the weekly coverage check (see `UI/` above). It loads the current week on the
  first `OnPageShown` after the window was shown (switching pages keeps the week on screen);
  hiding to tray resets it, so reopening reloads the current week. Refresh re-reads it.
  Icons come from the embedded `app.ico` via `AppIcon.Load(size)`.
- **`ClaudePage`** - an interactive `claude` CLI inside the window. `ClaudeSessionHost` (owned by
  `TrayApp`) runs `%UserProfile%\.local\bin\claude.exe` (else `claude.exe` on PATH) under ConPTY in
  `DEV_REPO_PATH` (the user profile if missing); the page draws it with **xterm.js** in a **WebView2**
  (`Assets/terminal/`: `index.html` + vendored xterm 6 and addon-fit, MIT, copied to the output and
  served through the virtual host `noislogtray.terminal`; WebView2 data in `%AppData%\NoisLogTray\WebView2`).
  The WebView2 starts on the page's first show. Messages are JSON: page -> host `ready` / `resize` /
  `input` / `page` (Ctrl+1..5 typed in the terminal, which WebView2 keeps from the form's
  `ProcessCmdKey`; mapped by `ClaudePage.PageForShortcut`), host -> page `output` (base64 bytes, so
  split UTF-8 survives) / `theme` / `focus` / `reset`. Output is coalesced and flushed at most every
  16ms. A rebuilt page (Settings save) `Attach`es to the running session and replays its buffered
  output, then resizes to nudge a repaint. Ctrl+C copies a selection, else sends ^C. When the CLI
  exits the page says so and Enter starts a new session; the top bar shows the working directory
  and a Restart button (stop, clear the screen, start again). The csproj's `DropWebView2Wpf`
  target (also in the test project) removes the package's WPF wrapper reference, which otherwise
  clashes with WinForms' `WindowsBase` (MSB3277).
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
  "off", not "pending"). Each day carries a `TscReadState` (`Read`, `NotDue` for a future day,
  `NoToken`, `Failed`). With `allowSniff: false` (Dashboard background reads) it uses only
  `TryGetCachedGraphToken` / the override and never launches Chrome; no token is then `NoToken`
  rather than `Failed`. `LogOffAsync` writes the `OFF` marker (see "Days off").

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
`SixPmScheduler`. Pure/tested helpers: `TscCells`,
`TimeSlots`, `Timesheet`, `Leave`, `TicketParser`, `TicketQueue`.

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
  only succeeds from 18:00 on; past dates work anytime. `LogPage.HrmClosedForToday`
  greys out the TSC + HRM / HRM only log items for today before 18:00 ("(after 6 PM)",
  re-checked each time the menu opens) and the handlers still refuse with a status, rather than letting HRM fail confusingly. `SixPmScheduler` fires the
  daily run in-process at a **configurable** time (`LOG_TIME`, default 18:00 HCM;
  replacing the old external Task Scheduler job). Setting `LOG_TIME` earlier than 18:00
  makes today's HRM entries fail until 18:00 (the future-stop-time rule still applies).
  It **polls every 60s** rather than arming one long one-shot timer, so a sleep/wake or
  clock change can't silently skip the fire (a late wake still drains within ~1 min).
  `TrayApp.OnScheduledFireAsync` is the fire callback: if the queue has entries it
  drains; if the queue is **empty on a weekday** it opens the window as a reminder to
  log manually (silent on weekends). Saving a new `LOG_TIME` in Settings rebuilds the service, which
  calls `SixPmScheduler.SetFireTime` to re-arm without a restart.
  Complementing it, `TrayApp.CatchUpIfDue` drains when the queue already has a due
  entry (a past date, or today once it's 18:00+) - run on startup and after a
  successful TSC re-auth, so entries kept by a logged-out drain retry automatically.
  Note: the date picker is in local time; for a user outside Vietnam a near-midnight
  pick can differ from the HCM day (a tooltip on the date field flags this).
- **Days off are marked manually, via the window's "Log OFF..." menu item only.** There is no
  background poll - leave isn't marked often enough to justify one, and periodic
  headless Chrome sniffs to check were overkill. The button needs no ticket, takes any
  date, confirms first (it's the only path that overwrites a cell holding real work),
  writes the `OFF` marker, and drops that date's queued entries so the drain cannot
  undo it.
  `TrayApp.OnScheduledFireAsync` still makes one **read-only** HRM lookup
  (`LoggingService.GetOffDatesAsync`, no Graph token, no Chrome) before the 18:00
  reminder, so an approved day off doesn't nag - it just doesn't write anything. Only
  `status: Approved` **and** `periodType: AllDay` count - a half-day request is still
  half a working day and needs a real ticket. A **failed** lookup returns empty and the
  reminder still opens - nagging is the safe direction when a day off cannot be told
  from a missed one. The same read backs the weekly check's "off" display
  (`LoggingService.CheckWeekAsync`). There is **no holiday source**: the HRM MCP server
  exposes no public-holiday calendar (verified across all 29 tools), and in practice
  this team files leave requests for company holidays too. An HRM key without leave
  scope gets `FORBIDDEN`, which must degrade to "no off days" plus one log line - the
  app ships to users whose key is timesheet-only.
- **One browser at a time.** `LaunchPersistentContextAsync` locks the profile on
  disk, so every Playwright entry point goes through `BrowserLock.TryAcquire()` /
  `Release()` (reject-fast, not queued). HRM logging is browser-free and can run in
  parallel with a TSC sniff.
- **UI-thread marshaling.** Never touch WinForms controls from a background task
  directly - use `TrayApp.RunOnUi` or the `InvokeRequired`/`BeginInvoke` guards in
  `LogPage` / `ShellForm`. WinForms `async void` event handlers are the intended style here.
- **Config load never crashes startup.** `AppConfig.TryLoad` returns `null` + a
  message on a missing required key; on `null` the tray shows the `CredentialsForm` dialog
  (first-run) to collect the values, and if still missing surfaces a warning balloon
  and disables logging rather than throwing. Config is read from the `Config` map in
  `settings.json` (`%AppData%\NoisLogTray\settings.json`, via `AppSettings`), written by
  the Settings page or dialog through `AppConfig.SaveUserConfig` (read-modify-write so the theme/window
  keys survive); the process environment is a last-resort fallback per key (`Env.Get`).
  A legacy per-user `.env` is folded into `settings.json` once on load
  (`AppConfig.MigrateLegacyEnv`) then deleted, so config lives in one file. `settings.json`
  writes are **atomic** (temp + rename) and a corrupt file is preserved as
  `settings.json.bad` rather than silently reset (`AppSettings.LoadOrBackup`). The
  project `.env` is gitignored and never copied to a build. Secret keys
  (`JIRA_API_TOKEN`, `HRM_API_KEY`, `AZURE_DEVOPS_PAT`, `TEAMS_WEBHOOK_URL`, see `Secrets.Keys`) are encrypted at rest with
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
  `DEV_REPO_PATH` (repo Tasks Start/Open work in; default `AppConfig.DefaultDevRepoPath`),
  `AZURE_DEVOPS_PAT` (enables PR tracking) and `TEAMS_WEBHOOK_URL` (enables Teams posts); both are
  imported once from `<DEV_REPO_PATH>\docs\local-config\tokens.env` (`AppConfig.ImportDevTokens`)
  when not already in the config - a key the user cleared to empty is never re-imported,
  `HRM_PROJECT_ID`, `MS_GRAPH_TOKEN` (override that skips the sniff),
  `TSC_GRAPH_DRIVE_ID` / `_ITEM_ID` / `_SHARE_URL` / `_WORKSHEET`.

## Per-user data

Everything runtime lives under `%AppData%\NoisLogTray` (see `AppPaths`): `queue.json`
(the pending log queue), `settings.json` (theme, window position, **and** the `Config`
key/value map of secrets/settings written from Settings, via `AppSettings`), and
`logs/app-yyyy-MM-dd.log` (one file per day), plus `WebView2/` (the Claude terminal's browser
data). A legacy `.env` (`AppPaths.EnvPath`) is migrated into `settings.json` on
first load and removed. A missing or malformed `queue.json` yields an empty queue by
design so the 18:00 runner never throws; `AppSettings` likewise falls back to defaults on
a missing/bad `settings.json` (preserving the bad copy as `settings.json.bad`). `AppLogger`
deletes log files older than 30 days (`RetentionDays`) on the first write of each day;
the Settings "Open log folder" link opens the folder. The TSC Chrome profile (the saved
Microsoft session) lives separately at
`%UserProfile%\.tsc-daily-log-browser` (`TscTokenSniffer.ProfileDir`).

`tasks/` holds the Tasks ticket state (`.jira-*.json`, incl. `.jira-pr-tracking.json` for PR
tracking; the app-only `.pr-baseline-done` marker and `.jira-pr-comments.json` - unseen PR comment
counts per ticket, merged by the PR check, cleared by Open or the Dashboard's "Seen", pruned to
tickets still on the board - sit beside them), imported once from
`%AppData%\TaskWatcher\state`; the seen file is copied last and marks the import complete. A setup
failure disables Tasks for the session and the next start retries.

`tsc-written.json` (`TscWrittenDates`) lists the last 31 dates whose TSC cell this app wrote (or was
about to write); it is recorded before every TSC write. Start -> queue uses it: `AddStartedTicket`
never creates a fresh entry for such a day, since the drain would overwrite the cell with just that
ticket. It also refuses a non-MDP key, a weekend, a day whose entry has custom hours, any time at or
after min(`LOG_TIME`, 18:00), and (in `TrayApp`) any moment a drain is running - a ticket merged
mid-drain would survive alone and overwrite the cell; an
existing entry for the day gets the ticket merged in. A refusal is a notice telling the user to log
it in the window.

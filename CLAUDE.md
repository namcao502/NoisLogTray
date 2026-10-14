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

`NoisLogTray.slnx` (the SDK's current XML solution format) ties the five projects
together: the main app, the test project, and the three smoke exes. All target
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

# The same, while bin/publish/win-x64 is the copy that is running (e.g. with Claude sessions open in it)
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # swap the files; the app keeps its old copy
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Restart   # and stop + start it on the new build
```

A running exe or loaded DLL cannot be overwritten but can be renamed, so `scripts/publish.ps1` publishes to
`bin/publish/staging`, copies each file over, and for a file in use (the exe, `conpty.dll`, `x64\OpenConsole.exe`, the
icon fonts) renames the old one to `*.<timestamp>.old` before putting the new one in place. The next run deletes the
`*.old` files nothing holds any more. Plain `dotnet publish` fails on those files while the app runs.

Output: `bin/publish/win-x64/` - a single `NoisLogTray.exe` (runtime baked in) plus
the `.playwright/` driver folder (its `node.exe` kept out of the bundle by the csproj's
`KeepPlaywrightDriverBesideExe`, or TSC sign-in fails with "driver folder missing"), `Assets/`, `conpty.dll` + `x64\OpenConsole.exe` (the bundled
ConPTY, which must stay beside the exe) and `.env.example`. Zip that folder and share it.
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

`smoke/GraphAuthSmoke` is a parked spike: MSAL device code for a delegated `Files.ReadWrite.All` token,
which would retire the Chrome sniff. Blocked by the tenant's consent policy (2026-08); see its header.

Playwright needs a browser installed once. `channel=chrome` uses the system Chrome:
`pwsh bin/Debug/net10.0-windows/playwright.ps1 install chromium`.

## Architecture

Layered, with the UI at the top and two integration clients at the bottom. Pure
logic is isolated into small, unit-tested modules.

Folders follow the backend's WindowsApps convention (`Backend-DotNet/src/WindowsApps`)
and all share the flat `NoisLogTray` namespace (folder does not equal namespace):
- `Services/` - service + external-client classes: `LoggingService`, `SixPmScheduler`,
  `StartupService`, `GraphTscClient`, `HrmMcpClient`, `JiraClient`, `TscTokenSniffer`,
  `TicketQueue`, `UpdateService` (startup GitHub-Releases update check), `AppRebuild` (Settings' "Rebuild and restart",
  see `TrayApp`), and the Tasks
  board: `TicketStore` (JSON state files), `TicketBoard` (section logic), `TicketPoller` (Jira poll),
  `AzureDevOpsClient` + `PrTracker` + `PrUpdateChecker` (PR tracking), `TeamsNotifier`
  (Teams webhook cards, plus `PostAsUserAsync` for the ticket tab's own post), `JiraStatusFlow` (the walk to Ready to Test); the Claude terminal:
  `PseudoConsoleSession` (one child process on a Windows ConPTY, P/Invoke; it uses Microsoft's
  redistributable ConPTY (`conpty.dll` + `x64\OpenConsole.exe` beside the exe, from the
  `Microsoft.Windows.Console.ConPTY` package, copied by hand in the csproj and kept out of the single-file
  bundle) when `UsesBundledConpty`, else the inbox one, whose Windows 10 version re-renders the screen and
  shifts claude's input line; each start logs `[pty] session on the bundled|inbox ConPTY`. ConPTY keeps its output
  pipe open after the child exits, so the session watches the child (`Process.Exited`, `WatchForExit`) and closes the
  console then; the read loop sees EOF and raises `Exited` (logged `[claude] session exited`), which is what shows
  "Press Enter to start it again" after the terminal's shell exits (`exit`). The bundled one
  asks the terminal for DA1 (`ESC[c`), focus events and win32-input-mode at start; xterm.js answers the
  first and ignores the last. `STARTF_USESTDHANDLES` with null handles keeps a parent's redirected
  stdio (a test runner, `dotnet run` from a shell) from replacing the console's. Its environment drops
  `CLAUDE*`/`ANTHROPIC*` via `BuildEnvironmentBlock`, so a tray started from a Claude Code session
  does not make the embedded claude a child session with transcripts off, and the `NO_COLOR` / `AI_AGENT` /
  `DISABLE_AUTOUPDATER` that Claude Code puts in its tool shells (`TicketLauncher.IsClaudeSessionVariable`),
  which would turn the embedded claude black-and-white and stop its updates; it also sets
  `CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN=1`, because the mouse never reaches claude's fullscreen renderer
  (`"tui": "fullscreen"`) here, so wheel scroll and selection only work with the classic one), `ClaudeSessionHost`
  (owns that session for the tray's lifetime plus a ~1MB replay buffer; built from a working
  directory, an optional argument, `fallBackToProfile` and an optional `settingsFile` (the hook
  settings), and raises `Starting` (first line of `Start`, on the calling thread, before `Stop`) and
  `Started`) and `ClaudeHookServer` (a localhost `HttpListener` for the sessions' Claude hooks, see
  "Claude hook bridge" below); the Ticket workspace: `WorktreeService` (fetch + `git worktree add` off `origin/main`), `GitCommand` (one
  `git` run, stdout + stderr as UTF-8, a timeout that kills the process tree; `WorktreeService` and
  `GitChanges` go through it), `GitChanges` (a worktree's changed files and the two sides of each,
  see `ChangesView` below), `TicketWorktrees` (a ticket's backend worktree path and its sibling
  `*-Angular` worktrees), `TicketFiles` (the Files view's listing and reading, see `FilesView` below) and
  `WorktreeCleanupService` (reads every ticket's worktrees + docs state into `WorktreeCleanupRow`s and does the Remove / Trim
  docs, see "Worktree cleanup" under `TrayApp`), `KnowledgeMiner` (the cleanup dialog's "Mine knowledge": `claude -p
  "mine-knowledge <KEY>"` in the dev repo with the daily scan's flags, recorded in the scan's own log),
  `WindowsScheduledTask` (one Windows Task Scheduler task through the `Schedule.Service` COM API, late-bound with `dynamic`:
  read, Run, change `ExecutionTimeLimit`; the user's own tasks need no admin; a missing task surfaces as `FileNotFoundException`),
  `KnowledgeScanService` (the Schedules dialog's read of task `DailyKnowledgeScan` plus the last 1 MiB of
  `.daily-knowledge-scan.log`, numbered from the whole file; never throws, each half carries its own error) and
  `TicketLauncher` (the `--continue` lookup for a worktree and the VS Code launch: `cmd /c code`, the path in the
  `NOIS_VSCODE_TARGET` variable so cmd never expands a `%NAME%` inside it, `-g path:line` when a line is given).
- `Helpers/` - utilities: `TicketParser` (incl. `TryParseKey` for a key or Jira link), `TicketSearch`, `TimeSlots`, `TscCells`, `Timesheet`, `Leave`,
  `Hcm`, `Env`, `AppPaths`, `AppLogger`, `BrowserLock`, `AppConfig`, `AppSettings`,
  `AppIcon` (`AppIcon.Load(size)`: icons from the embedded `app.ico`), `TicketSort` (earliest due first, undated last, then key; Tasks + Dashboard),
  `WorktreeCleanup` (pure: classifies `WorktreeFacts` into a `CleanupVerdict`, mined tickets, slim targets, `WorktreeKeyOf` a folder),
  `TerminalTabs` (pure: the Terminal page's tab names (`^claude\d*$`, also their hook session names), labels, resume keys,
  the migration of the old per-folder session ids),
  `KnowledgeScan` (pure: the scan log split into runs, each run's `ScanRunOutcome`, `NewestOutcome`, `NoticeFor`),
  `JobRuns` (the last 10 runs of each `AppJob` in `job-runs.json`, locked + atomic like `TscWrittenDates`),
  `PrCommentAlerts` (pure edits of the unseen-PR-comment map), `DashboardDataBuilder` (Dashboard data), `DashboardTodos` (pure: the Dashboard's to-do list and the week's due days),
  `ClaudeAttention` (pure: parses a hook payload, maps it to the session's wait, words the notice),
  `Retry` (transient-transport-failure retry with backoff, used by the Jira/Graph/HRM clients),
  `TeamsPost` (pure: the ticket's Teams post draft, its subject/body split and the body's HTML),
  `BusyWork` (`BusyWork.Shared`: the work the user may be waiting on; `Begin(label)` returns a handle whose `Dispose`
  ends it, thread-safe. The Jira/PR check, the drain, the log column's actions, the week card and Dashboard coverage
  reads, Add by key, Start's worktree, Ready to Test, the Changes/Files listings, the Teams post and the Settings
  verify all register; a new slow job should too).
- `Models/` - record types (+ the `CredentialCheck` and `NoticeKind` enums): `QueueEntry`,
  `JiraVerifyResult`, `DrainResult`/`EntryLogResult`/`OffWriteResult`,
  `GraphTscOptions`, `TimeSlot`, `ToolEnvelope`, `CredentialCheck`, `UpdateInfo`; Tasks: `JiraTicket`, `DismissedTicket`,
  `TicketSection`, `DueUrgency`, `DevPriorityLevel`, `TicketPollResult`, `PrStatus`, `TrackedPr`,
  `PrNotification`, `PrCheckResult`, `StartStage`, `QueueAddResult`, `PrLookupResult`, `PrCommentAlert`;
  worktree cleanup: `CleanupVerdict`, `WorktreeFacts`, `WorktreeCleanupRow`, `WorktreeCreateResult`;
  Schedules: `ScheduledTask`, `ScheduledTaskState`, `KnowledgeScanRun`, `KnowledgeScanSnapshot`, `ScanRunOutcome`,
  `KnowledgeScanNotice`, `AppJob`, `JobRun`, `AppJobStatus`;
  Ticket page: `TicketPr`, `TicketWorktree`, `ChangeSet`, `FileChange`, `FileChangeStatus`, `TicketFileKind`, `TicketFileContent`;
  Claude hooks: `ClaudeHookEvent`, `ClaudeWaitReason`; Terminal page: `TerminalTabSetting`; ticket tables: `TaskTableColumn`, `TableCell`; Dashboard: `DashboardData`, `TodoItem`, `TodoKind`, `ClaudeWait`; coverage: `DayCoverage`, `TscReadState`;
  shell: `ShellPage`.
- `Interface/` - abstractions (`IJiraClient`, implemented by `JiraClient`; `IAzureDevOpsClient`, by
  `AzureDevOpsClient`; `IShellPage`, implemented by the shell's pages).
- `UI/` - WinForms. `Theme` is a central light/dark palette (`Theme.Action*` are the ticket action button fills; `Theme.Badge` is the red of the
  sidebar badges and the tab dots); controls read it at paint
  time and subscribe to `Theme.Changed` to repaint (Settings' Theme pills call
  `Theme.Toggle`; `ShellForm`'s and each page's `ApplyTheme` re-color native controls). It defaults to dark
  and persists the choice through `AppSettings` (`Theme.Load` runs in `Program.Main`;
  `Theme.Toggle` does a read-modify-write so it keeps the window position). `Lang` is the
  same pattern for the UI language (English default, or Vietnamese): `Lang.T("English",
  "Tiếng Việt")` picks the text at display time, `Lang.LongDate`/`ShortDate` format dates
  per language, the choice persists as `AppSettings.Language` (`Lang.Load` in
  `Program.Main`), and Settings' Language pills call `Lang.Toggle`.
  `ShellForm`, `SidebarNav`, the pages and `TrayApp.ApplyLanguage` re-label on `Lang.Changed`;
  other dialogs read the language when opened. Logs and code stay English. Also `TrayApp`,
  `ShellForm` + `SidebarNav` + `ShellLayout` (the one app window, see below), the pages
  `DashboardPage` (with `StatTile`, `TodoRow` and `DashboardRow`, see below), `WorkPage` (the board `TasksPage` with `TaskRow`, beside the log
  column `LogPage` with the card `WeekCard`; see below), `ClaudePage` and `TicketWorkspacePage` (with `TicketWorkspaceView`; both below, built on the web
  views `TerminalView`, `ChangesView`, `FilesView` and `WebViewSetup`); then `WeekCard` (the log column's week card: HRM hours + TSC
  ticket per weekday; an under-logged past weekday is a clickable `ClickableRow` that raises
  `DayRequested` -> the log column puts that date in New entry via `LogPage.PrepareForDate`.
  An approved day off (`DayCoverage.IsOff`) reads gray "off"
  for HRM and is judged only on the TSC marker - green `OFF`, else amber "off - not
  marked" and clickable, including when it is still in the future so planned leave can
  be marked ahead. A TSC cell that was not read (`DayCoverage.TscState` `NoToken` / `Failed`)
  reads gray "not read" and never makes a day clickable), `SettingsPanel` (the Settings body shared
  by the Settings page and the dialog; a saved secret shows as a fixed 8-dot mask, kept aside in `_secretValues` and
  read back through `FieldText` while the mask is untouched, and focusing it selects the mask so typing replaces it),
  `SettingsPage` (the shell's Settings page: the panel on a
  `Card` spanning the whole page width; from `SettingsPanel.WideMinWidth` the option groups
  and the Jira site/email share a row; a width change rebuilds the panel and keeps typed values;
  Dev repo path has a "Browse..." folder picker), `CredentialsForm` (a thin dialog around `SettingsPanel`, used only for first-run and
  while the config is invalid; it gets no cleanup button), `WorktreeCleanupDialog` (Settings' "Clean up worktrees...", see
  "Worktree cleanup" under `TrayApp`), `TeamsPostView` (the ticket tab's Teams post editor, see the Ticket page), and
  **no Windows-default popups**: `CardDialog` is the dialog frame (borderless rounded card, `Theme.DialogBorder`, a soft
  shadow from `WindowShadow`, drag the background to move; `CredentialsForm`, `ConfirmDialog`, `WorktreeCleanupDialog`
  and `SchedulesDialog` derive from it). `WindowShadow.Attach(form, radius)` (also on the calendar popup) is a
  click-through layered window kept just below the form, owned like it, its bitmap redrawn only on a new size or theme;
  `CS_DROPSHADOW` is not used there, since on Windows 10 it is a faint right/bottom edge that a user setting turns off;
  `ConfirmDialog.Ask(owner, title, message, confirmText, confirmByDefault, destructive)` replaces every
  `MessageBox` - the confirm button names the action ("Quit", "Close tab", "Dismiss"), red when `destructive`,
  Enter = Cancel unless `confirmByDefault`, Esc = Cancel; a null owner (Quit from the tray menu while the window
  is hidden) centers it on screen, on top and in the taskbar. It asks before: Dismiss, Clear queue, Log OFF,
  Ready to Test, Quit / closing a tab / changing the Claude folder while the terminal runs, and saving unverified
  Settings. `ThemedToolTip` is the owner-drawn tooltip every `ToolTip` field uses;
  `ThemedToolTip.Track(label)` is how an ellipsis label is set up (instead of a bare `AutoEllipsis = true`): any
  ToolTip text on a `Label` switches off its own Windows "full text" tooltip, so the shared `FullText` instance
  takes over and pops only while `IsCut`. A static, shared tooltip uses `SetUntilDisposed`, or it would keep
  every rebuilt row alive. **No Windows scrollbars either**: `ThemedScrollBar.Attach(target)` (a `ScrollableControl`
  with AutoScroll, or a multiline `TextBox`) clips the target's native bar
  away with a window `Region` and lays a sibling over that strip that draws a thin rounded thumb and drives the
  scroll (drag, track click = page, wheel forwarded). The target keeps all of its own scrolling and layout (rows still
  leave `SystemInformation.VerticalScrollBarWidth` free); the bar follows it on every `Application.Idle`, so call
  sites never notify it. Not inside a `FlowLayoutPanel`/`TableLayoutPanel` parent, which would place the sibling
  itself. Every AutoScroll area and both multiline boxes are attached. **One scrollbar look everywhere**: a pill 8px
  thick (11px under the mouse or while dragged), 3px in from the outer edge, no track, at 40% / 60% / 80% of
  `TextSecondary`, shown whenever there is something to scroll. `ThemedScrollBar` draws it; the Changes/Files pages
  get it from `::-webkit-scrollbar` CSS (transparent borders place the thumb) plus overrides of Monaco's own bars
  (`scrollbar` editor options + `body .monaco-scrollable-element` CSS), and the terminal from CSS on xterm 6's
  VS Code-style bar (`.xterm-scrollable-element`, its idle fade turned off) with the `scrollbarSlider*` theme colors.
  Change all four together. The one exception is the terminal, whose pill is twice the size (16px, 22px under the mouse)
  so it is easy to grab; `overviewRuler.width: 28` widens xterm's track to hold it, and the fit addon keeps that free. **Striped lists**: every other row of a list sits on `Theme.RowStripe(surface)`, an
  opaque shade darker than the surface behind it (in both themes, so the hover color still differs) - Tasks (counted
  per section), the Dashboard's to-do list and two tables, the bell history, the Queue (per date) and the week card's
  days; rows are rebuilt on a theme switch, so they pick the color when built. A striped row hands that color to its
  buttons (`MacButton.Surface`, `CloseButton.Surface`) so their rounded corners blend in, and a hover restores it on
  leave. The folder
  picker (`FolderBrowserDialog`) and the Windows balloons stay native. **One icon set**: `Icons` draws Fluent UI
  System Icons (Microsoft, MIT; `Assets/icons`, loaded with `PrivateFontCollection.AddFontFile`, which also
  registers them with GDI for `TextRenderer`) as font glyphs - `Icons.Draw`, the shared `Small`/`Medium`/
  `MediumFilled` fonts. Constants are the
  `_20_` code points; look a new one up in the font's JSON map (fluentui-system-icons repo) and add it to `Icons`,
  whose test checks every constant is in the font. Sidebar: Board / TaskList / WindowConsole / Ticket /
  Settings, the selected row filled; header, X buttons, chevrons, `MacButton.Glyph` (alone or beside the text,
  `GlyphAfterText` for a dropdown), the calendar and the Changes/Files toolbars and trees (`@font-face` on the same
  TTF) all use it, as do the week card's previous/next, the "Done" of a ticket already done in Jira (`ArrowRight`
  beside the text) and the PR status marks: `FactTag.Runs` draws ✓ ● ✕ as CheckmarkCircle / Record / DismissCircle,
  while the text keeps the characters for notices and Teams posts. The tray icon's menu uses `ThemedMenuRenderer`
  like every other menu. Arrows that are prose (the Ready to Test steps tooltip, "Alt+↑") stay text. `UiScale.Factor` is a
  fixed 125%; there is no zoom setting. Also
  owner-drawn controls: `NotificationBell` + `NoticeToast` (see "Notices" under `ShellForm`), `BusySpinner` (left of
  the bell: a turning arc while `BusyWork` has anything running, shown only after 250ms so a quick job never flickers
  it, its tooltip naming the running work; idle it is drawn empty but keeps its place, so the header never shifts),
  `FactTag` (a pill: a soft fill of its tone with the text in that tone; with an action it is a link, as the ticket
  header's PR tags are),
  `WindowGlyphButton` (header menu / minimize / maximize-restore glyphs),
  `ThemedMenuRenderer` (every dropdown and the tray menu, built by `CreateMenu`: a card like the bell's history -
  rounded `Region` and border, roomy rows (`ItemAdded` pads each item), a rounded hover, the Fluent checkmark in
  `Theme.Accent`, inset separators; opened right-aligned under their button so they stay inside the window), `NoticeHistoryPopup` + `NoticeHistoryRow` (the bell's history: a rounded card
  under the bell, a `ToolStripDropDown` hosting it so any outside click or Esc closes it without taking activation;
  newest first, each notice with its kind icon, up to three lines (the rest in a tooltip) and its time, scrolling past
  440px; a press closes it, then runs the notice's target), `MacButton`
  (rounded button; `Primary` blue, `Secondary` neutral, `Tinted(text, color)` its own fill - the ticket action
  colors `Theme.ActionStart` green / `ActionDone` teal / `ActionReadyToTest` purple, Open uses `Accent`; `ShowDot`
  paints a `Theme.Badge` attention dot in its top-right corner), `Card` (rounded card surface; `WeekCard` derives from it), `RoundedHost` (rounded border
  around native controls like the list/textbox; `OnWindow` for one on the window background, as in the header),
  `HeaderToolbar` (a page's controls in the shell header, see `ShellForm`), `HeaderTabStrip` (a `HeaderToolbar` of
  `HeaderTab`s - the Ticket and Terminal pages' tabs: text and × in one pill like a browser tab, an optional ▾ menu area
  (Alt+Down), a dot while Claude waits; more tabs than fit scroll sideways with the wheel, the selected one is kept in
  view, and an optional "+" sits after the last tab or at the right edge), `RoundedDatePicker` (rounded date
  field with a custom `ModernCalendar` popup in a rounded `CalendarPopupForm`, replacing
  the native `DateTimePicker`/`MonthCalendar`), `WillLogRow` (one owner-drawn Will Log
  line), `ClickableRow` (a focusable/keyboard-activatable `Panel` for clickable rows -
  sidebar items, board section headers and actionable weekly days).
  Owner-drawn interactive controls (`MacButton`, `CloseButton`, `WindowGlyphButton`, `RoundedDatePicker`,
  `ClickableRow`, `HeaderTab`, `FactTag`, `NoticeHistoryRow`) paint a keyboard-focus ring and set
  `AccessibleName`/`AccessibleRole` so the app is operable by keyboard and screen reader. The ring (and the focus
  highlight) shows only while `ShowFocusCues` is on - Windows turns it on with Tab - and `OnChangeUICues` repaints:
  a clicked button keeps focus, and a ring left on it after a mouse click reads as stuck.
- `Program.cs` stays at the repo root.

- **`Program.cs`** - entry point. Single-instance `Global\` mutex, global exception
  handlers routed to `AppLogger`, `FocusReleaseFilter` (a message filter: a left click on a part of a window that cannot
  take focus - a card, a label, a row - takes focus off the text box that had it, once the click is handled, as on a web
  page; a click on the box's `RoundedHost` frame, a `ThemedScrollBar` or a WebView2 leaves it), then
  `Application.Run(new TrayApp())`.
- **`TrayApp` (ApplicationContext)** - owns the process lifetime, the `NotifyIcon`
  + context menu, and the scheduler. Background work (sniff, MCP, drain) runs off
  the UI thread and is marshaled back through a hidden `Control` (`RunOnUi`) for
  tooltip/balloon/log updates. `_draining` is an `Interlocked` guard so only one
  drain runs at a time. When config is missing it runs `RunFirstRunSetup` (a
  `BeginInvoke` after the message loop starts, not a modal in the ctor) to show
  `CredentialsForm`. Otherwise Settings is a page: the sidebar's ⚙ item, or a config notice's target
  (`EditCredentials`), opens the shell on `ShellPage.Settings`; only while there is no working config
  (`_service == null`) does `EditCredentials` fall back to the dialog. Both host `SettingsPanel`: instant-apply pill groups (Language, Theme, Start with Windows via
  `StartupService`, and on a row of its own the Jira/PR check interval (`TicketCheckInterval`); selected pill = `MacButton.Selected`; one row from `WideMinWidth`), the account fields verified on Save
  (Enter in a field = Save), and a footer: "Open log folder", plus on the page "Clean up worktrees...", "Schedules..."
  (see `SchedulesPage`; a red dot while a bad knowledge-scan run is unseen) and "Rebuild and restart" (a footer button
  that does not fit starts a new row). No Quit button there on purpose -
  beside Settings it reads as "close this dialog"; quitting is the tray menu's "Quit".
  **Rebuild and restart** (`TrayApp.Rebuild.cs`, `AppRebuild`) shows only when the exe runs from `<repo>\bin\publish\win-x64`
  (`AppRebuild.FindSourceRepo`). After a confirm it runs `scripts\publish.ps1` off the UI thread (output to the log as
  `[rebuild]`), then starts the new exe with `--wait-for-exit <pid>` and quits through `ShutDown` (Quit minus its question):
  `Program.Main` waits for that process (up to a minute) before taking the single-instance mutex. A failed build is a notice
  and the app keeps running.
  **Worktree cleanup** (`TrayApp.WorktreeCleanup.cs`, `WorktreeCleanupDialog`, `WorktreeCleanupService`, `WorktreeCleanup`): the Settings page's
  footer button opens a dialog with one row per ticket that has a worktree (BE + sibling `*-Angular`) or a `docs\MDP-*` folder,
  as a table: TICKET / VERDICT / DOCS / REASONS on one line (`WorktreeCleanupDialog.Columns`, column titles over the list,
  reasons cut with a tooltip, an action's error in red there) and the row's buttons always shown at its right.
  Verdicts (`WorktreeCleanup.Classify`): Keep, no Remove button (git could not be read, the ticket's terminal runs, or a
  commit is on no remote); Check, confirmed with its reasons (uncommitted files besides the local-only `LocalOnlyFiles`, a
  `docs\local-config` that differs from main, In progress on the board, a worktree on another branch, a broken (half-removed)
  worktree, or not shipped: a PR still open or only abandoned, or no PR and no merge commit naming the key on `origin/main`);
  Ready (none of those); NoWorktree (docs only). `RemoveChecked` re-reads the state just before deleting. Remove order: sync the backend worktree's `docs\MDP-*` back to the dev repo, unlink the `node_modules` junction first,
  `git worktree remove --force` (frontends first, backend last so a failed one can be retried), `worktree prune` + delete the folder
  only when git says the worktree is not listed, then `branch -d` for `<KEY>` and `<KEY>-dev`. Trim docs (`SlimDocs`, NoWorktree rows only)
  deletes the slim targets of `docs\<KEY>` in main and in every worktree's docs copy, since the Stop hook would copy them back;
  a ticket not mined yet asks first. "Mined" is read from the daily scan's `.daily-knowledge-scan.log` / `.retry`; "merged"
  means a merge commit subject on `origin/main` or `origin/development` of the dev repo or a sibling `*-Angular` repo names
  the key (`MergedKeys`: one `git log --first-parent --merges` per repo and branch, ~1.5s in all) or a tracked PR completed.
  Only a merged row not mined yet has "Mine knowledge" (`KnowledgeMiner.Mine`, up to 15 min); unmerged work is not mined. On success it appends the scan's own
  `Mining <KEY> ...` line plus the result and drops the key from the retry file, on failure a line without "Mining", so only a
  real run marks the ticket mined. Every action re-reads just its row; the delegates
  read Started / PR / running state on the UI thread and run git in `Task.Run` under `BusyWork`; on close the tabs are pruned.
  Recreating a removed worktree (Start / Open) checks out the old `<KEY>` branch if it still exists and merges `origin/main`
  into it (`WorktreeCreateResult`); Open asks first.
  `SettingsPage` re-reads the saved values each time it is shown (unless a Save is verifying), so
  leaving the page or Esc (hide to tray) drops half-done edits; window activation does not reload. The tray menu is just Open and Quit (plus "Download update..." when
  one exists); every page, Settings included, is reached from the window. Open and a left click on the icon (`OpenShell`) show the **Dashboard** when the
  window is hidden or not built yet, and keep the current page when it is already on screen; the
  18:00 reminder still opens the Work page. Logging and TSC session actions live
  only in the window, so they are not duplicated there. `ShowShell(page)` builds the shell and its
  pages lazily (`_shell`, `_dashboardPage`, `_logPage` + `_tasksPage` inside `_workPage`, `_claudePage`, `_ticketPage`,
  `_settingsPage`) and shows the
  requested page. The Terminal page's sessions (`_terminalHosts`, one per tab) and the per-ticket sessions
  (`_ticketSessions`) outlive every shell rebuild; Quit and `Dispose` end them (Quit asks
  first (`ConfirmDialog`, default Cancel) while a ticket's terminal is running). Ticket state changes refresh
  the ticket views via `RefreshTicketViews` (Tasks + Dashboard + the Ticket page headers); launch stages and the
  checking state are pushed to the board and the Dashboard.
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
  (one row per `ShellPage` - Dashboard, Work, Terminal (`ShellPage.Claude`), Ticket, and Settings pinned at the bottom), and on the right a
  header (menu button that collapses the sidebar to glyphs, page title, busy spinner, bell, then minimize /
  maximize-restore / close; theme and language live in Settings) over the current page. The title is as wide as its
  text; `AddPage`'s optional header control docks right after it and shows only while its page is current: the
  Terminal page's tabs, the Work board's Add by key / Check now and the Ticket tabs
  (each a `HeaderToolbar`, docked Fill up to the header's buttons). A `HeaderToolbar` answers `WM_NCHITTEST` with
  `HTTRANSPARENT` on its own background, so a drag or double-click on its empty part reaches the header and still
  moves or maximizes the window. Pages are
  `UserControl`s added with `AddPage`; they may implement `IShellPage` (`OnPageShown` when the page
  becomes visible or the window is shown on it, `OnActivatedRefresh` on window activate, `OnHidden`
  to every page on hide-to-tray). The shell's `MinimumSize` is the open sidebar plus the largest
  page `MinimumSize`, recomputed in `AddPage`.
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
  Work row shows a red **badge** with the tickets that newly need action since the Work page was last viewed
  (`TrayApp.WorkBadge.cs`): `TasksPage.AttentionKeys` (`<KEY>|ready-to-test` for a Ready to Test button,
  `<KEY>|done-in-jira` for an In progress ticket already Done in Jira), raised via `AttentionChanged` on every
  `Reload`, minus `AppSettings.SeenWorkAttention`, which a view of the page (`PageViewed`, or a change while it is
  watched in the foreground) replaces with the current keys; the row's own status dot keeps marking what still waits.
  The Claude and Ticket rows get badges from the Claude hook bridge (below), and the Settings row a `1` while a bad
  knowledge-scan run is unseen (see `SchedulesPage`).
  **Keys:** Ctrl+1 .. Ctrl+4 switch to Dashboard / Work / Terminal / Ticket (Settings
  has no shortcut). Esc clears a non-empty input
  box (one with a placeholder, so not the inline hours field), otherwise hides to tray - except on
  the Terminal and Ticket pages while they have open tabs, where Esc goes to the CLI (it
  interrupts a running turn); the
  header's close button and Alt+F4 also **hide to tray**; `TrayApp` owns exit.
  **Notices:** every user-facing message goes through `TrayApp.Notify`, which records
  it in the bell history (`ShellForm.PostNotice`, last 20, session-only; the
  header bell shows a red dot until opened) and shows it as an in-window `NoticeToast`
  under the bell while the window is on screen (3s, errors 6s; capped at 2 lines). A Windows balloon is
  added unless the window is the **foreground** one (`IsForeground`), so a result that
  lands while the window is covered by another app is not missed. A notice arriving while the
  window is hidden clears any toast. A notice may carry a target (`Notify`'s `open`): a click on its
  toast, its bell-history line or its balloon (`_balloonOpen`, the last balloon's) goes there - a
  Claude wait to that session (its Terminal tab via `ShowTerminalTab`, or the ticket's tab via `ShowTicketTab`; neither
  starts a session for a closed tab), new / Done-in-Jira tickets to Work, PR merges to the Dashboard,
  new PR comments to the PR in the browser, log and drain results to Work, config problems to
  Settings, an update to its download page. A balloon without a target opens the window. While a balloon click
  runs (`_balloonClicked`), `ShowShell` uses `ShellForm.ForceForeground` (`AttachThreadInput` to the foreground
  thread, then `SetForegroundWindow`): a background process is otherwise refused the foreground and Windows only
  flashes the taskbar button. Other background shows (the 18:00 reminder) keep a plain `Activate`, so they never
  take focus from an app the user is typing in.
  **Size before Anchor:** a page builds its controls at a fixed design size and assigns `Size`
  (and `MinimumSize`) **before** setting any `Anchor`, because WinForms records each anchor's edge
  distances when `Anchor` is assigned; anchoring first records them against the default size and
  the layout breaks on the first resize. Lists whose rows are built at the list's width re-render
  (posted via `BeginInvoke`) only when that width really changes; the board re-renders on any card size
  change, coalesced to one posted render per burst.
- **`DashboardPage`** (partial with `DashboardPage.Rows.cs` (the two tables) and `DashboardPage.Todos.cs` (the to-do rows))
  - today at a glance, placed by `ShellLayout.DashboardBounds`: a header line (today's date, "Re-check" re-reads the
  week, "Log queue now" -> the tray's guarded drain, disabled while `SetDraining(true)`), four `StatTile`s (a `Card`
  with a title, a colored dot + value and a detail line: QUEUE (today's tickets, auto-log time and countdown), TSC TODAY,
  HRM TODAY (a short day before HRM takes today's hours is gray "logs at <time>", a weekend gray), THIS WEEK (`n/m days
  logged`, the missing days)), then from `TwoColumnMinWidth` (`DashboardColumns`) the to-do card left (at least 560) and
  In progress over New right (`max(min(680, W-620), (W-60)/2)`); narrower, the tiles go two by two and the three cards
  stack (`DashboardMinHeight` 648). Header and tiles are placed by `LayoutCards` alone, no anchors.
  **To do** (`DashboardTodos.Build`, pure; rows are `TodoRow`: a tinted Fluent icon, the key as a Jira link + a title
  over a gray detail line, up to two buttons always shown; busy tickets show their stage and wait), in this order:
  Claude waiting (permission / question before a finished turn; `TrayApp.RefreshClaudeAttention` pushes
  `SetClaudeWaits`, labelled like the notices; Open -> `ClaudeSessionRequested` -> that tab), Ready to Test, new PR
  comments (View PR opens it and marks them seen, Seen), Done in Jira (Done), overdue (Open, or Start for a New ticket),
  today not queued (`TicketQueue.TodayRefusal` null, no today entry, today's TSC cell not written, no drain, the week read
  and today not off; Queue -> `QueueTicketsRequested` with the Started tickets not Done in Jira, not busy, `CanQueue`),
  days not fully logged (weekdays before today, and today once `max(LOG_TIME, 18:00)` passed with nothing left to drain,
  per `WeekCard.IsActionable`; Log -> `LogDayRequested` -> the Work page on that date) and worktrees of shipped tickets
  (Done, every live main PR merged, no open tab, the backend worktree folder still there; Clean up -> the cleanup
  dialog). Rows are rebuilt only when `DashboardTodos.Signature` changes or theme / language / width / a stage does;
  a hidden page renders once on show. **In progress** (columns Status / Due / MAIN / DEV; "All tickets" in its title
  row raises `PageRequested(Work)`): Started tickets, "Queue" (`QueueTicketsRequested` ->
  `TrayApp.QueueTicketsForToday`, which Start also uses; its refusals are one notice, grouped by reason) and Open.
  **New** (Status / Due / ETA / P, with Check and the last check's time at `TicketCheckInterval.Minutes`): pending
  tickets with Start. As on the board, table buttons show while the row is hovered or focused; a card is at least
  `ShellLayout.DashboardCardMinHeight` (124) tall. Lists are sorted by `TicketSort.ByDue`. The page only draws local
  data (`DashboardDataBuilder` over the ticket store + queue, `TscWrittenDates`, the worktree folders) and raises
  events; `TrayApp` does the work and re-seeds busy/checking state into a rebuilt page. The week (Monday to today, a
  weekend day only when it is today) is read once per window show with `allowSniff: true`, so it sniffs a Graph token
  only when none is cached (shared with the week card's first read, see `WeekCard`); the midnight rollover re-reads
  without Chrome, so TSC may then read "not read (click Re-check)" (`TscReadState.NoToken`); Re-check reads with
  `allowSniff: true`. After a drain that had a today entry, `TrayApp` re-reads it without a sniff (the drain's token is
  still cached), but only if the page had already loaded it.
- **`WorkPage`** - the Work page (`ShellPage.Work`, Ctrl+2): the old Log and Tasks pages in one, since both picked
  tickets from Jira. The board (`TasksPage`, left) and the log column (`LogPage`, right, `ShellLayout.LogColumnWidth`
  = one card (`LogPage.CardW` 460, as narrow as the week card's header and the New entry row allow) plus its 20px right
  margin) sit side by side from `ShellLayout.WorkSideBySideMinWidth` (the board's
  `DesignWidth` + the column), each full height (`ShellLayout.WorkBounds`); a narrower page stacks the column at its
  natural height (`LogPage.NaturalHeight`) over the board (at least its `DesignHeight`) and the page scrolls. There
  only the page scrolls (`LogPage.SetStacked` turns the column's own scrolling off), and the page's `AutoScroll` is
  turned on only after the columns are placed, or the side-by-side bounds would scroll it sideways. A row's
  **"+ Log"** (`TasksPage.LogRequested`, key + summary) calls `LogPage.AddTicketToInput`, which marks the ticket
  verified with that summary and focuses the ticket box; `LogPage.EntryTicketsChanged` (every edit of the box)
  feeds `TasksPage.SetEntryKeys`, so a typed ticket's row shows a disabled "In entry" and an accent bar
  (re-render debounced 200ms, like the board's search). `WorkPage` forwards `IShellPage` to both. With Tasks off this session the board's
  place shows the "Tasks are disabled" text. There is no "My tickets" list or custom JQL any more: the board's
  search filters, and "Add by key/link" puts any other ticket on the board. Add by key sits in the shell header
  (`WorkPage.HeaderControl` = `TasksPage.HeaderControl`), apart from the search, which is inside the board's card.
- **`LogPage`** - the log column of the Work page, structured like the old web app: `Card`s, input first:
  "New entry" (date + ticket, then one action row: the primary "Add to queue", a "Log now" dropdown
  with TSC + HRM / TSC only / HRM only, and a "more" dropdown with Log OFF... / Check TSC
  session / Re-authenticate TSC; the dropdowns are `ContextMenuStrip`s painted by
  `ThemedMenuRenderer`), "Queue" (titled QUEUE; called "Will log" in code, described
  below) and the week card (`WeekCard`, see below).
  **Layout:** `LayoutCards` (on every `OnSizeChanged`) places the three cards from
  `ShellLayout.LogCardBounds`, stacked; the Queue takes any extra height, so the week card ends at the bottom
  margin. A column shorter than the built cards scrolls (`_body.AutoScroll`; cards are placed at their bounds
  plus `AutoScrollPosition`, kept clear of the scroll bar via `ScrollBarOverflow`). Focusing the ticket box
  scrolls back to the top. Only `Bounds` change, so a resize keeps typed text. The cards carry no `Anchor`; the
  controls inside them do (the Queue list stretches on all four sides).
  `BuildLayout` stores the cards and their built heights before assigning `Size`, so that first
  `OnSizeChanged` reproduces the built bounds before `AnchorForResize` records the distances.
  "Will log" has one row per ticket - a
  status dot, the colored
  ticket key, and its time slots; the dot reflects Jira verification, green valid /
  red not found / amber error, via `VerifyTicketsAsync` debounced on typing and on
  blur, tickets added from the board pre-marked valid. While typing it previews the typed tickets for
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
  "Log queue now" (secondary; raises `DrainRequested` -> the tray's guarded drain) + "Clear queue" (asks first)
  buttons - this is the single view of what's scheduled (there is no separate queue card). The
  list **scrolls internally** (`WillLogHostH` sizes it for a date header + 3 ticket rows at its built
  height). "Log OFF" needs no ticket and takes any date,
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
  Keyboard fast path: showing the page and a board row's "+ Log" both focus the ticket
  box, and Enter in the ticket box queues (only when "Add to queue" is enabled). Hiding the window clears
  the half-typed ticket text (`OnHidden`), because the date resets to today on reopen.
- **`TasksPage`** (with `TaskRow`) - the board of the Work page: Jira tickets as a table in sections New / In
  progress / Done / Dismissed. The search box sits in the card above the table, as wide as the list (debounced 200ms);
  "Add by key/link" (placeholder "Add MDP-1234 or link") + Add, "Check now" and the last check's time are a
  `HeaderToolbar` in the shell header (`HeaderControl`, laid out by `LayoutToolbar`: a narrow header drops the time,
  then narrows the Add box from 200px); Add's result is a notice (`StatusRaised`), as the header has no room for a
  result line. It re-renders on show only when data changed while hidden. The sections are stacked in one list across
  the full card width at any window size (no kanban columns; Done and Dismissed start collapsed, so each app start
  shows only what still needs work); rows move via their buttons. Any card resize re-renders once per burst (posted),
  keeping the list's scroll. A `TaskRow` is **one line**: key link, summary (cut with "..." and a tooltip), then the
  cells status, Due, ETA, P, Logged, MAIN and DEV drawn at the columns `TaskTableLayout.For` gives (`TaskRow.LayoutFor`,
  shared with the `TaskTableHeader` column titles under the search, so they line up). Fixed column widths come from
  `RowColumns.TableWidths` (each column's widest cell and title in either language; status 120px is cut with "...");
  the summary takes the rest, and a board too narrow for a 160px summary drops ETA, then P, Logged, MAIN and DEV.
  Cells are plain text (`TaskRow.Cell`): only what needs action is colored - Due red overdue / orange soon, P red top
  / orange high, Logged red when no time is logged, the status purple (orange for an In progress ticket Jira already
  finished, with a "Done in Jira" tooltip) - and an empty cell is a gray "–". MAIN and DEV are `TaskRow.PrCell` over the
  ticket's `DashboardDataBuilder.LatestPrs`: one word per branch across its repos - Merged (green) once every repo's PR
  merged, "1/2 merged" (blue) while some wait, Open (blue) before any did, Abandoned (gray) when that is all there is (an
  abandoned PR counts only when no repo has another), "–" with no PR; the tooltip lists `<RepoName> #<id>: <status>`
  per PR, and a busy row shows "–" in both. A purple dot leads the status while
  the row needs action (Ready to Test, or Done in Jira); a busy row shows its stage in the status cell. The buttons
  (Start / Done / Open / Ready to Test tinted, Restore / Reopen neutral, see `MacButton`; Reopen on a Done row moves it
  back to In progress (`TicketBoard.Reopen`), where Open recreates a removed worktree; Dismiss is a `CloseButton` X at
  the right edge, asked first; "+ Log" leftmost, "In entry" while the ticket is in New entry, none when Dismissed) sit
  in a panel at the row's right edge, on the row's own background over the cells there, shown only while the cursor is
  over the row or focus is in it (`RefreshActions`: every control of the row re-checks the cursor, since moving onto a
  button raises the row's MouseLeave; Tab reaches the key link, which shows them, then the buttons). That row is
  `TicketTableRow` (key link, summary, drawn `TableCell`s with their tooltips, the hover panel; `LayoutFor(rowWidth,
  columns)` keeps only a table's own columns), which `TaskRow` (`BoardColumns`: every column) and the Dashboard's
  `DashboardRow` extend. Each front-end repo in a PR tooltip is named by `DashboardDataBuilder.RepoName`, which the
  ticket header's PR tags, the PR notices and `TeamsPost`'s `[FE <App>]` headers also use.
  `TrayApp` polls
  Jira every `TicketCheckInterval.Minutes` (5 by default; Settings' "Check Jira and PRs every" pills 5 / 10 / 15 / 30 / 60,
  instant-apply, persisted as `AppSettings.TicketCheckMinutes`; `Changed` re-arms `_tasksTimer`) via `TicketPoller` and notifies new assignments and In-progress tickets that
  turned Done in Jira. The same poll moves a Done ticket Jira sent back for more work to In progress and notifies it
  (`TicketBoard.ReopenSentBack`, before `UpdateTicketDetails` overwrites the stored status): `JiraStatusFlow.IsSentBack`
  is a move from past pre-work (PR & Code Review onward) into In Progress / QA Failed / Reopened, so a ticket marked
  Done while Jira still read In Progress stays Done. The assigned query (`statusCategory != Done`) holds such a ticket
  again, so this costs no extra Jira call. A row's **Start** (`TrayApp.StartTicketAsync`, in `TrayApp.TicketTabs.cs`) opens
  the ticket's workspace tab (see the Ticket page); Start no longer launches VS Code or writes
  `.vscode/tasks.json`. A missing worktree `<DEV_REPO_PATH>\.claude\worktrees\<KEY>` is created off the
  UI thread by `WorktreeService.Create` (on the old `<KEY>` branch plus a merge of `origin/main` when that branch
  still exists, see "Worktree cleanup") and the key joins `_shipPending`, so the tab's first Claude
  session runs `ship <KEY>` (it waits until that session actually starts); an existing worktree just
  opens, and its session runs `--continue` when the worktree already has a Claude session. A missing
  dev repo or a failed worktree is a notice and opens no tab. Then, always in this order: `TicketBoard.Start` (In progress), `RefreshTicketViews`, the tab
  (only when the worktree is ready) and the queue for today via `TicketQueue.AddStartedTicket`.
  **Open** (`TrayApp.OpenTicketAsync`) opens the tab on an existing worktree and clears the ticket's
  unseen-PR-comment alert; a missing worktree asks "Recreate the worktree?" and creates it the way Start does, but
  without `ship`, and it touches neither board nor queue. While Start or Open makes a worktree the row shows the
  `StartStage` (`Fetching`, then `CreatingWorktree`, then `MergingMain` on an old branch; `UpdatingJira` is Ready to Test's); `TrayApp._launchStages` holds the in-flight
  stages (so a rebuilt `TasksPage` is seeded from it, and a second click on a busy ticket is
  ignored, Open included) and the `finally` always clears the stage.
  The same check also runs **PR tracking** when `AZURE_DEVOPS_PAT` is set, even if the Jira
  poll failed: `PrUpdateChecker` reads the Azure DevOps PRs of every Started ticket (and Done ones
  with an open PR) and notifies merges and new comments. The very first run only records a baseline
  (`.pr-baseline-done`, marked only after a complete pass with no network, per-ticket or per-branch
  failure; a failed main/-dev branch still reports the healthy one but counts the pass incomplete),
  so existing PRs do not flood notices. New tickets, PR merges and new PR
  comments are also posted to Teams via `TeamsNotifier` when `TEAMS_WEBHOOK_URL` is set (never
  throws; the URL is a secret and is never logged). **Ready to Test** (`TrayApp.MoveToReadyToTestAsync`)
  shows on an In progress / Done row once its `dev` PR merged: it walks the ticket through Jira's
  transitions (`JiraStatusFlow.GetStepsToReadyToTest`, from Jira's **live** status so it never walks
  backward), stops at the first missing transition, then re-reads the ticket. It asks first (testers see the
  move). Deployed to Dev / Ready
  to Test stay a manual click because CI deploys after the merge; the merge notice only reminds.
  This replaces TaskWatcher: **shut TaskWatcher down and remove it from startup**, or both apps
  post the same events to Teams.
- **`WeekCard`** - the weekly coverage check, a card in the log column (see `UI/` above). It reads the current
  week on the Work page's first `OnPageShown` after the window was shown (`LoadIfNeeded`) with `allowSniff: true`,
  like the Dashboard's first coverage read, so it is current without a Refresh: with no cached Graph token
  it sniffs one (headless Chrome, at most once per token lifetime), and `AcquireGraphTokenAsync`'s lock makes
  the other reader, and a Log now started meanwhile, wait for that token instead of finding the browser busy.
  The previous / next week buttons read without Chrome (the token is usually cached by then); a day TSC could
  not read shows "not read", and the card's status line then says "Refresh reads it". Refresh may always
  launch Chrome. Hiding to tray resets it to the current week (`Reset`). After a log action in the
  log column (TSC + HRM, TSC only, HRM only, Log OFF) and after a drain (`TrayApp.RefreshAfterDrain`), a loaded
  card re-reads without Chrome (`ReloadIfLoaded`); a read requested while one runs follows it.
- **`ClaudePage`** - the Terminal page (labelled "Terminal"; `ShellPage.Claude` in code): a `HeaderTabStrip` in the
  shell header (`HeaderControl`) with one tab per terminal, labelled with its folder's name (the full path in the
  tooltip), and "+" for a new terminal in the dev repo; below it the selected terminal's `TerminalView`, built the first
  time that tab is shown (an empty-state label when none is open, `HasOpenTabs`). **Every terminal (this page
  and each ticket tab) is a PowerShell that runs claude**: `ClaudeSessionHost` (owned by `TrayApp`) starts `pwsh.exe`
  when on PATH, else `powershell.exe`, as `-NoLogo -NoExit -Command "function global:claude { & '<claude.exe>' --settings
  '<hooks>' @args }; claude <argument>"` under ConPTY, with claude.exe from `%UserProfile%\.local\bin` (else `claude.exe`
  on PATH). So `/exit` (or Ctrl+C twice) leaves the PowerShell prompt, where `claude update`, `claude -c`, `claude
  --model ...` or any command runs, and a `claude` typed there still gets the hook settings (a function wins over an
  exe in PowerShell; the function calls the `.exe` so it never calls itself). `exit` ends the terminal.
  **Terminal tabs** live in `TrayApp.Terminals.cs`: `AppSettings.TerminalTabs` (name + folder, null folder = the dev
  repo; null list = never saved, so the first run after the upgrade makes one `claude` tab in the old
  `ClaudeDirectory`) and `SelectedTerminal`; `_terminalHosts` holds one `ClaudeSessionHost` per tab, made the first
  time the page shows it, disposed when its tab closes. A tab's name (`claude`, `claude2`, ... via
  `TerminalTabs.NextName`; loaded names must match `^claude\d*$`) is also its hook session name. A tab's ▾ (selected
  first) lists the dev repo plus up to `ClaudeFolders.MaxRecent` recently picked folders (`RecentClaudeDirectories`)
  and "Choose another folder..."; a pick asks before stopping a running terminal, then restarts it there
  (`TerminalView.RestartSession`); a vanished folder falls back to the dev repo. Closing a running tab asks first
  (deferred via `BeginInvoke`, like the Ticket page), drops the tab's resume ids, then disposes its host.
  Each tab resumes its own last conversation in its folder with `--resume <id>`
  (`TicketLauncher.GetResumeArgument`, only while the transcript exists). The id comes from the hooks'
  `session_id` and is kept per tab and folder in `AppSettings.ClaudeSessionIds` (`TerminalTabs.SessionKey`,
  `claude2|<folder>`; ids saved before tabs, keyed by folder alone, go to the `claude` tab); /clear moves it to the new
  session, and so does a claude typed at the prompt in that folder, but not one started after a `cd` elsewhere
  (`TicketLauncher.IsTranscriptOfFolder` checks the hook's `transcript_path`). It is not `--continue`, since the user
  runs other Claude sessions in the dev repo and `--continue` would pick the newest of those. The command line is
  built by `ClaudeSessionHost.BuildCommandLine`: the argument follows the first `claude` - flags such as `--continue` /
  `--resume <id>` go bare, prompts such as `ship <KEY>` are single-quoted; nothing may hold a double quote, which would
  end the `-Command` string.
- **`TerminalView`** - the reusable terminal (extracted from `ClaudePage`): it draws a session host with
  **xterm.js** in a **WebView2** (`Assets/terminal/`: `index.html` + vendored xterm 6 and addon-fit,
  MIT). **`WebViewSetup`** creates the one shared WebView2 environment (data in
  `%AppData%\NoisLogTray\WebView2`) and serves `<exe>\Assets` to every web view through the virtual
  host `noislogtray.assets`; it also sets `DefaultBackgroundColor` to the window background (no white
  flash in dark mode) and blocks popups and navigation away from that host. A click in a page raises the WebView2's `GotFocus` without moving the form's `ActiveControl` off the control focused before, which then kept its focus look and took focus back on the next window activation; `WebViewSetup.MakeActive` moves it to the view (compared with `ActiveLeaf`, the innermost active control, so setting it does not raise `GotFocus` in a loop). Each page's URL also carries the
  current theme (the terminal's `theme` = the same object as its `theme` message; Changes/Files `colors` + `dark`,
  applied by a `<head>` script and given to Monaco at creation), so the first paint is already themed: the `theme`
  message only follows `ready`, which for Changes/Files waits for Monaco, and the pages' CSS defaults are dark. A view starts its
  WebView2 on its first `Activate()`; later calls re-fit with a `fit` message (the window may have
  been resized while hidden). Messages are JSON: page -> host `ready` / `resize` /
  `input` / `page` (Ctrl+1..4 typed in the terminal, which WebView2 keeps from the form's
  `ProcessCmdKey`; mapped by `TerminalView.PageForShortcut`) / `pasteRequest` / `openPath` / `openUrl`, host -> page `output` (base64
  bytes, so split UTF-8 survives) / `theme` (xterm's theme plus the `jump` button's card colors) / `labels` / `focus` /
  `reset` / `fit` / `paste`. While the view is scrolled above the newest line (`viewportY < baseY`, checked on every
  scroll and render) a round ArrowDown button sits at the bottom center; a click scrolls to the bottom and focuses the
  terminal, ready to type. Output is coalesced and flushed at
  most every 16ms. A rebuilt view (Settings save) `Attach`es to the running session and replays its buffered
  output, then resizes to nudge a repaint. A mouse selection (drag or double-click) is copied on release;
  Ctrl+V and a right-click paste through the host (`pasteRequest`; the page would need a clipboard permission), which
  picks by `TerminalView.PasteFor`: text goes back for `term.paste` (bracketed paste) and wins over an image beside it;
  an image alone only sends claude its image-paste key, Alt+V on Windows (`ESC v`), so claude reads the clipboard
  itself and inserts an `[Image #N]`; files copied in Explorer paste as their quoted paths;
  `minimumContrastRatio` 4.5 keeps claude's dark-background colors readable on either theme (it matters on light).
  Ctrl+C copies a selection, else sends ^C. Shift+Enter sends Ctrl+J (`\n`, claude's newline), since
  xterm.js sends `\r` for it like Enter and would submit. Ctrl+click on a web URL (the same link provider, trailing
  punctuation dropped) or an OSC 8 hyperlink (`linkHandler`) posts `openUrl`, and the host opens the browser only for
  http(s) (`TaskRow.WebUrl`, also the Files view's check), on the Terminal page and in ticket tabs alike. claude prints
  file paths as OSC 8 links too, which xterm prefers over the path provider below, so a `file:` link or a bare path
  there (`TerminalView.LinkPath`) opens like a Ctrl+clicked path; anything else is logged as `[claude] link not opened`. Ctrl+click on
  a file path (an xterm link provider: `C:\...` or a relative path with an extension, optionally `:line[:col]`, never
  inside a URL) posts `openPath`;
  `TerminalView.ResolvePathLink` resolves it against the session's folder and raises `FileRequested` only for a file
  that exists. The Terminal page opens it in VS Code; a ticket tab shows it in its Files view when `FilesView.Locate`
  places it in the ticket's docs (or a worktree's copy of them) or a worktree, else opens VS Code. When the shell
  exits the view says so and Enter starts a new one, running claude with its argument again (there is no Restart
  button). The terminal takes its view's whole height. The csproj's `DropWebView2Wpf`
  target (also in the test project) removes the package's WPF wrapper reference, which otherwise
  clashes with WinForms' `WindowsBase` (MSB3277).
- **`TicketWorkspacePage`** (with `TicketWorkspaceView`) - the Ticket page (`ShellPage.Ticket`, the
  sidebar row after Claude, Ctrl+4): a `HeaderTabStrip` in the shell header (`HeaderControl`; one `HeaderTab` per
  ticket, the key and its × in a single pill, the × circled on hover; a middle click or Delete also closes, the waiting
  dot sits in its corner - an empty-state label when none is open, `HasOpenTabs`; events `CloseRequested` / `VsCodeRequested` /
  `SelectionChanged` / `PageRequested`) over the selected ticket's `TicketWorkspaceView`. The view
  has a header - row 1 is the Jira key link, the summary (cut with "..." and a tooltip), the facts as title + value
  text colored like the board's cells (`FactCells`: Status / Due / ETA / P / Logged) and the
  buttons (`KeyRow`: the summary gives way down to 160px, then the facts take a row of their own); below it one PR row
  per repo (`PrGroups`: "BE", "FE External", ... in a label column, then that repo's PRs as clickable `FactTag`s,
  `PrTagText` = branch, status mark, number, comments; merged green, open blue, else gray)
  (`DashboardDataBuilder.LatestPrs` -> `TicketPr`, `Url` from `AzureDevOpsClient.PullRequestUrl`)
  and the Claude / Changes / Files / Teams view buttons plus VS Code (each button is as wide as its longer label,
  English or Vietnamese) - over a `TerminalView`, a `ChangesView`, a `FilesView` or a
  `TeamsPostView` (the last three created on their first click; all live while the tab is open). The header's VS Code button opens only the backend worktree
  folder (`TicketLauncher.OpenVsCode`), with no Claude inside it (the Files toolbar's opens the selected file). The ▴/▾ button at the far right collapses the
  header to its key row (the PR rows, and the facts when they had a row of their own, hidden) for every tab at once
  (`AppSettings.TicketHeaderCollapsed`).
  **Teams view** (`TeamsPostView`, built by `TrayApp.CreateTeamsPostView` in `TrayApp.TeamsPost.cs`) posts a
  ticket announcement **as the user**: the app POSTs
  `{"subject": <first line>, "message": <the rest as HTML>}` to the user's own Power Automate workflow
  (`TEAMS_POST_WORKFLOW_URL`), which posts it in a channel under their account. Without that URL the button
  is only a notice and the tab keeps its view. The editor (on a `Card`, up to 960px wide) loads `docs\<KEY>\teams-post.md` (dev repo first, then the ticket's
  worktree) or else `TeamsPost.Build`'s template (`<KEY> <summary>`, an empty description line, then `[BE]` /
  `[FE <App>]` blocks of the tracked PRs), with the caret on the description line; each show re-reads the
  draft while it has no edits (Claude may rewrite the file), and "Reload draft" re-reads it anyway. Send or
  Ctrl+Enter posts. A blank post or one with only the title line is refused in the status line; for a ticket
  already posted (`AppSettings.TeamsPostedAt`, entries kept 180 days, recorded by `RecordTeamsPost` on
  `Posted`) the first Send only warns and a second one posts again; closing the tab while sending cancels the
  request. The workflow answers 202 on receipt, so a failure inside the flow is not seen by the app.
  **Workflow setup (once):** in Teams create a flow from "Send webhook alerts to a channel", edit it in
  make.powerautomate.com, replace the "Post card" action in the condition's True branch with "Post message in
  a chat or channel" (Post as: User, Post in: Channel, Message = `triggerBody()?['message']`, Subject =
  `triggerBody()?['subject']`), set the trigger to "Anyone", save and paste the trigger URL into Settings.
  **Tabs and sessions** live in `TrayApp.TicketTabs.cs`, not in the page, so a shell rebuild
  reattaches: `_ticketSessions` holds one `ClaudeSessionHost` per open ticket, created with
  `fallBackToProfile: false` (a vanished worktree must not silently run Claude elsewhere) and the
  argument `ship <KEY>` while the key is in `_shipPending`, else `TicketLauncher.GetIdlePrompt`
  (`--continue` when the worktree already has a Claude session, else none); the session's first hook
  (`OnClaudeHook`) clears the pending flag, or `Started` when the start had no hook settings
  (`ClaudeSessionHost.HasHooks`), so a claude that fails inside the PowerShell keeps `ship` for the next try. The open keys persist in
  `settings.json` as `openTicketTabs` (on load only keys shaped like `ABC-123` are kept, since keys
  end up in paths, git refs and a cmd.exe line). `PruneTicketTabs`, run when the shell
  is built, drops a tab whose backend worktree is gone and whose session is not running (skipped
  while the dev repo itself is missing, so a mistyped path in Settings does not lose the tabs).
  Closing a tab asks first (`ConfirmDialog`, default Cancel) while its terminal still runs, then stops the session; the
  dialog is raised via `BeginInvoke` because the close comes from a mouse or key handler of the `HeaderTab` that
  `SetTabs` disposes.
- **Claude hook bridge** (`ClaudeHookServer`, `ClaudeAttention`, `TrayApp.ClaudeAttention.cs`) - tells
  the window when a Claude session waits for the user. `TrayApp` starts the server in its ctor: an
  `HttpListener` on a free `localhost` port under a random per-run token path
  (`http://localhost:<port>/<token>/<session>`; http.sys answers 404 outside that prefix, so the
  token check is free). Every session's `ClaudeSessionHost` gets a `settingsFile`
  (`HookSettingsFor`, resolved at each start) = `%AppData%\NoisLogTray\claude-hooks\<session>.json`,
  whose HTTP hooks (UserPromptSubmit / PreToolUse / PostToolUse / Notification / Stop / SessionEnd) POST to that
  session's URL; claude merges them with the user's own hooks and the server always answers `200 {}`,
  so it never blocks or alters a turn. The `/hooks` menu does not list `--settings` hooks even though
  they run; to check them, look for the `[hooks] <session>: ...` log lines. The session name is the Terminal tab's
  name (`TerminalTabs.IsTerminalSession`: `claude`, `claude2`, ...), else the ticket key. No free port or an unwritable settings file
  only means that session starts without hooks, as before.
  **Waiting rules** (`ClaudeAttention.Next`): Stop -> `Finished`, unless its `background_tasks` still lists a
  running `subagent` (`ClaudeHookEvent.SubagentsRunning`): the turn ended only to wait for background agents, so the
  wait is left as it was and no "done" notice fires; claude starts another turn when they finish (UserPromptSubmit ...
  Stop with an empty list), and that Stop is the done. A background shell or Monitor (`type: "shell"`) does not hold it
  back, since a dev server may run for good; a `permission_prompt` /
  `worker_permission_prompt` Notification -> `Permission` (2.1.284 draws AskUserQuestion as a
  permission dialog, so its prompt stays `Question`; the prompt only comes after the dialog is open 6s);
  `elicitation_dialog` / `elicitation_url_dialog` / `agent_needs_input` or a PreToolUse of
  AskUserQuestion -> `Question`; UserPromptSubmit and any other
  tool event end the wait; `idle_prompt` changes nothing. A subagent's tool event (payload has
  `agent_id`) ends only a `Permission` wait, because background agents keep running after a Stop.
  **Badges and notices:** `TrayApp.OnClaudeHook` (marshaled from the pool thread) keeps
  `_claudeWaits` (session -> reason) and logs each change as `[hooks]`. A (re)starting terminal (`Starting`), an ended
  one (`Exited`), a claude that ended inside a running terminal (`SessionEnd`: /exit, /clear, Ctrl+C twice; SessionStart
  takes no HTTP hooks) and a closed or pruned tab clear their wait (`Dispose` raises no `Exited`). A `Finished` wait also clears once seen
  (`ClaudeAttention.ClearsWhenSeen`; `AcknowledgeSeenWaits` runs on `ShellForm.PageViewed` - page switch, window
  shown or activated - and on a tab switch, and a Stop arriving while `IsWatching` sets no dot); `Permission` /
  `Question` stay until answered, since claude is blocked on them. `RefreshClaudeAttention` (also run when the shell
  is built) sets a count badge on the Claude row (waiting terminals) and on the Ticket row (waiting tickets), and a
  dot on each waiting tab (`ClaudePage.SetWaiting` / `TicketWorkspacePage.SetWaiting` -> `HeaderTab.ShowDot`). A new
  wait is also a `Notify` ("MDP-1: Claude is done - your turn." / needs permission / is asking; a terminal is labelled
  "Terminal <folder>") unless `IsWatching`: the window is the foreground one and already on that session (the Terminal
  page with that tab selected, or the Ticket page with that ticket selected, whichever view is open). A hook for a closed tab or an ended
  session is ignored.
- **`ChangesView`** - the ticket's changes as a read-only diff. `TicketWorktrees.Find` lists the
  backend worktree first, then every sibling `*-Angular` repo that has a
  `.claude\worktrees\<KEY>`; `GitChanges.List(worktree, ticketKey)` runs per worktree. The file list
  on the left is grouped `"BE - Backend-DotNet"` / `"FE - Portal-Angular"`, each a folder tree like Azure DevOps
  (folders first, a folder holding only one subfolder merged into it, folders open/close and stay so across
  re-lists); the right is a **Monaco** diff (`Assets/diff/index.html`, two columns unless Inline is on, Monaco's
  narrow-window inline switch disabled, language validation off). A toolbar above both holds the list toggle,
  Refresh, the open file's path, previous/next change (F7 / Shift+F7) and file (Alt+Up/Down), and Inline; the list
  is resizable by its right edge. List width (em), list hidden and Inline are each posted (only the one that changed) as a `layout` message and kept
  in `AppSettings` (`DiffListWidthEm`, `DiffListHidden`, `DiffInline`); toolbar labels come from a `labels`
  message (re-sent on a language switch). It
  re-lists on every `Activate`, Refresh and language switch; CRLF is normalized, and a binary or
  over-1MB file shows a placeholder. `GitChanges` computes the base as
  `merge-base refs/heads/<KEY> origin/main` - the ticket branch is `<KEY>`, with `<KEY>-dev` for the
  dev PR. If HEAD is on `<KEY>` it is the working-tree diff against that base, untracked files
  included; otherwise (e.g. `<KEY>-dev`, after git-cp merged development into it) only the committed
  `base..<KEY>` diff, so the development merge never shows. `src/docker-compose.yml` is excluded
  (a local-only edit the worktree skill carries), every git call passes `--no-optional-locks`, and
  `ReadOriginal` / `ReadModified` check the size first (`cat-file -s` for a blob, the file length on
  disk), so nothing over 1MB is read whole. Monaco is vendored
  under `Assets/monaco` (monaco-editor **0.52.2** `min/vs`, the last AMD build; nls files removed,
  ~12MB; `LICENSE-monaco.txt`) and copied to the output with the rest of `Assets/`.
- **`FilesView`** - the ticket's files, read-only. The list on the left has one group per source: `Docs - <KEY>`
  (`TicketFiles.DocsPath`: `docs\<KEY>` in the dev repo, else in the ticket's backend worktree; every file, by
  name) and then each `TicketWorktrees.Find` worktree (`"BE - Backend-DotNet"`, `"FE - Portal-Angular"`;
  `TicketFiles.ListWorktree` = tracked + untracked-not-ignored via `git ls-files`, minus files deleted on disk, with
  `--no-optional-locks`). A group that cannot be listed shows its error and the others still list. Each group is a
  folder tree like `ChangesView`'s whose folders build their rows on first open (a 10k-file worktree costs only its top
  level); a filter box above it switches to a flat list of the first 500 matches. The right shows the selected file by
  `TicketFileKind`: `Text` in Monaco (`Assets/files/index.html`, same vendored Monaco), `Markdown` rendered with a
  Source / Rendered switch, `Image` inline (a data URL, up to 10MB; images in rendered markdown are asked for
  one by one); `Binary`, `TooLarge` (text over `GitChanges.MaxChars`, an image over 10MB) and a failed read (kind
  `error`) show a note with a Show in folder button. The page can only name a file as a (group, path) pair, and the host opens only a pair it listed itself
  (`FilesView.FullPath`), so a made-up path reads and opens nothing. It re-lists on every `Activate`, Refresh and
  language switch; the open file is sent again only when its write time or size changed (or the language
  switched), so a re-list keeps its scroll. VS Code opens the file (`TicketLauncher.OpenVsCode`) and Show in folder
  runs `explorer.exe /select`. `Assets/files/markdown.js` renders with **marked** (`Assets/marked`, MIT,
  `LICENSE-marked.txt`), loaded before Monaco's AMD loader. Docs come from Jira and other people, so raw HTML is escaped (shown as text) except `<br>`, and a link
  becomes an anchor only for http(s) or a relative path that stays inside the group (a relative link to a listed
  file opens it in the view, http(s) opens the browser); `javascript:`, `data:`, `//host` and the like stay plain
  text. List width (em) and list hidden are posted as a `layout` message and kept in `AppSettings`
  (`FilesListWidthEm`, `FilesListHidden`); labels come from a `labels` message. A Ctrl+clicked terminal path arrives
  through `Reveal`, answered after the listing that the view's `Activate` starts: a listed file gets a `reveal`
  message (the page clears the filter, opens the file's folders, selects it and goes to the line, switching a rendered
  markdown to Source) when the listed copy is the clicked file or byte-equal to it (`ShowsSameContent`: the docs group
  lists the dev repo's copy of a worktree's docs). A differing copy, a file the list does not hold (gitignored, or its
  group failed) or a view that failed to start opens the clicked file in VS Code instead.
- **`SchedulesPage`** - the body of Settings' "Schedules..." (`SchedulesDialog`, a modal `CardDialog` that owns the page and
  calls its `OnPageShown` / `OnActivatedRefresh` / `OnHidden`; `TrayApp.OpenSchedules`, which only activates an open one and
  opens nothing while another modal holds the window). Card one is the daily knowledge scan (Windows
  task `DailyKnowledgeScan`, `docs\local-config\daily-knowledge-scan.ps1` in the dev repo): a task line (schedule, time
  limit, next run, Running / Disabled) and a newest-run line from `KnowledgeScan.NewestOutcome`; Run now (asks first, off
  while running or disabled), Open log (VS Code at the selected run's first line), Refresh; time-limit pills 1/2/3/4/6 h
  (`WindowsScheduledTask.SetExecutionTimeLimit`); the ten newest runs on the left (start, duration, outcome) and the selected
  run's log lines on the right (a growing run gets its new lines appended). Card two is the tray's own jobs (`AppJob`:
  auto-log at `LOG_TIME`, the Jira/PR check, the catch-up drain) with schedule, next run, last result and a tooltip of the
  last ten, recorded through `JobRuns.Record` in `TrayApp.AutoLog.cs` (`OnScheduledFireAsync`, `CatchUpIfDue`; `DrainAsync`
  returns its outcome) and `TrayApp.TicketCheck.cs` (`CheckTicketsAsync`). The page reads off the UI thread on show and every
  minute while shown, and rebuilds its rows only when what they show changed.
  **Runs from the log** (`KnowledgeScan.ParseRuns`): a run starts at `Found N ticket` / `No tickets reached Done` /
  `ERROR: JIRA_API_TOKEN not found` / `ERROR: Jira query failed` (the Jira-failed line and the "No tickets" line after it
  are one run); `KnowledgeMiner`'s `(NoisLogTray)` line and its one result line belong to no run; problems are only the
  script's own `FAILED (exit` / `Giving up on ` / `ERROR:` lines. The task's run is the one starting within 5 min after
  `LastRunTime` (a hand run after it is its own run); `0x41306` on it is `StoppedByTaskScheduler`, named without the
  current limit, which may not be the one that stopped it; a task start newer than every run with a non-zero result is
  `StartFailed`. **Watch** (`TrayApp.Schedules.cs`): every 5 min, at startup and after each dialog read,
  `KnowledgeScan.NoticeFor`; the first look only records a baseline; an `Unfinished` / `Stopped` / `StartFailed` run gets
  one notice (`AppSettings.KnowledgeScanJudgedRunAt`), every bad run including `With problems` (the script toasts those
  itself) gets the Settings badge and the button's dot until the dialog is opened or activated (`KnowledgeScanSeenRunAt`);
  nothing while the dialog is the active window, a notice's click opens Settings and then the dialog (posted, off the
  click), and a failed Task Scheduler read decides nothing.
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
  `NoToken`, `Failed`). With `allowSniff: false` (week paging, the re-reads after a log or drain, the
  Dashboard's midnight rollover) it uses only
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
  (`JIRA_API_TOKEN`, `HRM_API_KEY`, `AZURE_DEVOPS_PAT`, `TEAMS_WEBHOOK_URL`, `TEAMS_POST_WORKFLOW_URL`, see `Secrets.Keys`) are encrypted at rest with
  Windows DPAPI (CurrentUser) - stored `enc:`-prefixed, decrypted only when building
  the runtime config; `AppConfig` upgrades any leftover plaintext secret on load.
  Required keys: `JIRA_BASE_URL`, `JIRA_EMAIL`, `JIRA_API_TOKEN`, `HRM_API_KEY`.
  Optional: `TSC_GRAPH_COLUMNS` (per-user columns in the shared workbook; default
  `M, J` via `TscCells.TargetColumns`, parsed by `TscCells.ParseColumns`),
  `LOG_TIME` (daily auto-log + reminder time; the dialog uses 12-hour `h:mm tt`, e.g.
  `6:00 PM`, and `AppConfig.ParseLogTime` also accepts 24-hour `H:mm`; default 18:00 via
  `AppConfig.DefaultLogTime`),
  `DEV_REPO_PATH` (repo Tasks Start/Open work in; default `AppConfig.DefaultDevRepoPath`),
  `AZURE_DEVOPS_PAT` (enables PR tracking) and `TEAMS_WEBHOOK_URL` (enables Teams posts); both are
  imported once from `<DEV_REPO_PATH>\docs\local-config\tokens.env` (`AppConfig.ImportDevTokens`)
  when not already in the config - a key the user cleared to empty is never re-imported,
  `TEAMS_POST_WORKFLOW_URL` (the user's own "post as me" workflow for the ticket tab's Post to Teams; never imported),
  `HRM_PROJECT_ID`, `MS_GRAPH_TOKEN` (override that skips the sniff),
  `TSC_GRAPH_DRIVE_ID` / `_ITEM_ID` / `_SHARE_URL` / `_WORKSHEET`.

## Per-user data

Everything runtime lives under `%AppData%\NoisLogTray` (see `AppPaths`): `queue.json`
(the pending log queue), `settings.json` (theme, window position, the open Ticket tabs
(`openTicketTabs`), the knowledge-scan watch's run starts (`knowledgeScanJudgedRunAt`, `knowledgeScanSeenRunAt`), the Terminal page's tabs, selected tab, recent folders and resumable session ids (`terminalTabs`,
`selectedTerminal`, `recentClaudeDirectories`, `claudeSessionIds`), the Work badge's seen tickets (`seenWorkAttention`),
when each ticket was last posted to
Teams (`teamsPostedAt`), the Ticket header, Changes and Files layout (`ticketHeaderCollapsed`, `diffListWidthEm`,
`diffListHidden`, `diffInline`, `filesListWidthEm`, `filesListHidden`), **and** the `Config` key/value map of secrets/settings written from Settings,
via `AppSettings`), and `logs/app-yyyy-MM-dd.log` (one file per day), plus `WebView2/` (the web
views' browser data: terminal and diff) and `claude-hooks/` (one `<session>.json` hook settings
file per Claude session, rewritten at every session start since it holds the run's port and token).
A legacy `.env` (`AppPaths.EnvPath`) is migrated into `settings.json` on
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

`job-runs.json` (`JobRuns`) holds the last 10 runs of each in-app job for the Schedules dialog.

`tsc-written.json` (`TscWrittenDates`) lists the last 31 dates whose TSC cell this app wrote (or was
about to write); it is recorded before every TSC write. Start -> queue uses it: `AddStartedTicket`
never creates a fresh entry for such a day, since the drain would overwrite the cell with just that
ticket. It also refuses a non-MDP key, a weekend, a day whose entry has custom hours, any time at or
after min(`LOG_TIME`, 18:00), and (in `TrayApp`) any moment a drain is running - a ticket merged
mid-drain would survive alone and overwrite the cell; an
existing entry for the day gets the ticket merged in. A refusal is a notice telling the user to log
it in the window.

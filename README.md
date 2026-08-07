# NOIS Daily Log

A Windows system-tray app that automates your daily work-log entries from a single
tray icon. It writes each day's tickets to two destinations:

- **TSC** - a shared Excel workbook on SharePoint, via the Microsoft Graph Excel API.
- **HRM** - `api-hrm.nois.vn`, via its MCP server over Streamable HTTP.

It is a .NET 10 WinForms app (`net10.0-windows`). It runs resident in the tray, logs
on demand, and auto-logs a queued day at a configurable time (default 6:00 PM).

## Requirements

- Windows 10/11.
- Google Chrome installed (used to sign in to Microsoft and sniff a Graph token for TSC).
- To build from source: the .NET 10 SDK. End users of a self-contained build need nothing.

## Build, run, test

Run from the repo root.

```sh
# Build everything (main app, tests, smoke exes)
dotnet build

# Run the tray app (starts the tray icon; no console UI)
dotnet run --project NoisLogTray.csproj

# Unit tests
dotnet test tests/NoisLogTray.Tests
```

## First-run setup

On first launch (or via the tray menu "Edit credentials..."), a dialog collects your
settings and stores them in `%AppData%\NoisLogTray\settings.json`. The dialog verifies
your Jira and HRM credentials before saving.

Fields:

| Field | Notes |
|-------|-------|
| Jira site URL | e.g. `https://newoceaninfosys.atlassian.net` |
| Jira email | your Atlassian account email |
| Jira API token | create at id.atlassian.com -> API tokens |
| HRM API key | your HRM key |
| TSC columns | your columns in the shared workbook (default `M, J`) |
| Daily log time | 12-hour, e.g. `6:00 PM` (default) - drives auto-log and the reminder |

After saving, use the tray menu "Re-authenticate TSC" once to sign in to Microsoft
(opens a browser). That session is reused headlessly afterwards.

## Daily use

Open the window from the tray (double-click or "Open"). It has:

- **Log entries** - your open Jira tickets (click to add), plus date and ticket inputs.
- **Will log** - a preview of what will be logged; with the input empty it shows the
  whole persisted 6 PM queue grouped by date. A status dot shows Jira verification.
- **Actions** - Queue for the daily time, Log now, Log TSC, Log HRM, Check TSC, Re-auth.
- **Activity** - a scrolling log at the bottom with a timestamp on each line and the
  green/red result of each action.

At the daily log time (Vietnam time), any queued entries auto-log to TSC + HRM. If the
queue is empty on a weekday, the window opens as a reminder to log manually. Closing the
window (X) hides it to the tray; use "Quit" in the tray menu to exit.

Time note: all logging uses Asia/Ho_Chi_Minh time. HRM rejects future stop times, so
**today's** hours only log successfully from 6 PM onward - queue them and let the daily
run handle it. Past dates log any time.

## Configuration keys

Stored under `"config"` in `settings.json`. Any missing key falls back to a process
environment variable of the same name. See `.env.example` for the full list.

- Required: `JIRA_BASE_URL`, `JIRA_EMAIL`, `JIRA_API_TOKEN`, `HRM_API_KEY`.
- Optional: `TSC_GRAPH_COLUMNS` (default `M, J`), `LOG_TIME` (default `6:00 PM`),
  `HRM_PROJECT_ID`, `MS_GRAPH_TOKEN` (skips the token sniff),
  `TSC_GRAPH_DRIVE_ID` / `_ITEM_ID` / `_SHARE_URL` / `_WORKSHEET`.

## Where your data lives

Everything is per-user under `%AppData%\NoisLogTray\`:

```
settings.json   theme, window position, and the config/secrets map
queue.json      the pending queue that auto-logs at the daily time
logs\app.log    activity log
```

The saved Microsoft/TSC browser session lives separately at
`%UserProfile%\.tsc-daily-log-browser`.

## Distributing

Build a self-contained single-file exe (no .NET needed on the target PC):

```sh
dotnet publish NoisLogTray.csproj -p:PublishProfile=win-x64
```

Output is `bin/publish/win-x64/` - a single `NoisLogTray.exe` plus the `.playwright/`
driver folder and `.env.example`. Zip that folder and share it. No secrets ship; each
recipient fills the first-run dialog. Google Chrome must be installed on the target PC.

## More detail

`CLAUDE.md` documents the architecture, cross-cutting rules, and the smoke projects.

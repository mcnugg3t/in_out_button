# In / Out Button

Lightweight Windows C# applet for syncing git repositories at the start and end of a work session.

## What it does

- Lets you select folders to scan for git repositories (shown in the "Folders to scan" list; a scan runs automatically at startup).
- Recursively discovers repos under those folders and shows each repo's last commit age, uncommitted-changes marker (`●`), and separate **Git** and **Data** status columns, so a failed rclone push and a failed git push are told apart at a glance. `Last message` shows the failing side's root-cause line first; hover a status cell for that side's full summary.
- `Sign in` runs `git pull` for every discovered repo, then pulls dataset folders for *active* repos (see below), then runs persistent-memory's `agent-cooperation/agent-setup/sync-machine.ps1` (Claude settings and global instructions, Codex global instructions, and both clients' selected skills; logged as `machine sync:`; a failure is logged and ignored).
- `Sign out` rescans first, pushes dataset folders for active repos, then runs `git add -A`, commits staged changes with `MM-DD-YY updates`, and runs `git push`. If the push is rejected because another machine pushed first, it runs `git pull --rebase` once and pushes again; a rebase that conflicts is aborted and reported, leaving the local commit intact.
- Git-only / data-only batch buttons keep the two processes separable:
  - `Git pull (all)` — `git pull` everywhere, no rclone.
  - `Pull data (all)` / `Push data (all)` — rclone only, honoring the active-repo gate.
- Logs full git and rclone output, flags failed commands and commands that time out, and marks successful commands that emitted Git warnings as `OK with warnings`.
- Provides selected-repo actions for quick fixes:
  - `Pull`
  - `Commit + sync`, which commits local changes, runs `git pull --rebase`, then pushes
  - `Discard + pull`, which runs `git reset --hard HEAD` and then pulls
  - `Pull data` / `Push data`, which sync the selected repo's dataset folders regardless of activity
- Can register itself to launch on Windows startup.
- Optionally syncs gitignored dataset folders to a cloud remote via [rclone](https://rclone.org), so large data follows you across machines alongside the code (see below).

Settings are stored in `%APPDATA%\InOutButton\settings.json`.

## Assistant setup at sign-in

After the pull batch, the app runs `agent-cooperation/agent-setup/sync-machine.ps1`
from the discovered `persistent-memory` checkout, falling back to
`<user-profile>/Tools/persistent-memory`. The script renders root
`AGENTS.global.md` into Codex's `AGENTS.md`: under inherited `CODEX_HOME` when
set, otherwise `<user-profile>/.codex`. It also maintains Claude's global
pointer and settings and installs selected skills for both clients. Python
3.10+ must be on the app's `PATH`. Start the app with the same `CODEX_HOME`
used by Codex; start a fresh assistant session after an instruction update.

The installer backs up managed updates and refuses conflicting local edits.
A missing interpreter, installer error, or refusal appears in the `machine
sync:` log; it does not fail the rest of sign-in. Check that log before assuming
the instructions are current. Credentials and client sign-in remain separate.

The executable launched at Windows startup must include `RunMachineSyncAsync`.
Pulling this repository updates source, not an already-built executable. With
the app closed, rebuild the configuration used by its startup entry (for the
default development path, `dotnet build -c Debug --no-restore`). Then launch
that executable, or update the startup registration from the intended build.

The wrapper and real installer have been tested with disposable Windows
profiles and default/custom Codex homes. The full Sign in action and remote
machines have not been exercised for this setup change; those would perform
repository and dataset synchronization beyond the installer checks.

## AI usage and Tasks tabs

The middle of the window has three tabs: **Repos** (the folders, rclone, and repo grid above), **AI usage**, and **Tasks**. The title, the Sign in / Sign out buttons, and the log stay visible on every tab. The two new tabs are local HTML pages from `web\` shown in WebView2 (Microsoft's embedded Edge browser control) and need the Evergreen WebView2 runtime; without it each tab shows a message and the Repos tab works as before. Design and contracts: [docs/2026-10-17-usage-and-tasks-tabs-spec.md](docs/2026-10-17-usage-and-tasks-tabs-spec.md), and for the burn-pace view, the Antigravity reader, and the token refresh, [docs/2026-10-18-burn-pace-gemini-token-spec.md](docs/2026-10-18-burn-pace-gemini-token-spec.md).

- **AI usage** shows how much of each Anthropic, OpenAI, and Google usage limit is left and how fast it is being spent. See [AI usage tab](#ai-usage-tab).
- **Tasks** is a three-lane board (Projects, Admin, Misc) saved to `persistent-memory\tasks\tasks.json`, so sign-out commits it with everything else. When no persistent-memory clone is found the board saves to `%APPDATA%\InOutButton\tasks.json` and says so in a banner. Editing is locked while a batch action runs, and the board reloads when it ends.

Settings for these tabs have no controls; edit `settings.json` by hand:

- `UsageRefreshMinutes` (default 5): minutes between reads while the AI usage tab is showing.
- `CodexExecutable`: path to `codex`. When unset, the app finds it on `PATH`.
- `ClaudeExecutable`: path to Claude Code's `claude.exe`. When unset, the app finds `claude` on `PATH` and resolves the npm shim to the native `claude.exe`.
- `AgyExecutable`: path to the Antigravity CLI, `agy`. When unset, the app finds it on `PATH`.
- `ClaudeRefreshAuto` (default `true`): refresh an expired Claude Code token without a click. Set it to `false` to refresh only from the button.
- `TasksFile`: path to the task board file, overriding the default.

### AI usage tab

The tab is a burn-pace view. Each provider gets a caption row with its name and plan, followed by one tile per usage meter. A tile shows the meter's severity (normal, warning, or critical) and, in large numbers, the percent left and the time to reset. A provider that reports a weekly meter and no 5-hour meter gets a placeholder tile that says so.

Below the numbers, a chart plots percent used against the fraction of the window that has elapsed. A dotted diagonal marks even pace: the rate that reaches 100% exactly at the reset. Observed history draws as a solid line. Inferred segments draw dashed: the climb from zero to the first reading, and the flat line from the last reading to now. To see the exact numbers, the reset time, and whether usage is above or below even pace, hover over a tile or move keyboard focus to it. The page scrolls when the tiles outgrow the tab.

The tab reads on first open, every `UsageRefreshMinutes` while it is showing, after each batch action, and when you click **Refresh**. If a scan or batch action is running when you first open the tab, the first read waits until it ends. The tab reads three providers:

- Anthropic: the Claude Code login's usage meters, read with the access token in Claude Code's `.credentials.json` file.
- OpenAI: Codex's rate-limit windows, read through `codex app-server`.
- Google: five meters in three groups, read with `agy -p /usage --output-format json`. The groups are Gemini Models (weekly and 5-hour), Claude and GPT models (weekly and 5-hour), and Gemini 4 Argon (weekly). The plan label is "Antigravity CLI". The Claude and GPT models group is capacity inside Antigravity, separate from the Anthropic subscription.

### Provider setup

Each provider is read through a command-line tool that is already signed in on this machine. The running app sees a `PATH` change only after you relaunch it.

- Anthropic needs Claude Code signed in. The [Claude Code token refresh](#claude-code-token-refresh) also needs `claude` on `PATH` or set in `ClaudeExecutable`.
- OpenAI needs `codex` on `PATH` or set in `CodexExecutable`.
- Google needs `agy` on `PATH` or set in `AgyExecutable`. Sign in once by running `agy` in a terminal. Each read overwrites the CLI's log at `%APPDATA%\InOutButton\agy-usage.log`. The log holds the account email but no token.

### Failed reads and stored readings

The app keeps each provider's last good reading, and the history behind each chart, in `%APPDATA%\InOutButton\usage-store.json`. When a read fails, the provider's caption shows the error and the age of the last good reading. Its tiles show that reading dimmed and dated as stale, for up to seven days. A stale meter whose window has reset since the last read shows `window reset at <time> since the last read` instead of its old numbers. Without a reading from the last seven days, the provider shows one card with the error. At startup the tab shows the stored readings at once, marked `not read yet since the app started`, and then reads live.

When a Google read fails, the app waits 10 minutes before the next try, and an hour once three reads in a row have failed. If `agy` ran but reported no usage, the message is `Antigravity CLI did not report usage; run agy in a terminal to check its sign-in`. To skip the wait after you fix the sign-in, click **Refresh**. Refresh doesn't skip a wait that a provider's server asks for after a rate-limit error.

### Claude Code token refresh

Claude Code's access token lasts eight hours. When the token has expired, or Anthropic rejects it, the app asks Claude Code to refresh it. It runs these steps in order and stops as soon as the token in `.credentials.json` changes:

1. `claude auth status`, which makes no model call.
2. A request to Haiku for a one-word reply, with tools, MCP (Model Context Protocol) servers, hooks, and transcripts turned off. It takes about 12 seconds and about 1,000 input tokens.

Each step writes a `token refresh:` line to the log. While the token is expired or rejected and not yet replaced, the tab shows the error with a **Refresh via Claude Code** button.

The automatic refresh runs at most once per token and never within 30 minutes of the last run. The button skips the once-per-token limit but not the 30-minute one: a click inside those 30 minutes reads without refreshing and says when the last refresh ran. Set `ClaudeRefreshAuto` to `false` to turn off the automatic refresh and keep the button. The app never writes `.credentials.json`. Only Claude Code does.

### Known limits

- A meter that reports no reset time doesn't appear.
- The tab updates all three providers together, after the slowest read finishes. A token refresh can hold the other tiles for up to 150 seconds, usually about 15. The tab keeps showing the previous tiles meanwhile.
- What `agy -p` does when its Google sign-in has lapsed hasn't been tested. It might try to open a browser. Each read is capped at 25 seconds, and the backoff limits how often it runs.

## Dataset sync (rclone)

Repos can opt in to syncing large, gitignored folders (datasets, model weights, etc.) to a cloud remote. This layers onto the existing buttons:

- **Sign in** runs `git pull`, then for each opted-in repo pulls its dataset folders down from the remote (`rclone copy` remote → local).
- **Sign out** pushes dataset folders up to the remote (`rclone copy` local → remote) *first*, then runs the git commit/push.
- Per-repo `Pull data` / `Push data` buttons run the same operations on the selected repo without touching git.

### Active-repo gate

Datasets only need to move while a project is being worked on, so batch data sync (Sign in / Sign out / the `(all)` data buttons) skips repos that aren't *active*. A repo is active when its last commit is within the `Active (days)` window (default 14) **or** its working tree has uncommitted changes — the second condition is what catches a repo being worked on right now at sign-out, before its commit exists. Skipped repos show `idle` in the grid's Data column and get a log line explaining why; uncheck "Only sync data for active repos" to sync every opted-in repo regardless. The per-repo `Pull data` / `Push data` buttons always run, since selecting a repo is already an explicit choice.

It always uses `rclone copy`, never `sync`/`bisync` — so it adds and overwrites but never deletes at the destination. This is safe across machines (a missing local folder just gets pulled, never propagated as a deletion), at the cost of no automatic conflict resolution if the same file is edited on two machines before syncing.

### Setup

1. Install [rclone](https://rclone.org/downloads/) and put it on `PATH`. The app probes `rclone --version` at startup; if it isn't found, the rclone controls are disabled. (After installing, a fresh login/reboot is needed so launched processes inherit the updated `PATH`.)
2. Configure a remote once per machine with `rclone config` (e.g. a Google Drive remote named `gdrive`). The OAuth token rclone stores is local to each machine and does not travel with your repos.
3. In the app's **Data sync (rclone)** group, set the remote name, the remote root path (where everything is stored under the remote), a timeout, and the active-repo window. Use **Test remote** (`rclone lsd <remote>:`) to confirm auth is healthy.

### Per-repo opt-in: `.rclone-sync.json`

A repo opts in by committing a `.rclone-sync.json` at its root listing the local folders to sync:

```json
{
  "folders": [
    { "local": "data" }
  ]
}
```

- `folders[].local` (required): path relative to the repo root — a folder, or a single file (e.g. `proficiency.db`; files go through `rclone copyto`). Should be gitignored (the app checks with `git check-ignore` at scan time and warns if it isn't).
- `folders[].remote` (optional): explicit remote subpath. If omitted, derived as `<RcloneRemoteRoot>/<repo-name>/<local>`.
- `folders[].exclude` (optional): rclone `--exclude` patterns relative to the folder, applied on both pull and push. Use it to keep a regenerable raw file local while syncing the rest.
- `remote` (optional, top-level): overrides the app's global remote for this repo. Usually omitted.

Because the file is committed, the sync instructions travel with the repo to every machine — no per-repo setup on a fresh clone. A repo without this file is git-only and untouched by the rclone machinery.

A missing side is normal, not a failure: an entry that doesn't exist locally yet (fresh clone) is skipped on push, and an entry that has never been pushed is skipped on pull. Both are reported as `nothing to push` / `nothing on remote yet` in the Data column's message.

### Remote path layout

Each folder maps deterministically to `<remote>:<RcloneRemoteRoot>/<repo-name>/<local-folder>`. For example, a repo `rrl_alp_scraper` with `RcloneRemoteRoot = rock_river/projects` syncs its `data/` folder to `gdrive:rock_river/projects/rrl_alp_scraper/data`. rclone creates the destination path automatically; nothing needs to exist beforehand.

## Run

```powershell
dotnet run
```

To run the tests:

```powershell
dotnet test tests/InOutButton.Tests
```

Two tests call the real `agy` and `claude auth status`. They run only when the environment variable `INOUT_LIVE_TOOLS` is `1` (in PowerShell, `$env:INOUT_LIVE_TOOLS = '1'`).

The copy of the app launched at Windows sign-in locks `bin\Debug`. While it runs, build and test with `--artifacts-path` set to a scratch folder, and never run a cleaning build in place. To ship a change, close the app, run `dotnet build -c Debug`, and relaunch it.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The published app will be in `bin\Release\net8.0-windows\win-x64\publish\`.

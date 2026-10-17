# Spec: AI usage tab and Tasks tab

Date: 2026-10-17. Author: Fable, from Caleb's request and his answers to the
forks (WebView2, tasks file in persistent-memory, Gemini empty state, usage
candidate A, tasks candidate B). Design candidates and the brief are in
`docs/design/2026-10-17-candidates/`.

## 1. What changes for the user

The window gains a tab strip in its middle row. The title, the Sign in / Sign
out button row, and the log stay visible on every tab.

- **Repos**: the existing folders card, rclone card, toolbar, and repo grid,
  unchanged.
- **AI usage**: one column per subscription. Anthropic shows three rings
  (session, weekly all models, weekly Fable only), OpenAI shows one ring per
  rate-limit window the account reports, Google shows an empty state with two
  buttons. Each ring shows the percent left; a thin bar under it shows how far
  through the window we are, labelled "resets in 2 h 17 m · 7:00 PM". A
  "Refresh" button and an "Updated 4:43 PM" line sit in the tab header.
- **Tasks**: three lanes, Projects, Admin, Misc. Cards show a progress glyph,
  title, due chip, priority flag, checklist count, and a repo label on Projects
  cards. In-progress cards first, then not started by due date, then a
  collapsed "Completed (n)" footer. A filter row (All, Due this week, Overdue)
  and a search box. "+ Add task" at the foot of each lane. Clicking a card opens
  an in-page edit dialog. Dragging a card to another lane changes its bucket.

## 2. Architecture

```
Form1.cs            TabControl in main row 1; usage timer; wiring
WebHost.cs          WebView2 wrapper: lazy init, virtual host, JSON bridge
Usage.cs            UsageSnapshot/ProviderUsage/UsageMeter, three readers, UsageService
Tasks.cs            TaskItem, TaskDocument, TaskStore
web/shared.css      theme tokens copied from Theme in Form1.cs
web/bridge.js       send()/onMessage() over window.chrome.webview, browser fallback
web/usage.html      candidate A, driven by messages
web/tasks.html      candidate B, driven by messages
tests/InOutButton.Tests/   xunit: parsers, window labels, TaskStore
```

The pages are plain HTML, CSS, and JavaScript with no libraries and no build
step. The `web/` folder is copied to the output directory on build
(`CopyToOutputDirectory=PreserveNewest`). C# is the single source of truth:
every page renders only what C# sends, and every user action is a message to
C# that answers with the full new state.

### 2.1 WebHost

- NuGet `Microsoft.Web.WebView2`, latest stable at build time.
- `WebHost : UserControl` holding one `WebView2` docked fill. `InitializeAsync`
  runs on first show: `CoreWebView2Environment.CreateAsync(null,
  %LOCALAPPDATA%\InOutButton\WebView2)`, `EnsureCoreWebView2Async(env)`,
  `SetVirtualHostNameToFolderMapping("inout.local", <exe dir>\web, Allow)`,
  then `Navigate("https://inout.local/<page>.html")`.
- Settings: `PreferredColorScheme = Light` (the WinForms chrome is light
  regardless of the OS theme), default context menus off, status bar off,
  dev tools on, built-in error page on.
- Bridge: C# to page with `PostWebMessageAsJson` (camelCase JSON); page to C#
  through `WebMessageReceived`, parsed as `JsonElement` and raised on the UI
  thread as `MessageReceived`. Messages posted before the page says
  `{type:"ready"}` are queued and flushed on ready.
- If `EnsureCoreWebView2Async` throws (runtime missing), the control shows a
  label: "WebView2 runtime not found. Install the Evergreen WebView2 runtime
  from Microsoft, then restart." and the log gets one line. The rest of the app
  is unaffected.

### 2.2 Usage data layer

Types (records, serialized camelCase):

```
UsageSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<ProviderUsage> Providers)
ProviderUsage(string Provider, string? Plan, string State /* ok | unavailable */,
              string? Message, IReadOnlyList<UsageMeter> Meters, string? Footnote,
              IReadOnlyList<ProviderAction> Actions, DateTimeOffset? UpdatedAt)
UsageMeter(string Key, string Name, int WindowMinutes, double UsedPercent,
           DateTimeOffset ResetsAt, string Severity /* normal | warning | critical */,
           string? Note)
ProviderAction(string Id, string Label)
```

**UsageService** owns one reader per provider and the policy:

- Per-provider cache of 60 s. `RefreshAsync(fresh: false)` serves the cache;
  `fresh: true` (the page's Refresh button) skips the cache.
- Per-provider cooldown. A cooldown is never skipped, fresh or not.
- Single flight per provider: concurrent callers share one pending read.
- Providers are read in parallel; one failing provider never hides another.
- A reader failure produces `State = unavailable` with a one-line `Message`
  that never contains a token, a URL query string, or a response body.

**Anthropic reader** (specification: `persistent-memory/agent-cooperation/
claude-usage-mcp/src/usage.mjs`, lines 100-230 for parsing and 453-542 for the
request; fixture `tests/fixtures/usage-2026-10-13.json` beside it):

- Credentials: `%CLAUDE_CONFIG_DIR%\.credentials.json` if the variable is set,
  else `~\.claude\.credentials.json`. Read as UTF-8, strip a BOM, take
  `claudeAiOauth.accessToken`, `subscriptionType`, `rateLimitTier`,
  `expiresAt` (epoch ms). The file is re-read on every live call and is never
  written. If `expiresAt` is in the past, don't call; report "Claude Code token
  expired; run any claude command to refresh it".
- Request: `GET https://api.anthropic.com/api/oauth/usage`, headers
  `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`,
  `Accept: application/json`, `User-Agent: InOutButton/<assembly version>`.
  10 s timeout, auto-redirect off (a redirect is a failure), no cookies.
- Response handling: 200 parse; 401 or 403 re-read the file once and retry only
  if the token changed, else "token rejected; run any claude command so Claude
  Code refreshes it"; 429 never retried, cooldown from `Retry-After` (seconds
  or HTTP date, default 60 s, floor 1 s); 5xx one retry after 1 s, then a 10 s
  cooldown; network error one retry after 1 s.
- Parsing: meters come from `limits[]`. `kind` `session` maps to Key `session`,
  Name "Session", 300 min; `weekly_all` to `weekly`, "Weekly, all models",
  10080 min; `weekly_scoped` to `weekly_<display_name lowercased>`, "Weekly,
  {scope.model.display_name} only", 10080 min, with Note "headroom {h}% of
  weekly" where h = (100 - percent) * allowance / 100 and allowance comes from
  `weekly_scoped_shares[]` joined on `limit_index`. Percent prefers the float
  `five_hour.utilization` / `seven_day.utilization` over the integer
  `limits[].percent` where the kind matches. `resets_at` is ISO 8601 with
  offset; parse with `DateTimeOffset`. Severity is the API's `severity`
  string, lower-cased; anything unrecognised becomes `normal`. Unknown kinds
  are ignored. If `limits[]` is absent, fall back to the `five_hour` and
  `seven_day` objects. Footnote from `extra_usage` when `is_enabled`: "Extra
  usage on · $U of $L used" with amounts scaled by `decimal_places`. Plan from
  `subscriptionType` and `rateLimitTier` ("Claude Max 20x" style when the tier
  is recognisable, else the raw strings).

**OpenAI reader** (Codex app-server; verified live 2026-10-17):

- Executable discovery, first hit wins: `settings.CodexExecutable`; the native
  exe beside the npm shim found with `where.exe codex`, at
  `<shim dir>\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\
  vendor\x86_64-pc-windows-msvc\bin\codex.exe`; else `node` with
  `<shim dir>\node_modules\@openai\codex\bin\codex.js`. Nothing found means
  "Codex CLI not found on PATH".
- Protocol: start `<exe> app-server` with stdin and stdout redirected, no
  shell, UTF-8. Write three lines:
  `{"id":1,"method":"initialize","params":{"clientInfo":{"name":"InOutButton","version":"<v>"}}}`,
  `{"method":"initialized"}`,
  `{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}`.
  Read stdout lines until a JSON object with `id` 2 arrives (notifications
  arrive in between and are ignored), then close stdin, wait up to 5 s, and
  kill the process tree if it is still alive. Whole call capped at 20 s.
- Parsing: `result.rateLimits.primary` and `.secondary`, each
  `{usedPercent, windowDurationMins, resetsAt (Unix seconds)}`; a null window is
  simply absent. Window label by duration, matching Codex's own rule: within
  5% of 300 min "5-hour", of 1440 "Daily", of 10080 "Weekly", else "{n}-hour".
  Key `codex_<label lowercased>`. Plan from `planType`. Severity: `critical` if
  `rateLimitReachedType` is non-null or usedPercent >= 100, `warning` if >= 80,
  else `normal` (the 80 mirrors the threshold Anthropic's API applied today;
  overridable). An error reply, or no reply within the cap, is unavailable with
  the error's `message` field when present.
- The app never reads `auth.json` and never refreshes a token. Codex does.

**Google reader**: always unavailable. Message "No local source on this
machine." Second line "The Gemini CLI stopped serving AI Pro on 2026-06-18. The
Antigravity CLI (agy) is not installed." Actions `gemini-usage` ("Open usage
page") and `antigravity-setup` ("Set up Antigravity"). When `agy` is found on
PATH the second line becomes "Antigravity CLI found; reading its usage report
is not implemented yet." Follow-up, not in this cut: parse `agy -p /usage`.

**Actions** from the page (`{type:"action", id}`) map to fixed URLs in C#:
`gemini-usage` to `https://gemini.google.com/`, `antigravity-setup` to
`https://antigravity.google/docs/install/`. The page never supplies a URL.

**Refresh policy** in Form1: a 60 s `System.Windows.Forms.Timer`. On each
tick, if the usage tab is selected and the last read is older than
`UsageRefreshMinutes`, refresh (not fresh). After `SignInAllAsync` finishes,
refresh once if the usage page has ever been shown. The page's Refresh button
posts `{type:"refresh"}` and gets a fresh read. None of this touches `SetBusy`.

**Logging**, quiet by default: one line per provider on its first successful
read of the session ("usage: Anthropic ok, 3 meters"), one line on every state
change (ok to unavailable or back, with the message), and one line when a
cooldown starts. Nothing on routine refreshes.

### 2.3 Page contract: usage

C# to page: `{type:"snapshot", snapshot:<UsageSnapshot>, busy:false}` after
every read, and `{type:"snapshot", busy:true}` while one is in flight so the
Refresh button can disable. Page to C#: `{type:"ready"}`, `{type:"refresh"}`,
`{type:"action", id}`.

The page renders from the snapshot and re-derives "resets in" and the fuse
position every 30 s from `resetsAt` and the clock, so the countdown stays
right between reads. Same-day resets show the time ("7:00 PM"); other days add
the weekday ("Fri 4:08 PM"). When a window has passed its reset without a new
read, the label reads "reset at 7:00 PM · refreshing" and the page posts
`{type:"refresh"}` once.

### 2.4 Tasks data layer

File: `settings.TasksFile`, default `<persistent-memory>\tasks\tasks.json`,
where persistent-memory is located exactly as `RunMachineSyncAsync` does (the
scanned repo named `persistent-memory`, else `~\Tools\persistent-memory`). If
that folder does not exist, fall back to `%APPDATA%\InOutButton\tasks.json`
and log once. The `tasks\` folder is created on first save.

```json
{
  "version": 1,
  "tasks": [
    {
      "id": "8f3c2a1e9b7d4c6f",
      "title": "Refit buffer pH GAM with September data",
      "bucket": "projects",
      "progress": "notStarted",
      "due": "2026-10-24",
      "priority": "normal",
      "repo": "rrl_sikora",
      "checklist": [{ "text": "Pull September rows", "done": false }],
      "notes": "",
      "created": "2026-10-17T22:10:00Z",
      "updated": "2026-10-17T22:10:00Z",
      "completed": null
    }
  ]
}
```

- `bucket`: projects | admin | misc. `progress`: notStarted | inProgress |
  complete. `priority`: normal | important | urgent. `due` is a date with no
  time, local. `repo` is a repo name from the scan or free text. Nulls are
  omitted on write; `id` is 16 hex characters from a GUID.
- Serialization: `System.Text.Json`, camelCase, enums as strings, indented,
  tasks ordered by `created` then `id`, so git diffs stay small.
- `TaskStore.Load()` reads and validates; a malformed file is reported in the
  log and the tab shows the error instead of an empty board (an empty board
  would invite a save that overwrites the file).
- `Upsert(task)` and `Delete(id)` each: if the file's last-write time differs
  from the time of the last load, reload first and apply the change onto the
  fresh document (one user action is one change, so this is exact), then write
  atomically (temp file in the same folder, then `File.Move` with overwrite).
  `completed` is set when progress becomes complete and cleared when it leaves.
- Sign-out commits the file with everything else in persistent-memory. Two
  machines editing without a sign-out in between can conflict on push; the
  existing `pull --rebase` retry handles the easy cases and a real conflict
  surfaces as a failed push for that repo, as today.

### 2.5 Page contract: tasks

C# to page: `{type:"state", today:"2026-10-17", file:"persistent-memory\\tasks\\tasks.json",
repos:["in_out_button", ...], tasks:[...], error:null}`. Sent on ready, after
every mutation, when the tab is shown and the file changed on disk, and after
sign-in. Page to C#: `{type:"ready"}`, `{type:"upsert", task:{...}}`,
`{type:"delete", id}`.

Page behaviour: lanes sort as in section 1; the progress glyph click cycles
notStarted, inProgress, complete; "Due this week" means not complete and due
within the next seven days or earlier; "Overdue" means not complete and due
before today; search matches title, repo, and notes. The edit dialog has
title, bucket, progress, due, priority, repo (with a datalist of `repos`),
checklist (add, toggle, remove), notes, Save, Cancel, and Delete with an
in-page confirmation. Enter in the lane's "+ Add task" box adds a notStarted
task with that title and the lane's bucket. Drag a card onto a lane to change
its bucket. The header's right side shows the file path in subtle text.

### 2.6 Settings

```csharp
public int UsageRefreshMinutes { get; set; } = 5;
public string? CodexExecutable { get; set; }   // override; discovered when null
public string? TasksFile { get; set; }         // override; derived when null
```

No new controls. These are edited in `settings.json` by hand.

### 2.7 Form1 changes

- `main` row 1 becomes a `TabControl` spanning both columns. The Repos page
  holds a 2-column `TableLayoutPanel` with the existing 300 px / fill styles
  and the existing `leftPanel` and `rightPanel`. The AI usage and Tasks pages
  each hold one `WebHost`, initialised on first selection.
- Tab selection drives the usage timer gate and the tasks reload check.
- `_repos` names feed the tasks page's `repos` list after every scan.
- `SignInAllAsync` ends by refreshing usage (if ever shown) and re-sending the
  tasks state (the pull may have changed the file).

### 2.8 Project file

- `PackageReference` to `Microsoft.Web.WebView2`.
- `<None Include="web\**" CopyToOutputDirectory="PreserveNewest" />`.
- `<Compile Remove="tests\**" />` and `<None Remove="tests\**" />` so the test
  project's sources never fold into the app.

## 3. Tests (xunit, `tests/InOutButton.Tests`)

Expected values come from captured data, not from the code under test.

1. Anthropic parser on the 2026-10-13 fixture: three meters with the fixture's
   percents and reset instants, the scoped meter's note, the plan string, the
   extra-usage footnote.
2. Anthropic parser with `limits[]` removed: falls back to the two window
   objects.
3. Codex parser on the 2026-10-17 captured reply (account id redacted): one
   meter "Weekly", 6 used, resets 2026-10-24T21:08:18Z; no 5-hour meter.
4. Codex parser on a synthetic reply with a 300-minute secondary window: two
   meters, the second labelled "5-hour".
5. Window label: 300, 285, 315, 1440, 10080, 10500, 720.
6. Codex severity: 79, 80, 100, and `rateLimitReachedType` set.
7. TaskStore: upsert then load round-trips every field; nulls are omitted.
8. TaskStore: an external edit between load and upsert survives the upsert.
9. TaskStore: delete removes only that id; no temp file remains after a write.
10. TaskStore: a malformed file loads as an error, not as an empty document.

## 4. Verification before the pre-review commit

- `dotnet build -c Debug -o <scratch>` succeeds with no warnings new to this
  change. Never build in place while `InOutButton.exe` is running.
- `dotnet test tests/InOutButton.Tests` passes.
- Run the scratch exe, open each tab, screenshot, close that process only.
  The usage tab shows live Anthropic and Codex numbers; the tasks tab shows
  the board; adding, editing, dragging, and deleting a task round-trips to
  `tasks.json`.
- No token, bearer header, or credentials path content appears in the log or
  in any exception message. Grep the sources for `accessToken` to confirm it is
  only ever read into a local.

## 5. Out of scope for this cut

- Parsing `agy -p /usage` for Gemini.
- A dark theme for the WinForms chrome.
- Task ordering by hand, assignees, attachments, recurring tasks.
- Styling the TabControl beyond defaults.
- Reading the Claude status-line JSON as an alternative source.

## 6. Risks named

- The Anthropic endpoint is undocumented. A shape change degrades to
  unavailable with the reason in the log, never to a crash.
- Codex reported only a weekly window and `planType` "prolite" today. The
  reader labels by duration and does not assume two windows.
- The Debug exe is locked while the app runs. Shipping means closing the app,
  `dotnet build -c Debug`, relaunch. Verification uses a scratch output.
- WebView2 runtime absence on another machine: handled by the label and log
  line; the Repos tab still works.
- The Theme's amber and red fail colorblind separation when adjacent. The pages
  use amber `#c48604`, which passes beside the Theme's green and red.

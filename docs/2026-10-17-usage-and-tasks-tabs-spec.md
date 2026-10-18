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
- Containment: `NavigationStarting` cancels any navigation whose URI is not
  under `https://inout.local/`; `NewWindowRequested` is marked handled and
  dropped (external links go through the action map only). Incoming messages
  must be a JSON object with a string `type` from the page's known set; anything
  else is ignored and counted in a debug log line at most once per session. The
  pages render all data through `textContent` or attribute assignment, never
  through `innerHTML` with data in it.
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
request; its fixture is copied to this repo as
`tests/fixtures/anthropic-usage-2026-10-13.json`):

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
  "Codex CLI not found on PATH". Discovery runs inside the whole-call cap and
  honours cancellation ("Codex discovery timed out" when the cap fires there);
  a successful `where.exe` find is kept for the session, a miss is not, and the
  override is checked on every read (Opus review, round 1).
- Protocol: start `<exe> app-server` with stdin, stdout, and stderr all
  redirected, no shell, UTF-8, through a dedicated `JsonLineProcess` driver
  (not `ProcessRunner`, which has no stdin and merges stderr into stdout).
  Write `{"id":1,"method":"initialize","params":{"clientInfo":{"name":"InOutButton","version":"<v>"}}}`
  and wait for the reply with `id` 1. Then write `{"method":"initialized"}` and
  `{"id":2,"method":"account/rateLimits/read","params":{"excludeResetCreditDetails":true}}`
  and read stdout lines until the object with `id` 2 arrives; notifications in
  between are ignored. Then close stdin and wait up to 5 s for exit.
- Driver contract: one `CancellationTokenSource` with the whole-call cap
  (default 20 s, injectable for tests) covers every read and write; stderr is
  drained concurrently into a buffer capped at 8 KB; stdout is capped at 256
  lines or 1 MB, after which the call fails as "unexpected output"; a
  non-JSON line is skipped; early exit, malformed output, a failed
  initialize, cancellation, and form close all end in the same `finally`,
  which kills the process tree if it has not exited. Nothing from stderr or
  from an unparsed stdout line is ever surfaced; only its length is logged.
- Parsing: `result.rateLimits.primary` and `.secondary`, each
  `{usedPercent, windowDurationMins, resetsAt (Unix seconds)}`; a null window is
  simply absent. Window label by duration, matching Codex's own rule: within
  5% of 300 min "5-hour", of 1440 "Daily", of 10080 "Weekly", else "{n}-hour".
  Key `codex_<label lowercased>`. Plan from `planType`. Severity: `critical` if
  `rateLimitReachedType` is non-null or usedPercent >= 100, `warning` if >= 80,
  else `normal` (the 80 mirrors the threshold Anthropic's API applied today;
  overridable). An error reply, or no reply within the cap, is unavailable.
- Messages shown for Codex are fixed strings for the known cases: executable
  not found, not signed in (an error reply whose message contains "login" or
  "auth"), timed out, exited early, unexpected output. Any other error text
  passes through `Scrub.Message` first (section 2.9) and is capped at 200
  characters. The same scrubber wraps every exception message that reaches
  `ProviderUsage.Message` or the log, for every provider.
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

**Refresh policy**, owned entirely by Form1; the page never schedules a read:

- First selection of the usage tab reads immediately (not fresh).
- A 60 s `System.Windows.Forms.Timer` ticks; if the usage tab is selected and
  the last completed read is older than `UsageRefreshMinutes`, read (not
  fresh). A hidden tab never triggers a read.
- After any batch workflow ends (`SetBusy(false)`), read once if the usage
  page has ever been shown, since sign-in may have refreshed the Claude token.
- The page's Refresh button posts `{type:"refresh"}`: a fresh read (cache
  skipped, cooldowns kept). When a meter's `resetsAt` passes, the page shows
  "reset at 7:00 PM · waiting for refresh" and posts `{type:"stale"}` once
  per meter per snapshot; Form1 treats `stale` as a normal read subject to the
  visibility gate and the cache, and only when some meter's `resetsAt` has
  passed and is later than its provider's `updatedAt`. Every refresh stamps a
  new snapshot even when each provider came from cache, so without that check
  a stale page could force a read every minute (Opus review, round 1).
- "Last read" is the completion time of the last `RefreshAsync`, success or
  failure, so a failing provider cannot cause a tight loop.
- None of this touches `SetBusy`.

**Logging**, quiet by default: one line per provider on its first successful
read of the session ("usage: Anthropic ok, 3 meters"), one line on every state
change (ok to unavailable or back, with the message), and one line when a
cooldown starts. Nothing on routine refreshes.

### 2.3 Page contract: usage

C# to page: `{type:"snapshot", snapshot:<UsageSnapshot>, busy:false}` after
every read, and `{type:"snapshot", busy:true}` while one is in flight so the
Refresh button can disable. Page to C#: `{type:"ready"}`, `{type:"refresh"}`,
`{type:"stale"}`, `{type:"action", id}`.

The page renders from the snapshot and re-derives "resets in" and the fuse
position every 30 s from `resetsAt` and the clock, so the countdown stays
right between reads. Same-day resets show the time ("7:00 PM"); other days add
the weekday ("Fri 4:08 PM"). When a window has passed its reset without a new
read, the label reads "reset at 7:00 PM · waiting for refresh" and the page
posts `{type:"stale"}` once per meter per snapshot and at most once per 60 s;
Form1 decides whether that becomes a read (section 2.2). (Corrected
2026-10-17 after the web builder found this paragraph contradicting 2.2.)

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
      "rev": 3,
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
  time, local. `repo` is a repo name from the scan or free text. Fields the app
  does not know, on the document or on a task, are kept from the file and
  written back unchanged; a task's come from the file, never from the page
  (Opus review, round 1). Nulls are
  omitted on write; `id` is 16 hex characters from a GUID.
- Serialization: `System.Text.Json`, camelCase, enums as strings, indented,
  tasks ordered by `created` then `id`, so git diffs stay small.
- `TaskStore.Load()` reads and validates; a malformed file is reported in the
  log and the tab shows the error instead of an empty board (an empty board
  would invite a save that overwrites the file).
- `rev` is a per-task revision counter, 1 on creation, incremented by every
  accepted write. The page sends back the `rev` it was given.
- `Upsert(task)` and `Delete(id, rev)` always reload the file first (it is
  small; last-write time is not trusted to detect every change). A write is
  accepted only when the task is new (`rev` 0 or absent, id unknown) or the
  incoming `rev` equals the on-disk `rev`. Otherwise the result is a rejection
  with a fixed message ("This task changed on disk. Your edit was not saved;
  reopen it to see the current version."), the document on disk is untouched,
  and the page keeps the dialog open with the user's draft. An upsert for an id
  the page knew (`rev` > 0) that is no longer on disk is rejected the same way,
  so a deleted task is never resurrected. `completed` is set when progress
  becomes complete and cleared when it leaves.
- Writes are atomic: temp file in the same folder, then `File.Move` with
  overwrite. A failed write (access denied, disk full) leaves the previous
  file intact, is reported to the page with the scrubbed exception message,
  and keeps the draft.
- All store I/O runs on a worker thread through one serial chain in Form1
  (`RunStoreAsync`), and every workflow entry point awaits
  `DrainTaskWritesAsync` right after `SetBusy(true)`, so git never runs while
  an accepted save is still flushing (Sol review, round 1, finding 2).
- A cross-process lock file in `%TEMP%` (named from a hash of the tasks file's
  full path, `TaskStore.LockPath`) is held for the whole read-check-write of
  `Load`, `Upsert`, and `Delete`, retried for 3 s, so two app instances on one
  machine cannot both pass the revision check and overwrite each other (Sol
  review, round 1, finding 3). A locked file returns an error result with a
  fixed message; nothing is written.
- Git coordination: while any workflow is busy (`SetBusy(true)`), the page is
  told `{type:"busy", busy:true}`, disables editing, and shows "Syncing
  repositories…"; a mutation that arrives anyway is rejected with a state
  re-send. When the workflow ends (`SetBusy(false)`), Form1 reloads the file
  and re-sends state, because sign-in, "Git pull (all)", a selected-repo pull,
  or a discard may all have changed it. This closes the window in which a save
  could land after `git add -A` and miss the sign-out commit.
- Sign-out commits the file with everything else in persistent-memory. Two
  machines editing without a sign-out in between can conflict on push; the
  existing `pull --rebase` retry handles the easy cases and a real conflict
  surfaces as a failed push for that repo, as today. Five-clone merge drills
  are out of scope for this cut (section 7).
- Path resolution waits for the first repo scan to finish, so the tasks page
  shows "Waiting for the repository scan" until then, and the fallback to
  `%APPDATA%` is shown as a persistent banner in the page ("Saving to a local
  file because persistent-memory was not found; these tasks will not follow
  you to other machines"), not only as a log line.
- `TaskPaths.Resolve` reports a `TaskPathSource`: `Override`, `ScannedRepo`,
  `DefaultClone`, or `LocalFallback`. `DefaultClone` means no scanned repo is
  named persistent-memory and `~\Tools\persistent-memory` was used; sign-in
  and sign-out touch only scanned repos, so that file never syncs, and the
  page shows a second persistent banner: "persistent-memory is not among the
  scanned repositories, so these tasks will not sync until its folder is
  added to the scan roots." (Opus review, round 1.)

### 2.5 Page contract: tasks

C# to page: `{type:"state", today:"2026-10-17", file:"persistent-memory\\tasks\\tasks.json",
fallback:false, unsynced:false, repos:["in_out_button", ...], tasks:[...], error:null}`. Sent
on ready, after every mutation (accepted or rejected), when the tab is shown
and the file changed on disk, and whenever a workflow ends. A rejected
mutation also carries `{type:"rejected", id, message}` so the page can keep
the dialog open and show why. `{type:"busy", busy:true|false}` gates editing.
Page to C#: `{type:"ready"}`, `{type:"upsert", task:{...}}` (the task carries
the `rev` it was given, or 0 for a new task), `{type:"delete", id, rev}`.

Page behaviour: lanes sort as in section 1; the progress glyph click cycles
notStarted, inProgress, complete; "Due this week" means not complete and due
within the next seven days or earlier; "Overdue" means not complete and due
before today; search matches title, repo, and notes. The edit dialog has
title, bucket, progress, due, priority, repo (with a datalist of `repos`),
checklist (add, toggle, remove), notes, Save, Cancel, and Delete with an
in-page confirmation. Enter in the lane's "+ Add task" box adds a notStarted
task with that title and the lane's bucket. Drag a card onto a lane to change
its bucket. The header's right side shows the file path in subtle text.

While a card's save is in flight, its progress glyph, its drag, and the
dialog's Save are disabled and dimmed, so two quick clicks cannot send two
upserts on one `rev` and have the second refused as changed on disk. The lock
lifts when a `state` carries that task at a higher `rev` than the one sent or
no longer carries it, when a `rejected` for that id arrives, or on
`busy:true` (Opus review, round 1).

### 2.6 Settings

```csharp
public int UsageRefreshMinutes { get; set; } = 5;
public string? CodexExecutable { get; set; }   // override; discovered when null
public string? TasksFile { get; set; }         // override; derived when null
```

No new controls. These are edited in `settings.json` by hand.

### 2.9 Scrubber

`Scrub.Message(string? text)` is applied to every string that originates
outside the app before it reaches a `ProviderUsage.Message`, a page, or the
log: Codex error text, exception messages, HTTP reason phrases. It replaces
with `[redacted]` any match of `Bearer\s+\S+`, `sk-[A-Za-z0-9_-]{8,}`, a JWT
shape `eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}`, a query string `\?\S+`, a
key-value pair whose key ends in token, key, secret, password,
authorization, cookie, or session after a prefix of up to 40 word characters
or hyphens (so `access_token`, `client_secret`, and `x-api-key` match), with
or without quotes around the key and the value (Sol review, round 1, finding
1), the value taking an optional scheme word first so `Authorization: Basic
<base64>` loses the credential (round 2); the prefix is capped because an
unbounded one took 30 s on 20 KB of `a-a-a-`; and, as a last net, any
run of 24 or more characters from `[A-Za-z0-9_-]` (opaque tokens look like
that; a GUID or a long file name in an exception message is redacted too,
which is accepted); collapses whitespace; and truncates to 200 characters
with "…". Tested with injected synthetic secrets (section 3).

### 2.10 Public surface the builders code against

```csharp
// Usage.cs
public sealed record UsageMeter(string Key, string Name, int WindowMinutes, double UsedPercent,
                                DateTimeOffset ResetsAt, string Severity, string? Note);
public sealed record ProviderAction(string Id, string Label);
public sealed record ProviderUsage(string Provider, string? Plan, string State, string? Message,
                                   string? Detail, IReadOnlyList<UsageMeter> Meters, string? Footnote,
                                   IReadOnlyList<ProviderAction> Actions, DateTimeOffset? UpdatedAt);
public sealed record UsageSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<ProviderUsage> Providers);
public static class Scrub { public static string Message(string? text); }
public static class AnthropicUsage {
    public static string CredentialsPath { get; }
    public static ProviderUsage Parse(string json, string? plan, DateTimeOffset now);   // pure
    public static Task<ProviderUsage> ReadAsync(HttpClient http, CancellationToken ct);
}
public sealed record CodexLaunch(string FileName, IReadOnlyList<string> LeadingArgs);
public static class CodexUsage {
    public static Task<CodexLaunch?> FindAsync(string? overridePath, CancellationToken ct);
    public static string WindowLabel(int minutes);                       // pure
    public static string Severity(double usedPercent, bool reached);      // pure
    public static ProviderUsage Parse(string replyJson, DateTimeOffset now); // pure
    public static Task<ProviderUsage> ReadAsync(CodexLaunch launch, TimeSpan cap, CancellationToken ct);
}
public static class GeminiUsage { public static ProviderUsage Read(); }
public sealed class UsageService {
    public UsageService(Action<string> log);
    public UsageSnapshot? Last { get; }
    public bool Busy { get; }
    public DateTimeOffset? LastCompleted { get; }
    public event Action<UsageSnapshot>? Updated;
    public event Action<bool>? BusyChanged;
    public Task RefreshAsync(bool fresh, AppSettings settings, CancellationToken ct);
}

// Tasks.cs
public enum TaskBucket { Projects, Admin, Misc }
public enum TaskProgress { NotStarted, InProgress, Complete }
public enum TaskPriority { Normal, Important, Urgent }
public sealed class ChecklistItem { public string Text { get; set; } = ""; public bool Done { get; set; } }
public sealed class TaskItem {
    public string Id { get; set; } = ""; public int Rev { get; set; }
    public string Title { get; set; } = ""; public TaskBucket Bucket { get; set; }
    public TaskProgress Progress { get; set; } public DateOnly? Due { get; set; }
    public TaskPriority Priority { get; set; } public string? Repo { get; set; }
    public List<ChecklistItem> Checklist { get; set; } = []; public string? Notes { get; set; }
    public DateTimeOffset Created { get; set; } public DateTimeOffset Updated { get; set; }
    public DateTimeOffset? Completed { get; set; }
}
public sealed class TaskDocument { public int Version { get; set; } = 1; public List<TaskItem> Tasks { get; set; } = []; }
public sealed record TaskStoreResult(bool Ok, bool Rejected, string? Message, TaskDocument Document);
public sealed class TaskStore {
    public TaskStore(string path);
    public string Path { get; }
    public static JsonSerializerOptions JsonOptions { get; }
    public TaskStoreResult Load();
    public TaskStoreResult Upsert(TaskItem incoming, DateTimeOffset now);
    public TaskStoreResult Delete(string id, int rev);
    public bool ChangedSinceLoad();
}
public static class TaskPaths {
    public static string Resolve(AppSettings settings, IEnumerable<RepoRow> repos, out bool fallback);
}
```

### 2.7 Form1 changes

- `main` row 1 becomes a `TabControl` spanning both columns. The Repos page
  holds a 2-column `TableLayoutPanel` with the existing 300 px / fill styles
  and the existing `leftPanel` and `rightPanel`. The AI usage and Tasks pages
  each hold one `WebHost`, initialised on first selection.
- Tab selection drives the usage timer gate and the tasks reload check.
- `_repos` names feed the tasks page's `repos` list after every scan, and the
  tasks path is resolved once the first scan completes.
- `SetBusy(true)` posts `busy:true` to the tasks page; `SetBusy(false)` posts
  `busy:false`, reloads the tasks file if it changed, re-sends state, and
  triggers a usage read if the usage page has ever been shown.
- Form close cancels any in-flight usage read through its token.

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
7. TaskStore: upsert then load round-trips every field; nulls are omitted;
   `rev` is 1 after creation and 2 after one edit.
8. TaskStore: an external edit to task B between load and an upsert of task A
   survives the upsert.
9. TaskStore: a stale upsert (incoming `rev` behind the disk) is rejected, the
   file is unchanged, and the result carries the fixed message.
10. TaskStore: an upsert of a task deleted on disk (incoming `rev` > 0, id
    unknown) is rejected; it is not resurrected.
11. TaskStore: delete removes only that id; delete with a stale `rev` is
    rejected; no temp file remains after any write.
12. TaskStore: a malformed file loads as an error, not as an empty document;
    an unsupported `version` loads as an error.
13. TaskStore: a write that fails (target file made read-only) leaves the
    previous content intact and returns an error result.
14. Scrub: a bearer token, an `sk-` key, a JWT, a query string, and a
    `token=` pair are all replaced; plain text passes through; a 1,000
    character message is cut to 200.
15. Codex driver: pointed at a process that never answers (PowerShell
    `Start-Sleep`), with a 2 s cap, returns unavailable "timed out" within 5 s
    and leaves no child process running.
16. Codex driver: pointed at a process that prints garbage and exits, returns
    unavailable "exited early" or "unexpected output", never throws.
17. Codex parser: an error reply with a synthetic bearer token in `message`
    yields a Message with no token in it.

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

- **Burn pace tiles for the usage tab.** Caleb's preferred view is candidate C
  (burn pace tiles), with A (rings and fuses) second; A was already built when
  he said so (2026-10-17), so A ships now and C is the first follow-up. It is a
  page-only change: `web/usage.html` renders the same snapshot, so C can
  replace A or sit behind a view toggle in the page header without touching
  C#. The candidate markup is in `docs/design/2026-10-17-candidates/`.
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

## 7. Plan review log

Astra (gpt-6-astra, high), 2026-10-17 18:17, in
`docs/2026-10-17-usage-and-tasks-tabs-spec.review/20261017-181714/`.

| # | Finding | Triage | Change |
|---|---|---|---|
| 1 | Reload-before-write still loses edits: a stale whole-task upsert overwrites newer fields and can resurrect a deleted task; mtime is not a reliable change detector | Real, blocking | Per-task `rev`, always reload before write, stale and resurrecting writes rejected with the draft kept (2.4, 2.5, tests 9 to 11) |
| 2 | Task saves are not coordinated with git; a save after `git add -A` misses the sign-out commit; only sign-in triggered a reload | Real, blocking | Busy gate on the page during any workflow; reload and re-send when any workflow ends (2.4, 2.7) |
| 3 | Credential-safety contract contradicted itself: Codex `error.message` was forwarded unscrubbed | Real, blocking | Fixed messages for known cases, `Scrub.Message` on everything else and on every exception message (2.2, 2.9, tests 14, 17) |
| 4 | Process and UI contract incomplete: no cancellation on blocked reads, cleanup on every exit path unspecified, `ProcessRunner` unfit (no stdin, merged stderr), store I/O on the UI thread | Real, blocking | `JsonLineProcess` driver contract with one cap token, bounded buffers, kill in `finally`; store I/O on a worker (2.2, 2.4, tests 15, 16) |
| 5 | Refresh rules had two owners; no immediate first read; page-initiated refresh ignored visibility | Real, medium | Form1 owns scheduling; page only reports `stale`; immediate read on first show (2.2) |
| A | Verify every machine resolves the same repo; fallback could strand tasks; opening Tasks before the scan finishes | Partly | Path resolved after the first scan; fallback shown as a banner in the page, not only a log line (2.4). The five-machine inventory is Caleb's to confirm at first use on each machine |
| B | Five disposable clones, concurrent same-task edits through git merge | Declined for this cut | The in-app race is closed by `rev`; a git-level conflict surfaces as a failed push, as for every other file in persistent-memory. Recorded as a known limitation |
| C | Bridge origin and navigation restrictions, message validation, safe rendering | Real | Added to 2.1 |
| D | "No secrets" is not established by a grep | Real | Replaced by the injection tests (14, 17) plus the grep as a secondary check |

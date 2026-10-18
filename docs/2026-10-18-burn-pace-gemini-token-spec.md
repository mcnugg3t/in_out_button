# Burn pace view, Gemini through Antigravity, and a sturdier Anthropic reader

Spec written 2026-10-18 for the second session on the AI usage tab. The
first session's spec, `2026-10-17-usage-and-tasks-tabs-spec.md`, remains the
design record for the tab's host, message channel, scrubber, and the Codex
driver; this spec changes only what it names. Section 12 holds the
plan-review triage once it has run.

## 1. Goals and non-goals

Goals, in Caleb's order:

1. Replace the rings-and-fuses page with the burn-pace view (design
   candidate C) and make the chart a real burn history rather than one line
   from the window start.
2. Read Google usage from the Antigravity CLI, which is installed and signed
   in on this machine and will be on the others.
3. Stop the Anthropic tile from going blank every morning. The Claude Code
   access token lives eight hours; the tile said "token expired" for every
   hour until the first Claude Code use of the day.

Non-goals:

- Refreshing or writing Claude Code's `.credentials.json` from this app.
  Only Claude Code writes that file. Unchanged from the first spec.
- Calling any Google endpoint directly. Google suspended accounts for
  third-party use of the Antigravity endpoint in March 2026; only the CLI
  talks to Google.
- A view toggle between A and C. C replaces A; A stays in git history and in
  `docs/design/2026-10-17-candidates/`.
- The Tasks tab's switch to candidate A. Separate job.
- The nullable `resets_at` debt (O4). Still deferred; see section 10.

## 2. Facts established today

Each line was verified on this machine on 2026-10-18.

- `agy -p /usage --output-format json` returns one JSON object in about 3 s
  with `status`, `response` (the tab-separated text report), and
  `command.data.groups[].buckets[]`, each bucket carrying `id`, `name`,
  `window` (`weekly` or `5h`), `remaining_fraction` (0..1), `reset_time`
  (ISO 8601 UTC). `num_turns` is 0 and no model endpoint is called; the CLI
  log shows `Print mode: running slash command /usage` and a
  `retrieveUserQuotaSummary` fetch. Captured as
  `tests/fixtures/agy-usage-2026-10-18.json`.
- The report has three groups: "Gemini Models" (weekly and 5-hour),
  "Claude and GPT models" (weekly and 5-hour), and "Gemini 4 Argon" (weekly
  only). An idle window reports `reset_time` as now plus the window, so its
  elapsed fraction is 0.
- `agy` refreshes its own Google token from the Windows keyring on every run
  (one-hour access tokens; the log line is `browser.go: token refreshed`).
  The app never sees that token.
- `--log-file <path>` redirects the CLI's per-run log and overwrites it on
  each run. Without it every run leaves a ~20 KB file in
  `~/.gemini/antigravity-cli/log/`.
- From Git Bash, `/usage` is rewritten to `C:/Program Files/Git/usage` by
  MSYS path conversion and reaches the model as a prompt, which then burns
  five model calls. The app starts processes with `ArgumentList` and no
  shell, so this cannot happen in the app; it is recorded so nobody repeats
  the test from Git Bash.
- Claude Code's `.credentials.json` holds `claudeAiOauth.{accessToken,
  refreshToken, expiresAt, refreshTokenExpiresAt, scopes, subscriptionType,
  rateLimitTier}` and a top-level `organizationUuid`. Today's `expiresAt`
  was exactly 8.00 h after the file's write time.
- `claude auth status` exists (Claude Code 2.1.299), takes under 3 s, prints
  JSON with `loggedIn` and `subscriptionType`, and did not touch the
  credentials file while the token was valid. Whether it refreshes an
  expired token is unknown; the docs do not say which commands refresh.
- `claude.cmd` in the npm folder runs
  `node_modules\@anthropic-ai\claude-code\bin\claude.exe`, a native
  executable the app can start directly.
- `claude -p` accepts `--tools ""`, `--strict-mcp-config` (with no
  `--mcp-config`, no MCP server starts), `--no-session-persistence`,
  `--max-turns 1`, `--model haiku`, and `--system-prompt`. `--bare` is
  documented as never reading OAuth credentials, so it must not be used
  for a refresh.
- The `ClaudeUsage` tray app (`~/Tools/claudeusagewin`) is in HKCU Run and
  running. It refreshes the token five minutes before expiry without a
  client id, overwrites the file without a temp-and-rename, drops
  `refreshTokenExpiresAt`, `rateLimitTier` and `organizationUuid`, writes
  `subscriptionType` as null, and logs the full tokens to debug output.
  This app's plan label reads the two fields it drops. Retiring it is
  Caleb's call and is outside this spec.

## 3. Contract changes

All additive. A page that ignores the new fields still renders. Serialized
camelCase by `WebHost`, as before.

### 3.1 `ProviderUsage.State` gains `stale`

`ok | unavailable | stale`. A `stale` provider carries the meters, plan, and
footnote of its last successful read, `UpdatedAt` is that read's time, and
`Message` (plus optional `Detail`) says why the live read failed. The page
shows the tiles dimmed with the reading's age. `UsageState.Stale = "stale"`.

Rules in `UsageService`:

- A provider's outcome becomes `stale` when the live read fails and the
  store holds an `ok` reading for that provider from the last 7 days.
  Older than that, or none, and it stays `unavailable` as today.
- `Plan` comes from the failure when it has one (Anthropic's expired path
  reads the plan from the file), else from the stored reading.
- `Actions` come from the failure. Stale is a display state, not a change
  to what the user can do.

### 3.2 `UsageMeter` gains `History`

```csharp
public sealed record UsagePoint(DateTimeOffset At, double UsedPercent);
public sealed record UsageMeter(string Key, string Name, int WindowMinutes, double UsedPercent,
                                DateTimeOffset ResetsAt, string Severity, string? Note,
                                IReadOnlyList<UsagePoint> History);
```

`History` holds the readings the app has made of this meter inside its
current window, oldest first, including the current reading as the last
point. Membership is by window identity (section 4). Readers construct
meters with an empty history; the service fills it from the store after
each read. The page draws the curve from the window's origin through these
points, and draws the segments it infers (origin to the first point, and
the flat tail from the last point to now) dashed, so observed and inferred
consumption look different.

### 3.3 Action `claude-refresh`

The Anthropic reader attaches `ProviderAction("claude-refresh", "Refresh via
Claude Code")` to its expired and rejected outcomes. Form1 handles the id by
running the refresh ladder (section 5) with the once-per-token bound
bypassed, then `ReadUsageAsync(fresh: true)`. The page keeps rendering
actions as buttons; it never learns what an id does.

### 3.4 Settings

Added to `AppSettings`, hand-edited in `%APPDATA%\InOutButton\settings.json`:

```csharp
public string? ClaudeExecutable { get; set; }      // override; discovered when null
public string? AgyExecutable { get; set; }         // override; discovered when null
public bool ClaudeRefreshAuto { get; set; } = true; // run the refresh ladder without a click
```

`UsageService.RefreshAsync(bool fresh, UsageOptions options, CancellationToken ct)`
replaces the growing parameter list, with
`UsageOptions(string? CodexExecutable, string? ClaudeExecutable, string? AgyExecutable, bool ClaudeRefreshAuto)`.

## 4. The usage store

One file, `%APPDATA%\InOutButton\usage-store.json`, owned by a new
`UsageStore` class (`UsageStore.cs`):

```json
{
  "version": 1,
  "last": { "Anthropic": { ...ProviderUsage with state ok... } },
  "history": { "Anthropic/weekly": [[1792358324731, 43.0], ...] }
}
```

- `last` keeps the most recent `ok` `ProviderUsage` per provider, with its
  `History` stripped (history lives in `history`).
- `history` keys are `<provider>/<meter key>`; values are
  `[epochMs, usedPercent, resetsAtEpochMs]` triples, oldest first. The third
  value is the window identity: the meter's `ResetsAt` at the time of the
  observation. On every live `ok` read (never a cache hit, which is the same
  observation again) the service appends a point per meter, drops points
  older than 8 days, and caps each series at 3,000 points (seven days at
  five minutes is 2,016). Points are not deduplicated: a repeated value at a
  new time is a real observation that the meter did not move.
- Window membership is by identity, not by time range: a point belongs to
  the current window when `point.resetsAt == meter.ResetsAt` to the second.
  A point taken in the previous window, even one second before the reset,
  carries the old reset time and is excluded. An idle Antigravity bucket
  reports a moving reset time, so each idle read is its own one-point
  window, which draws as a dot at the origin; two idle reads a minute apart
  must not merge, which is why there is no tolerance. A provider whose
  reset time jitters within a window would fragment its own history and
  degrade to the single-reading chart; that is the accepted failure.
- All store access runs under one lock: load, append, filter, save. The
  readers finish in parallel, so appends serialize here.
- Written after every read that changed it, atomically (temp file in the
  same folder, then `File.Move` with overwrite). A write failure is logged
  once per run and never thrown to the UI thread; the in-memory store keeps
  going. Read once at startup. A missing file is an empty store. A file that
  does not parse, or carries a `version` the app does not know, is renamed
  to `usage-store.bak.json` (overwriting an older `.bak`) and treated as
  empty, so a newer app's data is not destroyed by an older build.
- Two app instances would overwrite each other's store. The app assumes one
  instance per login session, as it does for `settings.json`.
- The service fills each meter's `History` from the store filtered to the
  current window before posting, for `ok` and `stale` alike.
- Under test the store takes an explicit path; the default is the settings
  folder.

## 5. Anthropic: the refresh ladder

### 5.1 Trigger

The ladder is part of the Anthropic read, not a separate flow: inside
`ReadOutcomeAsync`, when the reader would report `Expired` (local
`expiresAt` check) or `Rejected` (401 or 403 with an unchanged token), it
runs the ladder, re-reads the file, and either goes on to the live read
with the new token or fails with the ladder's own message. So a manual
`claude-refresh` is one `RefreshAsync(fresh: true, options with
ForceClaudeRefresh = true)`: `Busy` covers the ladder, and the message the
page shows is the ladder's, never the pre-ladder one.

When started automatically (`ClaudeRefreshAuto`) it runs at most once per
distinct `accessToken` value per app process, and never within 30 minutes
of its previous automatic run. The token string is the key, not
`expiresAt`, because `expiresAt` can be absent and because a refreshed
token that is then rejected would otherwise re-arm the ladder. The forced
path ignores the once-per-token guard but keeps the 30-minute floor
against repeated clicks (a click inside the floor reads live without the
ladder and says so in the message). The ladder never runs concurrently
with itself.

### 5.2 Steps

Each step is a child process started with `ArgumentList`, no shell, no
window, stdin closed at once (`claude -p` appends piped stdin to the
prompt and would wait for its end), stdout and stderr drained to bounded
buffers whose content is never surfaced (only lengths are logged), working
directory `%APPDATA%\InOutButton` (no project `CLAUDE.md`, no git). The
ladder waits at most 60 s for step 1 and 90 s for step 2. A step that is
still running at its deadline is not killed: killing Claude Code between
receiving a rotated refresh token and writing the file could strand the
login. The ladder stops waiting, logs "step still running after N s, left
to finish", and reports as if the step had not advanced the token; the
next read picks up whatever the step eventually wrote. `--max-turns 1`
and the API's own timeouts bound such a process in practice, and the
30-minute floor bounds how many could exist.

1. `claude auth status`. No model call.
2. `claude -p "Reply with the single word OK." --model haiku --max-turns 1
   --tools "" --strict-mcp-config --no-session-persistence
   --setting-sources "" --output-format json
   --system-prompt "You reply with one word."`

`--setting-sources ""` loads no user, project, or local settings, so no
hook runs; `--strict-mcp-config` with no `--mcp-config` starts no MCP
server; `--no-session-persistence` writes no transcript. Measured on
2026-10-18 from an empty folder: 11.8 s, 1,006 input tokens (all cache
creation), 4 output tokens, one turn, no file left in the folder, no
transcript under `~/.claude/projects`, credentials file untouched.
`--bare` is not used: it skips hooks too, but it never reads OAuth
credentials and so cannot refresh them.

After each step the reader re-reads `.credentials.json`. If the
`accessToken` string differs from the one that triggered the ladder, the
ladder stops, logs `token refresh: <step> replaced the token (expiresAt
<time>)`, and the reader proceeds to the live HTTP read with the new
token. The token string is the test, not `expiresAt`: a 401 with a future
`expiresAt` (revoked token, scope change) is only cured by a new token.
If neither step replaces it, the reader reports the message "Claude Code
token expired; the refresh ladder did not replace it" (or "… rejected …"),
the `claude-refresh` action, and the log keeps each step's exit code,
duration, and output lengths. When `claude` is not found the whole ladder
is skipped; the message then says "Claude Code (claude) not found; set
ClaudeExecutable".

The log line is the experiment: it records which step is enough. If the
first morning's log shows step 1 advancing the token, step 2 becomes dead
code to delete.

### 5.3 Finding `claude`

`ClaudeExecutable` when set; else `where.exe claude`. The hits on this
machine are the extensionless npm shim and `claude.cmd`, neither of which
`UseShellExecute = false` can start. So a hit is resolved to
`<shim folder>\node_modules\@anthropic-ai\claude-code\bin\claude.exe` when
that exists, or used as is when it already ends in `.exe`; otherwise it
counts as not found (no `cmd.exe` is ever involved). A find is cached; a
miss is not; a cached find that fails to start is discarded, as the Codex
driver does.

### 5.4 Bounds, cost, and side effects

- Duration: two steps of at most 60 s each, so the ladder holds the
  Anthropic read for at most 120 s; the other providers are not held.
- Count: once per `expiresAt` value per app process when automatic. A
  token expires once per eight hours, so three automatic runs a day is the
  ceiling for an app that runs all day; a relaunch allows one more for the
  same token. Manual clicks coalesce into the single flight; a click during
  a run joins it.
- Cost: step 2 is one Haiku call of about a thousand input tokens and one
  output word (measured in 5.2).
- Session safety: the ladder only ever asks Claude Code to act. Claude Code
  serializes its own refresh across processes (its errors page documents
  "another Claude Code process is refreshing it" as a state), and a
  running session re-reads the file it owns. What the app cannot
  guarantee is the tray app in section 2, which writes the same file on
  its own clock; that is a reason to retire it, not something the ladder
  can defend against.
- Account: the child inherits the app's environment, so it uses the same
  `CLAUDE_CONFIG_DIR` and the same login the reader reads. An advanced
  `expiresAt` is treated as "try the live read now", never as success; the
  usage request decides.

### 5.5 What stays, and the plan label

The local `expiresAt` pre-check stays, now as the ladder's trigger. The 401
re-read-once rule stays. The 60 s cache, the no-retry on 429, and the
`Retry-After` cooldown stay.

When an `ok` read carries a null `Plan` (the tray app's rewrite nulls
`subscriptionType` and drops `rateLimitTier`), the service keeps the plan
from the stored last reading, so the caption does not blank between two
of its rewrites.

## 6. Google: the Antigravity reader

`GeminiUsage` keeps its class name and provider string "Google" and becomes
asynchronous:

```csharp
public static Task<ReaderOutcome> ReadOutcomeAsync(string? overridePath, CancellationToken ct);  // 25 s cap
internal static Task<ReaderOutcome> ReadOutcomeAsync(AgyLaunch launch, TimeSpan cap, CancellationToken ct);
internal static ProviderUsage Parse(string replyJson, DateTimeOffset now);
```

### 6.1 Driver

- Find `agy`: `AgyExecutable` when set; else `where.exe agy` with the
  Codex driver's caching rules. Not found: today's empty state, unchanged
  message and actions.
- Start `agy` with arguments `-p /usage --output-format json --print-timeout 20s
  --log-file <%APPDATA%\InOutButton\agy-usage.log>`. Never pass
  `--disable-slash-commands`: it would turn `/usage` into a prompt and spend
  five model calls. Stdin closed.
- Whole-call cap 25 s (injectable for tests). Stdout capped at 1 MB,
  stderr at 8 KB, both through the same bounded-pipe pattern as
  `JsonLineProcess` (add a `ReadAllAsync` to it, or a sibling helper that
  reuses its `Start` and `Dispose`).
- Descendants die with the parent. `JsonLineProcess.Start` puts the child
  in a Windows job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
  (CreateJobObject, SetInformationJobObject, AssignProcessToJobObject;
  about forty lines of P/Invoke), and `Dispose` closes the job handle
  after its kill. `Process.Kill(entireProcessTree: true)` alone leaves
  grandchildren alive once the parent has exited, which is exactly the
  case where a child holding stdout open makes the read wait for the cap.
  This covers the Codex driver too. A browser `agy` might open through an
  already running browser is outside any job; the cap and cooldown bound
  how often that could happen.
- The redirected `agy` log is overwritten on every run and was 19 to 28 KB
  in today's runs; it holds the account email and trace ids and no token
  (checked: the keyring line logs only the expiry). Nothing from it is
  read by the app.
- Outcomes: timeout "Antigravity CLI timed out after 25 s"; the CLI's own
  print timeout, recognised by the fixed marker `print timeout` in its
  stderr (the only thing ever matched in stderr; its text is still never
  surfaced): "Antigravity CLI timed out after 20 s"; could not start
  "Antigravity CLI (agy) could not be started"; non-zero exit or a
  `status` other than `SUCCESS` or stdout that is not one JSON object:
  "Antigravity CLI did not report usage; run agy in a terminal to check its
  sign-in" with `Detail` = `Scrub.Message` of the first 200 characters of
  stdout when it is not JSON, else of `response`.
- Backoff: a failure sets a soft cooldown of 10 minutes; the third
  consecutive failure and every one after it sets 60 minutes; a success
  clears the count. A soft cooldown is skipped by `fresh` (the Refresh
  button, the `claude-refresh` action), so signing in at a terminal and
  clicking Refresh works at once. The service therefore keeps two cooldown
  kinds: hard, from a server's `Retry-After`, never skipped; soft, the
  app's own backoff, skipped by `fresh`. The Anthropic 5xx 10 s cooldown
  and this one are soft; the 429 one is hard.

### 6.2 Parse

From `command.data.groups[]`, in report order, each `buckets[]` entry
becomes one meter:

| Field | Source |
|---|---|
| `Key` | `bucket.id` (`gemini-weekly`, `gemini-5h`, `3p-weekly`, `3p-5h`, `gemini-argon-wtu-weekly`); a repeated id keeps the first bucket and logs the duplicate |
| `Name` | `group.name` verbatim ("Gemini Models", "Claude and GPT models", "Gemini 4 Argon") |
| `WindowMinutes` | `window`: `weekly` → 10080, `5h` → 300; any other value skips the bucket with one log line |
| `UsedPercent` | `(1 - remaining_fraction) * 100`, rounded to one decimal, clamped 0..100; a missing or non-numeric fraction skips the bucket with one log line |
| `ResetsAt` | `reset_time` parsed as ISO 8601; unparsable skips the bucket with one log line |
| `Severity` | the shared thresholds used for Codex |
| `Note` | null |

Provider-level: `Plan` = "Antigravity CLI", `Footnote` = null, `Actions` =
empty on `ok`. A report with no usable bucket is "usage report changed shape
(no buckets)". `response` text is never shown; the JSON data is the
contract. A parser exception of any kind is a failure with the fixed
shape-change message, never a crash. Every failure in this section, parse
failures included, sets the 10-minute cooldown. Note that `agy` exits 0
and reports `status: "SUCCESS"` when its own `--print-timeout` fires,
with a partial or empty `response` and no `command.data`; that lands on
the shape-change path, which is the right outcome.

### 6.3 Why the five meters all show

The Claude and GPT bucket is Opus 5.5 and Sonnet 5.5 capacity through
Antigravity, separate from the Anthropic subscription; Argon has its own
weekly meter. Caleb's reviewer roster now includes Argon. Hiding any of the
three groups would hide spare compute.

## 7. The page: burn pace

`web/usage.html` is rewritten; `web/shared.css`, `web/bridge.js` keep their
roles. The message contract is unchanged on the wire except for the
additive fields in section 3.

### 7.1 Layout

- Header as today: title, "Updated <time>", Refresh button disabled while
  busy.
- One caption row per provider (name, plan, right-aligned footnote), then
  that provider's tiles in a three-column grid. Providers in the order
  posted. The page scrolls vertically when the tiles outgrow the tab.
- An `unavailable` provider renders one card spanning all three columns
  with its message, detail, and action buttons.
- A provider with a weekly meter and no 5-hour meter gets a ghost tile
  "No 5-hour window reported" after its meters, as the mock shows. No
  other absence is invented.

### 7.2 Tile

Top row: meter name, severity chip with the word (`normal`, `warning`,
`critical`), window label on the right ("5-hour window", "7-day window",
or the meter's `note` when present, as "headroom 15% of weekly" is today).
Numbers row: `<left>% left` and `<d h m> to reset`. Chart below.

Severity classes `normal | warn | crit` map to `--accent`, `--warn`
(`#c48604`), and `--fail` from `shared.css`; the mock's `#b45309` is not
used. Critical gets its own fill and stroke, which the mock lacked.

### 7.3 Chart

x is the fraction of the window elapsed, y is percent used, both 0..1 of
the plot box. Drawn with `createElementNS` and attributes only.

- Grid: top, middle, and base lines; a right edge at the reset.
- Guide: a dotted diagonal from the origin to (1, 100), the even-pace line.
  The first tile of each provider labels it "even pace".
- Burn path: starts at the origin (a window starts at zero by definition),
  passes through each `history` point at (`elapsedAt(point.at)`,
  `point.usedPercent`), ends at the last point, then runs flat to now when
  now is later than the last point. Observed segments (point to point) are
  solid; inferred segments (origin to the first point, and the flat tail)
  are dashed. Without history the path is one dashed segment from the
  origin to (now, used): a single reading implies a rate, it does not
  observe one.
- Area under the path in the severity wash; the path in the severity
  stroke; a dot at the end; "now" under the dot; "reset" at the right.
- The 30 s tick re-derives "to reset", the now x-position, and the flat
  tail from the clock; the history points are fixed once posted.
- Every x is clamped to 0..1 and points are sorted by time before drawing,
  so a point the service should not have sent still cannot draw outside
  the plot box or run backwards.

### 7.4 Tooltip and keyboard

Hover or focus on a tile shows: `<used>% used · <left>% left`, "Resets
<absolute time with weekday when not today>", the window length, and
"Above even pace: <elapsed>% of the window has passed" or "Below even
pace: …". Text through `textContent`; the tile carries an `aria-label`
with the same lines.

### 7.5 Stale and reset-passed states

- A `stale` provider's caption gains "last read <age> · <message>" and
  its action buttons (the Anthropic one carries "Refresh via Claude
  Code"). Its tiles get class `stale`: muted numbers, grey wash, the chart
  path drawn to the reading's time rather than to now, with no flat tail.
  Everything on a stale tile is evaluated at the reading's time, not now:
  the numbers row reads `<left>% left as of <time>`, the pace verdict in
  the tooltip and the `aria-label` compares the reading against the
  elapsed fraction at the reading's time, and "to reset" is still
  counted from now because the reset is a fact about the clock, not the
  reading.
- An `ok` meter whose `resetsAt` has passed shows "reset at <time> ·
  waiting for refresh" in place of "to reset", as today, and the page
  posts `{type:"stale"}` under today's rules (once per meter per snapshot,
  at most once per 60 s).
- A `stale` meter whose `resetsAt` has passed is a finished window and
  carries no information about the current one. It renders as a ghost
  tile: name and window label, no percent, no severity chip, no pace, no
  path, and the line "window reset at <time> since the last read · waiting
  for a live read". Showing last night's 20% left with a warning wash for
  a window that reset at 7 AM would be a false reading. `stale` providers
  never post `stale`; the timer, the Refresh button, and the end of a
  workflow are the ways back.

### 7.6 Browser fixture

`web/sample-data.js` gains: the Google provider as `ok` with the five
meters from the captured report (two with nonzero usage so the chart has
shape), history points on the Anthropic weekly meter, and a `stale`
Anthropic variant selectable with `?scenario=stale` so the page can be
checked in a browser. The host never loads it.

## 8. Form1 wiring

- `OnUsageMessage` `action` case: `claude-refresh` is one
  `_usage.RefreshAsync(fresh: true, options with ForceClaudeRefresh = true, ct)`;
  the ladder runs inside the Anthropic read (section 5.1). The two URL
  actions are unchanged; unknown ids are ignored as today.
- `ReadUsageAsync` passes `UsageOptions` built from settings.
  `UsageOptions` gains `bool ForceClaudeRefresh`, false everywhere except
  that action.
- First read: when the usage tab is first shown while a scan or workflow
  is busy, mark it shown and skip the immediate read; the existing
  `SetBusy(false)` read runs it when the machine is quieter. This is the
  deploy-day timeout from the handoff.
- Startup: construct `UsageStore` from the settings folder and pass it to
  `UsageService`. The service seeds `Last` from the store before any live
  read: each stored reading under 7 days old becomes a `stale` provider
  with the fixed message "not read yet since the app started" and no
  actions, so the page shows last night's numbers within a second of
  opening the tab, dated. The first live read follows at once (or after
  the scan, section above). The service logs a provider's `stale` result
  distinctly from `unavailable` on each flip, as it logs ok and
  unavailable today.

## 9. Tests

xunit in `tests/InOutButton.Tests`, same two-tier rule as before: expected
values come from the captured fixtures by hand, never from the code under
test.

Google:
1. `Parse` on `agy-usage-2026-10-18.json` yields five meters with the keys,
   names, windows, percents (0, 0, 0, 0, 1.0), and reset times read off the
   file; the Argon one is `2026-10-25T21:19:47Z`.
2. A mutated fixture with `remaining_fraction` 0.2 on `gemini-weekly` gives
   80.0 and the severity the shared thresholds assign.
3. An unknown `window` value skips that bucket and keeps the rest.
4. `status` other than `SUCCESS`, and stdout that is not JSON, give the
   fixed messages; `Detail` passes through `Scrub.Message`.
5. A scripted stand-in (`powershell.exe -Command` printing the fixture)
   drives the whole `ReadOutcomeAsync` path to `ok`.
6. A stand-in that sleeps past the cap gives the timeout message and the
   child is dead afterwards (same `IsAlive` check as the Codex test).

Store:
7. Round trip: save, load, equal; history filtered to a window keeps only
   the points whose reset identity matches, in order, with the current
   reading last; a point from the previous window taken one second before
   the reset is excluded.
8. Pruning: points older than 8 days drop; the cap holds.
9. A malformed file, and one with an unknown `version`, load as empty
   without throwing and leave a `.bak` beside the store.
10. A cache hit adds no point: two `RefreshAsync(fresh: false)` calls
    inside the cache window leave one observation.

Service:
11. A failed read with a stored `ok` reading from an hour ago yields
    `stale` with the stored meters, the failure's message, and the stored
    `UpdatedAt`; one from eight days ago yields `unavailable`.

Processes:
12. A stand-in that starts a grandchild holding stdout open and then exits
    at once: after the cap fires, the grandchild is dead (the job object
    test; the Codex timeout test keeps its parent check).

Anthropic ladder (no network; the credentials path is injected):
13. With a fake step list, a step that advances `expiresAt` stops the
    ladder and the reader goes on to the HTTP read; steps that do not
    advance it leave the expired outcome with the `claude-refresh` action.
14. The automatic ladder runs once per `expiresAt` value; a second expired
    read with the same value does not run it; the manual path does.
15. The credentials fixture is byte-identical after every reader path the
    tests drive (valid, expired, rejected): the app never writes it.
16. A fake step list where the token string changes but `expiresAt` does
    not still counts as replaced; one where `expiresAt` moves but the
    token string does not still counts as not replaced.

Service, continued:
17. Two concurrent `RefreshAsync` callers share one pending read and the
    store gains one point per meter, not two.
18. A soft cooldown is skipped by `fresh: true`; a hard one is not.
19. An `ok` Anthropic reading with a null plan keeps the stored plan.
20. The seeded `stale` providers at startup carry the fixed message and
    an empty action list.

Google, continued:
21. A stand-in that writes the marker line `[agy] print timeout after 20s`
    to stderr and exits 0 with a SUCCESS envelope lacking `command.data`
    yields the 20 s timeout message, not the shape-change one.

The existing Gemini empty-state test moves to the not-found path.

## 10. Risks and open questions

- **`claude auth status` may not refresh.** Then step 2 runs each morning
  at the cost of one Haiku call. Known the first morning from the log.
- **`agy` with a lapsed Google sign-in.** Unknown behaviour in print mode;
  it may try to open a browser. The cap, the tree kill, and the 10-minute
  cooldown bound the damage; the message tells Caleb to run `agy` in a
  terminal.
- **Idle-window reset semantics.** `reset_time` equal to now plus the
  window on an idle bucket is an inference from two runs. If a bucket
  instead reports a fixed schedule, the burn chart's x-origin shifts but
  nothing breaks.
- **Three fixed group names** are not depended on; only the bucket ids are
  used as keys and the names are displayed verbatim.
- **Tile count.** Anthropic 3, OpenAI 1 or 2, Google 5: ten tiles at the
  1060×750 launch size means the tab scrolls. Checked by screenshot before
  shipping.
- **O4 stays open.** A meter with null `resets_at` is still dropped. With
  the burn chart it has no x-axis anyway; the fix is a tile that says "no
  active window", which needs the nullable contract change and is deferred
  again.
- **The tray app.** While `ClaudeUsage` runs, the plan label can blank
  after one of its rewrites and the file can be mid-write when this app
  reads it. The reader already treats an unreadable file as a failure
  (now: a stale display), so the app survives it. Retirement is Caleb's
  call.

## 11. Delivery

Order, each step building and testing green with `--artifacts-path`
pointed at scratch while the login-launched exe holds `bin/Debug`:

1. `spec:` commit of this file and the fixture.
2. Plan review: Astra and Argon on this spec; triage into section 12.
3. Page port (web only), checked in a browser against `sample-data.js`
   and then in the app.
4. Store, stale state, history fill, Form1 options.
5. Antigravity reader and tests.
6. Refresh ladder and tests.
7. `pre-review:` commit; code review by Sol 6.1 and Grok 4.7; fixes;
   `post-review:` commit with `Reviewed-by:`.
8. Docs: CLAUDE.md usage section, README, HANDOFF.md (which also has
   control bytes at its Antigravity path line to repair), the
   persistent-memory project note, routing-log rows.
9. Ship: close the app, `dotnet build -c Debug`, relaunch, confirm the
   three tiles live.

## 12. Plan-review triage

Astra (high effort, 99 s) reviewed revision 1 of this spec; Argon (high
effort, 349 s) reviewed revision 2, which already carried Astra's triage,
so Argon's read is not independent of Astra's. Both had the first spec and
`Usage.cs` as context. Raw answers are in
`2026-10-18-burn-pace-gemini-token-spec.review/` (Argon's first run was
denied a file read in headless mode and produced nothing; the launcher
now passes `--add-dir` for the repos involved). Rows marked "fixed"
changed the section named.

| # | Reviewer | Finding | Judgement | Outcome |
|---|---|---|---|---|
| A1 | Astra | The Haiku step's flags do not disable hooks; a hook can run commands or more model calls. "Cannot race" the tray app is unsupported. | Real on hooks: `--tools ""` and `--strict-mcp-config` leave settings hooks live. The tray-app point is accepted as a limit, not a defect the ladder can fix. | Fixed in 5.2: `--setting-sources ""`, measured with no hook firing. 5.4 rewritten with the tray app named as an external writer. |
| A2 | Astra | `Process.Kill(entireProcessTree)` skips descendants once the parent has exited; the parent-only `IsAlive` test cannot show tree cleanup; the redirected log has no stated bound. | Real. A child holding stdout open after its parent exits is exactly the timeout case. | Fixed in 6.1: Windows job object on every child, test 12. Log bound and content stated. |
| A3 | Astra | Time-range filtering does not identify the window; the inclusive boundary admits an old-window point; no account identity. | Real on window identity. Account identity declined: one login per machine is the app's standing assumption. | Fixed in 3.2 and 4: points carry the window's reset time; membership by identity within 2 min. |
| A4 | Astra | Atomic replace does not stop lost updates between instances or between parallel readers; write failures, unknown versions, and recovery are unspecified. | Real within one instance; two instances declined (same assumption as `settings.json`). | Fixed in 4: one lock, `.bak` on unknown or unreadable, write failures logged; test 9 and 10. |
| A5 | Astra | The stale tile's "percent left" sounds current; pace compares an old reading with today's elapsed fraction; origin-to-first-point and the flat tail are inventions drawn as facts. | Real. | Fixed in 7.3 and 7.5: inferred segments dashed; stale numbers "as of <time>"; pace evaluated at the reading's time. |
| A6 | Astra | Verify refresh behaviour on the installed version with expired, rejected, revoked, and missing credentials; an advanced `expiresAt` is not proof the usage call succeeds. | Partly. Expired cannot be produced today without logging out; the ladder's log line is the planned observation. The second point is accepted. | 5.4: an advanced `expiresAt` only means "try the live read". Tests 13 to 15 cover the paths that can be faked. |
| A7 | Astra | Once per `expiresAt` is not a daily spending bound; ladder duration, cancellation, click coalescing, and limits across restarts are unstated. | Real. | Fixed in 5.4 with the ceiling derived from the token lifetime. |
| A8 | Astra | Parser cases: duplicate bucket ids, malformed field types, parser exceptions, every failure entering cooldown. | Real. | Fixed in 6.2. |
| A9 | Astra | A cached read must not become a new observation. | Real. | Fixed in 4 and test 10. |
| A10 | Astra | Two idle observations do not establish a zero-based consumption window for Antigravity. | Accepted as an open assumption; the store will record consecutive readings through a real window, which is the evidence. | 10 unchanged; 4 now draws an idle bucket as a one-point window so a wrong guess costs nothing. |
| G1 | Argon | A `stale` provider keeps showing a depleted meter, its wash, and its pace verdict for up to 7 days after that window has reset. | Real: last night's 20% with a warning wash for a window that reset at 7 AM is a false reading. | Fixed in 7.5: a stale meter past its reset is a ghost tile with "window reset at <time> since the last read". |
| G2 | Argon | Stopping the ladder on an advanced `expiresAt` fails the 401-with-future-expiry case, re-arms after a refreshed-then-rejected token, and has no key when `expiresAt` is null. | Real. | Fixed in 5.1 and 5.2: the token string is both the stop test and the once-per-token key; a 30-minute floor bounds automatic and forced runs; test 16. |
| G3 | Argon | `--print-timeout 20s` makes an `agy` timeout look like a shape change, and the 10-minute cooldown blocks the manual Refresh after the user signs in. | Real on both. | Fixed in 6.1: the stderr marker maps to a timeout message (test 21); cooldowns split into hard and soft, and `fresh` skips soft (test 18). |
| G4 | Argon | A 2-minute identity tolerance merges idle reads whose reset time moves, giving negative x. | Real. | Fixed in 4 (exact match, no tolerance) and 7.3 (clamp and sort). |
| G5 | Argon | A manual `claude-refresh` as "ladder, then fresh read" overwrites the ladder's message with the pre-ladder one and runs outside `Busy`; startup `stale` seeding from stored `ok` records has a null message. | Real. | Fixed in 5.1 and 8: the ladder runs inside the Anthropic read under `ForceClaudeRefresh`; seeded providers carry a fixed message (test 20). |
| G6 | Argon | `claude -p` with an open redirected stdin may block; killing `claude.exe` at the cap mid-refresh could strand a rotated refresh token. | Real on both; the second is the more dangerous. | Fixed in 5.2: stdin closed at once; ladder steps are waited for, never killed. |
| G7 | Argon | `agy -p /usage` while logged out may open a browser tab per cooldown. | Accepted as unverifiable today (would need logging out). | 6.1: escalating backoff to one hour after three failures. Recorded in 10. |
| G8 | Argon | `where.exe` returns shims `UseShellExecute = false` cannot start. | Real; the first draft already resolved to the native exe but did not say what happens without it. | Fixed in 5.3: a shim without the native exe beside it counts as not found. |
| G9 | Argon | Test 12 races job assignment against a fast-exiting child; no criteria for stale-past-reset tiles, plan restoration after a tray-app rewrite, x clamping, one point per shared read, or distinct `stale` logging. | Real. | The job test's stand-in sleeps before spawning; the rest are 5.5, 7.3, 7.5, 8, and tests 17 to 20. |

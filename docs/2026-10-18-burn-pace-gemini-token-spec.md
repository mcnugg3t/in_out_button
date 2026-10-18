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
point. Current window means `At >= ResetsAt - WindowMinutes` and
`At <= ResetsAt`. Readers construct meters with an empty history; the
service fills it from the store after each read (section 4). The page draws
the curve from the window's origin through these points.

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
- `history` keys are `<provider>/<meter key>`; values are `[epochMs, usedPercent]`
  pairs, oldest first. On every `ok` read the service appends a point per
  meter, drops points older than 8 days, and caps each series at 3,000
  points (seven days at five minutes is 2,016). Points are not deduplicated:
  a repeated value at a new time is a real observation that the meter did
  not move.
- Written after every read that changed it, atomically (temp file in the
  same folder, then `File.Move` with overwrite). Read once at startup. A
  missing or unreadable file is an empty store; a malformed file is logged
  once and treated as empty, never thrown to the UI thread.
- The service fills each meter's `History` from the store filtered to the
  current window (section 3.2) before posting, for `ok` and `stale` alike.
- Under test the store takes an explicit path; the default is the settings
  folder.

## 5. Anthropic: the refresh ladder

### 5.1 Trigger

The ladder runs when `AnthropicUsage` would report `Expired` (local
`expiresAt` check) or `Rejected` (401 or 403 with an unchanged token). It
runs at most once per distinct `expiresAt` value when started automatically
(`ClaudeRefreshAuto`), and whenever the user clicks `claude-refresh`. It
never runs concurrently with itself (single flight, like the readers).

### 5.2 Steps

Each step is a child process started with `ArgumentList`, no shell, no
window, capped at 60 s with a process-tree kill in `finally`, stdout and
stderr drained to bounded buffers whose content is never surfaced (only
lengths are logged), working directory `%APPDATA%\InOutButton` (no project
`CLAUDE.md`, no git).

1. `claude auth status`. No model call.
2. `claude -p "Reply with the single word OK." --model haiku --max-turns 1
   --tools "" --strict-mcp-config --no-session-persistence
   --output-format json --system-prompt "You reply with one word."`

After each step the reader re-reads `.credentials.json`. If `expiresAt` is
later than the value that triggered the ladder, the ladder stops, logs
`token refresh: <step> advanced expiresAt to <time>`, and the reader
proceeds to the live HTTP read with the new token. If neither step moves
it, the reader reports `Expired` (or `Rejected`) with the message
"Claude Code token expired; the refresh ladder did not advance it", the
`claude-refresh` action, and the log keeps each step's exit code, duration,
and output lengths. Step 2 is skipped when `claude` is not found; the
message then says "Claude Code (claude) not found; set ClaudeExecutable".

The log line is the experiment: it records which step is enough. If the
first morning's log shows step 1 advancing the token, step 2 becomes dead
code to delete.

### 5.3 Finding `claude`

`ClaudeExecutable` when set; else `where.exe claude`. When the hit is the npm
shim (an extensionless file or `.cmd`), prefer
`<shim folder>\node_modules\@anthropic-ai\claude-code\bin\claude.exe` when it
exists, so no `cmd.exe` is involved. A hit is cached; a miss is not; a cached
hit that fails to start is discarded, as the Codex driver does.

### 5.4 Cost and side effects

Step 2 is one Haiku call of a few thousand input tokens and one output
word, at most a few times a day. It writes no session file and starts no
MCP server. Claude Code coordinates its own cross-process refresh (the docs
list "another Claude Code process is refreshing it" as a known state), so
the ladder cannot race a running session.

### 5.5 What stays

The local `expiresAt` pre-check stays, now as the ladder's trigger. The 401
re-read-once rule stays. The 60 s cache, the no-retry on 429, and the
`Retry-After` cooldown stay.

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
  reuses its `Start` and `Dispose`). Process-tree kill in `finally`.
- Outcomes: timeout "Antigravity CLI timed out after 25 s"; could not
  start "Antigravity CLI (agy) could not be started"; non-zero exit or a
  `status` other than `SUCCESS` or stdout that is not one JSON object:
  "Antigravity CLI did not report usage; run agy in a terminal to check its
  sign-in" with `Detail` = `Scrub.Message` of the first 200 characters of
  stdout when it is not JSON, else of `response`. A failure sets a 10-minute
  cooldown for the provider so a broken `agy` runs at most six times an hour.

### 6.2 Parse

From `command.data.groups[]`, in report order, each `buckets[]` entry
becomes one meter:

| Field | Source |
|---|---|
| `Key` | `bucket.id` (`gemini-weekly`, `gemini-5h`, `3p-weekly`, `3p-5h`, `gemini-argon-wtu-weekly`) |
| `Name` | `group.name` verbatim ("Gemini Models", "Claude and GPT models", "Gemini 4 Argon") |
| `WindowMinutes` | `window`: `weekly` → 10080, `5h` → 300; any other value skips the bucket with one log line |
| `UsedPercent` | `(1 - remaining_fraction) * 100`, rounded to one decimal, clamped 0..100 |
| `ResetsAt` | `reset_time` parsed as ISO 8601; unparsable skips the bucket with one log line |
| `Severity` | the shared thresholds used for Codex |
| `Note` | null |

Provider-level: `Plan` = "Antigravity CLI", `Footnote` = null, `Actions` =
empty on `ok`. A report with no usable bucket is "usage report changed shape
(no buckets)". `response` text is never shown; the JSON data is the
contract.

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
  now is later than the last point. Without history the path is the
  mock's single segment from the origin to (now, used).
- Area under the path in the severity wash; the path in the severity
  stroke; a dot at the end; "now" under the dot; "reset" at the right.
- The 30 s tick re-derives "to reset", the now x-position, and the flat
  tail from the clock; the history points are fixed once posted.

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
- Any meter whose `resetsAt` has passed shows "reset at <time> · waiting
  for refresh" in place of "to reset", as today. For `ok` providers the
  page posts `{type:"stale"}` under today's rules (once per meter per
  snapshot, at most once per 60 s). For `stale` providers it does not post;
  the timer, the Refresh button, and the end of a workflow are the ways
  back.

### 7.6 Browser fixture

`web/sample-data.js` gains: the Google provider as `ok` with the five
meters from the captured report (two with nonzero usage so the chart has
shape), history points on the Anthropic weekly meter, and a `stale`
Anthropic variant selectable with `?scenario=stale` so the page can be
checked in a browser. The host never loads it.

## 8. Form1 wiring

- `OnUsageMessage` `action` case: `claude-refresh` calls
  `_usage.RefreshClaudeTokenAsync(options, ct)` then a fresh read; the two
  URL actions are unchanged; unknown ids are ignored as today.
- `ReadUsageAsync` passes `UsageOptions` built from settings.
- First read: when the usage tab is first shown while a scan or workflow
  is busy, mark it shown and skip the immediate read; the existing
  `SetBusy(false)` read runs it when the machine is quieter. This is the
  deploy-day timeout from the handoff.
- Startup: construct `UsageStore` from the settings folder and pass it to
  `UsageService`. `_usage.Last` at `ready` time may now carry `stale`
  providers from the store before any live read, so the page shows last
  night's numbers within a second of opening the tab. That is intended:
  the first live read follows at once.

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
   points inside it, in order, with the current reading last.
8. Pruning: points older than 8 days drop; the cap holds.
9. A malformed file loads as empty without throwing.

Service:
10. A failed read with a stored `ok` reading from an hour ago yields
    `stale` with the stored meters, the failure's message, and the stored
    `UpdatedAt`; one from eight days ago yields `unavailable`.

Anthropic ladder (no network; the credentials path is injected):
11. With a fake step list, a step that advances `expiresAt` stops the
    ladder and the reader goes on to the HTTP read; steps that do not
    advance it leave the expired outcome with the `claude-refresh` action.
12. The automatic ladder runs once per `expiresAt` value; a second expired
    read with the same value does not run it; the manual path does.

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

Filled after the review runs.

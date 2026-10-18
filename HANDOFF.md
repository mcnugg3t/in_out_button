# Handoff: In / Out Button

Written 2026-10-18 at the end of the session that rebuilt the AI usage tab
as the burn-pace view, added the Google reader, and made the Anthropic
reader survive an expired token. Post-review commit: `84822f6`. The app
is not running at the end of the session: see "Deploy state". Read with the spec,
`docs/2026-10-18-burn-pace-gemini-token-spec.md`, which is the design
record and holds both review triages (section 12 for the plan, the
`.review/code-round1/triage.md` beside it for the code), and this file,
which is the state. The previous session's handoff is in git history at
`9a8ca7e`.

## What shipped

- **Burn-pace usage page** (design candidate C, Caleb's preference). One
  tile per meter under a provider caption: percent left, time to reset,
  and a chart of percent used against the fraction of the window elapsed
  with a dotted even-pace guide. Observed history is solid; inferred
  segments (origin to the first reading, the flat tail to now) are dashed.
  Idle meters collapse into one "Untouched" strip; a provider with a
  weekly window and no 5-hour one shows the ghost tile.
- **History and stale readings.** `UsageStore` keeps
  `%APPDATA%\InOutButton\usage-store.json`: the last ok reading per
  provider and a per-meter history keyed by the window's reset time to the
  second (no tolerance: idle Antigravity buckets move their reset on every
  read). A failed read shows the stored reading dimmed and dated for up to
  seven days; a stale meter whose window has reset since is a ghost tile.
  At startup the tab shows the stored readings at once, marked "not read
  yet since the app started".
- **Google through the Antigravity CLI.** `agy -p /usage --output-format
  json` through `JsonLineProcess`, 25 s cap, the CLI's own print timeout
  read from its stderr marker, soft backoff 10 min then 60 min from the
  third failure. Five meters: Gemini Models (weekly, 5-hour), Claude and
  GPT models (weekly, 5-hour), Gemini 4 Argon (weekly). The CLI's per-run
  log goes to `%APPDATA%\InOutButton\agy-usage.log`. Verified live.
- **Anthropic refresh ladder.** When the token is expired or rejected the
  read runs `claude auth status`, then a one-word Haiku call with
  `--tools "" --strict-mcp-config --no-session-persistence
  --setting-sources ""` (measured: 11.8 s, about a thousand input tokens,
  no hook, no transcript). Keyed on the token string, once per token
  automatically and never within 30 min; steps are waited for and never
  killed. The app never writes the credentials file. A "Refresh via
  Claude Code" button forces it. `ClaudeRefreshAuto = false` turns the
  automatic part off.
- **Hardening.** Every child process sits in a kill-on-close job object
  (`ChildJob`), cooldowns are hard (a server's `Retry-After`) or soft (the
  app's backoff, skipped by Refresh), a set `ClaudeExecutable` or
  `AgyExecutable` is the only candidate, the first usage read waits for
  the repo scan.

Tests: xunit, 140-odd cases; two of them run only with
`INOUT_LIVE_TOOLS=1` because they call the real `agy` and `claude auth
status`. Build and tests pass with zero warnings.

## Caleb's rulings and the defaults this session took

Caleb set the three goals and asked for forks to be named. The defaults
taken, all overridable: C replaces A (no toggle); a real per-meter history
rather than the mock's single line; all five Google meters shown; the
two-layer expiry design (stale display plus the ladder) with the Haiku
call as the last step; Argon added to the plan-review script as a standing
reviewer. He did not rule on the `ClaudeUsage` tray app (below).

## Deploy state: the app is not running, and Avast is why

Post-review commit: `84822f6`. Read this section before anything else.

- The pre-review build `47a4cb1` was shipped at 17:24 on 2026-10-18 and
  ran for nineteen minutes: four timer reads, all three providers ok
  ("usage: Anthropic ok, 3 meters", "OpenAI ok, 1 meter", "Google ok, 5
  meters"), four history points per series in `usage-store.json`.
- The post-review build `84822f6` was built in place at 17:43 (0 warnings)
  and relaunched. Twenty-seven seconds after the window opened, Avast
  quarantined `bin\Debug\net8.0-windows\InOutButton.exe` as `IDP.Generic`
  (its behaviour-based catch-all), killed the process, deleted
  `obj\Debug\net8.0-windows\apphost.exe` as well, and removed the
  `InOutButton` value from `HKCU\...\CurrentVersion\Run`. Record: entry
  00000008 in `C:\ProgramData\Avast Software\Avast\chest\index.xml`,
  17:44:13. No post-review read happened; the store and the agy log are
  still from 17:43:10.
- The launcher exe is the SDK's app host with the dll name patched in, so
  it was byte-identical between the two builds. The verdict is about
  behaviour (a new unsigned exe that writes a Run key, reads
  `.credentials.json`, and spawns `codex` and `agy`), and Avast's
  AutoSandbox has been sandboxing every fresh build of this app (that is
  why windows took about 30 s to appear). Why the second build and not
  the first is not recorded anywhere Avast writes.
- A rebuild now fails: `CreateAppHost` gets "Access to the path
  '...\obj\Debug\net8.0-windows\apphost.exe' is denied". Avast is blocking
  that path. The SDK's own templates under
  `C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\` are
  intact. Nothing was launched after the quarantine.

Recovery, in order, all Caleb's to do because they change the machine's
antivirus settings: add an Avast exception for
`C:\Users\caleb\Tools\in_out_button\` (at least `bin\` and `obj\`), then
`dotnet build -c Debug` in the repo, launch
`bin\Debug\net8.0-windows\InOutButton.exe`, and tick "Launch on startup"
again to restore the login entry. Restoring from Avast's chest is the
alternative (the entry says `Restore=no`, so it needs the Avast UI). Avast
has flagged Caleb's own tools before (`Agronomy.exe`, persistent-memory's
`session-start.ps1`), so an exception for `~/Tools` may be the durable
answer. Never a cleaning build beside a running exe; verify builds with
`--artifacts-path <scratch>`.

## Review record

- Plan: Astra (99 s) and Argon (349 s, through the new `argon` launcher in
  persistent-memory's `plan-review.ps1`), raw answers under
  `docs/2026-10-18-burn-pace-gemini-token-spec.review/`. Nineteen findings
  triaged in the spec's section 12; Argon read revision 2 and so saw
  Astra's triage (not independent).
- Code: Sol 6.1 (medium, 79 s) and Grok 4.7 (low) on the diff
  `21ade4b..47a4cb1`, raw answers and `triage.md` in `.review/code-round1/`.
  Three real defects, each found by both: the 30-minute-floor path skipped
  the live read, a transiently unreadable store could be overwritten by an
  empty one, and a set executable override fell back to PATH. Fixed with
  tests in the post-review commit.
- Specimen stubs in persistent-memory `_candidates/` (2026-10-18): Git
  Bash rewriting `/usage` into a Windows path; `agy`'s print timeout
  exiting 0 with status SUCCESS; `Select-Object -First 1` randomising a
  native command's exit code; the cooldown-kind test that passed with the
  real path on the wrong kind.

## Known limitations and debt

- **The `ClaudeUsage` tray app** (`~/Tools/claudeusagewin`, in HKCU Run)
  refreshes the Claude token five minutes before expiry without a client
  id, overwrites `.credentials.json` without a temp-and-rename, drops
  `refreshTokenExpiresAt`, `rateLimitTier` and `organizationUuid`, writes
  `subscriptionType` as null, and logs the full tokens to debug output.
  The usage tab replaces it. Retiring its Run entry is Caleb's call: it
  is the only writer of that file other than Claude Code.
- **Whether `claude auth status` refreshes an expired token** is unknown.
  The ladder logs which step replaced the token ("token refresh: <step>
  replaced the token"); the first morning's log answers it. If step 1 is
  enough, step 2 is dead code to delete.
- **`agy` with a lapsed Google sign-in** in headless mode is untested; it
  might open a browser. The cap, the job object, and the backoff bound it.
- **Snapshot waits for all three readers**, so a ladder run (usually 15 s,
  at most about 150 s) holds the other tiles; the previous tiles show with
  busy on meanwhile. Per-provider posting is the improvement.
- **Job assignment race.** A child is assigned to its job just after it
  starts; a launcher that spawned a descendant and exited within
  milliseconds would leave it outside. Not the case for `agy` or `codex`.
- **The real viewport is small**: about 990 by 290 px at the 1060×750
  launch size. The page is compact and scrolls; a taller launch size or a
  larger share for the tab row would help every tab.
- **Nullable reset time (O4)** still drops a meter.
- **The Tasks tab** is still three lanes (B); Caleb prefers Planner by
  progress (A), page-only, in `docs/design/2026-10-17-candidates/`.
- **First window** took about 37 s to appear after the relaunch at deploy;
  not investigated (first launch of freshly built files is the guess).

## Settings added

`ClaudeExecutable`, `AgyExecutable` (both null means discovered on PATH),
`ClaudeRefreshAuto` (true), hand-edited in
`%APPDATA%\InOutButton\settings.json`.

## Routing

Rows for every model that touched this task are in persistent-memory's
`agent-cooperation/routing-log/log.jsonl` under task id
`2026-10-18-inout-burn-pace-gemini-token`.

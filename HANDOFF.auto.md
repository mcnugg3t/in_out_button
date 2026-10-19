<!-- auto-drafted 2026-10-18 19:39 by session-end hook, session e8eb7216-f5f3-45b4-b0d2-9e82c5377ee2; edit freely -->

# Handoff — In / Out Button, session ended 2026-10-18 19:38

## Current state

`main` is at `5282f4a`, pushed. The build is `84822f6` ("post-review: fix eleven code-review findings; compact page; docs"); the pre-review calibration specimen is `47a4cb1`. **155 tests pass, 0 warnings**; two of them (`tests/InOutButton.Tests/LiveToolsTests.cs`) only do real work when `INOUT_LIVE_TOOLS=1`, because they call the real `agy` and `claude`.

All three goals landed: the burn-pace Usage page, the Google/Antigravity reader (five meters), and the Anthropic refresh ladder with stale/ghost tiles.

**The app is not running, and cannot be rebuilt as-is.** Avast quarantined the freshly built `bin\Debug\net8.0-windows\InOutButton.exe` as `IDP.Generic` at 17:44:13 (chest entry 00000008, `Restore=no`), deleted `obj\Debug\net8.0-windows\apphost.exe`, and removed the `InOutButton` value from `HKCU\...\CurrentVersion\Run`. A rebuild now fails because Avast denies writing the apphost. Nothing launches at Caleb's next login until both the exe and the Run entry come back. Avast's AutoSandbox (`autosandbox.log`) also explains the 28–37 s cold starts.

The **pre-review** build did run live for 19 minutes and read all three providers in-app: Anthropic 3 meters ("Claude Max 20x"), OpenAI 1 meter ("prolite") plus a "No 5-hour window reported" ghost, Google 5 meters. `usage-store.json` reached 4 history points per series over timer reads at 17:25/17:31/17:37/17:43. The post-review build's tab was never verified live.

## Decisions made (and why)

- **Burn pace replaces rings-and-fuses, with no toggle.** Candidate A stays in git under `docs/design/2026-10-17-candidates/`.
- **Real history, not a straight line.** `UsageStore` keeps readings per meter in `%APPDATA%\InOutButton\usage-store.json`, keyed by the window's reset time; the page draws observed segments solid and inferred ones dashed.
- **Window identity is exact to the second, no tolerance** (Argon G4). Idle Antigravity buckets report reset as `now + window`, so a tolerance merged distinct windows and produced negative x coordinates. Confirmed live: idle resets moved an hour between fixture and read, while the in-use Argon bucket held 21:19:47.
- **Expired token, two layers.** A failed read shows the last good reading dimmed and dated; a stale window past its reset becomes a ghost tile (G1) rather than showing dead numbers. The ladder runs `claude auth status`, then a one-word Haiku call with `--tools "" --strict-mcp-config --no-session-persistence --setting-sources ""`. Keyed on the token hash, once per token, never within 30 min, with a "Refresh via Claude Code" button and `ClaudeRefreshAuto` to disable the automatic path. The app still never writes `.credentials.json`.
- **All five Gemini meters shown, by group**; idle (0% with all-zero history) meters collapse into one "Untouched" strip because the tab's web view is only ~720×234 CSS px at the launch size (1.375 device scale), where a three-column board clipped its third column.
- **Cooldowns are typed**: hard (server `Retry-After`) or soft (the app's own backoff, skipped by Refresh). Every child process sits in a kill-on-close job object (`ChildJob`); ladder steps deliberately run *outside* it, since killing a step could strand a rotated refresh token.
- **The 7-day staleness boundary is inclusive** — spec 3.1 wins over section 8's "under 7 days" wording.
- **Argon is now a plan reviewer** in `agent-cooperation/plan-review/plan-review.ps1`, passing `--add-dir` derived from each input's git top level.

## Open forks

- **Avast, Caleb's call.** Option 1 (the default offered): except `C:\Users\caleb\Tools\in_out_button\` (or all of `~/Tools`), `dotnet build -c Debug`, launch, re-tick "Launch on startup". Option 2: restore from quarantine via the Avast UI (`Restore=no`). Option 3: rebuild without an exception and expect another flag.
- **The old `ClaudeUsage` tray app** is still in the login Run entries. It rewrites `.credentials.json` non-atomically, drops three fields including the plan tier, and logs full tokens to debug output. The new tab replaces it; removing its Run entry is Caleb's decision.
- **Unknown:** whether `claude auth status` alone refreshes an expired token. The first morning's log line (`token refresh: <step> …`) answers it. Also unknown: what `agy` does headless if its Google sign-in lapses (reader caps at 25 s, kills the tree, 10-minute cooldown).
- **Snapshot still waits for all three readers**, so a morning ladder (up to 150 s) delays the OpenAI and Google tiles. Posting per provider, and the nullable reset time (O4), are the named next refinements. The Tasks tab's Planner-by-progress view is still open.
- Untouched Google meters carry a different reset on every history point. Harmless at 0%, but it's live behaviour, not a bug to chase.

## Tried and failed

- Git Bash rewrote `/usage` into a Windows path, which made the Antigravity report look like a dead end. From PowerShell it returns JSON in ~3 s with no model call.
- `powershell -File plan-review.ps1 -Models astra,argon` passed the comma list as one model name; rerun through PowerShell itself.
- Argon's first review exited 0 with empty stdout (`jetski: no output produced`) because `agy` denied reads outside the workspace; `--add-dir` fixed it. The smoke test missed it since `%TEMP%` reads were allowed.
- `Select-Object -First 1` on `git rev-parse` stopped git early and returned a stray exit 255, so the new `Get-RepoTop` helper silently fell back.
- Argon's rerun read the *revised* spec containing Astra's triage table, so it is **not independent of Astra**. Its line numbers point into `prompt.txt`, not source.
- Two test routes for the store-fault fix failed: an unwritable path (the store swallows its own save failures) and a test double (`UsageStore` is sealed). A null `Provider` reading was used instead.

## Files touched

`Usage.cs`, `UsageStore.cs`, `ChildJob.cs`, `Form1.cs`; `web/usage.html`, `web/sample-data.js`, `web/shared.css` (four new `*-wash` tokens per theme block); new tests `GeminiUsageTests.cs`, `ClaudeRefreshTests.cs`, `UsageServiceTests.cs`, `LiveToolsTests.cs`, edits to `UsageStoreTests.cs` and `UsageTests.cs`; `tests/fixtures/agy-usage-2026-10-18.json`; `docs/2026-10-18-burn-pace-gemini-token-spec.md` (revision 3, both reviews triaged) and its `.review/20261018-163507-argon/`; `CLAUDE.md`, `README.md`, `HANDOFF.md`, `.gitignore`. In persistent-memory: `plan-review.ps1` + its README, `routing-log/log.jsonl`, eight stubs in `calibration-specimens/_candidates/`, the project memory note, rebuilt indexes. The fixer's worktree was merged and cleaned up.

## Next steps

1. Resolve Avast, rebuild, relaunch, re-enable startup, then redo the live tab check on `84822f6` (screenshots of all three providers, the store, `agy-usage.log`).
2. Read the first morning's app log for the ladder's verdict on `claude auth status`.
3. Decide the `ClaudeUsage` Run entry.
4. Per-provider snapshot posting; O4's nullable reset time.
5. Run the librarian pass; two hook proposals are pending in persistent-memory.

While the app runs, build and test with `--artifacts-path <scratch>` — the login-launched exe locks `bin/Debug`.

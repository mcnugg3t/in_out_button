# Code review round 1: triage

Pre-review commit `47a4cb1`. Reviewers: Sol 6.1 (medium, 79 s, through
Codex with the packed spec and diff, read-only sandbox) and Grok 4.7 (low,
through fireworks-delegation's diff-review on the same prompt). Neither saw
the other's answer. Raw answers: `sol.md`, `grok47.md`. Triage is Fable's
and is not a verdict; a wrong row here is findable later.

| # | Reviewer | Finding | Judged | Action |
|---|---|---|---|---|
| S1 | Sol | A launcher that spawns a descendant and exits before `TryAssign` leaves that descendant outside the job; the job tests delay spawning and so cannot show it. | Real, accepted as a limit: `Process` cannot start suspended; `agy` and `codex` do not exit in the first milliseconds. | Spec 6.1 states the gap. No code change. |
| S2 | Sol | The 30-minute-floor branch returns "ran N min ago" without the live read spec 5.1 promises; its test never checks the endpoint was hit. | Real | Fixed: the floor branch reads live and only a 401/403 yields that message; the test asserts one endpoint call and zero steps, plus an `ok` sibling. |
| S3 | Sol | A store that exists but cannot be read at startup (a lock) loads empty with saving enabled, so the next reading overwrites the original without a backup. | Real | Fixed: one retry after 500 ms, then saves are blocked for the run with a log line; a test holds the file open and checks its bytes are unchanged after `RecordOk`. |
| S4 | Sol | Discovery and the 5 s pipe drain fall outside the step deadlines, so the ladder can exceed 150 s. | Real, low | Discovery cached per ladder; spec 5.4 says "about 150 s". |
| S5 | Sol | `BackoffIsTenMinutesThenAnHour…` tests the mapping only; nothing drives three consecutive failures and a reset. | Real | Fixed: a sequence test through the interpreter: 10, 10, 60 min, then a success resets to 10. |
| S6 | Sol | `ThePromptStepLoadsNoSettingsAndNoMcp` checks arguments, not that hooks or servers stay off in the installed CLI. | Accepted: the installed CLI was exercised by hand (spec 5.2 records the measurement); a test cannot own Claude Code's behaviour. | None. |
| S7 | Sol | The live-tool tests pass when `INOUT_LIVE_TOOLS` is unset, so a green run is no evidence they ran. | Accepted: xunit 2 has no runtime skip. The gated run's output is in the implementer's report and in this triage: five buckets read, `auth status` exit 0 in 2.6 s, credentials byte-identical. | README says how to run them. |
| S8 | Sol | Docs named in section 11 are not in the diff; the screenshot and in-app checks are not evidenced in the diff. | Expected at this commit: docs land in the post-review commit; screenshots are in the session scratchpad and the live check is the deploy step. | HANDOFF records the deploy evidence. |
| S9 | Sol | Page stale rendering and the ghost transition have no tests. | Accepted: the pages have no test harness; the browser fixture's `?scenario=stale` and the screenshots are the check. | None. |
| S10 | Sol | Check that `ReadAndRecordAsync` clears `_pending` in a `finally`. | Real: it was cleared only on the cancel path and at the end of the success path, so a store exception in `Settle` jammed the slot for the rest of the run. | Fixed: `finally`; a test with a faulting settle runs a second read. The settle call is also guarded so `RefreshAsync` cannot throw. |
| S11 | Fixer | `CodexUsage.FindAsync` had the same override fall-through as G3. | Real | Fixed with the same rule and a test. |
| G1 | Grok | The store's read-failure path (same as S3). | Real | Same fix as S3. |
| G2 | Grok | The floor branch skips the live read (same as S2). | Real | Same fix as S2. |
| G3 | Grok | A set `ClaudeExecutable` or `AgyExecutable` that does not resolve falls through to `where.exe`; spec 5.3 and 6.1 make the override the only candidate. | Real | Fixed: an unresolvable override is not found, with a message naming the setting; one test per finder. |
| G4 | Grok | HANDOFF.md still says the Gemini reader is future work. | Real, expected at this commit | HANDOFF.md is rewritten in the post-review commit. |
| G5 | Grok | The Google driver tests comment out the driver's appended arguments with `#`, so the argv is unchecked. | Real | Fixed: an argument-list test on the driver's builder. |
| G6 | Grok | `ACacheHitAddsNoPoint`, `FreshSkipsASoftCooldownButNotAHardOne`, and the stale-age tests each assert less than their names say (no store count, no reader-not-called check, no 7-day boundary). | Real | Fixed: the three assertions added. |
| G7 | Grok | `TheCredentialsFileIsNeverWritten` uses fake steps, so a write on the real process path would pass. | Accepted: the gated live test compares the real file's bytes around a real `auth status` step. | None. |
| G8 | Grok | `GeminiUsage` static state (`_failures`, `_discovered`) under xunit's parallel classes. | Real risk of flakiness | Fixed: state made instance-scoped or the tests serialized; the fixer's report says which. |
| G9 | Grok | Assumptions outside the diff: camelCase serialization of the new records, the `ready` branch posting `Last`, the `SetBusy(false)` resume, `OpenUsageAction`'s map, `ArgumentList` quoting of `""`, the job struct's bitness, `createElementNS`. | Checked by Fable | Serialization is reflection-based `JsonSerializerDefaults.Web` (WebHost.cs:17); `ready` posts `_usage.Last` (Form1); `SetBusy(false)` read unchanged; `.NET` quotes an empty `ArgumentList` entry as `""`; bitness: the csproj sets no `PlatformTarget`, so the app is AnyCPU and runs 64-bit here; `ChildJob` declares the pointer-sized fields as `IntPtr` and passes `Marshal.SizeOf` of the struct, and `SetInformationJobObject` validates that length, so the live grandchild-kill test passing is proof of the layout on this machine; the page's `svgEl` uses `createElementNS` (usage.html). |

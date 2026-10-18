1. Defects

High — UsageStore.cs, Load read-failure path. Quote: `catch (Exception ex) { Log?.Invoke($"usage store: could not read {_path}: {Scrub.Message(ex.Message)}; starting empty"); return; }`. Trigger: `usage-store.json` exists but `ReadAllText` throws (sharing violation, access denied, transient lock). The in-memory store is cleared and `_saveBlocked` is not set. The next `RecordOk` calls `Save` and `File.Move(..., overwrite: true)`, replacing a file the process never parsed. A locked or half-written store is destroyed. Spec only authorizes that for a file that does not parse or has an unknown version, and those paths go through `SetAside`.

Medium — Usage.cs, `RunLadderAsync` floor return. Quote: `if (ladder.LastRun is { } last && now - last < ClaudeRefreshLadder.Floor) { ... return (null, Refreshable(trigger.Plan, $"Claude Code token {word}; refresh ladder ran {minutes} min ago; try again later")); }`. Trigger: `ForceClaudeRefresh` within 30 minutes, token string unchanged (tray app did not rotate it, or the ladder already failed). Spec 5.1: "a click inside the floor reads live without the ladder and says so in the message". This returns before any usage HTTP call, so a click cannot show live meters until the floor ends.

Medium — Usage.cs, `ClaudeCli.FindAsync`. Quote: `if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath) && Resolve(overridePath) is { } chosen) return chosen;` then it falls through to `_discovered` / `where.exe`. Trigger: `ClaudeExecutable` set to a missing path, or to `claude` / `claude.cmd` with no `node_modules\@anthropic-ai\claude-code\bin\claude.exe` beside it. Spec 5.3: override when set; a shim without the native exe counts as not found; no `cmd.exe`. The set value is ignored and another `claude` may be started. Same shape in `GeminiUsage.FindAsync`: a set but missing `AgyExecutable` falls through to `where.exe agy`.

Low — HANDOFF.md, the hunk that only repairs the path. The following sentences still say parsing `agy -p /usage` is the next session and is not implemented. The diff implements that reader, so the handoff this change touches is false.

2. Spec gaps

- Spec 5.1, "a click inside the floor reads live without the ladder and says so in the message": not implemented (defect above). No test drives a forced click inside the floor and asserts an HTTP read; `AClickInsideTheFloorReadsWithoutTheLadder` only checks that steps do not run and the floor message is set.
- Spec 11, "Docs: CLAUDE.md usage section, README, HANDOFF.md ... persistent-memory project note, routing-log rows": not in this diff, except the HANDOFF path-byte repair. Traced, not reproduced whether those files exist elsewhere.
- Spec 7.3 / 7.5 (dashed inferred segments, stale "as of" numbers, pace at the reading's time, stale-past-reset ghost, x clamp): implemented in `web/usage.html` as far as the hunk goes; section 9 does not require an automated page test, and this diff has none. The hunk ends at `Bridge.on(msg => {`; the snapshot handler and 30 s timer are not in the diff.

3. Tests that check less than they claim

- `AStandInPrintingTheFixtureReadsOk` (and the print-timeout stand-in): leading args end with `script + " #"`, and the comment says that comments out the driver's appended `agy` arguments. A green run does not show that `-p /usage`, `--output-format json`, `--print-timeout 20s`, `--log-file`, or the absence of `--disable-slash-commands` were what the child executed.
- `TheCredentialsFileIsNeverWritten`: fake `ClaudeRefreshLadder` steps never start `ClaudeCli.RunStepAsync`. It would not catch a write added on the real process path.
- `ACacheHitAddsNoPoint`: asserts one posted history point and one fake-reader call. It would not catch `RecordOk` appending a second point with a new timestamp if the service still posted a single point from an earlier filter bug.
- `FreshSkipsASoftCooldownButNotAHardOne`: does not assert that `fresh: false` during a soft cooldown does not call the reader.
- `ClosingTheJobKillsAGrandchildWhoseParentExited`: sleeps 2 s before spawn, so it would not catch the production race in `JsonLineProcess.Start` (`process.Start()` then `TryAssign`, child already running).
- `AFailureWithAnHourOldReadingIsStale` / eight-day test: do not hit the 7-day boundary (`now - at <= maxAge`).

4. Assumptions to verify in the repo before merging

- `SettingsStore.SettingsDirectory` is `%APPDATA%\InOutButton`, and `WebHost` serializes `UsagePoint` / `History` camelCase (reflection or a source-gen context that includes the new types). Check the serializer used by `PostWebMessageAsJson` for `UsageSnapshot`.
- On first WebView `ready`, Form1 posts `_usage.Last` so the seeded stale snapshot is what the page shows. Check the `ready` branch of `OnUsageMessage`.
- `SetBusy(false)` still calls `ReadUsageAsync` when the usage tab was shown during a scan. The skip is in this diff; the resume path is not.
- `OpenUsageAction` ignores unknown ids and still maps `gemini-usage` and `antigravity-setup` only. Check that method.
- `sevClass` maps `warning` → `warn` and `critical` → `crit`, and `windowLabel` returns "5-hour window" / "7-day window". Those functions are outside the hunk.
- `ArgumentList` empty strings become `""` on this TFM (`--tools ""`, `--setting-sources ""`). Check `PasteArguments` / a logged command line.
- App is 64-bit so `ChildJob.ExtendedLimitInformation` matches `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`. Check `PlatformTarget`.
- xunit does not run `GeminiUsage` tests in parallel against static `_failures` / `_discovered`. Check the test assembly collection settings.
- `svgEl` uses `createElementNS`. Check `bridge.js` or the helper above the hunk.

5. Checked and cleared

- No app write to `.credentials.json` in the diff; ladder stop and once-per-token key are the token string; `--bare` is absent; stdin is closed; steps are not killed; 429 is `CooldownKind.Hard`; 5xx and agy backoff are soft; `fresh` skips only soft.
- Window identity is reset time to the second, no 2-minute tolerance; previous-window point one second before reset is excluded in `UsageStoreTests`; cap 3000 and 8-day prune match the spec; malformed and unknown version move to `.bak` when the move succeeds; cache hit does not call the reader.
- agy argv in `ReadCoreAsync` matches section 6.1; print-timeout marker is preferred over shape-change after stderr drain; not-found does not set a cooldown; duplicate bucket id keeps the first; parser exceptions return the shape-change string.
- Stale settle keeps stored meters, footnote, and `UpdatedAt`, takes plan from the failure when present, actions from the failure; seed message is "not read yet since the app started" with empty actions; stale log line is distinct.
- Page hunk: unavailable card spans the grid; weekly-without-5h ghost; stale pace and chart end at the reading; "to reset" uses now; stale providers do not post `{type:"stale"}`; past-reset stale becomes the ghost line from section 7.5; history sorted and x clamped. Traced, not reproduced against a browser.

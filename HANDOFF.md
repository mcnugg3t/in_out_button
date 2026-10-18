# Handoff: In / Out Button

Written 2026-10-17 at the end of the session that added the AI usage and
Tasks tabs. Post-review commit: the `post-review:` commit of 2026-10-17 (`git log --grep "post-review: AI usage"`). Read with the spec,
`docs/2026-10-17-usage-and-tasks-tabs-spec.md`, which is the design record,
and this file, which is the state.

## What shipped

Two tabs beside Repos, hosted in WebView2 and driven entirely by C#:

- **AI usage** (rings and fuses, design candidate A). Anthropic reads the
  Claude Code login's three meters over the OAuth usage endpoint; OpenAI reads
  Codex's rate-limit windows through `codex app-server`; Google is an empty
  state with two buttons. Verified live on this machine.
- **Tasks** (three lanes, design candidate B). `persistent-memory/tasks/tasks.json`,
  per-task revisions, stale-save rejection, a cross-process lock, atomic
  writes, a busy gate during workflows. The add, progress, delete, and
  rejected paths were exercised end to end through WebView2's debugging port.

Tests: xunit under `tests/InOutButton.Tests`, 60-odd cases over the parsers,
the scrubber, the Codex driver, and the task store. Build and tests pass with
zero warnings.

## Caleb's rulings this session

- Storage: a JSON file in persistent-memory under a root `tasks/` folder.
  Done; `tasks/README.md` there tells agents the schema and the revision rule.
- Usage view: he prefers candidate C (burn pace tiles); A shipped because it
  was built first. **Next session's first job**, page-only.
- Tasks view: he prefers candidate A (Planner, grouped by progress); B
  shipped. **Also next session**, page-only on the same message contract.
  Both candidate pages are in `docs/design/2026-10-17-candidates/`.
- Wrap-up was called after the weekly reset: no spare-compute work.

## Deploy state

Windows launches `bin\Debug\net8.0-windows\InOutButton.exe` at login. See the
closing message of the session, or `git log`, for whether the in-place rebuild
happened; if the running app predates the `post-review:` commit of 2026-10-17 (`git log --grep "post-review: AI usage"`), ship by closing
the window (no tray icon), `dotnet build -c Debug` in the repo, relaunch.
Never a cleaning build beside the running exe; verify with
`dotnet build --artifacts-path <scratch>`.

## Review record

- Plan: Astra, `docs/2026-10-17-usage-and-tasks-tabs-spec.review/20261017-181714/`,
  triage in the spec's section 7. Four blocking findings, all fixed in the
  spec before building.
- Code: Sol 6.1 and Opus 5.5, `.../code-round1/` with `triage.md`. Thirteen
  findings, eleven fixed, two deferred (O4 nullable reset time, O8 the
  timeout test's child-process claim).
- Specimen stubs in persistent-memory `_candidates/`: the reload-before-write
  rule that still lost updates (plan), and the task store that dropped fields
  its own README told agents to keep (code).

## Known limitations and debt

- Gemini: nothing local to read. The Gemini CLI stopped serving AI Pro on
  2026-06-18; Antigravity's `agy -p /usage` is the follow-up once `agy` is
  installed; the unofficial Antigravity endpoint is off the table (Google
  suspended accounts for third-party use in March 2026).
- Codex reported only a weekly window and `planType` "prolite" today; the
  reader labels by duration and assumes nothing.
- Anthropic error paths have no fake-server tests.
- No five-clone git merge drill for `tasks.json`; a real conflict surfaces as
  a failed push for persistent-memory, as for any other file there.
- The scrubber's 24-character opaque-run rule also redacts GUIDs and long file
  names inside exception messages. Accepted.
- TabControl styling is WinForms default. The form opens at 1060x750.

## Settings added

`UsageRefreshMinutes` (5), `CodexExecutable` (override), `TasksFile`
(override), all hand-edited in `%APPDATA%\InOutButton\settings.json`.

## Routing

Rows for every model that touched this task are in persistent-memory's
`agent-cooperation/routing-log/log.jsonl` under task id
`2026-10-17-inout-usage-tasks-tabs`.

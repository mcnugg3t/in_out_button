# Code review round 1: triage

Pre-review commit `d192281`. Reviewers: Sol 6.1 (medium, 117 s) and Opus 5.5
(default, 1,014 s), each with the spec and the code, neither with the other's
answer. Raw answers: `sol.md`, `opus.md`. Fixes landed in two batches by Opus
builders and are in the post-review commit. Triage is Fable's and is not a
verdict; a wrong row here is findable later.

| # | Reviewer | Finding | Judged | Action |
|---|---|---|---|---|
| S1 | Sol | JSON-quoted secrets pass the scrubber | Real | Key pattern allows quotes; 24-character opaque-run net; tests |
| S2 | Sol | A workflow can start while an accepted save is still flushing | Real | Serial store chain; `DrainTaskWritesAsync` after every `SetBusy(true)` |
| S3 | Sol | Two app instances can both pass the rev check and overwrite | Real | Cross-process lock file in `%TEMP%` around read-check-write; test |
| S4 | Sol | Failed quick-add loses the title | Real, minor | Input keeps its text until a state confirms the add; re-enabled on rejection |
| O1 | Opus | Unknown JSON fields dropped on every save, against the README's rule to agents | Real, specimen | `[JsonExtensionData]` on task and document; on-disk extras carried; test |
| O2 | Opus | Scrubber still misses `Authorization: Basic` and short values after `client_secret`, `cookie` | Real | Scheme-aware authorization case, `[\w-]*` key prefix, cookie and session; tests |
| O3 | Opus | Default `~\Tools\persistent-memory` path not in the scan roots never syncs, no banner | Real | `TaskPathSource` enum; `unsynced` banner for the default-clone case; test |
| O4 | Opus | A meter with null `resets_at` is dropped | Plausible, unverified | Deferred: the page shows a visible placeholder for a missing 5-hour meter; fix is nullable `ResetsAt` plus a fixture variant |
| O5 | Opus | Two quick clicks on one card: second rejected as "changed on disk" | Real, UX | In-flight id set on the page disables that card until the rev moves |
| O6 | Opus | `stale` can bypass `UsageRefreshMinutes` | Plausible | `stale` reads only when a reset happened after the provider's last read |
| O7 | Opus | Codex discovery outside the cap, every read, ignores close | Real | Discovery under the token, cancellation check before start, launch cached per session |
| O8 | Opus | Timeout test cannot tell a tree kill from a single kill; scrub tests used only handled spellings | Real | Scrub tests widened (S1, O2). Timeout test left as is: debt, see below |
| O9 | Opus | A crash between temp write and rename leaves a `.tmp` that sign-out commits | Real, minor | `tasks/*.tmp` added to persistent-memory's `.gitignore` |

Overlap: S1 and O2 are the same seam found twice; Sol found S2, S3, S4
uniquely, Opus found O1, O3, O5, O6, O7, O8 uniquely. Both cleared the token
handling, the revision logic, the atomic write, the busy gate, the driver's
cleanup, the bridge containment, and the UI-thread affinity.

## Debt carried out of this round

- O4: nullable reset time with a "no active window" state.
- O8: a timeout test whose stand-in process starts a child, so a tree kill is
  distinguishable from a single kill; the `node codex.js` launch path is
  untested.
- Anthropic error paths (401 re-read, 429 cooldown, 5xx retry) have no
  fake-server tests; checked against `usage.mjs` only.
- No five-clone git merge drill for `tasks.json` (declined at plan review).

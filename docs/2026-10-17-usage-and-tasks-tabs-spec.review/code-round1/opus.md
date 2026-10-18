# Opus 5.5 code review, round 1

Model Opus 5.5 through the Agent tool, default effort, 1,014 s, about
275k tokens, 2026-10-17 about 19:45. Read the pre-review commit `d192281`
while Sol's round-1 fixes were landing in the tree. Scope as for Sol:
Usage.cs, Tasks.cs, WebHost.cs, the Form1.cs diff, web/bridge.js,
web/tasks.html, web/sample-data.js, both test files; web/usage.html and
web/shared.css excluded. Raw answer follows, lightly reformatted; triage is in
`triage.md` beside this file.

---

**1. Every save deletes fields the app doesn't know about** (Tasks.cs:22-46, :232, :141, :305-318). `TaskItem` and `TaskDocument` have no `[JsonExtensionData]` property, so a field the app doesn't model is dropped when the file is read, and the next write leaves it out for every task in the file. Scenario: an agent follows `persistent-memory/tasks/README.md`, adds `"link"` and `"tags"` to task A and a top-level `"owner"`. Caleb then edits task B. The rewritten file has lost all three, and sign-out commits that. The README tells agents never to drop fields they do not understand, and the app does exactly that. Confirmed by running. Fix: extension data on both classes; in `Upsert`, copy `onDisk.Extra` onto the stored task; carry the document's extra; round-trip test.

**2. `Scrub.Message` misses common ways secrets are written** (Usage.cs:83, :95). At `d192281`, `access_token=`, `refresh_token=`, `api_key=`, `apiKey:`, `client_secret=`, JSON-quoted `"token":"…"` and `Authorization: Basic <b64>` all pass through. Confirmed by running. With Sol's in-progress fix, everything is handled except `Authorization: Basic <b64>` (the key pattern consumes only the scheme word; `+`, `/`, `=` break the 24-character run) and values shorter than 24 characters after `client_secret=` or `cookie: session=`. Fix: let the authorization case consume scheme and credential; allow a `[\w-]*` prefix before the keyword; add `cookie|session`; add the cases to the test.

**3. Tasks can save to a persistent-memory clone that never syncs, with no warning** (Tasks.cs:365-370). When no scanned repo is named `persistent-memory`, `Resolve` falls back to `~\Tools\persistent-memory` with `fallback=false`; sign-in and sign-out only loop over scanned repos, so that clone is never pulled or pushed. Not live on this machine because `Tools` is a scan root. Fix: flag the default-path case and show a banner.

**4. The Anthropic Session meter disappears when its reset time is null** (Usage.cs:398-401, :422-424). Any meter with a null `resets_at` is skipped; the reference parser keeps such meters. Whether the API sends null for an idle window is unverified. Fix: nullable `ResetsAt`, page shows "no active window", fixture variant.

**5. Two quick clicks on one card: the second is refused as "changed on disk"** (web/tasks.html:325-328, :483; Tasks.cs:124). The progress circle and drag send the whole task with the last-shown revision and there is no in-flight guard, so the second request carries the same revision and is rejected with the misleading message. Store side confirmed by running; page race plausible. Fix: in-flight id set on the page; disable that card's controls until the revision moves or a rejection arrives.

**6. A `stale` message can force a full read about every 60 s** (Form1.cs:1197, Usage.cs:1099). `RefreshAsync` builds a new snapshot with a new `UpdatedAt` even when every provider came from cache, which re-arms the page's once-per-snapshot dedupe; `UsageRefreshMinutes` is bypassed. Plausible. Fix: on `stale`, read only when some meter's `ResetsAt` is past and later than that provider's `UpdatedAt`.

**7. Codex discovery runs outside the time limit and ignores close** (Usage.cs:528-529, :671). `FindAsync` doesn't pass the token to `where.exe`; the 20 s limit starts after discovery; `where.exe` runs on every read; a form close during discovery still launches `codex.exe` before the first token check. Confirmed by reading. Fix: cancellation check before start, discovery under the limit, cache the found executable for the session.

**8. Some tests check less than they claim** (UsageTests.cs:271-284, :242-255). The timeout test claims it leaves no child process running, but `Start-Sleep` starts no children, so it cannot tell a tree kill from a single kill; the `node codex.js` path, the only one with a grandchild, is never run. The scrub test only used spellings the regex already handled. Nothing tested unknown-field round-trips or the default-path case.

Checked and cleared: token handling (read into a class local, never in a message; bodies never surfaced; stderr never read back; every exception message scrubbed); the task store's revision logic, atomic write, and refusal to save after a failed load; the busy gate (no yield between `SetBusy(false)` and `SetBusy(true)` inside sign-in and sign-out; every per-repo button goes through `SetBusy`); path resolution before the first scan; the Codex driver's cap, caps, and kill on every exit path; UI-thread affinity of callbacks, `Post` on a disposed host, timer and token on close, WebView2 disposal; refresh (no tight loop on failure, `fresh` keeps cooldowns, single flight); the bridge's origin check, navigation pin with trailing slash, popup drop, CSP, no `innerHTML`, pre-ready queue. Minor: a crash between the temp write and the rename would leave a `.tmp` file in the tasks folder that sign-out's `git add -A` would commit.

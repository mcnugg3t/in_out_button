# Sol 6.1 code review, round 1

Model gpt-6.1-sol through Codex 0.163.0-alpha.5, reasoning effort medium,
117 s, 2026-10-17 about 19:30. Scope: Usage.cs, Tasks.cs, WebHost.cs, the
Form1.cs diff, web/bridge.js, web/tasks.html, web/sample-data.js, both test
files. web/usage.html and web/shared.css were excluded (layout fix in flight).
Raw answer follows; triage is in `triage.md` beside this file.

---

1. **P1 — JSON-quoted secrets survive scrubbing.** Usage.cs:95
   **Scenario:** Codex returns an error message containing `{"token":"SYNTHETIC_OPAQUE_0123456789"}`. The quote between `token` and `:` defeats the key/value pattern; `ReplyError` forwards the secret into `ProviderUsage.Message`, then the page and unavailable-state log.
   **Confidence:** Confirmed with a .NET regex probe and source tracing; no live credential used. Existing tests cover unquoted pairs.
   **Smallest fix:** Use fixed messages for unknown Codex failures, or handle quoted secret keys before forwarding external errors.

2. **P1 — Starting a workflow does not wait for task writes already accepted.** Form1.cs:1317
   **Scenario:** A save passes `_busy` and starts its worker. The user then starts selected-repo Commit + sync or Discard + pull. Git runs while the save is still flushing or awaiting rename. The save can finish after staging, missing the commit, or replace the document produced by reset/pull. `SetBusy(true)` blocks subsequent requests but never drains accepted writes.
   **Confidence:** Confirmed by reading; timing scenario not executed.
   **Smallest fix:** Close mutation admission and await accepted writes before starting repository operations.

3. **P1 — Two app instances can both accept saves and silently overwrite each other.** Tasks.cs:60
   **Scenario:** Two instances read the same document before either writes. Each independently accepts its revision check, then replaces the whole file. The later rename removes the earlier accepted edit or newly added task. `_gate` protects only one store instance; the application has no single-instance guard.
   **Confidence:** Confirmed by reading, conditional on overlapping instances; not runtime reproduced. Existing tests place external edits entirely before the next save.
   **Smallest fix:** Enforce one application instance, or hold a shared cross-process lock across read, revision check, and replacement.

4. **P2 — Failed quick-add discards the task title.** web/tasks.html:415
   **Scenario:** Enter dispatches an upsert and immediately clears the input. A read-only file, malformed document, or newly started workflow causes rejection. The page displays a banner but cannot recover the submitted title.
   **Confidence:** Confirmed by reading. The write-failure test verifies file preservation, not draft preservation.
   **Smallest fix:** Retain the submitted title until persistence is acknowledged; correlate the response with a request or client-generated task ID.

Checked and cleared:

- All 49 tests passed, with build and restore output under the required artifacts path.
- Sequential stale updates, resurrection, stale deletes, unreadable-file saves, and tested replacement failures preserve disk contents.
- Codex stderr and unparsed stdout are not surfaced; buffers are bounded, initialization is acknowledged, and driver cleanup covers failure and cancellation.
- Extensionless npm-shim discovery is handled; `where.exe` has a 10-second timeout.
- Usage callbacks preserve the UI context; refresh caching, cooldown precedence, completion timestamps, timer stopping, and close cancellation are present.
- Bridge origin/navigation checks, popup suppression, pre-ready queuing, and task text rendering passed source inspection.
- Excluded layout files were not reviewed. No app or workflow was launched; no source files were changed.

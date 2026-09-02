# CLAUDE.md — In / Out Button

Context and implementation plan for the rclone-backed dataset sync feature. The app's existing behavior is documented in [README.md](README.md); this file covers the design for the rclone integration that should layer on top.

## Goal

Some of the user's git repos contain large datasets that are gitignored (too big or too messy for git). When the In / Out Button signs in or out, it should also sync those gitignored dataset folders to/from a single OneDrive remote configured in rclone, so that switching machines preserves both code and data.

## High-level design

- Source of truth for *which folders to sync* lives **in each repo**, in a file named `.rclone-sync.json` at the repo root. The app's existing scan already walks every repo; reading this file during scan adds rclone-syncable repos to the work list.
- Source of truth for *where data goes* is a **single OneDrive rclone remote** configured by the user (e.g. `onedrive:`). The remote name is stored in `%APPDATA%\InOutButton\settings.json` (one new field on `AppSettings`).
- Each dataset folder maps deterministically to a remote subpath: `<remote>:<remote-root>/<repo-name>/<relative-folder-path>`. The repo's `.rclone-sync.json` can override this if needed.
- Sync direction is tied to the existing buttons:
  - **Sign in** runs `git pull`, then `rclone copy <remote-path> <local-path>` per dataset folder (remote → local; missing locally is normal).
  - **Sign out** runs `rclone copy <local-path> <remote-path>` per dataset folder (local → remote), *then* `git add -A` / commit / push.
- Repos without `.rclone-sync.json` are unaffected — rclone steps are skipped silently.
- Rclone failures should behave like git failures today: surface in the repo row's `Status` / `LastMessage`, append the full output to the log, and count toward the failure tally. They should **not** abort the rest of the sign-in/out for other repos.

## `.rclone-sync.json` schema (per repo)

Minimal viable shape, placed at the repo root and committed:

```json
{
  "remote": "onedrive",
  "folders": [
    {
      "local": "data/raw",
      "remote": "InOutButtonData/<repo-name>/data/raw"
    },
    {
      "local": "datasets",
      "exclude": ["raw_dump.parquet", "*.tmp"]
    }
  ]
}
```

- `remote` (optional): overrides the global remote name from settings. Usually omitted.
- `folders[].local`: path relative to the repo root. Must match an entry in `.gitignore` (the app should warn but not block if it isn't ignored).
- `folders[].remote` (optional): explicit remote subpath. If omitted, derived as `<settings.RemoteRoot>/<repo-name>/<local>`.
- `folders[].exclude` (optional): list of rclone `--exclude` patterns (relative to the folder root), passed to `rclone copy` on both pull and push. Use it to keep a regenerable raw file local while still syncing the rest of the folder.

## New settings (additions to `AppSettings`)

```csharp
public string? RcloneRemote { get; set; }         // e.g. "onedrive"
public string RcloneRemoteRoot { get; set; } = "InOutButtonData";
public int RcloneTimeoutSeconds { get; set; } = 600;  // datasets can be slow
public int RcloneActiveDays { get; set; } = 14;       // commit-recency window for "active"
public bool RcloneActiveOnly { get; set; } = true;    // gate batch data sync to active repos
```

Surface these in the UI as a small "Rclone" group: remote name textbox, remote-root textbox, and a "Test remote" button that runs `rclone lsd <remote>:` and reports success/failure to the log.

## Active-repo gating (added 2026-08-24)

Batch data sync (Sign in / Sign out / the all-repos data buttons) only runs rclone for *active* repos: last commit within `RcloneActiveDays` **or** a dirty working tree (`git status --porcelain` non-empty). The dirty check is load-bearing at sign-out — the day's work isn't committed yet when the data push runs, so commit age alone would skip exactly the repo being worked on. `RcloneActiveOnly = false` disables the gate; the per-repo Pull data / Push data buttons ignore it (explicit selection). Activity is probed during scan (`git log -1 --format=%ct` + `git status --porcelain`, bounded parallelism) and shown in the grid ("Last commit" column, `●` dirty marker, Data column `sync`/`idle`).

Batch actions are also separable: "Git pull (all)" is git-only, "Pull/Push data (all)" are rclone-only; Sign in / Sign out remain the combined flows.

## Changes 2026-09-02 (sign-out failures on three repos)

Three unrelated causes, all reproduced and fixed:

- **File entries.** `.rclone-sync.json` may list a single file (`scratch_pad/proficiency.db`). `rclone copy` treats a file source as "copy into this folder" and dies when the destination is a file. `RcloneRunner` now detects file vs folder (local type; on a first pull, `rclone lsjson --stat` on the remote) and uses `copyto` for files, `copy --create-empty-src-dirs` for folders.
- **Missing sides.** A push of an entry that doesn't exist locally (fresh clone: `qc_modeling/data`) and a pull of an entry never pushed (rclone exit 3) are skips with a message, not failures.
- **Staged-changes probe.** `git diff --cached --quiet` ran git for windows' `astextplain` docx driver, which fails on word `~$` lock files (exit 128); sign-out then never committed. Probe now passes `--no-ext-diff --no-textconv`.
- **Push rejected.** Sign out on `persistent-memory` was rejected because another machine had pushed. `GitRunner.PushAsync` retries once with `pull --rebase`; a failing rebase is aborted.

Plumbing that came with it: `GitWorkflowResult` → `WorkflowResult` (lives in `ProcessRunner.cs`); actions return `RepoActionResult(Git, Data)` and the grid has separate **Git** and **Data** status columns with per-side tooltips; `ProcessRunner` summarises failures by the first `fatal:`/`error:`/`CRITICAL`/`ERROR`/`[rejected]` line (rclone timestamps stripped, exit code appended) instead of the last line; rclone runs with `--stats-log-level NOTICE` so the success summary can report files transferred; the gitignore warning uses `git check-ignore` (honours globs).

## Implementation steps

Each step should compile and run on its own, in this order:

1. **Add settings fields and UI controls.** Extend `AppSettings`, add inputs to [Form1.cs](Form1.cs)'s left panel under the scan-roots list. "Test remote" calls a new `RcloneRunner.TestRemoteAsync(string remote)` that wraps `rclone lsd <remote>:` using the same `Process` pattern as `GitRunner.RunGitAsync` (extract a shared `ProcessRunner` if it gets duplicative).

2. **Add `RcloneSyncConfig` model + loader.** New types alongside `RepoRow`:
   ```csharp
   public sealed record RcloneSyncConfig(string? Remote, IReadOnlyList<RcloneFolder> Folders);
   public sealed record RcloneFolder(string Local, string? Remote, IReadOnlyList<string> Exclude);
   ```
   Static `RcloneSyncConfig.TryLoad(string repoPath, out RcloneSyncConfig? config)` reads `.rclone-sync.json` and returns `false` if missing. Validation errors (malformed JSON, empty `folders`) should log a warning and return `false` — never throw out to the UI thread.

3. **Add `RcloneRunner`.** Two public methods:
   - `RclonePullAsync(string repoPath, RcloneSyncConfig config, AppSettings settings, int timeoutSeconds)` — for each folder, run `rclone copy <remote>:<remote-path> <local-path> --create-empty-src-dirs`.
   - `RclonePushAsync(...)` — same but with arguments reversed.

   Both return a `GitWorkflowResult` (rename to `WorkflowResult` if/when it stops being git-specific) so they slot into the existing log/status plumbing without inventing a parallel type. Concatenate per-folder summaries the same way `GitRunner.SignOutAsync` concatenates per-command summaries.

4. **Wire into Sign in / Sign out.** In `Form1.RunForAllReposAsync`:
   - Sign in: after `GitRunner.SignInAsync`, if `RcloneSyncConfig.TryLoad(...)` succeeds and `settings.RcloneRemote` is set, run `RcloneRunner.RclonePullAsync`. Merge its result into the repo's status the same way multi-step git results are merged.
   - Sign out: insert `RcloneRunner.RclonePushAsync` between the scan and `GitRunner.SignOutAsync`. Rationale: pushing data first means the commit can reference it (e.g. a manifest file inside the data folder that *is* checked in). If a rclone push fails for a repo, still attempt the git commit/push — those are independent.

5. **Selected-repo actions.** Add "Pull data" and "Push data" buttons in the per-repo row of buttons. Same handler shape as `RunForSelectedRepoAsync` already uses.

6. **Add `rclone` discovery / failure UX.** On startup, check `rclone --version` once and log the version (or a clear "rclone not found on PATH — dataset sync disabled" message). Disable the rclone UI affordances when missing rather than letting every sync fail with an exec error.

## Non-goals (for the first cut)

- **No two-way sync / conflict resolution.** `rclone copy` only — never `sync` (would delete things). If the user has local changes the remote doesn't, `copy` preserves both. Diverged-content conflicts are a known limitation; revisit if/when they bite.
- **No bisync.** Same reason — too easy to lose data.
- **No progress bars for individual files.** Just the same log-tail UX that git uses.
- **No automatic `.gitignore` editing.** The user adds folders to `.gitignore` themselves. The app can warn at scan time if a `.rclone-sync.json` folder isn't ignored, but doesn't modify the file.

## Watch-outs

- **OneDrive path lengths.** Windows + OneDrive can choke past ~260 chars. When building remote paths, prefer short `RcloneRemoteRoot` defaults and short repo names. Surface the full failing path in errors so the cause is obvious.
- **Spaces and unicode in paths.** Both `rclone` and the existing `Process` start with `ArgumentList.Add(...)` (no shell), so arguments don't need quoting — keep it that way; never build command strings with `string.Format`.
- **Timeouts.** Datasets are slow. Default `RcloneTimeoutSeconds` to 600 and let users raise it. Don't reuse `GitTimeoutSeconds` (120s).
- **`rclone copy` is not idempotent-cheap on huge trees.** It still has to stat every file. Acceptable for now — revisit with `--checksum` or `--size-only` flags if it becomes painful.
- **Auth.** Assume `rclone config` has been set up out-of-band by the user. The app's "Test remote" button is the user's signal that auth is healthy.

## Out of scope, but worth noting

- ~~Future: per-folder `--exclude` rules in `.rclone-sync.json`.~~ **Done** — `folders[].exclude` (2026-06-01).
- Future: a "dry run" button (`rclone copy --dry-run`) to preview before signing out.
- Future: optional manifest file (`.rclone-sync.lock`) listing the SHAs of synced files, committed to git, so code reviewers can see what data state goes with which commit.

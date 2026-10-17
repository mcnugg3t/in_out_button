# Plan review council

- Spec: C:\Users\caleb\Tools\in_out_button\docs\2026-10-17-usage-and-tasks-tabs-spec.md
- Prompt: prompt.txt (87910 chars)
- Framing question: Refute this plan. Where would it lose or overwrite the user's tasks.json across five machines, leak a credential into a log or message, hang the UI thread, or fail silently? Check the reload-before-write rule, the Codex process handling, the refresh and cooldown policy, and the Form1 integration against the attached code. Rank findings by severity and say which are blocking.
- Run started: 20261017-181714
- Outer timeout: 20 min

## Status

| Reviewer | Provider | Effort | Status | Seconds | Exit | Detail |
|---|---|---|---|---:|---:|---|
| Astra | OpenAI gpt-6-astra via codex | high | ok | 114.4 | 0 |  |

---

## Astra - ok - 114.4s

OpenAI gpt-6-astra via codex, effort high, file astra.md

(1) Five most confident objections, ranked

1. High — blocking: reload-before-write still loses edits.
Section 2.4 reloads the document, then upserts a whole task received from an older page state. If another machine changes its notes, saving the old dialog’s priority can erase those notes. It can also resurrect a deleted task. Modification time cannot reliably identify every content change, and another writer can intervene between checking and replacing. Atomic replacement prevents partial files; it does not prevent lost updates. Require revision checks and explicit conflict handling.

2. High — blocking: task saves are not coordinated with Git.
Form1.SetBusy disables existing buttons, not the Tasks page or its mutation handler. A task saved after StageAndCommitAsync runs “git add -A” can miss the commit while sign-out reports success. Writes during pull, rebase, or reset can be overwritten or disrupt Git. Refreshing only after SignInAllAsync also misses GitPullAllAsync and selected-repository operations. Coordinate task mutations with every workflow affecting the file, then reload after completion.

3. High — blocking: the credential-safety contract contradicts itself.
The general rule forbids response bodies, but the Codex reader explicitly forwards the response’s error.message into ProviderUsage.Message, which reaches the page and state-change log. That is uncontrolled provider text, potentially containing credentials or query parameters. ProcessRunner.RunLabeledAsync also retains raw output and exceptions, so reusing it would introduce another exposure path. Require fixed, safe error messages. Grepping accessToken references cannot establish that secrets never reach an output.

4. High — blocking: the process and UI execution contract is incomplete.
The 20-second cap does not specify cancellation of blocked reads/writes or cleanup after malformed output, early exit, initialization failure, or window closure. Killing is described after receiving id 2, leaving exceptional paths unclear. The existing ProcessRunner cannot directly implement this conversation: it lacks redirected stdin and combines stdout with stderr. Its timeout path also does not confirm termination. Separately, synchronous TaskStore operations reached from UI-thread messages can freeze the window on slow storage. Require cancellable asynchronous process operations, bounded output, cleanup on every exit, and file work outside the UI thread.

5. Medium — nonblocking for implementation, required before release: refresh rules have competing owners.
Form1 gates timer reads on tab visibility, but the page independently sends cache-bypassing refresh messages after resets. No visibility check is specified for those messages. “Once” has no defined lifetime across snapshots or navigation. Initial selection also has no explicit immediate read. Hidden pages can initiate reads, or a newly opened page can remain empty until a timer tick. Cooldown enforcement is specified correctly; request scheduling and freshness timestamps need one consistent definition.

(2) Assumptions to verify before building

- Every machine resolves the same intended repository and tracks tasks.json. Inventory all five machines’ roots, overrides, remote, branch, and ignore rules. Test opening Tasks before scanning finishes. An existing fallback clone is not automatically included in sign-out.
- Fallback storage will not strand tasks. Test missing clones followed by clone restoration, and malformed settings.json. SettingsStore.Load currently silently substitutes defaults, potentially switching away from TasksFile.
- Codex discovery, initialization order, and account context work everywhere. Test each installation type and inherited CODEX_HOME without printing credentials; verify initialization acknowledgment before sending subsequent requests.
- Git’s successful merges preserve task meaning. Exercise five disposable clones with concurrent same-task edits, delete/edit conflicts, and independent additions; inspect task contents, not just exit codes.

(3) Missing or untestable acceptance criteria

Require tests proving that stale saves cannot overwrite newer fields, deleted tasks are not resurrected, and same-timestamp changes are detected. Include disk-full, access-denied, interrupted replacement, unsupported document versions, and malformed external edits; failed saves must preserve both the file and the user’s draft.

Inject synthetic secrets into provider errors, stderr, and exceptions; assert their absence from logs and bridge messages. Test hung and malformed subprocesses, responsive UI, process cleanup, initial refresh, hidden tabs, cooldown expiry, and failure timestamps.

Specify bridge origin/navigation restrictions, message validation, and safe text rendering. Neither “no secrets” nor colorblind accessibility is established by the proposed grep or screenshots.

(4) First thing I would probe

Open a task editor, externally change that task’s notes and checklist, then save only a priority change from the old dialog. The specified whole-task upsert should expose lost updates immediately.

(5) Checked and cleared

The supplied code uses shell-free argument passing. Rejected pushes receive one rebase retry; conflicts are reported, with an abort attempt. The plan disables Anthropic redirects, never writes its credential file, maps actions to fixed URLs, and explicitly preserves cooldowns during fresh requests.

These are source-level checks. No implementation, runtime test, or five-machine verification was performed.

---

Three independent reads of the same prompt, not a verdict. Synthesis is Fable's
job: weigh the objections, discard the ones that do not apply, and decide what
changes before anything is built. Do not pass this file forward as a conclusion.

After judging, log each reviewer outcome in agent-cooperation/routing-log/log.jsonl.

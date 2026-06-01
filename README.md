# In / Out Button

Lightweight Windows C# applet for syncing git repositories at the start and end of a work session.

## What it does

- Lets you select folders to scan for git repositories.
- Recursively discovers repos under those folders.
- `Sign in` runs `git pull` for every discovered repo.
- `Sign out` rescans first, then runs `git add -A`, commits staged changes with `MM-DD-YY updates`, and runs `git push`.
- Logs full git output, flags failed commands and commands that time out, and marks successful commands that emitted Git warnings as `OK with warnings`.
- Provides selected-repo actions for quick fixes:
  - `Pull selected`
  - `Commit + sync selected`, which commits local changes, runs `git pull --rebase`, then pushes
  - `Discard + pull selected`, which runs `git reset --hard HEAD` and then pulls
- Can register itself to launch on Windows startup.
- Optionally syncs gitignored dataset folders to a cloud remote via [rclone](https://rclone.org), so large data follows you across machines alongside the code (see below).

Settings are stored in `%APPDATA%\InOutButton\settings.json`.

## Dataset sync (rclone)

Repos can opt in to syncing large, gitignored folders (datasets, model weights, etc.) to a cloud remote. This layers onto the existing buttons:

- **Sign in** runs `git pull`, then for each opted-in repo pulls its dataset folders down from the remote (`rclone copy` remote → local).
- **Sign out** pushes dataset folders up to the remote (`rclone copy` local → remote) *first*, then runs the git commit/push.
- Per-repo `Pull data` / `Push data` buttons run the same operations on the selected repo without touching git.

It always uses `rclone copy`, never `sync`/`bisync` — so it adds and overwrites but never deletes at the destination. This is safe across machines (a missing local folder just gets pulled, never propagated as a deletion), at the cost of no automatic conflict resolution if the same file is edited on two machines before syncing.

### Setup

1. Install [rclone](https://rclone.org/downloads/) and put it on `PATH`. The app probes `rclone --version` at startup; if it isn't found, the rclone controls are disabled. (After installing, a fresh login/reboot is needed so launched processes inherit the updated `PATH`.)
2. Configure a remote once per machine with `rclone config` (e.g. a Google Drive remote named `gdrive`). The OAuth token rclone stores is local to each machine and does not travel with your repos.
3. In the app's **Rclone** group, set the remote name, the remote root path (where everything is stored under the remote), and a timeout. Use **Test remote** (`rclone lsd <remote>:`) to confirm auth is healthy.

### Per-repo opt-in: `.rclone-sync.json`

A repo opts in by committing a `.rclone-sync.json` at its root listing the local folders to sync:

```json
{
  "folders": [
    { "local": "data" }
  ]
}
```

- `folders[].local` (required): path relative to the repo root. Should be in the repo's `.gitignore` (the app warns at scan time if it isn't).
- `folders[].remote` (optional): explicit remote subpath. If omitted, derived as `<RcloneRemoteRoot>/<repo-name>/<local>`.
- `remote` (optional, top-level): overrides the app's global remote for this repo. Usually omitted.

Because the file is committed, the sync instructions travel with the repo to every machine — no per-repo setup on a fresh clone. A repo without this file is git-only and untouched by the rclone machinery.

### Remote path layout

Each folder maps deterministically to `<remote>:<RcloneRemoteRoot>/<repo-name>/<local-folder>`. For example, a repo `rrl_alp_scraper` with `RcloneRemoteRoot = rock_river/projects` syncs its `data/` folder to `gdrive:rock_river/projects/rrl_alp_scraper/data`. rclone creates the destination path automatically; nothing needs to exist beforehand.

## Run

```powershell
dotnet run
```

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The published app will be in `bin\Release\net8.0-windows\win-x64\publish\`.

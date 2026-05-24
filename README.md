# In / Out Button

Lightweight Windows C# applet for syncing git repositories at the start and end of a work session.

## What it does

- Lets you select folders to scan for git repositories.
- Recursively discovers repos under those folders.
- `Sign in` runs `git pull` for every discovered repo.
- `Sign out` runs `git add -A`, commits staged changes with `MM-DD-YY updates`, then runs `git push`.
- Logs full git output, and flags failed commands and commands that time out.
- Provides selected-repo actions for quick fixes:
  - `Pull selected`
  - `Commit + sync selected`, which commits local changes, runs `git pull --rebase`, then pushes
  - `Discard + pull selected`, which runs `git reset --hard HEAD` and then pulls
- Can register itself to launch on Windows startup.

Settings are stored in `%APPDATA%\InOutButton\settings.json`.

## Run

```powershell
dotnet run
```

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

The published app will be in `bin\Release\net8.0-windows\win-x64\publish\`.

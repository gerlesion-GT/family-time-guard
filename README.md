# Family Time Guard

Simple screen-time rules for the kids' Windows computers (works on Windows Home):

- **Bedtime lock**: no more midnight gaming.
- **Daily game time** and **daily YouTube time**, with different rules for school days and weekends.
- **Homework is never blocked**: Office, school sites, Scratch and Roblox Studio don't count.
- Friendly warnings before time runs out, plus ideas for what to do next.
- **Parent options** on the child's PC, protected by a PIN: extra time, pause, resume.

👉 **Parents: see [docs/INSTALL-GUIDE.txt](docs/INSTALL-GUIDE.txt) and [docs/SETTINGS-GUIDE.txt](docs/SETTINGS-GUIDE.txt).**
Download the latest `FamilyTimeGuard.zip` from the **Releases** page on the right.

## Roadmap

| Phase | What | Status |
|---|---|---|
| 1 | Bedtime, game and YouTube budgets, warnings, parent PIN, activity log | ✅ this version |
| 2 | Telegram remote: alerts, "ask for more time" button, daily summary, commands from your phone | planned |
| 3 | Chrome extension: per-tab YouTube tracking, website lists, no incognito, Restricted Mode | planned |
| 4 | Earn game time with learning apps, weekly report | planned |

## How it works (technical)

One program, `FamilyTimeGuard.exe` (.NET 10, self-contained), runs in two roles:

- **Guard service** (`--service`, runs as SYSTEM): every 2 seconds it finds child sessions, looks for game processes,
  applies the rules from `src/FamilyTimeGuard.Core/RuleEngine.cs`, closes games, blocks YouTube through the hosts file,
  and logs the session off at bedtime. It also turns off browsers' DNS-over-HTTPS (via policy) so the block can't be skipped.
- **Tray helper** (`--agent`): started by the service inside the child's session with a protected process DACL,
  so a standard user can't end it from Task Manager. It reports the foreground window title
  (how YouTube and web games are detected) and shows the status icon, warnings and bedtime countdown.
  It talks to the service over the named pipe `\\.\pipe\FamilyTimeGuard`.

Data lives in `C:\ProgramData\FamilyTimeGuard` (administrators and SYSTEM only):
`config.json`, `state.json`, `usage\<child>\<date>.json`, `logs\activity-<date>.csv`, `logs\service.log`.

```
src/FamilyTimeGuard.Core   rules, schedule, config, protocol (cross-platform, unit-tested)
src/FamilyTimeGuard        Windows service + tray helper + command-line tools
tests/                     unit tests
installer/                 Install.cmd / Install.ps1 / Uninstall.*
.github/workflows          builds on Windows, runs tests and an install/uninstall smoke test, publishes releases
```

Build locally: `dotnet test tests/FamilyTimeGuard.Tests` and
`dotnet publish src/FamilyTimeGuard -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.

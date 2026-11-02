# AGENTS.md

DevCleaner: a .NET 10 tool that finds and safely cleans developer disk junk. Avalonia GUI + CLI over a shared core.

## Layout
- `src/DevCleaner.Core` – engines, providers, rules, search, duplicates, disk usage. **No OS-specific APIs here.**
- `src/DevCleaner.Platform.Windows` – Recycle Bin (P/Invoke), Restart Manager, registry. Assembly is `[SupportedOSPlatform("windows")]`; guard callers with `OperatingSystem.IsWindows()`.
- `src/DevCleaner.Platform.Unix` – placeholder (no trash yet).
- `src/DevCleaner.Infrastructure` – JSON persistence (`DataStore`) + composition root (`AppHost`).
- `src/DevCleaner.Gui` – Avalonia MVVM (CommunityToolkit.Mvvm). `src/DevCleaner.Cli` – System.CommandLine + Spectre.Console.
- `tests/DevCleaner.Tests` – xUnit; file-system tests use real temp folders (`TempTree`).

## Commands
```powershell
dotnet build DevCleaner.slnx
dotnet test tests\DevCleaner.Tests
dotnet run --project src\DevCleaner.Cli -- scan --root <dir> --json
dotnet run --project src\DevCleaner.Gui
```
- If the GUI is running, its exe is locked: build with `-o $env:TEMP\dc-gui-check` instead of closing the user's app.
- Set `DEVCLEANER_HOME=<temp dir>` to isolate settings/stats/history during manual tests.

## Conventions
- Warnings are errors; central package versions in `Directory.Packages.props`; repo `nuget.config` uses nuget.org only.
- Keep Avalonia ≥ 11.3.22 (older pulls a vulnerable `Tmds.DBus.Protocol`).
- New cleanup targets go into `src/DevCleaner.Core/Rules/builtin-rules.json` (data, not code). Use `markers`+`targets` for project folders, `paths` for fixed locations.
- New cleanup sources: implement `ICleanupProvider` (+ `ICustomCleaner` for non-file items) and register in `AppHost.Create`.
- Git logic lives in `Core/Git/GitInspector` (shells out to `git`, cached). `ScanEngine` annotates every project finding with repo dirty state; `GitIgnoredProvider` reports only whole folders an ignore rule matches directly.
- GUI uses compiled bindings: every template needs `x:DataType`. Keep `AutomationProperties.Name` on nav items/buttons (used by screenshot automation and screen readers).

## Safety rules (do not weaken)
- All deletes go through `CleanEngine`; never bypass `SafetyGuard` or exclusions.
- GUI deletes permanently by default ("Delete permanently" checkbox; uncheck for Recycle Bin). CLI defaults to Recycle Bin; `--permanent` is opt-in. Always confirm before cleaning.
- Never follow symlinks/junctions; rule targets must be plain folder names.
- Registry changes must be preceded by a `.reg` backup.
- Never run a real clean against the user's machine in tests or automation—use `--dry-run` or temp folders.

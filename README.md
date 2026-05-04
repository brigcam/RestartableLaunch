# RestartableLaunch

RestartableLaunch is a tiny Windows tray manager that makes other programs
eligible for Windows app restart. It starts target processes, stays alive in the
tray while they are running, and registers itself with `RegisterApplicationRestart`.

This is useful for applications that do not register themselves as restartable.

## Usage

```powershell
RestartableLaunch.exe "C:\Path\App.exe" [arguments...]
```

Example:

```powershell
RestartableLaunch.exe "C:\Program Files\Aegisub\aegisub.exe" "C:\Subtitles\episode.ass"
```

An explicit separator is also accepted:

```powershell
RestartableLaunch.exe -- "C:\Path\App.exe" [arguments...]
```

The first non-option argument is treated as the executable. Everything after it
is passed to the wrapped application.

## Tray UI

Run the executable without arguments, or use `--gui`, to open or reopen the tray
manager window.

Use `--list` or `-l` to print the monitored applications to the console:

```powershell
RestartableLaunch.exe --list
```

The GUI window also lists the currently monitored applications and their command
lines.

## Notes

- The tray manager must remain running for Windows to restart it.
- If a wrapped application is closed manually, it is removed from the monitored list.
- Unsaved application state is still the responsibility of the wrapped app.
- Virtual desktop placement is not restored.

## Build

```powershell
dotnet publish .\src\RestartableLaunch\RestartableLaunch.csproj -c Release
```

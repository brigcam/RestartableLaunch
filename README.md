# RestartableLaunch

RestartableLaunch is a tiny Windows tray manager that makes other programs
eligible for Windows app restart. It starts target processes, stays alive in the
tray while they are running, and registers itself with `RegisterApplicationRestart`.

This is useful for applications that do not register themselves as restartable.

## Usage

```powershell
RestartableLaunch.exe "C:\Path\Object"
```

Example:

```powershell
RestartableLaunch.exe "C:\Subtitles\episode.ass"
```

RestartableLaunch opens the object with its default Explorer action and monitors
the launched process when Windows returns one.

To launch a specific executable with arguments instead, use `--exec`:

```powershell
RestartableLaunch.exe --exec "C:\Path\App.exe" [arguments...]
```

## Tray UI

Run the executable without arguments, or use `--gui`, to open or reopen the tray
manager window.

Use `--list` or `-l` to print the monitored applications to the console:

```powershell
RestartableLaunch.exe --list
```

The GUI window also lists the currently monitored applications and their command
lines.

## Explorer context menu

RestartableLaunch can be added to Explorer's context menu for files, folders,
and drives. The menu command should invoke:

```powershell
RestartableLaunch.exe "%1"
```

This opens the selected object with its default Explorer action and records that
default-open request for restart.

## Virtual desktops

RestartableLaunch always tries to remember the virtual desktop of each monitored
window and move it back there during restore. This uses Windows shell COM
virtual desktop APIs directly. If Windows refuses a move, the application still
launches normally.

## Notes

- The tray manager must remain running for Windows to restart it.
- If a wrapped application is closed manually, it is removed from the monitored list.
- Unsaved application state is still the responsibility of the wrapped app.
- Virtual desktop placement is best effort because Windows may reject desktop
  moves after system updates or when the saved desktop no longer exists.

## Build

```powershell
dotnet publish .\src\RestartableLaunch\RestartableLaunch.csproj -c Release
```

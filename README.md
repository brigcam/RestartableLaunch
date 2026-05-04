# RestartableLaunch

RestartableLaunch is a tiny Windows tray manager that restores selected programs
at user logon. It starts target processes, stays alive in the tray while they are
running, and saves enough session state to relaunch them later.

This is useful for applications that do not restore themselves after sign-in.

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

To launch a specific executable with arguments instead, use `--run` or `-r`:

```powershell
RestartableLaunch.exe --run "C:\Path\App.exe" [arguments...]
```

## Tray UI

Run the executable without arguments, or use `--gui`, to open or reopen the tray
manager window.

Console output lives in the companion CLI executable:

```powershell
RestartableLaunchCLI.exe list
```

The GUI executable does not print to a console.

The GUI window lists the currently monitored applications and their command
lines. It also includes checkboxes to start RestartableLaunch at user logon and
to register or unregister RestartableLaunch in Explorer's context menu for the
current user.

Use **Add open window...** to choose a currently open window and make it
restartable. RestartableLaunch reads the owning process command line, remembers
the window position and virtual desktop when available, and starts monitoring it.
Select one or more monitored entries and use **Remove**, the context menu, or
the Delete key to remove entries that were added by mistake.

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
window and move it back there during restore. It first uses
`VirtualDesktopAccessor.dll` for the undocumented Windows virtual desktop
operations, then falls back to the Windows shell COM APIs. If Windows refuses a
move, the application still launches normally.

## Notes

- The tray manager must remain running for Windows to restart it.
- To restore apps after logon, enable the startup checkbox in the GUI.
- During shutdown or logoff, RestartableLaunch freezes the monitored list,
  saves it immediately, and ignores process-exit events so Windows closing apps
  cannot accidentally remove them from the next restore.
- If a wrapped application is closed manually, it is removed from the monitored list.
- Unsaved application state is still the responsibility of the wrapped app.
- Virtual desktop placement is best effort because Windows may reject desktop
  moves after system updates or when the saved desktop no longer exists.
- `VirtualDesktopAccessor.dll` is bundled from
  [Ciantic/VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor),
  MIT licensed. The license text is included under
  `third-party/VirtualDesktopAccessor/LICENSE`.

## Build

```powershell
dotnet publish .\src\RestartableLaunch\RestartableLaunch.csproj -c Release
```

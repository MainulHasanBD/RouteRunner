# RoutRunner

Windows tray supervisor for **9Router** (`:20128`) and **OmniRoute** (`:20129`), both running
under a full administrator token.

One window. One tray icon. Both routers started in a real PowerShell host, both verified to be
installed before anything spawns, both killed cleanly when you stop them.

```
9router  --port 20128      ->  powershell.exe -> 9router.cmd  -> node   (elevated)
omniroute --port 20129     ->  powershell.exe -> omniroute.cmd -> node   (elevated)
```

---

## Install (share this with anyone)

Unzip `RoutRunner-<version>-win-x64.zip` anywhere and run:

```powershell
.\install.ps1
```

That is the whole install. It:

1. raises one UAC prompt,
2. copies `RoutRunner.exe` to `C:\Program Files\RoutRunner`,
3. registers two scheduled tasks with `RunLevel=HighestAvailable`:
   - `RoutRunner` — logon trigger, starts minimized, **no UAC at logon**,
   - `RoutRunner.Launch` — on demand, **no UAC when you open the app**,
4. adds a Start-menu shortcut and an Add/Remove Programs entry,
5. starts it.

Switches:

```powershell
.\install.ps1 -InstallDir D:\Tools\RoutRunner     # different location
.\install.ps1 -NoStartupTask                      # no logon task
.\install.ps1 -NoLaunch                           # install only, do not start
```

Uninstall (also wired into Add/Remove Programs, and `/quiet`-friendly):

```powershell
"C:\Program Files\RoutRunner\uninstall.ps1"            # remove app, tasks, shortcut
"C:\Program Files\RoutRunner\uninstall.ps1" -Purge     # also delete settings + logs
```

The payload is **self-contained**: nothing has to be installed on the target machine — no .NET
runtime, no build tools. Node.js and the two npm packages still have to be there (the app
preflights and offers to `npm install -g` them).

## Elevation

RoutRunner must hold an administrator token, because 9Router and OmniRoute are its children and
inherit whatever token it has. Launch order when the exe starts unelevated:

1. `RoutRunner.Launch` task exists -> started silently, elevated, no prompt. (Normal case after
   install.)
2. No task -> one UAC prompt via `runas`.
3. UAC declined -> a dialog says nothing was started, process exits.

The window subtitle and the preflight log line both state the token. Each card shows `admin` or
`user token` next to the version once the service answers — that value is read back from the
child's real token (`TokenElevation`), not assumed.

## What it does on launch

1. **Preflight** — resolves `9router.cmd` / `omniroute.cmd` on `PATH` (falls back to
   `%APPDATA%\npm`), reads the version out of each package's `package.json`, and probes
   `node -v`. Nothing starts if either package is missing; the banner turns red and shows an
   **install** button that runs `npm install -g <pkg>` with live output in the log.
2. **Port check** — if the port already has a listener, RoutRunner will not start a duplicate.
   It marks the service `running (external)` and offers **Kill** to take the port.
3. **Start** — spawns each service in a hidden PowerShell host, pins the whole process tree to
   a Windows **job object** (kill-on-close), and proves readiness by TCP connect on
   `127.0.0.1:<port>` — no fixed sleeps.
4. **Minimize to tray** — closing the window keeps everything running. The icon carries the
   state: green both up, amber partial, red failure, grey all down.

Launching the exe again while it is resident does not open a second copy — the running instance
raises its window (named event, no dialog).

## Tray menu

| item | effect |
|---|---|
| 9Router / OmniRoute | live state lines (disabled, read-only) |
| Start all / Stop all | both at once |
| Services ▸ | start/stop one |
| Dashboards ▸ | open `http://localhost:<port>` |
| Start services on launch | auto-start on app launch |
| Auto-restart crashes | watchdog: up to 5 restarts, 5s x attempt backoff |
| Start with Windows (elevated task) | registers/removes both scheduled tasks |
| Exit | stops both trees, then quits |

## Window

- Per-service cards: state, port, install path, version, admin/user badge, Start / Stop / Open / Kill.
- Editable ports (applied on next start).
- Live log with per-service colouring, written to `%APPDATA%\RoutRunner\logs\*.log`
  (4 MB rotation), display capped at 2400 lines.

## Stop semantics

`Stop` terminates the job object -> PowerShell, cmd shim, node server and any grandchildren die
together. Port releases within a second. If a job terminate does not land, it falls back to
`taskkill /PID <pid> /T /F`.

If a listener was **not** started by RoutRunner, stopping requires the **Kill** button and a
confirmation — the app never silently touches a process it did not create.

## Files

```
C:\Program Files\RoutRunner\RoutRunner.exe      installed payload
C:\Program Files\RoutRunner\install.ps1         kept for repair/reinstall
C:\Program Files\RoutRunner\uninstall.ps1
%APPDATA%\RoutRunner\settings.json              ports, extra args, flags
%APPDATA%\RoutRunner\logs\9router.log
%APPDATA%\RoutRunner\logs\omniroute.log
```

`settings.json` is written on the first change. Defaults:

```json
{
  "NineRouterPort": 20128,
  "OmniRoutePort": 20129,
  "NineRouterArgs": "--no-browser",
  "OmniRouteArgs": "--no-open",
  "AutoRestart": true,
  "AutoStartServices": true,
  "StartMinimized": false,
  "MinimizeOnClose": true
}
```

The extra-args defaults suppress the browser each CLI opens on boot — RoutRunner's **Open**
button is the one that opens a dashboard. Clear them if you want the old behaviour.

## Build

```powershell
.\build.ps1                 # dist\RoutRunner.exe, framework-dependent single file (~250 KB)
.\build.ps1 -SelfContained  # dist\RoutRunner.exe, runtime baked in (~63 MB)
.\make-package.ps1          # dist\RoutRunner-<version>-win-x64.zip  <- the shareable one
```

`make-package.ps1` publishes the self-contained single-file payload, drops it next to
`install.ps1`, `uninstall.ps1` and this README, and zips the folder.
Requires the .NET 8 SDK (present: 8.0.413).

## Notes

- OmniRoute's own first boot prints: `listening on 0.0.0.0 with NO API-key requirement`.
  If that host is on a shared network, set `REQUIRE_API_KEY=true` in
  `%USERPROFILE%\.omniroute\.env`, or set `OMNIROUTE_SERVER_HOST=127.0.0.1`.
- OmniRoute's cold start is ~13-25 s on this machine; the card sits on `starting` until the port
  actually answers. Readiness timeout is 90 s for 9Router, 150 s for OmniRoute.
- The `.ps1` files are ASCII-only on purpose. PowerShell 5.1 reads BOM-less UTF-8 as ANSI, and a
  UTF-8 em dash decodes to `”`, which PowerShell honours as a quote — one non-ASCII character
  can flip quote parity and break the parse. Keep them ASCII.

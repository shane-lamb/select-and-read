# Driving the Windows VM from the Mac

Everything here exists so that runtime testing can be done from the Mac shell without
clicking around inside the VM. `deploy.sh` is the entry point:

```bash
./tests/vm/deploy.sh
```

That builds `win-arm64`, pushes to the guest, and prints the OCR fixture results. With
`--run` it starts the app itself in the interactive session instead of running fixtures,
so the tray icon, hotkeys and audio are live:

```bash
./tests/vm/deploy.sh --run
```

`--run` deliberately waits for the process to appear before reporting success. The
single-file exe spends a moment unpacking, so an immediate process list can show nothing —
and the app itself has no window, with its tray icon tucked behind the taskbar's `^` chevron
on a stock Windows 11. "It didn't start" is almost always one of those two.

`--no-build` skips the publish and reuses the existing binary; it combines with `--run`.
Either mode kills a stranded `SelectAndRead.exe` first — see trap 6 — because a running
binary also cannot be overwritten by the copy. The other modes:

```bash
./tests/vm/deploy.sh --stop                 # kill the app in the guest
./tests/vm/deploy.sh --shot /tmp/guest.png  # screenshot the guest, overlay and all
./tests/vm/deploy.sh --exec 'query session' # run one command, print its output
```

Prefer `--exec` over calling `vmrun` by hand. Every direct `vmrun` invocation needs
`-vp` and `-gp` on the command line, which puts both passwords into your shell history;
`--exec` reads them from the keychain instead, and applies the quoting rules in traps 1–4
for you.

## Before any of it works

**Four prerequisites, each of which fails in a way that looks like something else.**

**Credentials, from the login keychain.** Two secrets, because the VM is encrypted —
Windows 11 requires a vTPM and VMware requires an encrypted config to hold one, so
`vmx.encryptionType = "partial"` and *every* `vmrun` call needs `-vp` as well as
`-gu`/`-gp`. Store them once:

```bash
security add-generic-password -s sar-vm-encryption -a vm -w
security add-generic-password -s sar-vm-guest -a kryte -w
```

`-w` with no value prompts, so neither reaches shell history. Both passwords still land in
`vmrun`'s argv and are briefly visible in `ps`, which is accepted for a local test VM and is
why `deploy.sh` must never gain `set -x`.

**Only the service name (`-s`) matters.** `deploy.sh` looks each secret up by service alone,
so the account (`-a`) is a free label and need not be the guest username or the VM's name —
tying the lookup to `$VM_USER` would break the moment that changed. A `security
find-generic-password` that passes `-a` will report an item missing when it is only labelled
differently, which looks exactly like credentials that were never stored.

**VMware Tools must be installed and running in the guest.** Every guest operation goes
through it. Without it `vmrun` hangs for about four minutes and then reports
`VIX_E_TOOLS_NOT_RUNNING`, and `checkToolsState` says `unknown` rather than anything
diagnostic. A VM carried over from another hypervisor will not have it. Install it from
Fusion's **Virtual Machine > Install VMware Tools**, run `setup.exe` from the mounted CD,
and reboot.
**1b. `Register-ScheduledTask` can queue forever without ever dispatching.** The task
reports `LastTaskResult 0` and `Status: Queued`, nothing runs, and no error appears anywhere
including the TaskScheduler event log. `deploy.sh --run` fails the same way, so this looks
exactly like the app refusing to start. Creating the task with `schtasks` and `/it` instead
dispatches immediately:

```bash
prlctl exec "Windows 11" cmd /c 'schtasks /create /tn SarVis /tr "C:\sar-test\run-highlight.cmd" /sc once /st 00:00 /ru shane /it /f'
prlctl exec "Windows 11" cmd /c 'schtasks /run /tn SarVis'
```

Give the task a `.cmd` wrapper rather than a quoted powershell command line: quotes passed
through `prlctl exec` and then through the task's own argument parsing arrive mangled, and
the task then runs and does nothing at all.

**2. `prlctl capture` returns solid black while a fullscreen topmost GDI window is up.**
It cannot photograph the overlay. To see what the overlay actually looks like, run
`SelectAndRead.exe --freeze-to shot.png` from a *second* process while the overlay is
displayed — the app's own capture path works fine where the VM's framebuffer grab does not.

**The guest must be started from the Fusion UI, logged in, and unlocked.** Not merely
powered on: `-interactive` needs a live console session to target, and a lock screen makes
every interactive command fall back to a session that cannot draw.

**The .NET 10 Desktop Runtime must be installed in the guest**, because `deploy.sh` publishes
framework-dependent to keep the copy small. Nothing in this harness will tell you it is
missing — a `WinExe` with no runtime reports that in a message box, and traps 3 and 5 mean
you would see neither the box nor any output. Install it with:

```bash
./tests/vm/deploy.sh --exec 'winget install Microsoft.DotNet.DesktopRuntime.10 --silent --accept-package-agreements --accept-source-agreements --disable-interactivity'
```

Check it with `--exec 'C:\Progra~1\dotnet\dotnet.exe --list-runtimes'`, which needs both
`Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App`. Use the 8.3 path: `Program Files`
has a space in it, and trap 4 rules out quoting it.

## Ten things that will waste your afternoon if you don't know them

These were all measured on VMware Fusion 26 against Windows 11 ARM64. None are obvious, and
each produced a convincing false diagnosis first.

**1. `runProgramInGuest` does not split its argument string into argv.** It hands the whole
blob to the program, so only something that re-parses its own command line will cope.
`cmd.exe /c` does. `powershell.exe` does not, and fails with exit 1 every single time, with
no output anywhere to say why. Always go through `cmd.exe`:

```bash
vmrun ... runProgramInGuest "$VMX" -interactive 'C:\Windows\System32\cmd.exe' '/c powershell -NoProfile -File C:\sar-test\x.ps1 > C:\Users\Public\x.log 2>&1'
```

**2. vmrun appends a trailing space to that argument string.** Usually invisible — a
redirect target absorbs it, and most programs ignore it. It is fatal to a PowerShell script
with positional parameters, which binds the space and dies with `Cannot convert value " " to
type "System.Int32"`, and to `taskkill /F`, which reports `Invalid argument/option - ' '`.
Terminate the command so the space lands somewhere harmless:

```
/v:on /c <command> > C:\Users\Public\out.log 2>&1 & exit /b !ERRORLEVEL!
```

`/v:on` is what makes `!ERRORLEVEL!` expand at run time rather than parse time. This is the
shape `deploy.sh`'s `guest()` helper uses for everything, and it absorbs the trailing space
while still propagating the guest's real exit code.

**3. There is no stdout channel from the guest.** `runProgramInGuest` returns an exit code
and nothing else. Worse, `SelectAndRead.exe` invoked bare prints *nowhere at all*:
`AttachToParentConsole` writes only to a parent console it can attach to or to an existing
redirect, and vmrun gives it neither. Redirect inside the guest and copy the file back —
`--exec` does this for you.

**4. Embedded double quotes are mangled.** A command containing `"` comes back as
`The filename or extension is too long.` Write guest command lines without them; if a path
has spaces, use its 8.3 form or move the file.

**5. Only `-interactive` can draw.** Without it a guest command lands in session 0 — as the
real user, with the real profile, but on a 1024x768 desktop with no console window station,
where `--freeze-to` fails with `Screen capture failed.` and exit 3. With `-interactive` it
lands in the logged-on session (`SessionId` matches explorer's, `WindowStation=Console`) and
everything works. Session 0 is still genuinely useful for `--settings-metrics`, whose whole
point is a cramped display: it reports a 1024x768 working area there against 2048x1440 in
the real session.

**6. Kill leftover `SelectAndRead.exe` between runs.** A stranded overlay from a previous
run is on screen when the next run captures, so run N captures run N−1's dim wash. The
symptom is a crop that gets darker with each attempt. Use `--stop`, which kills by pid via
`killProcessInGuest` — `taskkill` through `cmd` walks straight into trap 2.

**7. Hide the driving console.** Always pass `-WindowStyle Hidden` to a driving PowerShell,
or its console window covers the desktop and the app faithfully captures *that* instead of
your test content. A crop full of your own script's output is this, not a bug.

**8. Never start the VM with `vmrun start`.** It works, but it spawns a `vmware-vmx` that
the Fusion window cannot attach to, so the VM shows as locked and you cannot open it for the
rest of the session. `deploy.sh` deliberately refuses to start a stopped VM and tells you to
use the Fusion UI instead. A VM started from the UI is fully drivable by `vmrun`.

**9. `runScriptInGuest` hangs.** It looks like the tidy way to run a multi-line script and
never returns — killed after eight minutes with no output. Copy a `.ps1` in and run it
through `cmd /c powershell -File` instead.

**10. A blanked guest display photographs as solid black.** `captureScreen` grabs the VM's
framebuffer, so once Windows powers the display down it returns a 69-byte all-black PNG,
indistinguishable from the app rendering black. Waking it needs a *real* input event:
`SetCursorPos` and `[Windows.Forms.Cursor]::Position` both move the pointer without
resetting the idle timer. `--shot` runs `wake-display.ps1` first for exactly this reason.
Note this affects only the hypervisor's framebuffer grab; the app's own `--freeze-to` goes
through `BitBlt` on the desktop and is unaffected.

## Three things this harness can do that are easy to miss

Worth knowing about rather than working around out of habit.

**Screenshots work, including over the overlay.** `--shot` photographs a fullscreen topmost
GDI window correctly — the crosshair, reticle and border of SPEC §2.2 are all visible in the
resulting PNG. Use `--freeze-to` when the question is what the app *itself* sees, since that
goes through its own capture path; use `--shot` when the question is what is on screen. The
one thing that comes back black is a blanked display — trap 10.

**Guest exit codes propagate.** `vmrun` reports the guest program's real exit status, so
`--settings-metrics` and the `--ocr-file` failure codes are usable as checks directly from
the Mac shell, and `--exec` passes them through.

**Guest commands run as the real user.** Even non-interactively, `WhoAmI` is the logged-on
account and `%APPDATA%` is the real profile, and DPAPI `CurrentUser` round-trips in both
sessions. An API key saved in Settings is therefore visible to a `--read-file` driven from
here, so the cloud path can be exercised end to end without a human at the guest.

## Cost of the file transfer

Everything is copied file by file with `CopyFileFromHostToGuest`; there are no shared
folders to configure, and no UNC paths. The price is throughput: about **1.7 MB/s**, cold or
warm, with no incremental mode. The 19 fixtures and scripts take about 10 s together, and the
fixture run itself about 5 s.

That throughput is why `deploy.sh` publishes **framework-dependent**: bundling .NET makes the
exe 148 MB and the copy 85 s, against **25 MB and ~16 s** without it. A full build-and-deploy
is about 45 s rather than a minute and a half. The release build stays self-contained, so
what this does not exercise is the shipped binary's own packaging — its single-file
extraction, and the longer startup that comes with it.

Because that one copy dominates, `deploy.sh` stamps the exe's size and mtime in the guest
and skips the copy when it matches, which is what makes a `--no-build --run` iteration take
seconds rather than a minute and a half. `--force-copy` overrides it.

## Driving mouse input

`drive-capture.ps1` is a worked example: it drags a rectangle over the overlay and saves
the crop. The crop's dimensions must equal the dragged rectangle exactly — that equality is
the whole coordinate contract in SPEC §4.

The script must call `SetProcessDpiAwarenessContext(-4)` (per-monitor v2) before moving the
cursor, so its coordinates are physical pixels and match what the app sees. This is not
theoretical any more: the VM runs at **200% scaling**, so a DPI-unaware process is told the
screen is 1024x768 when it is really 2048x1536, and a drag driven without that call lands at
half the intended coordinates.

```powershell
[void][M]::SetProcessDpiAwarenessContext([IntPtr](-4))
[void][M]::SetCursorPos($x1, $y1)
[M]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # left down
# …move in steps…
[M]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # left up
```

## What this harness cannot test

`typeKeystrokesInGuest` is unavailable: it fails with `Insufficient permissions in the host
operating system`, so synthetic keystrokes cannot be injected from the Mac and the global
hotkeys still have to be exercised by hand in the guest. Mouse input is unaffected, since
that is driven from inside the guest by `drive-capture.ps1` rather than by vmrun.

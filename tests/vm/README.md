# Driving the Windows VM from the Mac

Everything here exists so that runtime testing can be done from the Mac shell without
clicking around inside the VM. `deploy.sh` is the entry point:

```bash
./tests/vm/deploy.sh
```

That builds `win-arm64`, pushes to the guest, and prints the OCR fixture results.

## Four things that will waste your afternoon if you don't know them

These were all learned the hard way during bring-up. None are obvious, and each produced a
convincing false diagnosis first.

**1. `prlctl exec` runs as SYSTEM, in session 0.** It cannot draw, cannot receive input,
and has its own `%APPDATA%`. It is fine for `--ocr-file`, and useless for the overlay,
hotkeys or audio. For anything interactive, use `launch-interactive.ps1`, which registers a
scheduled task with an `Interactive` principal so the process lands in the logged-on user's
session:

```bash
prlctl exec "Windows 11" powershell -NoProfile -ExecutionPolicy Bypass \
  -File 'C:\sar-test\launch-interactive.ps1' \
  -Command 'powershell.exe' -Arguments '-NoProfile -WindowStyle Hidden -File C:\sar-test\my-test.ps1'
```

Always pass `-Arguments`, even when empty — `New-ScheduledTaskAction` fails without it.
And always pass `-WindowStyle Hidden` to the driving PowerShell: otherwise its console
window covers the desktop, and the app faithfully captures **that** instead of your test
content. A crop full of black with your own script's output in it is this, not a bug.

**2. `prlctl capture` returns solid black while a fullscreen topmost GDI window is up.**
It cannot photograph the overlay. To see what the overlay actually looks like, run
`SelectAndRead.exe --freeze-to shot.png` from a *second* process while the overlay is
displayed — the app's own capture path works fine where the VM's framebuffer grab does not.

**3. Kill leftover `SelectAndRead.exe` between runs.** A stranded overlay from a previous
run is on screen when the next run captures, so run N captures run N−1's dim wash. The
symptom is a crop that gets darker with each attempt.

```bash
prlctl exec "Windows 11" cmd /c 'taskkill /IM SelectAndRead.exe /F'
```

**4. Only Desktop, Documents and Downloads are shared** by default, so staging goes through
`~/Downloads`. Inside the guest the share is `\\Mac\Home\...`; the `Z:` mapping shown by
`net use` belongs to the interactive user and is not visible to SYSTEM. Use `robocopy`
rather than `copy` — `copy` with a wildcard against the share fails with a confusing
"cannot find the path specified".

## Driving mouse input

`drive-capture.ps1` is a worked example: it drags a rectangle over the overlay and saves
the crop. The crop's dimensions must equal the dragged rectangle exactly — that equality is
the whole coordinate contract in SPEC §4.

The script must call `SetProcessDpiAwarenessContext(-4)` (per-monitor v2) before moving the
cursor, so its coordinates are physical pixels and match what the app sees:

```powershell
[void][M]::SetProcessDpiAwarenessContext([IntPtr](-4))
[void][M]::SetCursorPos($x1, $y1)
[M]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # left down
# …move in steps…
[M]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # left up
```

## What this harness cannot test

The VM runs a single 3840×2024 display at 100% scaling, so it does not exercise the app at
any display scaling above 1:1 — the highest-risk remaining part of the design. That one is
worth testing here rather than on real hardware: change the guest's display scaling to
150%, re-run the drag, and confirm the crop still matches the dragged rectangle exactly.

See SPEC §13.4 for the rest of what still needs a real machine.

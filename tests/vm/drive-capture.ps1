# Drives a real mouse drag over the selection overlay and saves the resulting crop.
#
# Run it through launch-interactive.ps1 with -WindowStyle Hidden, otherwise this script's
# own console window covers the desktop and the app captures that instead of your content:
#
#   prlctl exec "Windows 11" powershell -NoProfile -ExecutionPolicy Bypass `
#     -File 'C:\sar-test\launch-interactive.ps1' `
#     -Command 'powershell.exe' `
#     -Arguments '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\sar-test\drive-capture.ps1'
#
# The crop's dimensions should equal ($X2-$X1) x ($Y2-$Y1) exactly. That equality is the
# whole coordinate contract in SPEC 4, so it is the single most valuable assertion here.

param(
  [int]$X1 = 225, [int]$Y1 = 365,
  [int]$X2 = 1260, [int]$Y2 = 665,
  [string]$Out = "C:\sar-test\crop.png"
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
"@

# Per-monitor v2, so these coordinates are physical pixels and match what the app sees.
# Without this the script's coordinates would be DPI-virtualised on a scaled display and
# the drag would land somewhere else entirely.
[void][M]::SetProcessDpiAwarenessContext([IntPtr](-4))

$p = Start-Process -FilePath "C:\sar-test\SelectAndRead.exe" `
     -ArgumentList "--capture-to", $Out -PassThru `
     -RedirectStandardError "C:\sar-test\cap-err.log" `
     -RedirectStandardOutput "C:\sar-test\cap-out.log"

Start-Sleep -Seconds 5          # let the overlay come up

[void][M]::SetCursorPos($X1, $Y1)
Start-Sleep -Milliseconds 500
[M]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # left down

for ($i = 1; $i -le 20; $i++) {
  [void][M]::SetCursorPos([int]($X1 + ($X2-$X1)*$i/20), [int]($Y1 + ($Y2-$Y1)*$i/20))
  Start-Sleep -Milliseconds 60
}

Start-Sleep -Milliseconds 400
[M]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # left up

$p.WaitForExit(20000) | Out-Null

# Wakes a blanked guest display so the hypervisor has something to photograph.
#
# `vmrun captureScreen` grabs the VM's framebuffer, so once Windows has powered the display
# down it returns a 69-byte solid-black PNG. That looks exactly like the app rendering
# black, which is the single most misleading result this harness can produce, so `--shot`
# calls this first.
#
# It has to be a real input event. SetCursorPos and [Windows.Forms.Cursor]::Position both
# move the pointer without resetting the display idle timer, and leave the screen dark.
# mouse_event is what actually counts as input.

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
}
"@

# Relative move out and back, so the pointer ends where it started. Small enough not to
# disturb an overlay drag that is already in progress.
[W]::mouse_event(0x0001, 8, 8, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
[W]::mouse_event(0x0001, -8, -8, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 700

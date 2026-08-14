# Displays a PNG at 1:1, at an exact physical-pixel position, in a borderless window.
#
# Exists so the word mark can be photographed over content whose word positions are known
# rather than guessed: show a fixture here at (X,Y), read the same fixture with
# `--read-local <png> --overlay X,Y`, and every mark must land on its own word. Anything
# off by the crop origin, by the upscale factor, or by a DPI conversion is then obvious in
# the screenshot instead of being a plausible-looking near miss.

param(
  [Parameter(Mandatory=$true)][string]$Path,
  [int]$X = 200,
  [int]$Y = 300
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class S {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
}
"@

# Per-monitor v2 before any window exists, so X and Y are physical pixels and the image is
# not resampled - the same reason the app itself is manifested that way (SPEC 4).
[void][S]::SetProcessDpiAwarenessContext([IntPtr](-4))

$image = [System.Drawing.Image]::FromFile($Path)

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition   = 'Manual'
$form.AutoScaleMode   = 'None'
$form.ShowInTaskbar   = $false
$form.TopMost         = $true

$box = New-Object System.Windows.Forms.PictureBox
$box.Image    = $image
$box.SizeMode = 'AutoSize'
$box.Location = New-Object System.Drawing.Point(0, 0)
$form.Controls.Add($box)

# Positioned and sized with SetWindowPos rather than through Bounds, so no WinForms scaling
# can move it away from the coordinates the mark will be computed against.
$form.Add_Shown({
  [void][S]::SetWindowPos($form.Handle, [IntPtr]::Zero, $X, $Y, $image.Width, $image.Height, 0x0004)
})

[System.Windows.Forms.Application]::Run($form)

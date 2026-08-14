# Photographs the word mark during a real reading.
#
# The mark's geometry is checked exactly by --highlight-metrics and its sequencing by
# --read-local, but neither shows that it is actually drawn. This does, by displaying a
# fixture at a known position and reading that same fixture with a matching origin, so every
# mark has to land on its own word. A mark that is drawn but mispositioned looks like a
# plausible near miss in any other test and is unmissable here.
#
# It deliberately does not drive the hotkey and drag - drive-capture.ps1 covers that path,
# and synthetic Alt+Space is unreliable enough to fail without saying so, which reads as
# "the mark is broken" when nothing was ever captured.
#
# Run it through an interactive scheduled task, or it lands in session 0 and draws nothing.
# Register-ScheduledTask was observed queueing without ever dispatching; schtasks /it works:
#
#   schtasks /create /tn SarVis /tr "C:\sar-test\run-highlight.cmd" /sc once /st 00:00 `
#            /ru <user> /it /f
#   schtasks /run /tn SarVis

param(
  [string]$Fixture = "C:\sar-test\windows-ui-text.png",
  [int]$X = 300, [int]$Y = 400,
  [int]$Shots = 8,
  [string]$OutDir = "C:\sar-test"
)

$ErrorActionPreference = 'Stop'
Get-Process SelectAndRead -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-Item (Join-Path $OutDir "highlight-*.png") -ErrorAction SilentlyContinue

# The content the marks must line up with.
$viewer = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @(
  "-NoProfile", "-ExecutionPolicy", "Bypass",
  "-File", "C:\sar-test\show-image.ps1",
  "-Path", $Fixture, "-X", $X, "-Y", $Y)

Start-Sleep -Seconds 4

$reader = Start-Process -FilePath "C:\sar-test\SelectAndRead.exe" -PassThru `
  -ArgumentList @("--read-local", $Fixture, "--overlay", "$X,$Y") `
  -RedirectStandardOutput (Join-Path $OutDir "read-local.log") `
  -RedirectStandardError  (Join-Path $OutDir "read-local.err")

# Recognition and synthesis both finish before the first word is marked.
Start-Sleep -Seconds 5

for ($i = 1; $i -le $Shots; $i++) {
  $shot = Join-Path $OutDir ("highlight-{0}.png" -f $i)
  Start-Process -FilePath "C:\sar-test\SelectAndRead.exe" -ArgumentList "--freeze-to", $shot -Wait
  Start-Sleep -Milliseconds 900
}

$reader.WaitForExit(60000) | Out-Null
Stop-Process -Id $viewer.Id -Force -ErrorAction SilentlyContinue

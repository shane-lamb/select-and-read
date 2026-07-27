# Runs a command in the logged-on user's interactive session.
#
# prlctl exec lands in session 0 as SYSTEM, which cannot draw or receive input, so
# anything involving the overlay, hotkeys or audio has to be relayed through a scheduled
# task with an Interactive principal. LogonType Interactive needs no stored password.
#
# -Arguments is optional here, but it must never reach New-ScheduledTaskAction empty:
# that cmdlet rejects both a missing -Argument and an empty one, so it is splatted in
# only when there is something to pass.
param([Parameter(Mandatory=$true)][string]$Command,
      [string]$Arguments = "",
      [string]$TaskName  = "SarInteractive",
      [string]$UserId    = "$env:COMPUTERNAME\shane")

$ErrorActionPreference = 'Stop'

$actionArgs = @{ Execute = $Command }
if ($Arguments) { $actionArgs.Argument = $Arguments }

$action    = New-ScheduledTaskAction @actionArgs
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Output "started $TaskName -> $Command $Arguments"

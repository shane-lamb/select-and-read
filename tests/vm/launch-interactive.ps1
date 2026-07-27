# Runs a command in the logged-on user's interactive session.
#
# prlctl exec lands in session 0 as SYSTEM, which cannot draw or receive input, so
# anything involving the overlay, hotkeys or audio has to be relayed through a scheduled
# task with an Interactive principal. LogonType Interactive needs no stored password.
#
# Always pass -Arguments, even empty: New-ScheduledTaskAction fails without it.
param([Parameter(Mandatory=$true)][string]$Command,
      [string]$Arguments = "",
      [string]$TaskName  = "SarInteractive",
      [string]$UserId    = "$env:COMPUTERNAME\shane")

$action    = New-ScheduledTaskAction -Execute $Command -Argument $Arguments
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Output "started $TaskName -> $Command $Arguments"

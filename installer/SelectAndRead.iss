; Inno Setup script for Select and Read (SPEC 12.4).
;
; Built by .github/workflows/release.yml with /DAppVersion=<n>; the fallback below only
; exists so the script can be compiled by hand.
#ifndef AppVersion
  #define AppVersion "0"
#endif

[Setup]
; The AppId is what makes a second install replace the first rather than sit beside it.
; It must never change - a new GUID means v<n+1> installs alongside v<n> instead of over
; it, leaving two copies and two Add/Remove Programs entries.
AppId={{1176E259-8AC7-49DC-A00D-963E6F8A4157}
AppName=Select and Read
AppVersion={#AppVersion}
AppPublisher=Shane Lamb
AppPublisherURL=https://github.com/shane-lamb/select-and-read
VersionInfoVersion={#AppVersion}.0.0.0

; Per-user, and deliberately not Program Files. Everything the app owns is already
; per-user - the asInvoker manifest, %APPDATA% config, the DPAPI CurrentUser API key and
; the HKCU Run entry - so a machine-wide install would need elevation it has no use for.
; The elevation is not merely redundant, it is harmful: an elevated installer hands the
; app an admin token, and the app then writes its API key and autostart entry into the
; wrong profile. Keeping PrivilegesRequired at "lowest" is what lets the [Run] entry below
; be a plain launch instead of needing runasoriginaluser, and it also means the uninstaller
; runs as the user, so the HKCU cleanup below reaches the right hive.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\SelectAndRead
DefaultGroupName=Select and Read

; The payload is x64. x64compatible still permits ARM64 Windows, which runs it under
; emulation and is what the test VM is. ArchitecturesInstallIn64BitMode is deliberately
; not set: it steers {autopf} and the registry view, and this install touches neither.
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763

; There is one sensible location and one program group, so asking about either is a page
; the user has to read and dismiss for no decision.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
WizardStyle=modern

; Restart Manager closes applications by posting WM_CLOSE to their top-level windows. A
; tray app has none, so RM cannot close it and degrades to demanding a reboot. The [Code]
; section below kills the process directly instead - which it must, because a running exe
; cannot be overwritten.
CloseApplications=no

; ~147 MB of self-contained .NET compresses well, and this is the download size.
; SolidCompression is deliberately absent - there is one file, so it would do nothing.
Compression=lzma2/max

OutputDir=Output
OutputBaseFilename=SelectAndRead-v{#AppVersion}-setup
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\SelectAndRead.exe

[Files]
Source: "..\publish\SelectAndRead.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Select and Read"; Filename: "{app}\SelectAndRead.exe"

[Registry]
; Never created here - only cleaned up at uninstall, so a user who never enabled "Start
; with Windows" is left untouched. The app owns writing this value (Config.cs); Setup only
; makes sure uninstalling does not strand it pointing at a deleted exe.
;
; "ValueType: none" is what stops the value being written at install - it suppresses the
; value while leaving ValueName in force, so uninsdeletevalue still knows what to remove.
; dontcreatekey covers the key itself.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "SelectAndRead"; ValueType: none; \
    Flags: dontcreatekey uninsdeletevalue

[Run]
; No runasoriginaluser needed: nothing here is elevated, so the app inherits exactly the
; token it should. skipifsilent keeps /VERYSILENT installs from launching it.
Filename: "{app}\SelectAndRead.exe"; Description: "Launch Select and Read"; \
    Flags: nowait postinstall skipifsilent

[Code]
{ A running exe cannot be overwritten, and the app has no window to close politely, so it
  is killed outright. "Not running" is the normal case, so the exit code is ignored. }
procedure KillRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sysnative}\taskkill.exe'), '/IM SelectAndRead.exe /F',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningApp;
  Result := True;
end;

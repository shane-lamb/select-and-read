#!/usr/bin/env bash
# Build on the Mac, push to the VMware Fusion Windows VM, and either run the OCR fixtures
# or launch the app itself in the guest's interactive session.
#
#   ./tests/vm/deploy.sh              build, deploy, run fixtures
#   ./tests/vm/deploy.sh --run        build, deploy, launch the app (tray, hotkeys, audio)
#   ./tests/vm/deploy.sh --no-build   skip the build; combines with --run
#   ./tests/vm/deploy.sh --force-copy re-copy the exe even if the guest's is current
#   ./tests/vm/deploy.sh --stop       kill the app in the guest
#   ./tests/vm/deploy.sh --shot x.png screenshot the guest, overlay and all
#   ./tests/vm/deploy.sh --exec '<cmd>'  run one command in the guest and print its output
#
# --exec exists so that ad-hoc guest commands do not each need the guest password typed on
# a command line, where it would land in shell history. Everything goes through the
# keychain instead.
#
# See README.md in this directory for why the deployment is this convoluted.

set -euo pipefail

VMX="${VMX:-$HOME/Virtual Machines.localized/Windows 11 64-bit Arm.vmwarevm/Windows 11 64-bit Arm.vmx}"
VM_USER="${VM_USER:-kryte}"
KEYCHAIN_GUEST="${KEYCHAIN_GUEST:-sar-vm-guest}"
KEYCHAIN_ENC="${KEYCHAIN_ENC:-sar-vm-encryption}"

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
GUEST='C:\sar-test'
PUBLIC='C:\Users\Public'
PUBLISH="$REPO/bin/Release/net10.0-windows10.0.19041.0/win-arm64/publish"

VMRUN="${VMRUN:-}"
[[ -n "$VMRUN" ]] || VMRUN="$(command -v vmrun 2>/dev/null || true)"
[[ -n "$VMRUN" ]] || VMRUN="/Applications/VMware Fusion.app/Contents/Public/vmrun"

BUILD=1
RUN_APP=0
FORCE_COPY=0
ACTION=fixtures
EXEC_CMD=""
SHOT_PATH=""
while (( $# )); do
  case "$1" in
    --no-build)   BUILD=0 ;;
    --run)        RUN_APP=1 ;;
    --force-copy) FORCE_COPY=1 ;;
    --stop)       ACTION=stop ;;
    --shot)       ACTION=shot; SHOT_PATH="${2:?--shot needs an output path}"; shift ;;
    --exec)       ACTION=exec; EXEC_CMD="${2:?--exec needs a command}"; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

[[ -f "$VMX" ]] || {
  echo "no .vmx at: $VMX" >&2
  echo "Set VMX= to the right path. Note the .vmwarevm bundle is part of it, and the" >&2
  echo "default contains spaces, so it must be quoted." >&2
  exit 1
}

# Looked up by service alone. There is exactly one secret behind each name and the account
# field is only a label, so tying the lookup to $VM_USER would break the moment the guest
# username changes. `security` exits 44 when an item is missing, which set -e would turn
# into a bare exit before the message below could explain what to do about it.
VM_ENCPASS="$(security find-generic-password -s "$KEYCHAIN_ENC" -w 2>/dev/null || true)"
VM_PASS="$(security find-generic-password -s "$KEYCHAIN_GUEST" -w 2>/dev/null || true)"
if [[ -z "$VM_ENCPASS" || -z "$VM_PASS" ]]; then
  echo "missing keychain credentials. Store them once with:" >&2
  echo "    security add-generic-password -s $KEYCHAIN_ENC -a vm -w" >&2
  echo "    security add-generic-password -s $KEYCHAIN_GUEST -a $VM_USER -w" >&2
  echo >&2
  echo "The first is the VM encryption password Fusion asks for when opening the VM; the" >&2
  echo "VM is encrypted because Windows 11 needs a vTPM. The second is the Windows account" >&2
  echo "password. -w with no value prompts, so neither reaches shell history." >&2
  exit 1
fi

# Both passwords reach vmrun's argv and are therefore briefly visible in `ps`. That is
# accepted for a local test VM, and it is why this script must never gain `set -x`.
vm()  { "$VMRUN" -T fusion -vp "$VM_ENCPASS" "$@"; }
vmg() { "$VMRUN" -T fusion -vp "$VM_ENCPASS" -gu "$VM_USER" -gp "$VM_PASS" "$@"; }

# The one safe way to run a guest command. Three separate findings force this exact shape:
#
#   * runProgramInGuest does not split its argument string into argv, it hands the whole
#     blob over, so only a program that re-parses its own command line works. cmd.exe does;
#     powershell.exe does not, and fails with exit 1 every time.
#   * vmrun appends a trailing space to that string. Usually invisible, but fatal to a
#     PowerShell script with positional parameters, which binds it and dies with
#     `Cannot convert value " " to type "System.Int32"`. `exit /b !ERRORLEVEL!` absorbs it.
#   * runProgramInGuest has no stdout channel at all, so output has to be redirected inside
#     the guest and copied back. Embedded double quotes are mangled by vmrun ("The filename
#     or extension is too long"), so the command must not contain any.
#
# /v:on is what makes !ERRORLEVEL! expand at run time rather than parse time, which is also
# what keeps the guest's real exit code propagating out through vmrun.
guest() { vmg runProgramInGuest "$VMX" -interactive 'C:\Windows\System32\cmd.exe' "/v:on /c $1 & exit /b !ERRORLEVEL!"; }

# Copy a guest file out and print it.
gcat() {
  local tmp; tmp="$(mktemp)"
  vmg CopyFileFromGuestToHost "$VMX" "$1" "$tmp" >/dev/null 2>&1 && cat "$tmp"
  rm -f "$tmp"
}

# Kill by pid rather than taskkill: no cmd, no quoting, and so no trailing-space trap.
kill_app() {
  local pid
  for pid in $(vmg listProcessesInGuest "$VMX" 2>/dev/null \
               | grep -i 'cmd=.*SelectAndRead\.exe' \
               | sed -n 's/^pid=\([0-9]*\).*/\1/p'); do
    vmg killProcessInGuest "$VMX" "$pid" >/dev/null 2>&1 || true
  done
}

# The VM must already be running, and must have been started from the Fusion UI. Starting
# it here with `vmrun start` would work, but it spawns a vmware-vmx that the Fusion window
# cannot attach to, leaving the VM showing as locked and unopenable for the rest of the
# session — the exact opposite of what --run is for.
if ! vm list 2>/dev/null | grep -Fq "$VMX"; then
  echo "the VM is not running. Start it from the VMware Fusion window, then re-run." >&2
  echo "Do not start it with 'vmrun start': that takes ownership away from the Fusion UI" >&2
  echo "and the VM then shows as locked." >&2
  exit 1
fi

tools="$(vm checkToolsState "$VMX" 2>/dev/null || true)"
if [[ "$tools" != running ]]; then
  echo "VMware Tools state: ${tools:-unknown} - every guest operation needs 'running'." >&2
  echo "Without it vmrun hangs for minutes and then reports VIX_E_TOOLS_NOT_RUNNING." >&2
  echo "Install it from the Fusion menu: Virtual Machine > Install VMware Tools." >&2
  exit 1
fi

case "$ACTION" in
  stop)
    kill_app
    echo "==> stopped"
    exit 0
    ;;
  shot)
    # This works over the fullscreen topmost overlay, but not over a blanked display, which
    # yields a 69-byte black PNG that reads as the app rendering black — so wake the screen
    # first. Best-effort: the script is only there if something has been deployed, and a
    # possibly-black shot beats refusing to take one.
    guest "powershell -NoProfile -ExecutionPolicy Bypass -File $GUEST\\wake-display.ps1" >/dev/null 2>&1 || true
    vmg captureScreen "$VMX" "$SHOT_PATH"
    echo "==> $SHOT_PATH"
    exit 0
    ;;
  exec)
    vmg deleteFileInGuest "$VMX" "$PUBLIC\\exec.log" >/dev/null 2>&1 || true
    rc=0
    guest "$EXEC_CMD > $PUBLIC\\exec.log 2>&1" >/dev/null 2>&1 || rc=$?
    gcat "$PUBLIC\\exec.log"
    exit $rc
    ;;
esac

if (( BUILD )); then
  echo "==> building win-arm64"
  # self-contained is false here so we don't bundle the framework, unlike the release build. This saves time transferring the exe to the VM.
  dotnet publish "$REPO/SelectAndRead.csproj" -c Release -r win-arm64 \
    --self-contained false -p:PublishSingleFile=true | tail -2
fi

# Always kill first: a stranded overlay from a previous run dims the next run's capture, and
# a running exe can be neither overwritten nor deleted by the copy below.
echo "==> killing any stranded SelectAndRead.exe"
kill_app

echo "==> copying into the guest"
vmg createDirectoryInGuest "$VMX" "$GUEST" >/dev/null 2>&1 || true   # errors if it exists

copy_in() { vmg CopyFileFromHostToGuest "$VMX" "$1" "$GUEST\\$(basename "$1")" >/dev/null; }

stamp="$(stat -f '%m %z' "$PUBLISH/SelectAndRead.exe")"
guest_stamp="$(gcat "$GUEST\\exe.stamp" 2>/dev/null || true)"
if (( FORCE_COPY )) || [[ "$stamp" != "$guest_stamp" ]]; then
  s=$SECONDS
  copy_in "$PUBLISH/SelectAndRead.exe"
  # Written only after the copy succeeds, so an interrupted deploy re-copies next time.
  stampfile="$(mktemp)"
  printf '%s' "$stamp" > "$stampfile"
  vmg CopyFileFromHostToGuest "$VMX" "$stampfile" "$GUEST\\exe.stamp" >/dev/null
  rm -f "$stampfile"
  echo "==> exe copied in $((SECONDS-s))s"
else
  echo "==> exe unchanged, skipped (--force-copy overrides)"
fi

for f in "$REPO"/tests/fixtures/*.png "$REPO"/tests/fixtures/*.expected.txt \
         "$REPO"/tests/vm/*.ps1 "$REPO"/tests/vm/*.cmd; do
  copy_in "$f"
done

if (( RUN_APP )); then
  echo "==> launching the app in the interactive session"
  # -noWait is not optional: the app never exits, so vmrun would otherwise block forever.
  vmg runProgramInGuest "$VMX" -interactive -noWait "$GUEST\\SelectAndRead.exe"

  # The single-file exe spends several seconds unpacking before the process is visible, so
  # wait for it rather than claiming success: "nothing happened" is otherwise just this.
  echo -n "==> waiting for the process"
  running=""
  for _ in $(seq 1 30); do
    running=$(vmg listProcessesInGuest "$VMX" 2>/dev/null | grep -i 'cmd=.*SelectAndRead\.exe' || true)
    [[ -n "$running" ]] && break
    echo -n "."
    sleep 1
  done
  echo

  if [[ -z "$running" ]]; then
    echo "==> it never started. Most likely the guest is locked or logged out, which leaves" >&2
    echo "    -interactive with no console session to target. Check with:" >&2
    echo "    ./tests/vm/deploy.sh --exec 'query session'" >&2
    exit 1
  fi

  echo "==> $running"
  echo "==> running in the VM. The tray icon may be under the taskbar's overflow chevron"
  echo "    rather than visible directly. Stop it with:"
  echo "    ./tests/vm/deploy.sh --stop"
  exit 0
fi

echo "==> running fixtures"
# Delete first: without this a failed run silently reprints the previous run's results,
# which is the most convincing false pass this harness can produce.
vmg deleteFileInGuest "$VMX" "$GUEST\\results.txt" >/dev/null 2>&1 || true
guest "$GUEST\\run-fixtures.cmd > $GUEST\\results.txt 2>&1" >/dev/null 2>&1 || true
gcat "$GUEST\\results.txt"

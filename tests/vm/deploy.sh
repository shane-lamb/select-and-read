#!/usr/bin/env bash
# Build on the Mac, push to the Parallels Windows VM, and either run the OCR fixtures
# or launch the app itself in the guest's interactive session.
#
#   ./tests/vm/deploy.sh              build, deploy, run fixtures
#   ./tests/vm/deploy.sh --run        build, deploy, launch the app (tray, hotkeys, audio)
#   ./tests/vm/deploy.sh --no-build   skip the build; combines with --run
#
# See README.md in this directory for why the deployment is this convoluted.

set -euo pipefail

VM="${VM:-Windows 11}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$HOME/Downloads/sar-test"          # must be under a Parallels-shared folder
GUEST='C:\sar-test'
PUBLISH="$REPO/bin/Release/net10.0-windows10.0.19041.0/win-arm64/publish"

BUILD=1
RUN_APP=0
for arg in "$@"; do
  case "$arg" in
    --no-build) BUILD=0 ;;
    --run)      RUN_APP=1 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if (( BUILD )); then
  echo "==> building win-arm64"
  dotnet publish "$REPO/SelectAndRead.csproj" -c Release -r win-arm64 \
    --self-contained -p:PublishSingleFile=true | tail -2
fi

echo "==> staging to $STAGE"
mkdir -p "$STAGE"
cp "$PUBLISH/SelectAndRead.exe" "$STAGE/"
cp "$REPO"/tests/fixtures/*.png "$REPO"/tests/fixtures/*.expected.txt "$STAGE/"
cp "$REPO"/tests/vm/*.ps1 "$REPO"/tests/vm/*.cmd "$STAGE/"

# Always kill first: a stranded overlay from a previous run dims the next run's capture,
# and a running exe cannot be overwritten by the copy below.
echo "==> killing any stranded SelectAndRead.exe"
prlctl exec "$VM" cmd /c 'taskkill /IM SelectAndRead.exe /F' >/dev/null 2>&1 || true

echo "==> copying into the guest"
# robocopy, not `copy`: `copy` with a wildcard against a Parallels share fails.
prlctl exec "$VM" cmd /c \
  "robocopy \"\\\\Mac\\Home\\Downloads\\sar-test\" \"$GUEST\" /NFL /NDL /NJH /NJS /NP" >/dev/null 2>&1 || true

if (( RUN_APP )); then
  echo "==> launching the app in the interactive session"
  # prlctl exec is SYSTEM in session 0 and cannot draw, so relay through a scheduled task.
  prlctl exec "$VM" powershell -NoProfile -ExecutionPolicy Bypass \
    -File "$GUEST\\launch-interactive.ps1" \
    -Command "$GUEST\\SelectAndRead.exe"

  # Start-ScheduledTask returns as soon as the task is queued, and the single-file exe
  # then spends several seconds unpacking itself before the process is visible. Wait for
  # it rather than claiming success: "nothing happened" is otherwise just this delay.
  echo -n "==> waiting for the process"
  for _ in $(seq 1 30); do
    running=$(prlctl exec "$VM" cmd /c 'tasklist /FI "IMAGENAME eq SelectAndRead.exe" /NH' 2>/dev/null | tr -d '\r' | grep SelectAndRead.exe || true)
    [[ -n "$running" ]] && break
    echo -n "."
    sleep 1
  done
  echo

  if [[ -z "$running" ]]; then
    echo "==> it never started. Check the task with:" >&2
    echo "    prlctl exec \"$VM\" powershell -NoProfile -Command 'Get-ScheduledTaskInfo SarInteractive'" >&2
    exit 1
  fi

  echo "==> $running"
  # Session 0 means the relay failed and the app cannot draw; see README trap 1.
  echo "==> running in the VM. The tray icon may be under the taskbar's overflow chevron"
  echo "    rather than visible directly. Stop it with:"
  echo "    prlctl exec \"$VM\" cmd /c 'taskkill /IM SelectAndRead.exe /F'"
  exit 0
fi

echo "==> running fixtures"
prlctl exec "$VM" cmd /c "$GUEST\\run-fixtures.cmd > $GUEST\\results.txt 2>&1" >/dev/null 2>&1 || true
prlctl exec "$VM" cmd /c "type $GUEST\\results.txt"

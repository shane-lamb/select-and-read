#!/usr/bin/env bash
# Build on the Mac, push to the Parallels Windows VM, and run the OCR fixtures.
#
#   ./tests/vm/deploy.sh              build, deploy, run fixtures
#   ./tests/vm/deploy.sh --no-build   deploy the existing build only
#
# See README.md in this directory for why the deployment is this convoluted.

set -euo pipefail

VM="${VM:-Windows 11}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$HOME/Downloads/sar-test"          # must be under a Parallels-shared folder
GUEST='C:\sar-test'
PUBLISH="$REPO/bin/Release/net10.0-windows10.0.19041.0/win-arm64/publish"

if [[ "${1:-}" != "--no-build" ]]; then
  echo "==> building win-arm64"
  dotnet publish "$REPO/SelectAndRead.csproj" -c Release -r win-arm64 \
    --self-contained -p:PublishSingleFile=true | tail -2
fi

echo "==> staging to $STAGE"
mkdir -p "$STAGE"
cp "$PUBLISH/SelectAndRead.exe" "$STAGE/"
cp "$REPO"/tests/fixtures/*.png "$REPO"/tests/fixtures/*.expected.txt "$STAGE/"
cp "$REPO"/tests/vm/*.ps1 "$REPO"/tests/vm/*.cmd "$STAGE/"

echo "==> copying into the guest"
# robocopy, not `copy`: `copy` with a wildcard against a Parallels share fails.
prlctl exec "$VM" cmd /c \
  "robocopy \"\\\\Mac\\Home\\Downloads\\sar-test\" \"$GUEST\" /NFL /NDL /NJH /NJS /NP" >/dev/null 2>&1 || true

echo "==> running fixtures"
prlctl exec "$VM" cmd /c "$GUEST\\run-fixtures.cmd > $GUEST\\results.txt 2>&1" >/dev/null 2>&1 || true
prlctl exec "$VM" cmd /c "type $GUEST\\results.txt"

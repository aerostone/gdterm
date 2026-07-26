#!/usr/bin/env bash
# pack-release.sh — thin wrapper for pack-release.ps1 when run under Windows Git Bash / WSL with powershell.exe
# On pure Linux (no MSBuild) this only validates file layout.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if command -v powershell.exe >/dev/null 2>&1; then
  exec powershell.exe -ExecutionPolicy Bypass -File tools/pack-release.ps1 "$@"
fi

if command -v pwsh >/dev/null 2>&1; then
  exec pwsh -ExecutionPolicy Bypass -File tools/pack-release.ps1 "$@"
fi

echo "No Windows PowerShell found. Layout check only (cannot MSBuild on this host)."
echo "Required for real pack: Windows + MSBuild + .NET 4.6.2 targeting pack."
echo
missing=0
for f in gdterm.sln \
  src/Gdterm.UI/Gdterm.UI.csproj \
  src/Gdterm.Tests/Gdterm.Tests.csproj \
  tools/pack-release.ps1; do
  if [[ -f "$f" ]]; then
    echo "  OK  $f"
  else
    echo "  MISSING $f"
    missing=1
  fi
done

# sln must list all projects with Build.0
build_lines=$(grep -c 'Build.0' gdterm.sln || true)
echo "  sln Build.0 entries: $build_lines (expect >= 26 for 13 projects × 2 configs)"
if (( build_lines < 26 )); then
  echo "  FAIL: sln missing Build configurations"
  missing=1
fi

# Terminal GUID must be valid hex
if grep -q 'DEFG' src/Gdterm.Terminal/Gdterm.Terminal.csproj 2>/dev/null; then
  echo "  FAIL: Terminal ProjectGuid still has invalid DEFG"
  missing=1
else
  echo "  OK  Terminal ProjectGuid hex"
fi

if [[ $missing -ne 0 ]]; then
  exit 1
fi
echo "Layout OK. Run tools/pack-release.ps1 on Windows to produce dist/gdterm."

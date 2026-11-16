#!/usr/bin/env bash
# ============================================================================
#  Module 12: Native AOT publish + pre-delivery smoke gates (fail-fast).
#  Same three gates as scripts/publish-aot.ps1 (pwsh) and scripts/publish-aot.cmd.
#
#  Gates:
#    1. PDBs are archived into symbols/ (never deleted). They carry the line
#       mapping for Native AOT crash stacks (IlcGenerateStackTraceData is on).
#    2. dxcompiler.dll / dxil.dll must sit next to the exe (paired delivery
#       layout, never a system-wide version search).
#    3. "exe --version" must really load DXC and print its version.
#
#  Usage:
#    scripts/publish-aot.sh                  full publish + gates
#    SKIP_PUBLISH=1 scripts/publish-aot.sh   re-run gates only
#    scripts/publish-aot.sh <dxcSourceDir>   custom dxc source directory
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$ROOT/src/MicroShader.Server/MicroShader.Server.csproj"
STAGE="$ROOT/dist/stg/bin"
DXCSRC="${1:-$ROOT/probes/dxc-aot/native}"

if [ "${SKIP_PUBLISH:-0}" != "1" ]; then
  echo "== 1/5 clean staging (avoid stale PDB / DLL left by incremental publish)"
  rm -rf "$STAGE"
  mkdir -p "$STAGE"

  echo "== 2/5 native AOT publish (-r win-x64 -p:PublishAot=true)"
  dotnet publish "$PROJECT" -c Release -r win-x64 -p:PublishAot=true -o "$STAGE" --nologo
fi

mkdir -p "$STAGE"

echo "== 3/5 gate 1: archive PDBs into symbols/ (keep crash line mapping)"
shopt -s nullglob
pdbs=("$STAGE"/*.pdb)
if [ ${#pdbs[@]} -gt 0 ]; then
  mkdir -p "$STAGE/symbols"
  mv -f "${pdbs[@]}" "$STAGE/symbols/"
fi
shopt -u nullglob

echo "== 4/5 gate 2: stage paired native dependencies next to the exe"
for name in dxcompiler.dll dxil.dll; do
  if [ ! -f "$DXCSRC/$name" ]; then
    echo "MISSING native dependency: $DXCSRC/$name" >&2
    exit 1
  fi
  cp -f "$DXCSRC/$name" "$STAGE/$name"
done
if [ ! -f "$STAGE/UnityShaderLsp.exe" ]; then
  echo "MISSING artifact: UnityShaderLsp.exe" >&2
  exit 1
fi

echo "== 5/5 gate 3: AOT exe self-check (really loads DXC)"
if ! "$STAGE/UnityShaderLsp.exe" --version; then
  echo "AOT smoke FAILED: DXC could not be loaded" >&2
  exit 1
fi

echo
echo "Delivery staging ready: $STAGE"
ls -1 "$STAGE"

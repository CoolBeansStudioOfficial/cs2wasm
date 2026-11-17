#!/bin/bash
# Runs the self-hosted compiler (tests/out/self2/cs2wasm.wasm, or $SELF) on the given arguments, from Git Bash on
# Windows: paths are given without the drive, with '/'. usage: tools/selfrun.sh [node options --] <cs2wasmc arguments>
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
ROOT="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || pwd)"
REF=$(ls -d "/c/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
export CS2WASM_REFERENCE_DIR="${REF#/c}" CS2WASM_FS_ROOT="C:/" CS2WASM_CACHE="/Users/$USERNAME/AppData/Local/cs2wasm"
NODEOPTS=()
for a in "$@"; do if [ "$a" = "--" ]; then HAVE=1; fi; done
if [ -n "$HAVE" ]; then while [ "$1" != "--" ]; do NODEOPTS+=("$1"); shift; done; shift; fi
exec node "${NODEOPTS[@]}" "$ROOT/host/run.mjs" "${SELF:-$ROOT/tests/out/self2/cs2wasm.wasm}" main -- "$@"

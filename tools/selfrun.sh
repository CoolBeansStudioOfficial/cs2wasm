#!/bin/bash
# Runs the self-hosted compiler (tests/out/self2/il2wasm.wasm, or $SELF) on the given arguments, from Git Bash on
# Windows: paths are given without the drive, with '/'. usage: tools/selfrun.sh [node options --] <il2wasm arguments>
# IL2WASM_SNAPSHOT=<file> (a path of this machine): the compiler's state kept from one run to the next, so a later
# build does not read the libraries again (host/snapshot.mjs; hello world 2.7 s where it is 7.2 s from the start)
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
ROOT="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || pwd)"
# (the reference pack of the .NET that DOTNET_ROOT names, else the default install)
DN=${DOTNET_ROOT:-/c/Program Files/dotnet}; DN=$(cygpath -u "$DN" 2>/dev/null || echo "$DN")
REF=$(ls -d "$DN/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
export IL2WASM_REFERENCE_DIR="${REF#/c}" IL2WASM_FS_ROOT="C:/" IL2WASM_CACHE="/Users/$USERNAME/AppData/Local/il2wasm"
NODEOPTS=()
for a in "$@"; do if [ "$a" = "--" ]; then HAVE=1; fi; done
if [ -n "$HAVE" ]; then while [ "$1" != "--" ]; do NODEOPTS+=("$1"); shift; done; shift; fi
exec node "${NODEOPTS[@]}" "$ROOT/host/run.mjs" "${SELF:-$ROOT/tests/out/self2/il2wasm.wasm}" main -- "$@"

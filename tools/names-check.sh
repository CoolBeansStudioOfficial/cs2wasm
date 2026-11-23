#!/bin/bash
# Compares the names RoslynNames.cs gives lambdas, local functions, closures and state machines with the ones Roslyn
# itself emits, for every conformance program (Compiler/RoslynNamesCheck.cs, IL2WASM_CHECK_NAMES=only: no build).
# usage: bash tools/names-check.sh [release] [Debug|Release]   (release: as a release build names them; the second
# argument is the configuration src/Il2Wasm.Cli was built in, Release by default). Prints each program's differences.
cd "$(dirname "$0")/.."
[ "$1" = release ] && export IL2WASM_CHECK_NAMES_RELEASE=1
CFG=${2:-Release}
export IL2WASM_CHECK_NAMES=only
OUT=$(mktemp -d)
n=0
for f in tests/Conformance/*.cs; do
  r=$(dotnet src/Il2Wasm.Cli/bin/$CFG/net11.0/il2wasm.dll "$f" -o "$OUT/x.wasm" 2>&1 | grep "names:")
  if [ -n "$r" ]; then echo "== $f"; echo "$r"; n=$((n+1)); fi
done
echo "names-check${1:+ $1}: $n programs differ"
rm -rf "$OUT"

#!/bin/bash
# Test 204 and the rule of the unlink that is not called (Compiler/Arenas.cs, docs/AUDIT.md A3.5, the build, round
# 10): a store over a slot that held nothing calls Rt.ArenaUnlink only when the store's link kept a count to give
# back. The test compiled as shipped with the skip off (CS2WASM_NO_UNLINK_SKIP=1: the same lines) and with the kept
# counts not asked (CS2WASM_UNLINK_SKIP_BREAK=1: other lines, groups that stay until the next store that links).
#   bash tools/unlink-skip-breaks.sh        exit 1 when the break leaves the output as it is, or off differs from on
cd "$(dirname "$0")/.."
C=src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc; O=tests/out/us; T=tests/Conformance/204_counts_given_back_after_the_store.cs
mkdir -p $O
bad=0
build() { # name, switches
  local m=$1; shift
  env X=1 "$@" $C $T -o $O/$m.wasm > /dev/null 2>&1 || { echo "failed to compile $m"; bad=1; }
  node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
build t204; build t204off CS2WASM_NO_UNLINK_SKIP=1
cmp -s $O/t204.txt $O/t204off.txt && echo "off: the same" || { echo "off: DIFFERS"; bad=1; }
build t204b CS2WASM_UNLINK_SKIP_BREAK=1
if cmp -s $O/t204.txt $O/t204b.txt; then echo "break: THE SAME"; bad=1; else echo "break: differs: $(diff $O/t204.txt $O/t204b.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
exit $bad

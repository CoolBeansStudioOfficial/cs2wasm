#!/bin/bash
# The proof of Wasm/FieldLoads.cs's conditions: each test of the pass (tests/Conformance/23*_field_read_*.cs)
# compiled once as it is and once without each condition in turn (CS2WASM_FIELD_HOIST_WITHOUT=<condition>: a wrong
# module, on purpose), and for each condition the tests whose output is then another, with the first line that
# differs. A condition that changes no test's output is not shown to be needed by any loop of the tests.
#   bash tools/field-hoist-proof.sh [condition ...]        (default: all of them)
#   EXTRA="CS2WASM_NO_FIELD_REUSE=1": more switches for both sides (the first stage alone)
cd "$(dirname "$0")/.."
R=$(pwd)
C=$R/src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc
O=$R/tests/out/fhproof; mkdir -p "$O"
conds=("$@")
[ ${#conds[@]} = 0 ] && conds=(address reflection layout volatile threads loop calls indirect assigned dominance merge inline machine null twice)
tests=$(ls tests/Conformance/23*_field_read_*.cs)
run() { timeout 60 node "$R/host/run.mjs" "$1" main 2>&1; }
for t in $tests; do
  n=$(basename "$t" .cs)
  env $EXTRA "$C" "$t" -o "$O/$n.wasm" >/dev/null 2>&1 || { echo "failed to compile $t"; exit 1; }
  run "$O/$n.wasm" > "$O/$n.txt"
done
none=0
for c in "${conds[@]}"; do
  hit=""
  for t in $tests; do
    n=$(basename "$t" .cs)
    if ! env $EXTRA CS2WASM_FIELD_HOIST_WITHOUT=$c "$C" "$t" -o "$O/$n.$c.wasm" >/dev/null 2>&1; then hit="$hit ${n%%_*}(does not compile)"; continue; fi
    run "$O/$n.$c.wasm" > "$O/$n.$c.txt"
    if ! cmp -s "$O/$n.txt" "$O/$n.$c.txt"; then
      d=$(diff "$O/$n.txt" "$O/$n.$c.txt" | grep '^>' | head -1 | cut -c3-60)
      hit="$hit ${n%%_*}[$d]"
    fi
  done
  if [ -z "$hit" ]; then echo "$c: NO TEST CHANGES"; none=1; else echo "$c:$hit"; fi
done
exit $none

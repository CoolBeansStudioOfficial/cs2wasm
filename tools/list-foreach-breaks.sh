#!/bin/bash
# Test 203 and the rules of `foreach` over a List<T> as it is written since round 10 (docs/AUDIT.md A3.5): the size's
# test first with the version asked again at the list's end, the list's fields read once before the loop, the changed
# list's throw on an enumerator made on the spot. The test compiled as shipped with each switch off (the same lines)
# and with each rule left out (other lines):
#   IL2WASM_LIST_FOREACH_BREAK=1   the version is not asked at the list's end
#   IL2WASM_LIST_FOREACH_BREAK=2   the enumerator handed to MoveNext has the list's version as it is now
#   IL2WASM_FIELD_HOIST_WITHOUT=calls / loop   a field is read once whatever the loop's calls / own code store
#   bash tools/list-foreach-breaks.sh        exit 1 when a break leaves the output as it is, or a switch changes it
cd "$(dirname "$0")/.."
C=src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm; O=tests/out/lf; T=tests/Conformance/203_foreach_reads_the_list_once.cs
mkdir -p $O
bad=0
build() { # name, switches
  local m=$1; shift
  env X=1 "$@" $C $T -o $O/$m.wasm > /dev/null 2>&1 || { echo "failed to compile $m"; bad=1; }
  node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
build t203
for s in IL2WASM_NO_LIST_TEST_FIRST=1 IL2WASM_NO_LIST_THROW_PATH=1 "IL2WASM_NO_LIST_TEST_FIRST=1 IL2WASM_NO_LIST_THROW_PATH=1" IL2WASM_NO_LIST_FOREACH=1 IL2WASM_NO_FIELD_HOIST=1; do
  build t203off $s
  cmp -s $O/t203.txt $O/t203off.txt && echo "$s: the same" || { echo "$s: DIFFERS"; bad=1; }
done
for s in IL2WASM_LIST_FOREACH_BREAK=1 IL2WASM_LIST_FOREACH_BREAK=2 IL2WASM_FIELD_HOIST_WITHOUT=calls IL2WASM_FIELD_HOIST_WITHOUT=loop; do
  build t203b $s
  if cmp -s $O/t203.txt $O/t203b.txt; then echo "$s: THE SAME"; bad=1; else echo "$s: differs: $(diff $O/t203.txt $O/t203b.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
done
exit $bad

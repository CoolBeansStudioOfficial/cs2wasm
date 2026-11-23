#!/bin/bash
# Rt.Reevaluate by the member's slot bits (the default since round 10 of docs/AUDIT.md A3.5; IL2WASM_NO_SLOT_WALK=1:
# by a visit of the object by its type) and test 201, whose nodes are made with their parent in a field and put into
# the parent's list after (the field becomes an up-reference at the add: Reevaluate). The test compiled as shipped
# with the walk off (the same lines) and with the walk leaving the member's first counted slot out
# (IL2WASM_SLOT_WALK_BREAK=1: other lines, with and without the check at the drop, which mends some of it later).
#   bash tools/slot-walk-breaks.sh        exit 1 when the break leaves the output as it is, or off differs from on
cd "$(dirname "$0")/.."
C=src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm; O=tests/out/sw; T=tests/Conformance/201_nodes_built_parent_first.cs
mkdir -p $O
bad=0
build() { # name, switches
  local m=$1; shift
  env X=1 "$@" $C $T -o $O/$m.wasm > /dev/null 2>&1 || { echo "failed to compile $m"; bad=1; }
  node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
build t201; build t201off IL2WASM_NO_SLOT_WALK=1
cmp -s $O/t201.txt $O/t201off.txt && echo "off: the same" || { echo "off: DIFFERS"; bad=1; }
build t201nc IL2WASM_NO_DROP_CHECK=1; build t201ncoff IL2WASM_NO_DROP_CHECK=1 IL2WASM_NO_SLOT_WALK=1
cmp -s $O/t201nc.txt $O/t201ncoff.txt && echo "off, no check at the drop: the same" || { echo "off, no check at the drop: DIFFERS"; bad=1; }
build t201b IL2WASM_SLOT_WALK_BREAK=1
if cmp -s $O/t201.txt $O/t201b.txt; then
  build t201ncb IL2WASM_NO_DROP_CHECK=1 IL2WASM_SLOT_WALK_BREAK=1
  if cmp -s $O/t201nc.txt $O/t201ncb.txt; then echo "break: THE SAME (with and without the check at the drop)"; bad=1
  else echo "break, no check at the drop: differs: $(diff $O/t201nc.txt $O/t201ncb.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
else echo "break: differs: $(diff $O/t201.txt $O/t201b.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
exit $bad

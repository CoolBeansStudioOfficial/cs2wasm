#!/bin/bash
# Test 201 and the rules of the node path (Rt.JoinPair, Rt.NodeUp, Rt.AddNode): the test compiled as shipped with
# each rule left out (IL2WASM_NODE_PATH_BREAK=n), its output against the plain build's. A rule about who owns a
# member (4, 5, 7, 8) leaves garbage that the check at the drop finds later, so the program prints the same: those
# are compared with that check off on both sides (IL2WASM_NO_DROP_CHECK=1), where the general way and the node
# path must still print the same and the broken rule must not.
#   bash tools/node-path-breaks.sh        exit 1 when a break leaves the output as it is, or off differs from on
cd "$(dirname "$0")/.."
C=src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm; O=tests/out/np; T=tests/Conformance/201_nodes_built_parent_first.cs
mkdir -p $O
bad=0
build() { # name, switches
  local m=$1; shift
  env X=1 "$@" $C $T -o $O/$m.wasm > /dev/null 2>&1 || { echo "failed to compile $m"; bad=1; }
  node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
build t201; build t201off IL2WASM_NO_NODE_PATH=1
cmp -s $O/t201.txt $O/t201off.txt && echo "off: the same" || { echo "off: DIFFERS"; bad=1; }
build t201nc IL2WASM_NO_DROP_CHECK=1; build t201ncoff IL2WASM_NO_DROP_CHECK=1 IL2WASM_NO_NODE_PATH=1
cmp -s $O/t201nc.txt $O/t201ncoff.txt && echo "off, no check at the drop: the same" || { echo "off, no check at the drop: DIFFERS"; bad=1; }
build t201pp IL2WASM_NO_PLAIN_PARENTS=1
cmp -s $O/t201.txt $O/t201pp.txt && echo "plain parents off: the same" || { echo "plain parents off: DIFFERS"; bad=1; }
for n in ${BREAKS:-1 2 6}; do
  build t201b$n IL2WASM_NODE_PATH_BREAK=$n
  if cmp -s $O/t201.txt $O/t201b$n.txt; then echo "break $n: THE SAME"; bad=1; else echo "break $n: differs: $(diff $O/t201.txt $O/t201b$n.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
done
for n in ${BREAKS_NC:-4 5 7 8}; do
  build t201ncb$n IL2WASM_NO_DROP_CHECK=1 IL2WASM_NODE_PATH_BREAK=$n
  if cmp -s $O/t201nc.txt $O/t201ncb$n.txt; then echo "break $n, no check at the drop: THE SAME"; bad=1; else echo "break $n, no check at the drop: differs: $(diff $O/t201nc.txt $O/t201ncb$n.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
done
exit $bad

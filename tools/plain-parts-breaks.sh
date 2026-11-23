#!/bin/bash
# Test 202 and the rules of a member's plain part (Rt.PartHolder, docs/AUDIT.md A3.5, the build, round 10): the test
# compiled as shipped with the parts off (CS2WASM_NO_PLAIN_PARTS=1: the same lines) and with each rule left out
# (CS2WASM_PLAIN_PARTS_BREAK=n: other lines). What a broken rule about a link leaves is found later by the check at
# the drop where it can be, so those are also compared with that check off on both sides (CS2WASM_NO_DROP_CHECK=1).
#   bash tools/plain-parts-breaks.sh        exit 1 when a break leaves the output as it is, or off differs from on
#   BREAKS="1 2 3 5 7" by default; 4 (JoinFresh's node a part) and 6 (a part of a class with a finalizer) are asked
#   with BREAKS_OPEN and only reported: see the audit for what is known of them.
cd "$(dirname "$0")/.."
C=src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc; O=tests/out/pp; T=tests/Conformance/202_plain_parts_of_members.cs
mkdir -p $O
bad=0
build() { # name, switches
  local m=$1; shift
  env X=1 "$@" $C $T -o $O/$m.wasm > /dev/null 2>&1 || { echo "failed to compile $m"; bad=1; }
  node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
build t202 CS2WASM_PLAIN_PARTS=1; build t202off CS2WASM_NO_PLAIN_PARTS=1
cmp -s $O/t202.txt $O/t202off.txt && echo "off: the same" || { echo "off: DIFFERS"; bad=1; }
build t202nc CS2WASM_NO_DROP_CHECK=1 CS2WASM_PLAIN_PARTS=1; build t202ncoff CS2WASM_NO_DROP_CHECK=1 CS2WASM_NO_PLAIN_PARTS=1
cmp -s $O/t202nc.txt $O/t202ncoff.txt && echo "off, no check at the drop: the same" || { echo "off, no check at the drop: DIFFERS"; bad=1; }
for n in ${BREAKS:-1 2 3 5 7}; do
  build t202b$n CS2WASM_PLAIN_PARTS_BREAK=$n
  if cmp -s $O/t202.txt $O/t202b$n.txt; then
    build t202ncb$n CS2WASM_NO_DROP_CHECK=1 CS2WASM_PLAIN_PARTS_BREAK=$n
    if cmp -s $O/t202nc.txt $O/t202ncb$n.txt; then echo "break $n: THE SAME (with and without the check at the drop)"; bad=1
    else echo "break $n, no check at the drop: differs: $(diff $O/t202nc.txt $O/t202ncb$n.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
  else echo "break $n: differs: $(diff $O/t202.txt $O/t202b$n.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
done
for n in ${BREAKS_OPEN:-4 6}; do
  build t202b$n CS2WASM_PLAIN_PARTS_BREAK=$n
  if cmp -s $O/t202.txt $O/t202b$n.txt; then echo "break $n (not proven by the test): the same"; else echo "break $n: differs: $(diff $O/t202.txt $O/t202b$n.txt | sed -n 2,3p | tr '\n' ' ' | cut -c1-170)"; fi
done
exit $bad

#!/bin/bash
# Wasm/ColdExits.cs is off unless IL2WASM_COLD_EXITS=1, so the suite runs test 208 without it. This compiles the test
# as it is (the output the suite compares with .NET's), with the pass, and with the pass and each of its rules taken
# out (IL2WASM_COLD_EXITS_WITHOUT=stack, rest, labels): with the pass the output must be the same, with a rule out it
# must differ or the module must not be valid.
#   bash tools/cold-exits-proof.sh        (exit 1 when a line below says FAILS)
cd "$(dirname "$0")/.."
C=src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm
[ -x "$C.exe" ] && C=$C.exe
T=tests/Conformance/208_counts_other_path_outside_loops.cs
O=tests/out/cold-exits-proof
mkdir -p $O
rc=0
$C $T -o $O/plain.wasm > /dev/null 2>&1 || { echo "the test does not compile: FAILS"; exit 1; }
node host/run.mjs $O/plain.wasm main > $O/plain.txt 2>&1
n=$(IL2WASM_COLD_EXITS=1 IL2WASM_COLD_EXITS_STATS=1 $C $T -o $O/on.wasm 2>&1 | grep -c "cold exits: [0-9]")
node host/run.mjs $O/on.wasm main > $O/on.txt 2>&1
if cmp -s $O/plain.txt $O/on.txt && [ "$n" -gt 5 ]; then echo "with the pass ($n loops of the module): the same output ($(wc -l < $O/plain.txt) lines)"; else echo "with the pass ($n loops): the output differs: FAILS"; rc=1; fi
for w in stack rest labels; do
  rm -f $O/$w.wasm
  if IL2WASM_COLD_EXITS=1 IL2WASM_COLD_EXITS_WITHOUT=$w IL2WASM_VALIDATE=1 $C $T -o $O/$w.wasm > $O/$w.build.txt 2>&1 && [ -f $O/$w.wasm ]; then
    timeout 60 node host/run.mjs $O/$w.wasm main > $O/$w.txt 2>&1   # (a module with a rule out may not end)
    if cmp -s $O/plain.txt $O/$w.txt; then echo "without $w: the same output: FAILS"; rc=1; else echo "without $w: $(diff $O/plain.txt $O/$w.txt | grep -c '^<') lines differ"; fi
  else
    echo "without $w: the module is not valid ($(grep -m1 -o 'invalid code[^:]*: [^:]*: [^(]*' $O/$w.build.txt | cut -c1-90))"
  fi
done
exit $rc

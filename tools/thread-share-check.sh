#!/bin/bash
# Every conformance test that is a program with threads, compiled once as it is and once with IL2WASM_SHARE_CHECK=1
# (a plain count of an object another thread made stops the program: a fault of the thread escape analysis,
# Compiler/Sharing.cs), each run once: the two must print the same. One process at a time; a few seconds a test.
#   bash tools/thread-share-check.sh [filter]        exit 1 when a checked build prints something else
# (a program is taken for one with threads when its compilation prints the analysis's line under IL2WASM_TIMES=1)
cd "$(dirname "$0")/.."
C=src/Il2Wasm.Cli/bin/Release/net11.0/il2wasm; O=tests/out/tsc
mkdir -p $O
bad=0; n=0
for f in tests/Conformance/*${1:-}*.cs; do
  grep -q -E "Thread|Task|Parallel|Timer|lock \(" "$f" || continue
  t=$(basename "$f" .cs)
  IL2WASM_TIMES=1 $C "$f" -o $O/$t.wasm > $O/$t.build 2>&1 || { echo "$t: does not compile"; continue; }
  grep -q "sharing:" $O/$t.build || continue
  entry=$(grep -o 'EntryPoint = "[a-z_0-9]*"' "$f" | head -1 | sed 's/.*"\(.*\)"/\1/'); entry=${entry:-main}
  IL2WASM_SHARE_CHECK=1 $C "$f" -o $O/$t.check.wasm > /dev/null 2>&1
  timeout 300 node host/run.mjs $O/$t.wasm $entry > $O/$t.txt 2>&1
  timeout 300 node host/run.mjs $O/$t.check.wasm $entry > $O/$t.check.txt 2>&1
  n=$((n + 1))
  if cmp -s $O/$t.txt $O/$t.check.txt; then echo "$t: the same ($(grep -o '[0-9]* types another thread can reach, [0-9]* statics' $O/$t.build | tail -1))"
  else echo "$t: DIFFERS: $(grep -m1 -o 'share check[^|]*' $O/$t.check.txt | cut -c1-160)"; bad=1; fi
done
echo "$n programs with threads"
exit $bad

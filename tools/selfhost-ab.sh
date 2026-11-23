#!/bin/bash
# Interleaved: self-hosted builds given as arguments, and NativeAOT-LLVM's build, hello world; ROUNDS rounds.
# Each line: the build, its time, its exit code, and its peak memory (the module's linear memory at the end, which
# never shrinks: the measure tools/bench.mjs takes; NativeAOT's through tools/bench-naot-wasm/peak.cjs).
# A build is a name in tests/out/self2 (W, or DIR for another directory) without .wasm.
cd /c/Users/Levi/Documents/GitHub/cs2wasm
W=/Users/Levi/Documents/GitHub/cs2wasm/tests/out/self2
D=${DIR:-$W}
# (the reference pack of the .NET that DOTNET_ROOT names, else the default install)
DN=${DOTNET_ROOT:-/c/Program Files/dotnet}; DN=$(cygpath -u "$DN" 2>/dev/null || echo "$DN")
REF=$(ls -d "$DN/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
T=$(mktemp)
for r in $(seq 1 ${ROUNDS:-3}); do
  for b in "$@"; do
    s=$(date +%s%N); IL2WASM_PRINT_MEMORY=1 IL2WASM_PROCESSORS=1 SELF=$D/$b.wasm timeout 300 bash tools/selfrun.sh $W/w42.cs -o $D/w42-$b.wasm > $T 2>&1; rc=$?
    pages=$(sed -n 's/^memory: \([0-9]*\) pages$/\1/p' $T)
    echo "$b $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(( ${pages:-0} * 65536 )) bytes"
  done
  s=$(date +%s%N); (cd tests/out/self2/naot-wasm-measure && MSYS_NO_PATHCONV=1 IL2WASM_CACHE=/c/Users/Levi/AppData/Local/il2wasm IL2WASM_REFERENCE_DIR="$REF" timeout 300 node -r C:/Users/Levi/Documents/GitHub/cs2wasm/tools/bench-naot-wasm/peak.cjs il2wasm-naot.js /c$W/w42.cs -o /c$W/w42n.wasm > $T 2>&1); rc=$?
  echo "naot $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(sed -n 's/^peak_bytes=//p' $T) bytes"
done
rm -f $T

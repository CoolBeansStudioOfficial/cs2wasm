#!/bin/bash
# Interleaved: self-hosted builds given as arguments, and NativeAOT-LLVM's build, hello world; ROUNDS rounds.
cd /c/Users/Levi/Documents/GitHub/cs2wasm
W=/Users/Levi/Documents/GitHub/cs2wasm/tests/out/self2
REF=$(ls -d "/c/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
for r in $(seq 1 ${ROUNDS:-3}); do
  for b in "$@"; do
    s=$(date +%s%N); CS2WASM_PROCESSORS=1 SELF=$W/$b.wasm timeout 300 bash tools/selfrun.sh $W/w42.cs -o $W/w42-$b.wasm > /dev/null 2>&1; rc=$?
    echo "$b $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc"
  done
  s=$(date +%s%N); (cd tests/out/self2/naot-wasm-measure && MSYS_NO_PATHCONV=1 CS2WASM_CACHE=/c/Users/Levi/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" timeout 300 node cs2wasmc-naot.js /c$W/w42.cs -o /c$W/w42n.wasm > /dev/null 2>&1); echo "naot $(( ($(date +%s%N)-s)/1000000 )) ms rc=$?"
done

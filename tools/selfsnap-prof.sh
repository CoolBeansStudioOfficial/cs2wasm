#!/bin/bash
# Where a build on a snapshot spends its time: the self-hosted compiler built by tools/selfphases.sh with names
# (tests/out/selfph/self.wasm; NAMES=1 bash tools/selfphases.sh), one build of PROG (hello world) that writes the
# snapshot, one on it under node --cpu-prof, then the profile by function (tools/profsum.mjs), under the first drain
# (tools/profincl.mjs) and under the final pass.
#   bash tools/selfsnap-prof.sh [lines=70]
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
OUT=tests/out/selfph
N=${1:-70}
cp ${PROG:-tests/Conformance/145_size_hello.cs} $OUT/w42.cs
W="${ROOT#?:}/$OUT"
export CS2WASM_SNAPSHOT="$ROOT/$OUT/prof.snap" CS2WASM_SNAPSHOT_WHY=1 CS2WASM_PROCESSORS=1 CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=1 SELF="$ROOT/$OUT/self.wasm"
rm -f "$CS2WASM_SNAPSHOT"; rm -rf $OUT/prof-snap
timeout 900 bash tools/selfrun.sh $W/w42.cs -o $W/w42-snap.wasm 2>&1 | grep -E "snapshot|bytes in"
timeout 900 bash tools/selfrun.sh $W/w42.cs -o $W/w42-snap.wasm 2>&1 | grep -E "snapshot|bytes in|^ +[0-9]+ ms +[0-9]+ MB  [a-z]|host\]" | cut -c1-150
timeout 900 bash tools/selfrun.sh --cpu-prof --cpu-prof-dir=$ROOT/$OUT/prof-snap -- $W/w42.cs -o $W/w42-snap.wasm 2>&1 | grep -E "snapshot|bytes in"
echo "=== by function"; node tools/profsum.mjs $OUT/prof-snap $N | cut -c1-200
echo "=== under the first drain (Driver.Drain)"; node tools/profincl.mjs $OUT/prof-snap "Driver.Drain" $N | cut -c1-200
echo "=== under the final pass (WasmModule.Encode)"; node tools/profincl.mjs $OUT/prof-snap "WasmModule.Encode" 30 | cut -c1-200

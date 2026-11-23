#!/bin/bash
# A program compiled by two compilers, run interleaved: the first line of its output from each, round by round.
#   bash tools/ab-wasm.sh <il2wasm A> <il2wasm B> <program.cs> [rounds=3] [node flags...]
# (for timings that move less than the machine's noise between two runs: on the homelab, with a heavy.sh slot)
A=${1:?compiler A}; B=${2:?compiler B}; P=${3:?program}; N=${4:-3}; shift 4 2>/dev/null
R=$(cd "$(dirname "$0")/.." && pwd)
O=$R/tests/out/ab; mkdir -p "$O"
X=$(basename "$P" .cs)
"$A" "$P" -o "$O/$X.a.wasm" >/dev/null 2>&1 || { echo "A failed to compile $P"; exit 1; }
"$B" "$P" -o "$O/$X.b.wasm" >/dev/null 2>&1 || { echo "B failed to compile $P"; exit 1; }
for i in $(seq 1 "$N"); do
  echo "A: $(node "$@" "$R/host/run.mjs" "$O/$X.a.wasm" | head -1)"
  echo "B: $(node "$@" "$R/host/run.mjs" "$O/$X.b.wasm" | head -1)"
done

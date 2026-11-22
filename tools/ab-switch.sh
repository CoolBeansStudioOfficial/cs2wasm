#!/bin/bash
# A program compiled with and without compiler switches, run in turn: its whole output on one line from each, round
# by round (for timings that move less than the machine's noise between two runs: on the homelab, with a slot).
#   bash tools/ab-switch.sh <SWITCH=1[,SWITCH2=1...]> <program.cs> [rounds=3] [entry=main] [node flags...]
#   on the homelab: bash tools/homelab.sh <branch> bash tools/ab-switch.sh CS2WASM_NO_DIRECT_SMALL=1 tools/tree-parts.cs 6
S=${1:?switches, comma separated}; P=${2:?program}; N=${3:-3}; E=${4:-main}; shift 4 2>/dev/null
R=$(cd "$(dirname "$0")/.." && pwd)
C=$R/src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc
O=$R/tests/out/ab; mkdir -p "$O"
X=$(basename "$P" .cs)
"$C" "$P" -o "$O/$X.plain.wasm" >/dev/null 2>&1 || { echo "failed to compile $P"; exit 1; }
env $(echo "$S" | tr ',' ' ') "$C" "$P" -o "$O/$X.switched.wasm" >/dev/null 2>&1 || { echo "failed to compile $P with $S"; exit 1; }
for i in $(seq 1 "$N"); do
  echo "with the switches: $(node "$@" "$R/host/run.mjs" "$O/$X.switched.wasm" "$E" | tr '\n' ' ')"
  echo "plain:             $(node "$@" "$R/host/run.mjs" "$O/$X.plain.wasm" "$E" | tr '\n' ' ')"
done

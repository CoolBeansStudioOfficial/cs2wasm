#!/bin/bash
# A program with threads that hangs rarely, run many times under load with every thread's stack dumped when a run
# hangs (host/hangdump.mjs): built by this checkout's compiler, checked and as shipped, each run RUNS times, PAR at
# once (tools/stress.mjs). The stacks of the hung runs are in tests/out/stress-fail.
#   bash tools/thread-hang-stress.sh tools/thread-board-hang.cs     (RUNS=400 PAR=16 DUMP=20 TIMEOUT=40000 MODES="checked shipped")
#   on the homelab: bash tools/homelab.sh <branch> bash tools/thread-hang-stress.sh tools/thread-board-hang.cs
#   NODE_FLAGS=--no-wasm-stack-checks (several: with commas); NAMES=0 for a module without its names section.
cd "$(dirname "$0")/.."
src=${1:?program.cs}
name=$(basename "$src" .cs)
C=src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc
if [ "${NAMES:-1}" = 1 ]; then export CS2WASM_NAMES=1; fi
mkdir -p tests/out/hang
bad=0
MODES=${MODES//,/ }
for mode in ${MODES:-checked shipped}; do
  out=tests/out/hang/$name.$mode.wasm
  if [ $mode = checked ]; then flag=--checked; else flag=; fi
  $C "$src" $flag -o $out 2>&1 | grep -E "error|cs2wasm:" || true
  [ -f $out ] || { echo "no module $out"; bad=1; continue; }
  echo "== $name $mode"
  CS2WASM_HANG_DUMP=${DUMP:-20} node tools/stress.mjs $out --runs ${RUNS:-400} --par ${PAR:-16} --timeout ${TIMEOUT:-40000} ${NODE_FLAGS:+--node-flags $NODE_FLAGS} > tests/out/hang/$name.$mode.txt 2>&1 || bad=1
  # (the counts of each distinct output, and the hung runs' stacks, top frames)
  grep -E "^ +[0-9]+  |runs, " tests/out/hang/$name.$mode.txt | cut -c1-160
  f=$(ls -t tests/out/stress-fail/$name.$mode.wasm.* 2>/dev/null | head -1)
  if [ -n "$f" ]; then grep -A3 -E "^-- |==== run" "$f" | grep -v "^--$" | head -${DUMPLINES:-200}; fi
done
exit $bad

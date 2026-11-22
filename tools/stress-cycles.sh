#!/bin/bash
# Tests 127 and 139 many times under load: a leak or an early free in the cycle runtime shows there rarely, and not
# in one run of the suite. Each is built by the suite's runner, as shipped and checked, then run RUNS times, PAR at
# once (tools/stress.mjs); every run of a module must print the same.
#   bash tools/stress-cycles.sh          (RUNS=200 PAR=8 by default; TESTS="187_two_tops_one_cycle ..." for other programs)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/stress-cycles.sh
#   NODE_FLAGS=--liftoff-only (several: with commas): flags for each run's Node; a compiler switch in the environment
#   (CS2WASM_NO_MEM_SYNC=1) reaches the build
cd "$(dirname "$0")/.."
# (every function named in the module, so a trap's stack reads; NAMES=0 for the module as the suite builds it)
if [ "${NAMES:-1}" = 1 ]; then export CS2WASM_NAMES=1; fi
bad=0
for t in ${TESTS:-127_everything_is_freed 139_racing_cyclic_stores}; do
  for mode in ${MODES:-shipped checked}; do
    # (the runner leaves a checked module in tests/out, one as shipped in tests/out/shipped)
    if [ $mode = shipped ]; then flag=--unchecked; dir=tests/out/shipped; else flag=; dir=tests/out; fi
    rm -f $dir/$t.wasm
    dotnet run --project tests/Cs2Wasm.Tests -c Release -- $t $flag 2>&1 | grep -E "^(PASS|FAIL)" || bad=1
    echo "== $t $mode"
    if [ ! -f $dir/$t.wasm ]; then echo "no module $dir/$t.wasm"; bad=1; continue; fi
    cp $dir/$t.wasm $dir/$t.stress.wasm
    node tools/stress.mjs $dir/$t.stress.wasm --runs ${RUNS:-200} --par ${PAR:-8} ${NODE_FLAGS:+--node-flags $NODE_FLAGS} || bad=1
  done
done
exit $bad

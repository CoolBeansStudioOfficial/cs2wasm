#!/bin/bash
# Tests 127 and 139 many times under load: a leak or an early free in the cycle runtime shows there rarely, and not
# in one run of the suite. Each is built by the suite's runner, as shipped and checked, then run RUNS times, PAR at
# once (tools/stress.mjs); every run of a module must print the same.
#   bash tools/stress-cycles.sh          (RUNS=200 PAR=8 by default)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/stress-cycles.sh
cd "$(dirname "$0")/.."
bad=0
for t in 127_everything_is_freed 139_racing_cyclic_stores; do
  for mode in --unchecked --checked; do
    flag=$mode; [ "$mode" = "--checked" ] && flag=
    dotnet run --project tests/Cs2Wasm.Tests -c Release -- $t $flag 2>&1 | grep -E "^(PASS|FAIL)" || bad=1
    cp tests/out/$t.wasm tests/out/$t.stress.wasm
    echo "== $t $mode"
    node tools/stress.mjs tests/out/$t.stress.wasm --runs ${RUNS:-200} --par ${PAR:-8} || bad=1
  done
done
exit $bad

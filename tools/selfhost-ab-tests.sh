#!/bin/bash
# Self-hosted compilers (no threads) timed in turn on conformance tests, ROUNDS rounds (3): each line the build,
# the test, its time, exit code and peak memory (the module's linear memory at the end, CS2WASM_PRINT_MEMORY).
# Then, with SELFDET=1 (the default), each build once through tools/selfdet.mjs on each test (byte for byte?).
#   bash tools/selfhost-ab-tests.sh <tests, commas> <name>=<path of a .wasm> ...
#   e.g. on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-ab-tests.sh 145_size_hello,088_feature_sweep_2
#        main=C:/Users/Levi/hl/x/tests/out/dropstats/plain.wasm br=tests/out/dropstats/plain.wasm
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
T=${1:?the tests}; shift
TMP=$(mktemp)
for r in $(seq 1 ${ROUNDS:-3}); do
  for t in $(echo "$T" | tr ',' ' '); do
    for v in "$@"; do
      n=${v%%=*}; p=${v#*=}
      p=$(cd "$(dirname "$p")" && pwd -W 2>/dev/null)/$(basename "$p")
      mkdir -p tests/out/selfab
      s=$(date +%s%N); CS2WASM_PRINT_MEMORY=1 CS2WASM_PROCESSORS=1 SELF="${p#?:}" timeout ${LIMIT:-600} bash tools/selfrun.sh "${ROOT#?:}/tests/Conformance/$t.cs" -o "${ROOT#?:}/tests/out/selfab/$n-$t.wasm" > $TMP 2>&1; rc=$?
      pages=$(sed -n 's/^memory: \([0-9]*\) pages$/\1/p' $TMP)
      echo "round $r $t $n $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(( ${pages:-0} * 64 / 1024 )) MB"
    done
  done
done
rm -f $TMP
if [ "${SELFDET:-1}" = 1 ]; then
  for t in $(echo "$T" | tr ',' ' '); do
    for v in "$@"; do
      n=${v%%=*}; p=${v#*=}
      echo "$n on $t: $(node tools/selfdet.mjs --self "$p" tests/Conformance/$t.cs 2>&1 | grep '^selfdet: self' | tr '\n' ' ')"
    done
  done
fi

#!/bin/bash
# The self-hosted compiler built with CS2WASM_DROP_STATS=1 (and the switches given), run on tests, the checks at the
# drop it made by where they were asked from (Runtime.cs, DsPrint's "why" rows: checks, members, visits, dead, failed
# by references from outside, checks of 1,000 members or more and their visits, the largest).
#   bash tools/drop-stats-self.sh <test,test> <name>=<SWITCH=1,SWITCH2=1> ...     (`as=`: as it is)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/drop-stats-self.sh 088_feature_sweep_2 as= off=CS2WASM_NO_STALE_NOTE=1
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
OUT=${OUT:-tests/out/dropstats}
mkdir -p $OUT
T=${1:?the tests}; shift
if [ "${BUILD:-1}" = 1 ]; then rm -rf $OUT/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $OUT/cli; fi
for v in "$@"; do
  n=${v%%=*}; sw=${v#*=}
  if [ "${BUILD:-1}" = 1 ]; then
    s=$(date +%s)
    env CS2WASM_NO_THREADS=1 DOTNET_GCHeapHardLimit=0x380000000 CS2WASM_DROP_STATS=1 X=1 $(echo "$sw" | tr ',' ' ') node tools/selfhost.mjs $OUT/cli/cs2wasmc.exe $OUT/$n.wasm --pathmap "$ROOT=/src" > $OUT/$n.build.txt 2>&1
    echo "built $n ($sw): rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/$n.wasm 2>/dev/null) bytes"
  fi
  for t in $(echo "$T" | tr ',' ' '); do
    s=$(date +%s)
    r=$(SELFDET_STDOUT=$OUT/$n.$t.txt node tools/selfdet.mjs --self $OUT/$n.wasm tests/Conformance/$t.cs 2>&1 | grep '^selfdet: self' | tr '\n' ' ')
    echo "$n on $t: $(( $(date +%s) - s )) s: $r"
    # (the last table printed)
    awk '/^drop checks /{blk=""} {if (/^drop checks |^why |^holds /) blk=blk $0 "\n"} END{printf "%s", blk}' $OUT/$n.$t.txt
  done
done

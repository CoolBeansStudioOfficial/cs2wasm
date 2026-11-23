#!/bin/bash
# How long the self-hosted compiler (no threads) takes on Console.WriteLine(42), built as it is and once for each set
# of compile-time switches given: a change of the runtime that makes it slow or never end does not show in the suite
# (its programs are small). Each run is stopped after LIMIT seconds (60; about 10 is usual). Exit 1 when the first
# build (as it is) fails, is stopped, or writes another module than the .NET-hosted compiler (tools/selfdet.mjs).
#   bash tools/selfhost-time.sh [<name>=<SWITCH=1,SWITCH2=1> ...]       (BUILD=0: the builds already there)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-time.sh noturn=IL2WASM_NO_TURN_HOLD=1
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
OUT=tests/out/selftime
mkdir -p $OUT
if [ "${BUILD:-1}" = 1 ]; then rm -rf $OUT/cli && cp -r src/Il2Wasm.Cli/bin/Release/net11.0 $OUT/cli; fi
cp tests/Conformance/145_size_hello.cs $OUT/w42.cs
W="${ROOT#?:}/$OUT"
bad=0; first=1
# (PLAIN=0: only the builds with switches, the first of them in the place of the one as it is)
for v in $([ "${PLAIN:-1}" = 1 ] && echo plain=) "$@"; do
  n=${v%%=*}; sw=${v#*=}
  if [ "${BUILD:-1}" = 1 ]; then
    s=$(date +%s)
    env IL2WASM_NO_THREADS=1 DOTNET_GCHeapHardLimit=0x380000000 X=1 $(echo "$sw" | tr ',' ' ') node tools/selfhost.mjs $OUT/cli/il2wasm.exe $OUT/$n.wasm --pathmap "$ROOT=/src" > $OUT/$n.build.txt 2>&1
    echo "built $n ($sw): rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/$n.wasm 2>/dev/null) bytes"
  fi
  for r in 1 2; do
    s=$(date +%s%N); IL2WASM_PROCESSORS=1 SELF="$ROOT/$OUT/$n.wasm" timeout ${LIMIT:-60} bash tools/selfrun.sh $W/w42.cs -o $W/w42-$n.wasm > $OUT/$n.run.txt 2>&1; rc=$?
    echo "$n on hello world: $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc$([ $rc = 124 ] && echo ' (stopped at the limit)')"
    if [ $rc != 0 ]; then [ $first = 1 ] && bad=1; break; fi
  done
  if [ $first = 1 ] && [ $bad = 0 ]; then
    # (SELFDET=145_,220_,193_: the tests, with commas)
    for t in $(echo "${SELFDET:-145_size_hello}" | tr "," " "); do
      f=$(ls tests/Conformance/$t*.cs | head -1)
      echo "$n against the .NET-hosted compiler on $f: $(node tools/selfdet.mjs --self $OUT/$n.wasm --cores 1 $f 2>&1 | grep -v '^    at \|^$\|^Node.js' | tail -2 | tr '\n' ' ')"
      [ ${PIPESTATUS[0]} = 0 ] || true
    done
  fi
  first=0
done
exit $bad

#!/bin/bash
# The self-hosted compiler (no threads) built once for each set of compile-time switches given, each then run on
# some programs against the .NET-hosted compiler (tools/selfdet.mjs: byte for byte, or how it fails): which switch
# makes a miscompiled compiler right again.
#   bash tools/selfhost-variants.sh <test name> <name>=<SWITCH=1,SWITCH2=1> ...      (`plain=`: as it is)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-variants.sh 230_field_read_in_loops plain= noreuse=CS2WASM_NO_FIELD_REUSE=1
#   BUILD=0: the builds already there ($OUT/<name>.wasm; OUT=tests/out/selfvar, or tests/out/selfsw: as-is, switched).
#   <test name>: several with commas. RUNENV=A=1,B=1: switches for both compilers while they compile the tests.
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
OUT=${OUT:-tests/out/selfvar}
mkdir -p $OUT
T=${1:?the test}; shift
if [ "${BUILD:-1}" = 1 ]; then rm -rf $OUT/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $OUT/cli; fi
for v in "$@"; do
  n=${v%%=*}; sw=${v#*=}
  if [ "${BUILD:-1}" = 1 ]; then
    s=$(date +%s)
    env CS2WASM_NO_THREADS=1 DOTNET_GCHeapHardLimit=0x380000000 X=1 $(echo "$sw" | tr ',' ' ') node tools/selfhost.mjs $OUT/cli/cs2wasmc.exe $OUT/$n.wasm --pathmap "$ROOT=/src" > $OUT/$n.build.txt 2>&1
    echo "built $n ($sw): rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/$n.wasm 2>/dev/null) bytes"
  fi
  for t in 145_size_hello $(echo "$T" | tr ',' ' '); do
    if [ "${SHOW:-0}" = 1 ]; then
      # (a failure with its stack: build with CS2WASM_NAMES=1 for every function's name)
      echo "$n on $t${RUNENV:+ with $RUNENV}:"
      env $(echo "$RUNENV" | tr ',' ' ') node tools/selfdet.mjs --self $OUT/$n.wasm tests/Conformance/$t.cs 2>&1 | grep -v '^$\|^Node.js\|selfdet: net:\|CW0202' | cut -c1-230 | head -45
      continue
    fi
    echo "$n on $t${RUNENV:+ with $RUNENV}: $(env $(echo "$RUNENV" | tr ',' ' ') node tools/selfdet.mjs --self $OUT/$n.wasm tests/Conformance/$t.cs 2>&1 | grep -v '^    at \|^$\|^Node.js\|selfdet: net:' | head -6 | tr '\n' ' ')"
  done
done

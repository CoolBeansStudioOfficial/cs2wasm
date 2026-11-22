#!/bin/bash
# The self-hosted compiler (no threads, the build that is like NativeAOT-LLVM's) with and without compile-time
# switches, in this checkout: builds it as it is and once more with the switches given set (CS2WASM_NO_X=1 ...), then
# runs both on Console.WriteLine(42) in turn, ROUNDS rounds (3), one thread: time, exit code, peak memory (the
# module's linear memory at the end), and whether the two wrote the same module.
#   bash tools/selfhost-switch.sh CS2WASM_NO_RIPE_PAGES=1 CS2WASM_NO_STATIC_HOIST=1     (BUILD=0: the builds already there;
#   SHOW=1: the last lines each run printed)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-switch.sh CS2WASM_NO_RIPE_PAGES=1
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
OUT=tests/out/selfsw
mkdir -p $OUT
if [ "${BUILD:-1}" = 1 ]; then rm -rf $OUT/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $OUT/cli; fi
# (a program with an entry of its own: top-level statements are an error in a library, which is what cs2wasmc compiles)
cp tests/Conformance/145_size_hello.cs $OUT/w42.cs
build() { # name, switches
  n=$1; shift
  s=$(date +%s)
  env CS2WASM_NO_THREADS=1 DOTNET_GCHeapHardLimit=0x380000000 "$@" node tools/selfhost.mjs $OUT/cli/cs2wasmc.exe $OUT/$n.wasm --pathmap "$ROOT=/src" > $OUT/$n.build.txt 2>&1
  echo "built $n: rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/$n.wasm 2>/dev/null) bytes"
}
if [ "${BUILD:-1}" = 1 ]; then build as-is X=1; build switched "$@"; fi
W="${ROOT#?:}/$OUT"
T=$(mktemp)
for r in $(seq 1 ${ROUNDS:-3}); do
  for b in $([ $((r % 2)) = 1 ] && echo as-is switched || echo switched as-is); do
    s=$(date +%s%N); CS2WASM_PRINT_MEMORY=1 CS2WASM_PROCESSORS=1 SELF="$ROOT/$OUT/$b.wasm" timeout 600 bash tools/selfrun.sh $W/w42.cs -o $W/w42-$b.wasm > $T 2>&1; rc=$?
    pages=$(sed -n 's/^memory: \([0-9]*\) pages$/\1/p' $T)
    echo "$b $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(( ${pages:-0} * 65536 )) bytes"
    if [ $rc != 0 ] || [ "${SHOW:-0}" = 1 ]; then tail -5 $T; fi
  done
done
rm -f $T
cmp $OUT/w42-as-is.wasm $OUT/w42-switched.wasm && echo "the two wrote the same module ($(stat -c %s $OUT/w42-as-is.wasm) bytes)"
# (and the compiler run by .NET: the same module)
$OUT/cli/cs2wasmc.exe $OUT/w42.cs -o $OUT/w42-dotnet.wasm > /dev/null 2>&1
cmp $OUT/w42-as-is.wasm $OUT/w42-dotnet.wasm && echo "the same as the compiler run by .NET writes"

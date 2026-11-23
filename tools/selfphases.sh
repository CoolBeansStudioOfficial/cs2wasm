#!/bin/bash
# One hello-world compile's phases on the three sides, in turn: the self-hosted compiler (built here without
# threads, as NativeAOT-LLVM's build has none), NativeAOT-LLVM's WebAssembly build of the compiler in the same Node
# (tests/out/self2/naot-wasm-measure of the main checkout: it is as old as it was published), and the compiler run
# by .NET on one processor and on all. Each with CS2WASM_TIMES=1 (the compiler's phases on stderr) and, for the
# self-hosted one, CS2WASM_HOST_TIMES=1 (the module made, instantiated, main returned).
#   bash tools/selfphases.sh            BUILD=0: the build already in tests/out/selfph; NAMES=1: with function names
#   PROF=1: after the rounds, one run of the self-hosted compiler (and one of NativeAOT's) under node --cpu-prof,
#   summed by tools/profsum.mjs (NAMES=1 to read it); NODE_FLAGS="--x --y": node options for the self-hosted runs
#   PROG=tests/Conformance/088_x.cs: another program than hello world
#   ALLCORE=1: also the self-hosted compiler and .NET's on one processor with CS2WASM_ALL_CORE_LIBS=1 (every core
#   library read, as before UpstreamSources.Needed took only those a program uses); SHOW=0: the times only
#   on the homelab: bash tools/homelab.sh <branch> env NAMES=1 PROF=1 bash tools/selfphases.sh
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
MAIN=/c/Users/$USERNAME/Documents/GitHub/cs2wasm
OUT=tests/out/selfph
mkdir -p $OUT
if [ "${BUILD:-1}" = 1 ]; then
  rm -rf $OUT/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $OUT/cli
  s=$(date +%s)
  env CS2WASM_NO_THREADS=1 CS2WASM_NAMES=${NAMES:-0} DOTNET_GCHeapHardLimit=0x380000000 $BUILD_ENV node tools/selfhost.mjs $OUT/cli/cs2wasmc.exe $OUT/self.wasm --pathmap "$ROOT=/src" > $OUT/self.build.txt 2>&1
  echo "built: rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/self.wasm 2>/dev/null) bytes"
fi
cp ${PROG:-tests/Conformance/145_size_hello.cs} $OUT/w42.cs
W="${ROOT#?:}/$OUT"
DN=${DOTNET_ROOT:-/c/Program Files/dotnet}; DN=$(cygpath -u "$DN" 2>/dev/null || echo "$DN")
REF=$(ls -d "$DN/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
CACHE=/Users/$USERNAME/AppData/Local/cs2wasm
NAOT=$MAIN/tests/out/self2/naot-wasm-measure
self() { CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=${HOST_TIMES:-1} CS2WASM_PRINT_MEMORY=1 CS2WASM_PROCESSORS=1 SELF="$ROOT/$OUT/self.wasm" timeout 900 bash tools/selfrun.sh "$@" $NODE_FLAGS -- $W/w42.cs -o $W/w42-self.wasm; }
naot() { (cd $NAOT && CS2WASM_TIMES=1 CS2WASM_CACHE=/c$CACHE CS2WASM_REFERENCE_DIR="$REF" timeout 900 node "$@" cs2wasmc-naot.js /c$W/w42.cs -o /c$W/w42-naot.wasm); }
net() { CS2WASM_TIMES=1 CS2WASM_CACHE=$CACHE CS2WASM_REFERENCE_DIR="${REF#/c}" timeout 900 $OUT/cli/cs2wasmc.exe $W/w42.cs -o $W/w42-net.wasm --pathmap /=/; }
timed() { # label, command...
  l=$1; shift
  s=$(date +%s%N); "$@" > $OUT/$l.txt 2>&1; rc=$?
  echo "$l $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc"
}
for r in $(seq 1 ${ROUNDS:-3}); do
  timed self self
  [ "${ALLCORE:-0}" = 1 ] && CS2WASM_ALL_CORE_LIBS=1 timed self-allcore self
  [ -d $NAOT ] && timed naot naot
  DOTNET_PROCESSOR_COUNT=1 timed net-1 net
  [ "${ALLCORE:-0}" = 1 ] && CS2WASM_ALL_CORE_LIBS=1 DOTNET_PROCESSOR_COUNT=1 timed net-1-allcore net
  timed net-all net
done
[ "${SHOW:-1}" = 1 ] && for l in self self-allcore naot net-1 net-all; do
  [ -f $OUT/$l.txt ] || continue
  echo "=== $l (the last round)"
  cut -c1-170 $OUT/$l.txt
done
if [ "${PROF:-0}" = 1 ]; then
  rm -rf $OUT/prof $OUT/prof-naot
  timed self-prof self --cpu-prof --cpu-prof-dir=$ROOT/$OUT/prof
  echo "=== self-hosted, by function"
  node tools/profsum.mjs $OUT/prof ${TOP:-70}
  if [ -d $NAOT ]; then
    timed naot-prof naot --cpu-prof --cpu-prof-dir=$ROOT/$OUT/prof-naot
    echo "=== NativeAOT-LLVM, by function"
    node tools/profsum.mjs $OUT/prof-naot ${TOP:-70}
  fi
fi
# VERIFY=1: then tools/selfhost-verify.sh (the build's modules against the .NET-hosted compiler's, four programs)
if [ "${VERIFY:-0}" = 1 ]; then bash tools/selfhost-verify.sh; fi

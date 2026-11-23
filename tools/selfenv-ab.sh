#!/bin/bash
# The self-hosted compiler (no threads, as tools/selfhost-switch.sh builds it: tests/out/selfsw/as-is.wasm) run with
# and without environment variables it reads when it compiles (a switch of the compiler itself, where
# tools/selfhost-switch.sh's are switches of the build that makes it), in turn with NativeAOT-LLVM's build of the
# compiler when the main checkout has one: ROUNDS rounds (5) on PROG (hello world), one thread; each line the time,
# the exit code and the peak memory; then whether the two wrote the same module, and the phases of each
# (CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=1, one more run each).
#   bash tools/selfenv-ab.sh CS2WASM_NO_LIB_DOC_SKIP=1          (BUILD=0: the build already there; NAMES=1: with names;
#   VERIFY=1: then tools/selfdet.mjs on four programs with the build as it is; SNAP=1: then tools/selfsnap.mjs with it)
#   on the homelab: bash tools/homelab.sh <branch> env ROUNDS=5 bash tools/selfenv-ab.sh CS2WASM_NO_LIB_DOC_SKIP=1
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
ROOT="$(pwd -W 2>/dev/null || pwd)"
MAIN=/c/Users/$USERNAME/Documents/GitHub/cs2wasm
OUT=tests/out/selfsw
mkdir -p $OUT
if [ "${BUILD:-1}" = 1 ]; then
  rm -rf $OUT/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $OUT/cli
  s=$(date +%s)
  env CS2WASM_NO_THREADS=1 CS2WASM_NAMES=${NAMES:-0} DOTNET_GCHeapHardLimit=0x380000000 node tools/selfhost.mjs $OUT/cli/cs2wasmc.exe $OUT/as-is.wasm --pathmap "$ROOT=/src" > $OUT/as-is.build.txt 2>&1
  echo "built: rc=$? $(( $(date +%s) - s )) s, $(stat -c %s $OUT/as-is.wasm 2>/dev/null) bytes"
fi
cp ${PROG:-tests/Conformance/145_size_hello.cs} $OUT/w42.cs
W="${ROOT#?:}/$OUT"
DN=${DOTNET_ROOT:-/c/Program Files/dotnet}; DN=$(cygpath -u "$DN" 2>/dev/null || echo "$DN")
REF=$(ls -d "$DN/packs/Microsoft.NETCore.App.Ref/"*/ref/net11.0 | tail -1)
NAOT=$MAIN/tests/out/self2/naot-wasm-measure
T=$(mktemp)
mkdir -p $OUT/a $OUT/b
one() { # label, directory, env...
  l=$1; d=$2; shift 2
  s=$(date +%s%N); env CS2WASM_PRINT_MEMORY=1 CS2WASM_PROCESSORS=1 SELF="$ROOT/$OUT/as-is.wasm" "$@" timeout 900 bash tools/selfrun.sh $W/w42.cs -o $W/$d/w42.wasm > $T 2>&1; rc=$?
  pages=$(sed -n 's/^memory: \([0-9]*\) pages$/\1/p' $T)
  echo "$l $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(( ${pages:-0} * 65536 )) bytes"
  if [ $rc != 0 ]; then tail -5 $T; fi
}
for r in $(seq 1 ${ROUNDS:-5}); do
  if [ $((r % 2)) = 1 ]; then one as-is a X=1; one "with $*" b "$@"; else one "with $*" b "$@"; one as-is a X=1; fi
  if [ -d $NAOT ]; then
    s=$(date +%s%N); (cd $NAOT && CS2WASM_CACHE=/c/Users/$USERNAME/AppData/Local/cs2wasm CS2WASM_REFERENCE_DIR="$REF" timeout 900 node cs2wasmc-naot.js /c$W/w42.cs -o /c$W/w42n.wasm > $T 2>&1); rc=$?
    echo "naot $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc"
  fi
done
cmp $OUT/a/w42.wasm $OUT/b/w42.wasm && echo "the two wrote the same module ($(stat -c %s $OUT/a/w42.wasm) bytes)"
echo "=== the phases, as it is"
one as-is a CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=1 > /dev/null; grep -v -E "^ +[0-9]+ ms +[0-9]+ MB +(finish|arenas|static)|library: +[0-9]+ ms  System\.[A-OQ-Z]" $T | cut -c1-200
echo "=== the phases, with $*"
one with b CS2WASM_TIMES=1 CS2WASM_HOST_TIMES=1 "$@" > /dev/null; grep -v -E "^ +[0-9]+ ms +[0-9]+ MB +(finish|arenas|static)|library: +[0-9]+ ms  System\.[A-OQ-Z]" $T | cut -c1-200
rm -f $T
if [ "${VERIFY:-0}" = 1 ]; then
  rc=0
  for t in tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs; do
    echo "--- $t"
    node tools/selfdet.mjs --self $OUT/as-is.wasm --cores 1 $t 2>&1 | tail -4
    [ ${PIPESTATUS[0]} = 0 ] || rc=1
  done
  echo "verify: $([ $rc = 0 ] && echo all the same || echo FAILED)"
fi
if [ "${SNAP:-0}" = 1 ]; then
  node tools/selfsnap.mjs --self $OUT/as-is.wasm --rounds ${ROUNDS:-5} --naot "$(cygpath -m $NAOT 2>/dev/null || echo $NAOT)" tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs
fi

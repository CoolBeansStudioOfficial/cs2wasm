#!/bin/bash
# Thread scaling of the threaded self-hosted compiler (docs/FINISH.md check 5): builds this checkout's threaded
# self-hosted compiler (named, so profiles read) once per commit, then times it on hello world on each thread count,
# interleaved, ROUNDS rounds (default 3). Also the PLINQ programs of tools/bench-threads, built by the .NET-hosted
# compiler, on the same counts.
# usage: bash tools/thread-scale.sh [counts...]          (default: 1 4 12 16 24)
#   PROF=n: afterwards one profiled run on n threads, summed per thread and by caller of the stripes' waits
#   ONLY=bench: the PLINQ programs only (no self-hosted build)
#   BUILD=<name>: the compiler is tests/out/thr/<name>.wasm (built from this checkout when missing)
set -e
cd "$(dirname "$0")/.."
ROOT=$(pwd)
DROOT=${ROOT#/[a-z]}
OUT=$ROOT/tests/out/thr
mkdir -p "$OUT"
C=$(git rev-parse --short HEAD)
B=${BUILD:-thr-$C}
COUNTS=${*:-1 4 12 16 24}
CLI=src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc.exe
# the PLINQ programs
for p in tools/bench-threads/*.cs; do
  n=$(basename "$p" .cs)
  $CLI "$p" -o "$OUT/$n.wasm" > /dev/null
done
if [ "$ONLY" != bench ] && [ ! -f "$OUT/$B.wasm" ]; then
  # (a copy of the .NET-hosted compiler: the checkout's can be rebuilt meanwhile)
  rm -rf "$OUT/cli-$C"; cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 "$OUT/cli-$C"
  s=$(date +%s)
  CS2WASM_NAMES=1 DOTNET_GCHeapHardLimit=0x380000000 node tools/selfhost.mjs "$OUT/cli-$C/cs2wasmc.exe" "$OUT/$B.wasm" > "$OUT/$B.build.log" 2>&1 || { tail -20 "$OUT/$B.build.log"; exit 1; }
  echo "built $B in $(( $(date +%s) - s )) s, $(stat -c %s "$OUT/$B.wasm") bytes"
fi
cat > "$OUT/w42.cs" <<'EOF'
using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Console.WriteLine(42);
}
EOF
T=$(mktemp)
for r in $(seq 1 ${ROUNDS:-3}); do
  for n in $COUNTS; do
    line="round $r, $n threads:"
    for p in tools/bench-threads/*.cs; do
      b=$(basename "$p" .cs)
      o=$(CS2WASM_PROCESSORS=$n timeout 300 node host/run.mjs "$OUT/$b.wasm" main 2>&1 | tail -1)
      line="$line $b $o;"
    done
    if [ "$ONLY" != bench ]; then
      s=$(date +%s%N)
      CS2WASM_PROCESSORS=$n SELF=$DROOT/tests/out/thr/$B.wasm timeout 600 bash tools/selfrun.sh "$DROOT/tests/out/thr/w42.cs" -o "$DROOT/tests/out/thr/w42-$B-$n.wasm" > $T 2>&1 && rc=0 || rc=$?
      line="$line compiler $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc"
      [ $rc = 0 ] || tail -3 $T
    fi
    echo "$line"
  done
done
rm -f $T
if [ "$ONLY" != bench ]; then
  for n in $COUNTS; do cmp -s "$OUT/w42-$B-1.wasm" "$OUT/w42-$B-$n.wasm" 2>/dev/null || echo "output on $n threads differs from 1 thread's"; done
fi
if [ -n "$PROF" ] && [ "$ONLY" != bench ]; then
  P=$OUT/prof-$B-$PROF; rm -rf "$P"; mkdir -p "$P"
  CS2WASM_PROCESSORS=$PROF SELF=$DROOT/tests/out/thr/$B.wasm bash tools/selfrun.sh --cpu-prof --cpu-prof-dir=$DROOT/tests/out/thr/prof-$B-$PROF -- "$DROOT/tests/out/thr/w42.cs" -o "$DROOT/tests/out/thr/w42-prof.wasm" > /dev/null 2>&1 || true
  node tools/profthreads.mjs "$P" 3 | tail -30
  node tools/profsum.mjs "$P" 2>/dev/null | head -40
  for f in StripeWait StripeLock LockGroupOf; do echo "== callers of $f"; node tools/profcallers.mjs "$P" "$f" 4 2>/dev/null | head -40; done
fi

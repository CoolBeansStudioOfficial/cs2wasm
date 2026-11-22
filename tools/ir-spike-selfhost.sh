#!/bin/bash
# The IR spike on the self-hosted compiler (docs/AUDIT.md, "Steps 5 and 6: the IR optimizer, design and spike"):
# two no-threads builds from this checkout, as shipped and with the spike's passes on (their statistics printed), then
# ROUNDS interleaved runs of each on Console.WriteLine(42), one thread, with each run's wall time and peak memory.
# usage (from the checkout's root, e.g. through tools/homelab.sh): bash tools/ir-spike-selfhost.sh [rounds]
set -e
ROOT=$(pwd)
O=$ROOT/tests/out/irspike
mkdir -p $O
rm -rf $O/cli && cp -r src/Cs2Wasm.Cli/bin/Release/net11.0 $O/cli
cat > $O/w42.cs <<'EOF'
System.Console.WriteLine(42);
EOF
export DOTNET_GCHeapHardLimit=0x380000000
if [ ! -f $O/base.wasm ]; then
  s=$(date +%s); CS2WASM_NO_THREADS=1 node tools/selfhost.mjs $O/cli/cs2wasmc.exe $O/base.wasm > $O/base.log 2>&1
  echo "base build $(( $(date +%s) - s )) s: $(grep '^cs2wasm:' $O/base.log | tail -1)"
fi
if [ ! -f $O/ir.wasm ]; then
  s=$(date +%s); CS2WASM_NO_THREADS=1 CS2WASM_IR_PAIRS=1 CS2WASM_IR_PAIRS_THROW=1 CS2WASM_IR_GVN=1 CS2WASM_IR_STATS=1 \
    node tools/selfhost.mjs $O/cli/cs2wasmc.exe $O/ir.wasm > $O/ir.log 2>&1
  echo "ir build $(( $(date +%s) - s )) s: $(grep '^cs2wasm:' $O/ir.log | tail -1)"
fi
grep -E "^ir|^  rejected|^ir loads|^  drop by" $O/ir.log | head -24
set +e
P=/${O#/c/}
T=$(mktemp)
for r in $(seq 1 ${1:-6}); do
  for b in base ir; do
    s=$(date +%s%N); CS2WASM_PRINT_MEMORY=1 CS2WASM_PROCESSORS=1 SELF=$O/$b.wasm timeout 300 bash tools/selfrun.sh $P/w42.cs -o $P/w42-$b.wasm > $T 2>&1; rc=$?; [ $r = 1 ] && [ $rc != 0 ] && tail -5 $T
    pages=$(sed -n 's/^memory: \([0-9]*\) pages$/\1/p' $T)
    echo "$b $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc peak $(( ${pages:-0} * 65536 )) bytes"
  done
done
rm -f $T
cmp $O/w42-base.wasm $O/w42-ir.wasm && echo "outputs byte-identical"

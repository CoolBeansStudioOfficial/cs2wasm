#!/bin/bash
# After `NAMES=1 PROF=1 bash tools/selfphases.sh`: its two profiles (tests/out/selfph/prof, the self-hosted compiler;
# prof-naot, NativeAOT-LLVM's build) taken apart by phase: the libraries' parse (under Library.BuildOne) and the
# first drain (under Driver.Drain), each side's functions by self time under the phase, then who calls the runtime's
# counting and arena functions (two frames up), then the two sides method by method.
#   bash tools/selfprof-report.sh [lines=45]
cd "$(dirname "$0")/.."
N=${1:-45}
P=tests/out/selfph/prof; Q=tests/out/selfph/prof-naot
echo "=== the parse, self-hosted (under Library.BuildOne)"; node tools/profincl.mjs $P "Library.BuildOne" $N | cut -c1-200
[ -d $Q ] && { echo "=== the parse, NativeAOT-LLVM"; node tools/profincl.mjs $Q "Library__BuildOne" $N | cut -c1-200; }
echo "=== the first drain, self-hosted (under Driver.Drain)"; node tools/profincl.mjs $P "Driver.Drain" $N | cut -c1-200
[ -d $Q ] && { echo "=== the first drain, NativeAOT-LLVM"; node tools/profincl.mjs $Q "Driver__Drain" $N | cut -c1-200; }
for f in "rt.release" "rt.retain" "Rt.ArenaRelease" "Rt.ArenaLink" "Rt.ArenaUnlinkOld" "Rt.SlotOwner" "Rt.ArenaRetain" "Rt.LinkMembers" "rt.frame" "virtual "; do
  echo "=== who calls $f"; node tools/profcallers.mjs $P "$f" 2 14 | cut -c1-260
done
if [ -d $Q ]; then
  echo "=== method by method"
  node tools/profcompare.mjs $(ls $P/*.cpuprofile | head -1) $(ls $Q/*.cpuprofile | head -1) | head -90 | cut -c1-200
fi

#!/bin/bash
# The fast stand-in for the self-hosted compiler's speed (tools/roslyn-bind-bench/Bench.cs: Roslyn parsing
# System.Private.CoreLib's sources and binding the method bodies hello world reaches, as the compiler's front end does),
# built by cs2wasm and by NativeAOT-LLVM and timed interleaved in Node with V8's normal tiering. Git Bash on Windows.
# usage: bash tools/roslyn-bind-bench.sh build [name [cs2wasmc]]   cs2wasm's build to tests/out/bindbench/<name>.wasm
#                                                                (default name: bb; cs2wasmc: this checkout's Release build)
#        bash tools/roslyn-bind-bench.sh build-at <commit>...    a past commit's compiler built, then the bench by it
#                                                                as p-<commit> (to compare a change with its parent)
#        bash tools/roslyn-bind-bench.sh build-naot          NativeAOT-LLVM's build (EMSDK or C:/tools/emsdk; once)
#        bash tools/roslyn-bind-bench.sh time [name...]         ROUNDS (default 5) interleaved rounds: each cs2wasm
#                                                                build named, then NativeAOT-LLVM's; BODIES (default 400)
# Each line: the build, its wall time in ms, its exit code, then the program's own phases (parse, declare, bind).
# cs2wasm's build has no threads (CS2WASM_NO_THREADS=1), as NativeAOT-LLVM's browser-wasm, and runs told one processor.
# Heavy: run builds and timings on the homelab (tools/homelab.sh); NativeAOT's build needs emsdk (this machine only,
# a small build) and its tests/out/bindbench/naot can be copied there.
# Cost (homelab, 2026-11-21): cs2wasm's build 2.2-4 min, 18-23 MB; NativeAOT's 53 s; a run 6.5 s against 3.0 s.
# How it tracked the self-hosted compiler (homelab, 5 interleaved rounds, BODIES=400; docs/FINISH.md's Log for the
# self-hosted side): main 2.17x NativeAOT (self-hosted 1.98x); stream J (fd83e75 to 95ceb64) -23% (-20%); stream N
# (a85d88f to 3be4326) -2.5% (-5%); N round 2 (480a101 to 96ed1d7) -3% (-2%); K round 4 (05e8cd0 to 1191d18) 0 to -1%
# (-1.5 to -2.3%: below what it resolves); K round 3 (3be4326 to 480a101) +4 to +6% where the self-hosted compiler was
# unchanged: round 2's inlining still shows in the parse phase here, and the 10 MB less code to compile that took it
# back there does not (this module is a quarter the size). So: a change it shows above about 3% in the same direction
# as the bind phase is likely real; a parse-phase gain from inlining needs the self-hosted compiler to confirm.
# Rounds move 3% with the machine's load; compare medians from one interleaved run only.
set -e
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ROOTW="$(cd "$ROOT" && pwd -W 2>/dev/null || pwd)"
OUT=$ROOT/tests/out/bindbench
NAOT=$OUT/naot
mkdir -p "$OUT"
SRC=$ROOTW/tools/roslyn-bind-bench
BIN=$ROOTW/src/Cs2Wasm.Cli/bin/Release/net11.0
CACHE=${CS2WASM_CACHE:-C:/Users/$USERNAME/AppData/Local/cs2wasm}
CACHE=$(cygpath -m "$CACHE" 2>/dev/null || echo "$CACHE")
RT=$CACHE/runtime-v11.0.0
UP=$ROOTW/src/Cs2Wasm/Upstream
# host/run.mjs reads files under CS2WASM_FS_ROOT (C:/) by drive-less paths; NativeAOT's build sees the drive at /c
nodrive() { echo "${1#?:}"; }
mounted() { local d=${1:0:1}; echo "/${d,,}${1#?:}"; }
cmd=${1:-time}; shift || true
case $cmd in
build)
  name=${1:-bb}; cli=${2:-$BIN/cs2wasmc.exe}
  s=$(date +%s%N)
  CS2WASM_NO_THREADS=1 DOTNET_GCHeapHardLimit=${HEAP:-0x300000000} "$cli" "$SRC/Bench.cs" "$SRC/Cs2WasmMain.cs" \
    -r "$BIN/Microsoft.CodeAnalysis.dll" -r "$BIN/Microsoft.CodeAnalysis.CSharp.dll" -o "$(cygpath -m "$OUT")/$name.wasm" $CS2WASMC_ARGS
  # (a module runs with the host of the checkout whose compiler built it: <checkout>/src/Cs2Wasm.Cli/bin/Release/net11.0;
  # kept relative to this checkout when inside it, so that tests/out/bindbench can be copied to another machine)
  h="$(cd "$(dirname "$cli")/../../../../.." && pwd)/host/run.mjs"
  echo "${h#$ROOT/}" > "$OUT/$name.host"
  echo "built $name.wasm in $(( ($(date +%s%N)-s)/1000000 )) ms, $(stat -c %s "$OUT/$name.wasm") bytes"
  ;;
build-at)
  # a past commit's compiler (its src, host and build files from git, built in tests/out/bindbench/c-<commit>), then
  # the bench built by it as p-<commit>
  for c in "$@"; do
    D=$OUT/c-$c
    if [ ! -f "$D/src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc.exe" ]; then
      mkdir -p "$D"
      git -C "$ROOTW" archive "$c" src host Directory.Build.props global.json | tar -x -C "$D"
      (cd "$D" && dotnet build src/Cs2Wasm.Cli -c Release -v q -nologo 2>&1 | grep -E " error |rror\(s\)" || true)
    fi
    [ -f "$OUT/p-$c.wasm" ] || bash "$0" build "p-$c" "$D/src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc.exe" 2>&1 | grep -v "^cs2wasm: "
  done
  ;;
build-naot)
  EMSDK=${EMSDK:-C:/tools/emsdk}; export EMSDK
  E=$(cygpath -u "$EMSDK")
  export PATH="$E:$E/upstream/emscripten:$PATH"
  py=$(ls -d "$E"/python/*/ 2>/dev/null | head -1); [ -n "$py" ] && export EMSDK_PYTHON="$(cygpath -m "$py")python.exe"
  nd=$(ls -d "$E"/node/*/ 2>/dev/null | head -1); [ -n "$nd" ] && export EMSDK_NODE="$(cygpath -m "$nd")bin/node.exe"
  s=$(date +%s%N)
  dotnet publish "$SRC/roslyn-bind-bench-naot.csproj" -c Release -o "$(cygpath -m "$NAOT")" -v q -nologo
  echo "built naot in $(( ($(date +%s%N)-s)/1000000 )) ms, $(stat -c %s "$NAOT/bind-bench-naot.wasm") bytes"
  ;;
time)
  [ $# -gt 0 ] || set -- bb
  T=$(mktemp); L=$(mktemp)
  for r in $(seq 1 ${ROUNDS:-5}); do
    for b in "$@"; do
      s=$(date +%s%N)
      host=$(cat "$OUT/$b.host" 2>/dev/null || echo "host/run.mjs")
      case $host in /*|?:*) ;; *) host=$ROOTW/$host ;; esac
      CS2WASM_PROCESSORS=1 CS2WASM_FS_ROOT=C:/ node "$host" "$(cygpath -m "$OUT")/$b.wasm" main -- "$(nodrive "$UP")" "$(nodrive "$RT")" ${BODIES:-400} > $T 2>&1 && rc=0 || rc=$?
      echo "$b $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc $(head -1 $T)" | tee -a $L
      [ $r = 1 ] && grep '^check\|rror' $T | head -3
    done
    if [ -f "$NAOT/bind-bench-naot.js" ]; then
      s=$(date +%s%N)
      (cd "$NAOT" && node bind-bench-naot.js "$(mounted "$UP")" "$(mounted "$RT")" ${BODIES:-400} > $T 2>&1) && rc=0 || rc=$?
      echo "naot $(( ($(date +%s%N)-s)/1000000 )) ms rc=$rc $(head -1 $T)" | tee -a $L
      [ $r = 1 ] && grep '^check\|rror' $T | head -3
    fi
  done
  node "$SRC/summary.mjs" < $L
  rm -f $T $L
  ;;
*) echo "usage: $0 build [name [cs2wasmc]] | build-at <commit>... | build-naot | time [name...]"; exit 2 ;;
esac

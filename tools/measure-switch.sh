#!/bin/bash
# A compiler switch measured every way that needs no NativeAOT build, in one go (one slot on the homelab): the frame
# scenes and the benchmarks with and without it in turn (tools/frames-ab.mjs, tools/bench-ab.mjs), programs of their
# own (tools/ab-switch.sh), the self-hosted compiler built both ways and timed (tools/selfhost-switch.sh: build time,
# run time, memory, the two outputs), and the self-hosted compiler as it is against the .NET-hosted one, byte for
# byte (tools/selfdet.mjs).
#   bash tools/measure-switch.sh CS2WASM_NO_FIELD_HOIST=1 [X=1,Y=1 ... : more builds for the frames and the benchmarks] [program.cs ...]
#   on the homelab: bash tools/homelab.sh <branch> bash tools/measure-switch.sh CS2WASM_NO_FIELD_HOIST=1 tools/churn-array.cs
#   SELF=0: without the self-hosted compiler's part (a quarter of an hour less). FRAMES=0: without the frames and the
#   benchmarks. ROUNDS=5. SELFDET="<test> ...": the tests the self-hosted compiler compiles besides hello world.
cd "$(dirname "$0")/.."
S=${1:?the switch, e.g. CS2WASM_NO_X=1}; shift
R=${ROUNDS:-5}
extra=(); programs=()
i=0
for x in "$@"; do
  case "$x" in
    *.cs) programs+=("$x");;
    *) i=$((i + 1)); extra+=("alt$i=$x");;
  esac
done
if [ "${FRAMES:-1}" = 1 ]; then
echo "== frames (as it is; switched: $S; ${extra[*]})"
node tools/frames-ab.mjs --rounds $R --js-young 32 as-is= "switched=$S" "${extra[@]}" 2>&1 | grep -v "CW0202\|^cs2wasm:"
echo "== benchmarks"
node tools/bench-ab.mjs --rounds $R as-is= "switched=$S" "${extra[@]}" 2>&1 | grep -v "CW0202\|^cs2wasm:"
fi
for p in "${programs[@]}"; do
  echo "== $p"
  bash tools/ab-switch.sh "$S" "$p" $R
done
if [ "${SELF:-1}" = 1 ]; then
  echo "== the self-hosted compiler, as it is and with $S"
  bash tools/selfhost-switch.sh "$S"
  ls -la tests/out/selfsw/*.wasm 2>&1 | awk '{print $5, $9}'
  echo "== the self-hosted compiler against the .NET-hosted one"
  node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm tests/Conformance/145_size_hello.cs
  for t in $(echo "${SELFDET:-230_field_read_in_loops 232_field_read_indirect_stores}" | tr "," " "); do
    node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm tests/Conformance/$t.cs 2>&1 | grep -v "^    at \|^$\|^Node.js"
  done
  echo "== the self-hosted compiler built with $S against the .NET-hosted one"
  for t in 145_size_hello $(echo "${SELFDET:-230_field_read_in_loops}" | tr "," " "); do
    node tools/selfdet.mjs --self tests/out/selfsw/switched.wasm tests/Conformance/$t.cs 2>&1 | grep -v "^    at \|^$\|^Node.js"
  done
fi

#!/bin/bash
<<<<<<< HEAD
# tools/selfdet.mjs for several programs, one after the other (given together they are one program's sources): the
# self-hosted compiler tools/selfhost-switch.sh left (tests/out/selfsw/as-is.wasm) against the .NET-hosted one, each
# test's module byte for byte. One command with no argument that holds a space, for tools/homelab.sh:
#   bash tools/homelab.sh <branch> bash tools/selfdet-set.sh            (the four the overnight rules name)
#   bash tools/selfdet-set.sh 202_ 203_ 204_                            (others, by the start of their names)
cd "$(dirname "$0")/.."
bad=0
for p in ${*:-220_ 193_ 088_}; do
  t=$(ls tests/Conformance | grep -E "^$p" | head -1)
  if [ -z "$t" ]; then echo "no test $p"; bad=1; continue; fi
  echo "== $t"
  node tools/selfdet.mjs --self ${SELF:-tests/out/selfsw/as-is.wasm} --cores 1 tests/Conformance/$t 2>&1 | tail -3
=======
# After tools/selfhost-switch.sh (which leaves the self-hosted compiler at tests/out/selfsw/as-is.wasm and compares
# hello world): the other programs the self-host check asks for, each compiled by the self-hosted compiler and by the
# .NET-hosted one (tools/selfdet.mjs): each must say "self-1 = net, byte for byte". 088 is a program with threads.
#   bash tools/selfdet-set.sh [tests...]       on the homelab: bash tools/homelab.sh <branch> bash tools/selfdet-set.sh
cd "$(dirname "$0")/.."
bad=0
for t in ${*:-220_collections_grow_by_moving 193_subtrees_replaced_and_moved 088_feature_sweep_2}; do
  echo "== $t"
  node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm --cores 1 tests/Conformance/$t.cs 2>&1 | tail -3
>>>>>>> origin/claude/threads-1
  [ ${PIPESTATUS[0]} = 0 ] || bad=1
done
exit $bad

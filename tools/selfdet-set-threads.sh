#!/bin/bash
# After tools/selfhost-switch.sh (which leaves the self-hosted compiler at tests/out/selfsw/as-is.wasm and compares
# hello world): the other programs the self-host check asks for, each compiled by the self-hosted compiler and by the
# .NET-hosted one (tools/selfdet.mjs): each must say "self-1 = net, byte for byte". 088 is a program with threads.
#   bash tools/selfdet-set.sh [tests...]       on the homelab: bash tools/homelab.sh <branch> bash tools/selfdet-set.sh
cd "$(dirname "$0")/.."
bad=0
for t in ${*:-220_collections_grow_by_moving 193_subtrees_replaced_and_moved 088_feature_sweep_2}; do
  echo "== $t"
  node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm --cores 1 tests/Conformance/$t.cs 2>&1 | tail -3
  [ ${PIPESTATUS[0]} = 0 ] || bad=1
done
exit $bad

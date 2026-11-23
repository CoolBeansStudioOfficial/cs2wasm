#!/bin/bash
# What a change to the self-hosted compiler's speed is checked with, in one turn on the homelab: the self-hosted
# compiler built (no threads), hello world with and without the switches given (ROUNDS rounds, in turn with
# NativeAOT-LLVM's build), its modules against the .NET-hosted compiler's on four programs (tools/selfdet.mjs), the
# snapshot (tools/selfsnap.mjs), then two larger programs with and without the switches (3 rounds each) and four
# builds in one instance.
#   bash tools/selfhost-speed-final.sh IL2WASM_NO_LIB_DOC_SKIP=1 IL2WASM_NO_LIB_RESTART=1
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-speed-final.sh <switches>
cd "$(dirname "$0")/.."
env ROUNDS=${ROUNDS:-5} VERIFY=1 SNAP=1 bash tools/selfenv-ab.sh "$@"
for p in tests/Conformance/220_collections_grow_by_moving.cs tests/Conformance/088_feature_sweep_2.cs; do
  echo "##### $p"
  env BUILD=0 ROUNDS=3 PROG=$p bash tools/selfenv-ab.sh "$@" | grep -v -E "^ +[0-9]+ ms  encode:|library: +[0-9]+ ms  "
done

#!/bin/bash
# The self-host check in one run (one job of tools/homelab.sh): tools/selfhost-switch.sh with the switches given (the
# self-hosted compiler built as it is and with them, each compiling hello world three times: time, exit code, peak
# memory, the same module, and hello world against the .NET-hosted compiler byte for byte), then tools/selfdet.mjs
# with the as-is build on hello world and three larger programs (a heap corruption once showed only on programs
# larger than hello world: 220, 193, 088) and those named in SET, by the start of their names (tools/selfdet-set.sh):
# each must say "self-1 = net, byte for byte". Exits 1 when one differs or does not end.
#   bash tools/selfhost-check.sh [IL2WASM_NO_X=1 ...]
#   bash tools/homelab.sh <branch> env SET=202_,203_,204_ bash tools/selfhost-check.sh IL2WASM_NO_UNLINK_SKIP=1 ...
# (two sessions wrote this script the same night, claude/tree-3 and claude/churn-5; this is the two in one)
cd "$(dirname "$0")/.."
rc=0
bash tools/selfhost-switch.sh "$@" || rc=1
bash tools/selfdet-set.sh 145_size_hello 220_ 193_ 088_ $(echo "$SET" | tr ',' ' ') || rc=1
echo "selfhost-check: $([ $rc = 0 ] && echo all the same || echo FAILED)"
exit $rc

#!/bin/bash
# The self-host check in one run (one job of tools/homelab.sh): tools/selfhost-switch.sh with the switches given (the
# self-hosted compiler built as it is and with them, each compiling hello world three times: time, exit code, peak
# memory, the same module), then tools/selfdet.mjs with the as-is build on hello world and three larger programs
# (a heap corruption once showed only on programs larger than hello world): each must say "self-1 = net, byte for
# byte". Exits 1 when one differs or does not end.
#   bash tools/selfhost-check.sh [CS2WASM_NO_X=1 ...]
cd "$(dirname "$0")/.."
bash tools/selfhost-switch.sh "$@"
rc=0
for t in tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs; do
  echo "--- $t"
  node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm --cores 1 $t 2>&1 | tail -3
  [ ${PIPESTATUS[0]} = 0 ] || rc=1
done
echo "selfhost-check: $([ $rc = 0 ] && echo all the same || echo FAILED)"
exit $rc

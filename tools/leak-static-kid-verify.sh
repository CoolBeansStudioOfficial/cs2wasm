#!/bin/bash
# The verification of claude/leak-static-kid (Rt.TurnOver) after the suite, in one turn on the homelab:
#   1 stress of tests 300, 301, 127, 139, 187, 192 to 201 (tools/stress-cycles.sh, 200 loaded runs a mode);
#   2 the self-hosted compiler built as is and with CS2WASM_NO_TURN_OVER=1, hello world timed (tools/selfhost-switch.sh);
#   3 tools/selfdet.mjs with the as-is self-hosted compiler on 145, 220, 193, 088
#   on the homelab: bash tools/homelab.sh claude/leak-static-kid bash tools/leak-static-kid-verify.sh [first step]
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
FROM=${1:-1}
st() { echo "##### step $1 at $(date +%H:%M:%S): $2"; }
[ $FROM -le 1 ] && { st 1 "stress"; TESTS=300_,301_,127_,139_,187_,192_,193_,194_,195_,196_,197_,198_,199_,201_ bash tools/stress-cycles.sh 2>&1 | grep -v -E "^ +[0-9]+/"; }
[ $FROM -le 2 ] && { st 2 "self-hosted compiler, hello world"; ROUNDS=3 bash tools/selfhost-switch.sh CS2WASM_NO_TURN_OVER=1 2>&1; }
[ $FROM -le 3 ] && { st 3 "selfdet"; for t in tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs; do echo "--- $t"; node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm --cores 1 $t 2>&1 | tail -4; done; }
st end "done"

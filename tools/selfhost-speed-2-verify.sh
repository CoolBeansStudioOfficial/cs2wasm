#!/bin/bash
# The verification of claude/selfhost-speed-2 in one turn on the homelab (one slot, the steps in order of what they
# prove; each step's output as it comes, and in tests/out/verify-chain.log):
#   1 the suite, checked and as shipped; 2 the self-hosted compiler built (no threads), hello world with and without
#   CS2WASM_NO_LIB_DOC_SKIP=1 CS2WASM_NO_LIB_RESTART=1 (the same module), tools/selfdet.mjs on 145, 220, 193, 088;
#   3 tools/selfsnap.mjs: every module written on a snapshot the .NET-hosted compiler's (145, 220, 193, 088 and two
#   programs with threads), hello world from the start, on a snapshot and NativeAOT-LLVM's build in turn; 4
#   tools/selfsnap-stale.mjs: a snapshot that no longer holds is let go; 5 test 088 from the start, on a snapshot and
#   NativeAOT-LLVM's in turn; 6 stress of 250, 127 and 139; 7 tools/libs-rules-check.sh (every program, the same
#   modules across the library rules; the host's snapshot rules)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfhost-speed-2-verify.sh [first step]
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1
FROM=${1:-1}
MAIN=/c/Users/$USERNAME/Documents/GitHub/cs2wasm
NAOT="$(cygpath -m $MAIN/tests/out/self2/naot-wasm-measure 2>/dev/null || echo $MAIN/tests/out/self2/naot-wasm-measure)"
LOG=tests/out/verify-chain.log
: > $LOG
st() { echo "##### step $1 at $(date +%H:%M:%S): $2"; }
{
[ $FROM -le 1 ] && { st 1 "the suite"; node tools/suite.mjs 2>&1 | tail -15; }
[ $FROM -le 2 ] && { st 2 "the self-hosted compiler built; hello world; selfdet"; env ROUNDS=3 VERIFY=1 bash tools/selfenv-ab.sh CS2WASM_NO_LIB_DOC_SKIP=1 CS2WASM_NO_LIB_RESTART=1 2>&1 | grep -v -E "^ +[0-9]+ ms  encode:|library: +[0-9]+ ms  "; }
[ $FROM -le 3 ] && { st 3 "selfsnap: 145 first, then 220 193 088 139 197"; node tools/selfsnap.mjs --self tests/out/selfsw/as-is.wasm --rounds 5 --naot "$NAOT" tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs tests/Conformance/139_*.cs tests/Conformance/197_*.cs 2>&1; }
[ $FROM -le 4 ] && { st 4 "selfsnap-stale"; node tools/selfsnap-stale.mjs --self tests/out/selfsw/as-is.wasm 2>&1; }
[ $FROM -le 5 ] && { st 5 "selfsnap: 088 first"; node tools/selfsnap.mjs --self tests/out/selfsw/as-is.wasm --rounds 3 --naot "$NAOT" tests/Conformance/088_*.cs 2>&1 | grep -v -E "^ +[0-9]+ ms +[0-9]+ MB"; }
[ $FROM -le 6 ] && { st 6 "stress 250 127 139"; bash tools/stress-also.sh 250_library_left_out_then_needed 2>&1 | tail -30; }
[ $FROM -le 7 ] && { st 7 "libs-rules-check"; bash tools/libs-rules-check.sh 4 2>&1 | tail -40; }
st end "done"
} 2>&1 | tee -a $LOG

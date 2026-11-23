#!/bin/bash
# The rules of what a compile reads, against their switches, over every conformance program, the benchmark programs
# and tools/corelibs (tools/corelibs-check.mjs: the same module byte for byte, or the same failure): both switches
# at once (a compile as it was before the rules), then the host's snapshot rules (tools/snapshot-check.sh). Exits 1
# when one fails.
#   bash tools/libs-rules-check.sh [par=4]         on the homelab: bash tools/homelab.sh <branch> bash tools/libs-rules-check.sh
#   EACH=1: each switch in a pass of its own as well; ALL=1: and the older rule (the libraries a program can reach
#   against all the core libraries)
cd "$(dirname "$0")/.."
PAR=${1:-4}
rc=0
echo "=== as it was: every reported library from the start, the libraries' documentation comments parsed"
node tools/corelibs-check.mjs --par $PAR --env IL2WASM_NO_LIB_RESTART=1 --env IL2WASM_NO_LIB_DOC_SKIP=1 || rc=1
if [ "${EACH:-0}" = 1 ]; then
  echo "=== a library reported only for a candidate left out, against every reported library from the start"
  node tools/corelibs-check.mjs --par $PAR --env IL2WASM_NO_LIB_RESTART=1 || rc=1
  echo "=== the libraries' documentation comments passed over, against parsed"
  node tools/corelibs-check.mjs --par $PAR --env IL2WASM_NO_LIB_DOC_SKIP=1 || rc=1
fi
if [ "${ALL:-0}" = 1 ]; then
  echo "=== the libraries a program can reach, against all the core libraries (ALL=1)"
  node tools/corelibs-check.mjs --par $PAR || rc=1
fi
echo "=== the host's snapshot"
bash tools/snapshot-check.sh | tail -1
[ ${PIPESTATUS[0]} = 0 ] || rc=1
echo "libs-rules-check: $([ $rc = 0 ] && echo passed || echo FAILED)"
exit $rc

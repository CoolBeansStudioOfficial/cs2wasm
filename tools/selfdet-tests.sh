#!/bin/bash
# The self-hosted compiler built in this checkout (tools/selfhost-switch.sh: as it is, and with the switch given,
# IL2WASM_NO_TURN_HOLD=1 by default) against the .NET-hosted one on hello world and the tests named, byte for byte
# (tools/selfdet.mjs): a fault of the runtime that shows only in a program larger than hello world.
#   bash tools/selfdet-tests.sh 220_ 193_ 088_          (SWITCH=IL2WASM_NO_X=1)
#   on the homelab: bash tools/homelab.sh <branch> bash tools/selfdet-tests.sh 220_ 193_ 088_
cd "$(dirname "$0")/.."
bad=0
bash tools/selfhost-switch.sh "${SWITCH:-IL2WASM_NO_TURN_HOLD=1}" || bad=1
ls -la tests/out/selfsw/*.wasm 2>&1 | awk '{print $5, $9}'
for t in 145_ "$@"; do
  f=$(ls tests/Conformance/$t*.cs 2>/dev/null | head -1)
  if [ -z "$f" ]; then echo "no test $t"; bad=1; continue; fi
  echo "== $f"
  node tools/selfdet.mjs --self tests/out/selfsw/as-is.wasm $f 2>&1 | grep -v "^    at \|^$\|^Node.js" || true
  [ ${PIPESTATUS[0]} = 0 ] || bad=1
done
echo "selfdet-tests: $([ $bad = 0 ] && echo all the same || echo FAILED)"
exit $bad

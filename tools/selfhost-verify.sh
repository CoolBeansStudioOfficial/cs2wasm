#!/bin/bash
# The self-hosted compiler built by tools/selfphases.sh (tests/out/selfph/self.wasm) writes the .NET-hosted
# compiler's modules byte for byte: tools/selfdet.mjs on hello world and on three larger programs (a heap corruption
# once showed only on programs larger than hello world). Exits 1 when one differs or does not end.
#   bash tools/selfhost-verify.sh [program.cs ...]
cd "$(dirname "$0")/.."
T=${*:-tests/Conformance/145_size_hello.cs tests/Conformance/220_*.cs tests/Conformance/193_*.cs tests/Conformance/088_*.cs}
rc=0
for t in $T; do
  echo "--- $t"
  node tools/selfdet.mjs --self tests/out/selfph/self.wasm --cores 1 $t 2>&1 | tail -4 || rc=1
  [ ${PIPESTATUS[0]} = 0 ] || rc=1
done
echo "selfhost-verify: $([ $rc = 0 ] && echo all the same || echo FAILED)"
exit $rc

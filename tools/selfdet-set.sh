#!/bin/bash
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
  [ ${PIPESTATUS[0]} = 0 ] || bad=1
done
exit $bad

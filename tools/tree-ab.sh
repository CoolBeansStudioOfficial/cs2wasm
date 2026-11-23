#!/bin/bash
# The tree's tools with and without compiler switches, one switch (or a comma list taken together) after the other:
# tools/ab-switch.sh for tools/tree-build.cs (the build and the drop of the scene tree), tools/tree-frame.cs (an
# ordinary frame by phase), tools/tree-parts.cs (the rebuild in the scene) and tools/tree-node.cs (a node by its
# steps). One command with no argument that holds a space, for tools/homelab.sh:
#   bash tools/homelab.sh <branch> bash tools/tree-ab.sh CS2WASM_NO_UNLINK_SKIP=1 CS2WASM_NO_PLAIN_PARTS=1,CS2WASM_NO_PLAIN_PARENTS=1
#   ROUNDS=5 (rounds of each program), PROGRAMS="tree-build tree-frame" (which)
cd "$(dirname "$0")/.."
for s in "$@"; do
  for p in ${PROGRAMS:-tree-build tree-frame tree-parts}; do
    echo "== $s: tools/$p.cs"
    bash tools/ab-switch.sh "$s" tools/$p.cs ${ROUNDS:-5} | cut -c1-260
  done
done

#!/bin/bash
# tools/stress-cycles.sh for the tests of the trees' paths as well as its own two: 127 and 139, then 201 to 203 and 192 to
# 199 (RUNS, PAR, MODES as there). One command with no argument that holds a space, for tools/homelab.sh:
#   bash tools/homelab.sh <branch> bash tools/stress-trees.sh
cd "$(dirname "$0")/.."
bad=0
bash tools/stress-cycles.sh || bad=1
T=$(ls tests/Conformance | grep -E "^(20[123]|19[2-9])_" | sed 's/\.cs$//' | tr '\n' ' ')
TESTS="$T" bash tools/stress-cycles.sh || bad=1
exit $bad

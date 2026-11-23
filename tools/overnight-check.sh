#!/bin/bash
# The checks the overnight rules ask of a final code commit, in one turn of the homelab's queue (the queue is a
# lottery among every session's jobs; three turns of it were an evening): the suite in both modes, the self-host
# check (tools/selfhost-check.sh with the switches given, SET for more tests than its three), and the stress of the
# trees' tests with 127 and 139 (tools/stress-trees.sh). Each part's lines are kept in tests/out/overnight/, and the
# last lines of each are printed at the end again.
#   bash tools/homelab.sh <branch> env SET=202_,203_,204_ bash tools/overnight-check.sh IL2WASM_NO_UNLINK_SKIP=1 ...
cd "$(dirname "$0")/.."
O=tests/out/overnight; mkdir -p $O
bad=0
echo "== suite"; node tools/suite.mjs > $O/suite.txt 2>&1 || bad=1; tail -4 $O/suite.txt
echo "== self-host"; bash tools/selfhost-check.sh "$@" > $O/selfhost.txt 2>&1 || bad=1; tail -40 $O/selfhost.txt
echo "== stress"; bash tools/stress-trees.sh > $O/stress.txt 2>&1 || bad=1; grep -E "^==|runs, |^PASS|^FAIL|^ +[0-9]+  " $O/stress.txt | cut -c1-160 | tail -150
echo "== overnight-check: $([ $bad = 0 ] && echo all passed || echo SOMETHING FAILED)"
exit $bad

#!/bin/bash
# The verification of claude/thread-hangs-1 and claude/thread-leaks-1 after the suite and the stress, in one homelab
# turn: 1 the self-host check (tools/selfhost-switch.sh: the self-hosted compiler built, hello world timed, self-1 =
# net on 145; then tools/selfdet-set.sh: 220, 193, 088); 2 the two programs that hung, 2,000 loaded runs each with
# every thread's stack dumped if one hangs (tools/thread-hang-stress.sh: tools/thread-notes-hang.cs as shipped,
# tools/thread-board-hang.cs checked, as their notes say they were built); 3 tests/Load/215b (a test of tests/Load,
# which tools/stress-cycles.sh does not build), 500 loaded runs a mode.
#   bash tools/homelab.sh <branch> bash tools/thread-hangs-verify.sh [first step]
cd "$(dirname "$0")/.."
FROM=${1:-1}
st() { echo "##### step $1 at $(date +%H:%M:%S): $2"; }
[ $FROM -le 1 ] && { st 1 "self-host check"; bash tools/selfhost-switch.sh 2>&1 | tail -12; bash tools/selfdet-set.sh 2>&1 | tail -12; }
[ $FROM -le 2 ] && { st 2 "the two programs, 2,000 runs each"
  env MODES=shipped RUNS=2000 PAR=16 DUMPLINES=60 bash tools/thread-hang-stress.sh tools/thread-notes-hang.cs
  env MODES=checked RUNS=2000 PAR=16 DUMPLINES=60 bash tools/thread-hang-stress.sh tools/thread-board-hang.cs; }
[ $FROM -le 3 ] && { st 3 "tests/Load/215b, 500 loaded runs a mode"; env RUNS=500 PAR=8 DUMPLINES=60 bash tools/thread-hang-stress.sh tests/Load/215b_stale_notes_threads.cs; }
st end "done"

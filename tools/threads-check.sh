#!/bin/bash
# The checks of a change to the runtime's code for threads, one after the other (for tools/homelab.sh, one job):
#   share     every program with threads under the share check (tools/thread-share-check.sh)
#   breaks    the escape rules' tests, each rule weakened and off (tools/thread-escape-breaks.sh)
#   suite     the suite, both modes
#   forced    the suite with every program compiled for threads (CS2WASM_EXP_THREADMODE=1: run on one thread, every
#             group of classes is one thread's, Arenas.LocalScc)
#   stress    the thread and cycle tests under load (RUNS=500 a mode by default here)
#   selfhost  the self-hosted compiler built and run (tools/selfhost-switch.sh), byte for byte on 220, 193, 088
#   bash tools/threads-check.sh [part...]        (default: all of them)
cd "$(dirname "$0")/.."
parts=${*:-share breaks suite forced stress selfhost}
for p in $parts; do
  echo "=== $p $(date +%H:%M)"
  case $p in
    share) bash tools/thread-share-check.sh | grep -v ": the same" ;;
    breaks) bash tools/thread-escape-breaks.sh ;;
    suite) node tools/suite.mjs 2>&1 | grep -v "^  " ;;
    forced) CS2WASM_EXP_THREADMODE=1 node tools/suite.mjs 2>&1 | grep -v "^  " ;;
    stress) RUNS=${RUNS:-500} TESTS="139_racing_cyclic_stores 218_racing_stores_many_rounds 215b_stale_notes_threads 127_everything_is_freed 187_two_tops_one_cycle 131_async_children_are_freed 275_engine_thread_statics 276_captured_locals_between_threads 277_empty_arrays_every_thread_reads 278_array_copies_between_threads 279_private_work_and_handed_results 290_groups_of_one_thread 291_fixed_empty_arrays 305_thread_loops_as_without 306_flat_destroys_and_arrays_with_threads" bash tools/stress-cycles.sh 2>&1 | grep -v "^PASS" ;;
    selfhost) bash tools/selfhost-switch.sh X=1; bash tools/selfdet-set.sh 145_ 220_ 193_ 088_ ;;
  esac
done
echo "=== done $(date +%H:%M)"

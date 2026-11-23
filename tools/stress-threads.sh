#!/bin/bash
# The thread tests many times under load, in one job (tools/stress-cycles.sh for each: built as shipped and checked,
# RUNS runs, PAR at once, every run of a module must print the same): the thread escape analysis's tests 275 to 279
# and the cycle tests that use threads. A wrapper because tools/homelab.sh hands a command on word by word.
#   bash tools/stress-threads.sh            on the homelab: bash tools/homelab.sh <branch> bash tools/stress-threads.sh
cd "$(dirname "$0")/.."
TESTS="${TESTS:-275_engine_thread_statics 276_captured_locals_between_threads 277_empty_arrays_every_thread_reads 278_array_copies_between_threads 279_private_work_and_handed_results 187_two_tops_one_cycle 131_async_children_are_freed}" bash tools/stress-cycles.sh

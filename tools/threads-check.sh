#!/bin/bash
# The checks of a change to the runtime's code for threads, one after the other (for tools/homelab.sh, one job):
# the share check over every program with threads, the escape rules' tests, the suite both modes, and the suite
# with every program compiled for threads (CS2WASM_EXP_THREADMODE=1: run on one thread, every group of classes is
# one thread's, Arenas.LocalScc). Give the parts to run, default all: share breaks suite forced
#   bash tools/threads-check.sh [share] [breaks] [suite] [forced]
cd "$(dirname "$0")/.."
parts=${*:-share breaks suite forced}
for p in $parts; do
  echo "=== $p"
  case $p in
    share) bash tools/thread-share-check.sh | grep -v ": the same" ;;
    breaks) bash tools/thread-escape-breaks.sh ;;
    suite) node tools/suite.mjs 2>&1 | grep -v "^  " ;;
    forced) CS2WASM_EXP_THREADMODE=1 node tools/suite.mjs 2>&1 | grep -v "^  " ;;
  esac
done

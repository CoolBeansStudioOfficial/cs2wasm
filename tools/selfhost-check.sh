#!/bin/bash
# The self-host check of the overnight rules in one job: tools/selfhost-switch.sh with the switches given (the
# self-hosted compiler built as it is and with them, both run on hello world, their modules compared, and hello world
# against the .NET-hosted compiler byte for byte), then tools/selfdet-set.sh with the compiler it built (tests 220,
# 193, 088 and those named in SET, by the start of their names).
#   bash tools/homelab.sh <branch> env SET=202_,203_,204_ bash tools/selfhost-check.sh CS2WASM_NO_UNLINK_SKIP=1 ...
cd "$(dirname "$0")/.."
bad=0
bash tools/selfhost-switch.sh "$@" || bad=1
bash tools/selfdet-set.sh 220_ 193_ 088_ $(echo "$SET" | tr ',' ' ') || bad=1
exit $bad

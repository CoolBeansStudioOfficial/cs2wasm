#!/bin/bash
# tools/stress-cycles.sh on tests 127 and 139 and on the tests named, in one turn (each name its own word, as
# tools/homelab.sh passes them): a session's new tests stressed with the two that always are.
#   bash tools/stress-also.sh 250_library_left_out_then_needed
#   on the homelab: bash tools/homelab.sh <branch> bash tools/stress-also.sh <test>...
cd "$(dirname "$0")/.."
TESTS="127_everything_is_freed 139_racing_cyclic_stores $*" bash tools/stress-cycles.sh

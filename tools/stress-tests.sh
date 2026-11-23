#!/bin/bash
# tools/stress-cycles.sh for the tests named as arguments (through tools/homelab.sh an argument is one word, and
# TESTS wants several): bash tools/homelab.sh <branch> bash tools/stress-tests.sh 230_field_read_in_loops 231_field_read_address_taken
cd "$(dirname "$0")/.."
TESTS="$*" exec bash tools/stress-cycles.sh

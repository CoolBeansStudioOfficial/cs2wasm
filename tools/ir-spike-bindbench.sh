#!/bin/bash
# The IR spike on tools/roslyn-bind-bench.sh (docs/AUDIT.md, "Steps 5 and 6: the IR optimizer, design and spike"):
# the bench built as shipped (ir0), with count pairs (irp), with loads reused (irg) and with both (irb), the spike's
# statistics printed, then ROUNDS (default 7) interleaved rounds. usage: bash tools/ir-spike-bindbench.sh
set -e
B="bash tools/roslyn-bind-bench.sh"
$B build ir0 2>&1 | grep -E "^built|error" || true
CS2WASM_IR_PAIRS=1 CS2WASM_IR_STATS=1 $B build irp 2>&1 | grep -E "^built|^ir|rejected|error" | head -12 || true
CS2WASM_IR_GVN=1 CS2WASM_IR_STATS=1 $B build irg 2>&1 | grep -E "^built|^ir|error" | head -4 || true
CS2WASM_IR_PAIRS=1 CS2WASM_IR_PAIRS_THROW=1 CS2WASM_IR_GVN=1 CS2WASM_IR_STATS=1 $B build irb 2>&1 | grep -E "^built|^ir|rejected|error" | head -12 || true
ROUNDS=${ROUNDS:-7} $B time ir0 irp irg irb

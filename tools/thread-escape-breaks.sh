#!/bin/bash
# Tests 275 to 279 and the rules of the thread escape analysis (Compiler/Sharing.cs: which objects another thread can
# reach): each test compiled as shipped with CS2WASM_SHARE_CHECK=1 (a plain count of an object another thread made
# stops the program) must print what it prints without the check; compiled with its rule deliberately weakened it
# must not (the check names the object), and with the rule switched off it must again.
#   bash tools/thread-escape-breaks.sh        exit 1 when a weakened rule leaves the output as it is, or a sound build differs
cd "$(dirname "$0")/.."
C=src/Cs2Wasm.Cli/bin/Release/net11.0/cs2wasmc; O=tests/out/te
mkdir -p $O
bad=0
build() { # test, name, switches
  local t=$1 m=$2; shift 2
  env X=1 "$@" $C tests/Conformance/$t.cs -o $O/$m.wasm > $O/$m.build 2>&1 || { echo "failed to compile $m"; bad=1; }
  timeout 120 node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
}
one() { # test, the weakening switch, the rule's off switch
  local t=$1 n=${1%%_*}
  build $t t$n
  build $t t$n-check CS2WASM_SHARE_CHECK=1
  cmp -s $O/t$n.txt $O/t$n-check.txt && echo "$n checked: the same" || { echo "$n checked: DIFFERS: $(head -c 300 $O/t$n-check.txt | tr '\n' ' ')"; bad=1; }
  build $t t$n-weak CS2WASM_SHARE_CHECK=1 $2
  if cmp -s $O/t$n.txt $O/t$n-weak.txt; then echo "$n with $2: THE SAME"; bad=1; else echo "$n with $2: differs: $(grep -m1 -o 'share check[^|]*' $O/t$n-weak.txt | cut -c1-150)"; fi
  if [ -n "$3" ]; then
    build $t t$n-off CS2WASM_SHARE_CHECK=1 $3
    cmp -s $O/t$n.txt $O/t$n-off.txt && echo "$n with $3: the same" || { echo "$n with $3: DIFFERS: $(head -c 300 $O/t$n-off.txt | tr '\n' ' ')"; bad=1; }
  fi
}
one 275_engine_thread_statics CS2WASM_EXP_ALL_ENGINE_STATICS=1 CS2WASM_NO_ENGINE_STATICS=1
one 276_captured_locals_between_threads CS2WASM_EXP_DISPLAY_FIRST_ONLY=1 CS2WASM_NO_DISPLAY_SOURCES=1
one 277_empty_arrays_every_thread_reads CS2WASM_EXP_EVERY_ARRAY_EMPTY=1 CS2WASM_NO_EMPTY_ARRAYS=1
one 278_array_copies_between_threads CS2WASM_EXP_NO_PTR=1 CS2WASM_NO_PTR_IDS=1
one 279_private_work_and_handed_results CS2WASM_SHARING_NONE=1 CS2WASM_NO_SAME_TYPE_REFS=1
exit $bad

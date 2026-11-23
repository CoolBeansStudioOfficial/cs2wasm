#!/bin/bash
# Tests 275 to 279, 290, 291 and the rules of the thread escape analysis (Compiler/Sharing.cs: which objects another thread can
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
  timeout ${TL:-120} node host/run.mjs $O/$m.wasm main > $O/$m.txt 2>&1
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
# (groups of cycle-capable objects that are each one thread's, Arenas.LocalScc: an array with no element pinned as it
# is made, or every group taken for one thread's)
one 290_groups_of_one_thread CS2WASM_EXP_NO_EMPTY_PIN=1 CS2WASM_NO_LOCAL_GROUPS=1
# (a static readonly field's one write fixes an array with no element: every pinned object fixed, a let-go one stays)
one 291_fixed_empty_arrays CS2WASM_EXP_FIXED_PINS_ANY=1 CS2WASM_NO_FIXED_PINS=1
# (the foreach over a list of the thread's own, which borrows the list and each element: off, the same output. No
# weakened build of this rule is shown to break: an element of a list every thread can reach that another thread
# lets go of stays until the borrowing thread's next safe point, and a safe point inside the loop is a store the
# borrow is refused for; the one program that got past both hangs for another reason, tools/thread-board-hang.cs)
build 279_private_work_and_handed_results t279-nolist CS2WASM_NO_LOCAL_LIST_LOOPS=1
cmp -s $O/t279.txt $O/t279-nolist.txt && echo "279 with CS2WASM_NO_LOCAL_LIST_LOOPS=1: the same" || { echo "279 with CS2WASM_NO_LOCAL_LIST_LOOPS=1: DIFFERS"; bad=1; }
# (a getter of an object no other thread can reach returns its field borrowed, List<T>'s indexer: off, the same.
# What both rules rest on is that the list is no other thread's: CS2WASM_SHARING_NONE above)
build 279_private_work_and_handed_results t279-nogetter CS2WASM_NO_LOCAL_GETTERS=1
cmp -s $O/t279.txt $O/t279-nogetter.txt && echo "279 with CS2WASM_NO_LOCAL_GETTERS=1: the same" || { echo "279 with CS2WASM_NO_LOCAL_GETTERS=1: DIFFERS"; bad=1; }
# (a second rule on a test built above: a delegate made of a lambda reaches that lambda's closure, test 276's thread
# lambdas and what they capture; and a lambda runs on a worker when a delegate of it can be reached by one, test
# 275's thread lambdas, whose code names the crates' statics)
more() { # test, the weakening switch, the rule's off switch
  local t=$1 n=${1%%_*} w=${2%%=*}
  build $t t$n-$w CS2WASM_SHARE_CHECK=1 $2
  if cmp -s $O/t$n.txt $O/t$n-$w.txt; then echo "$n with $2: THE SAME"; bad=1; else echo "$n with $2: differs: $(grep -m1 -o 'share check[^|]*' $O/t$n-$w.txt | cut -c1-150)"; fi
  build $t t$n-off2 CS2WASM_SHARE_CHECK=1 $3
  cmp -s $O/t$n.txt $O/t$n-off2.txt && echo "$n with $3: the same" || { echo "$n with $3: DIFFERS: $(head -c 300 $O/t$n-off2.txt | tr '\n' ' ')"; bad=1; }
}
more 276_captured_locals_between_threads CS2WASM_EXP_LAMBDA_SITES_UNREACHED=1 CS2WASM_NO_LAMBDA_SITES=1
more 275_engine_thread_statics CS2WASM_EXP_NO_LAMBDA_RUNS=1 CS2WASM_NO_LAMBDA_SITES=1
more 290_groups_of_one_thread CS2WASM_EXP_ALL_GROUPS_LOCAL=1 CS2WASM_NO_LOCAL_GROUPS=1
# (test 305: loops over objects no other thread can reach compiled as without threads, RcFastPaths.cs on the plain
# counts and FieldLoads.cs on the fields Sharing.FieldMayBeShared clears. Weakened without the share check, which
# keeps every count a call: the atomic counts written out as plain ones lose counts of the Gem two threads count;
# every field read once by a loop never sees the flag another thread sets (a hang, the time limit). Each rule off:
# the same)
plain() { # test, the weakening switch, the rule's off switch (no share check)
  local t=$1 n=${1%%_*} w=${2%%=*}
  TL=30 build $t t$n-$w $2
  if cmp -s $O/t$n.txt $O/t$n-$w.txt; then echo "$n with $2: THE SAME"; bad=1; else echo "$n with $2: differs: $(head -c 150 $O/t$n-$w.txt | tr '\n' ' ')"; fi
  build $t t$n-off-$w $3
  cmp -s $O/t$n.txt $O/t$n-off-$w.txt && echo "$n with $3: the same" || { echo "$n with $3: DIFFERS: $(head -c 300 $O/t$n-off-$w.txt | tr '\n' ' ')"; bad=1; }
}
build 305_thread_loops_as_without t305
build 305_thread_loops_as_without t305-check CS2WASM_SHARE_CHECK=1
cmp -s $O/t305.txt $O/t305-check.txt && echo "305 checked: the same" || { echo "305 checked: DIFFERS: $(head -c 300 $O/t305-check.txt | tr '\n' ' ')"; bad=1; }
plain 305_thread_loops_as_without CS2WASM_EXP_FAST_ATOMIC=1 CS2WASM_NO_PLAIN_FAST=1
plain 305_thread_loops_as_without CS2WASM_FIELD_HOIST_WITHOUT=threads CS2WASM_NO_LOCAL_FIELD_LOADS=1
# (test 306: test 235's flat destructions on a worker, in place as without threads, and the copies and clears of
# arrays no other thread can reach without the places' locks, their elements counted by type. Weakened: every array
# taken for one thread's, two threads' copies and clears take one Gem's count out at once; every class taken for
# flat, the chain of Links is let go nested, with no count of the depth. Each rule off: the same)
build 306_flat_destroys_and_arrays_with_threads t306
build 306_flat_destroys_and_arrays_with_threads t306-check CS2WASM_SHARE_CHECK=1
cmp -s $O/t306.txt $O/t306-check.txt && echo "306 checked: the same" || { echo "306 checked: DIFFERS: $(head -c 300 $O/t306-check.txt | tr '\n' ' ')"; bad=1; }
plain 306_flat_destroys_and_arrays_with_threads CS2WASM_EXP_ALL_SLOTS_LOCAL=1 CS2WASM_NO_LOCAL_ARRAY_OPS=1
plain 306_flat_destroys_and_arrays_with_threads CS2WASM_EXP_THREAD_FLAT_ANY=1 CS2WASM_NO_THREAD_FLAT=1
build 306_flat_destroys_and_arrays_with_threads t306-noranges CS2WASM_NO_TYPED_RANGES=1
cmp -s $O/t306.txt $O/t306-noranges.txt && echo "306 with CS2WASM_NO_TYPED_RANGES=1: the same" || { echo "306 with CS2WASM_NO_TYPED_RANGES=1: DIFFERS"; bad=1; }
exit $bad

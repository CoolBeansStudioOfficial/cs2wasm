#!/bin/bash
# Tests 275 to 279, 290 and the rules of the thread escape analysis (Compiler/Sharing.cs: which objects another thread can
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
# (groups of cycle-capable objects that are each one thread's, Arenas.LocalScc: an array with no element pinned as it
# is made, or every group taken for one thread's)
one 290_groups_of_one_thread CS2WASM_EXP_NO_EMPTY_PIN=1 CS2WASM_NO_LOCAL_GROUPS=1
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
exit $bad

# Memory-model slice S1 against its bars (claude/s1-bars, 2026-11-24)

Measured on main 7b71d43, with all six S1 rows and the solver effects. The only change is this branch's RcStats fix
(91452f0, instrumentation only: with counting off, the bytes are identical). Two builds of every program:

- **planned**: `IL2WASM_MEM_PLAN=1`;
- **naive**: `IL2WASM_MEM_NAIVE=1`, the baseline.

Measurement only: no compiler code changed, apart from the RcStats owner fix the coordinator asked for.

## The bar table

| bar (MEMORY-IR 11, S1; 3.8) | result | verdict |
|---|---|---|
| removal share at least 90% executed, on the IL tests, the bench kernels and NetWasm cases with allocation | mean of 34 programs 72.5% (73% without the runtime); summed over every operation 64.5%; only 3 of 34 at 90% or more (NBody 100.0%, Shapes 99.9%, Dictionary case 90.9%) | **fails** |
| no leaks: live objects at exit equal .NET's per type (program types) | planned equal on 33 of 34. The exception is SortedList, a cycle that is S2's (naive the same). Planned never above naive on any type. The three cases where the plan frees more than naive are naive leaks (proven below), not early frees | **passes** (one S2 cycle, known) |
| bench.mjs's `tree` and binarytrees-shaped kernels at or below NativeAOT-LLVM (wasm, same engine) | tree 1.95x NativeAOT's (naive 3.54x); every kernel's geometric mean 2.69x (naive 4.49x); word_count alone below (0.97x) | **fails** |
| frames.mjs: no spikes above NativeAOT-LLVM | churn, collections, strings and tree have p99.9 and worst frame below NativeAOT's, planned and naive; medians 2.2-4.7x slower. bigdrop **does not run**: destroy recursion overflows the stack (bug 4) | **fails** (bigdrop) |
| hello world size not above 970,075 B (AUDIT il-generics) | planned 1,204,423 B, naive 1,277,136 B: the plan is 5.7% smaller, but both are above the old figure. The IL front end's hello world has grown since that figure was recorded (step 1: 1,377,218 B) | **fails** as written; the plan itself shrinks the module |
| analysis at most 20% of build time | planned minus naive is 22% of the planned build, over 10 programs (15-29%, a shared desk). This leaves out the solve and summaries, which run in both builds | **fails**, narrowly |
| analysis at most 25% more peak memory | +10% (-1% to +16%) | passes |
| checked build: IL tests and kernels planned, no traps | IL tests 178 of 178 planned and verified, 161 of 161 naive; 20 of 20 kernel and hello builds with no trap and the same checksums | passes |
| outputs | every planned and naive build of all 34 programs prints the expected output (the NetWasm cases against the suite's .NET record) | passes |

## How it was measured

- Programs:
  - hello world;
  - the nine tools/bench kernels as programs of their own: tools/bench/Programs.cs plus a Main that runs one kernel
    once and prints its checksum;
  - the 24 allocation-heavy NetWasm cases row e chose (the shipped mf-base cache's builds).
- Counts (1): `node tools/il-rcstats.mjs run <program> --live`, under the desk's heavy lock. The tables come from
  `node tools/s1-bars.mjs <keep dirs> --oracles <dirs>`, and the causes from `node tools/s1-causes.mjs <keep dirs>`.
- Leaks (2), the .NET side:
  - the cases: tools/il-live-oracle/run.sh's heap walks (row e's, homelab);
  - hello world and the kernels: the new tools/il-live-oracle/run-kernels.sh (homelab), which builds each one as a
    console program and runs it with the startup hook.
- Speed (3): `tools/il-speed.mjs` (new). bench.mjs and frames.mjs build with the old front end, so the script builds
  the same sources with the IL front end, in both configurations, and times them the way those tools do.
  - **Built on the desk, run on the homelab.** The homelab has no Emscripten SDK, so NativeAOT-LLVM's WebAssembly
    build can't be made there. `il-speed.mjs build` (desk) publishes tools/bench-naot-wasm and
    tools/frames-naot-wasm and compiles the IL modules. The directory is then copied to the homelab, where
    `bash tools/homelab.sh claude/s1-bars node tools/il-speed.mjs run <dir> --runs 3 --js-young 32` runs it in the
    same Node (24.19.0) for every side.
  - frames: one module per scene (`Main() => Frames.Run("<scene>")`), because the IL front end passed Main(string[])
    a null array (bug 3, fixed on main at 83371bb after these runs).
- Size and compile cost (4):
  - each build of hello world and the kernels, naive and planned interleaved, 2 or 3 runs each, on a shared desk;
  - time and peak working set are of the compiler process alone, polled with C:\s1b\peak.ps1 (the OS's
    PeakWorkingSet64);
  - IL2WASM_MEM_TRACE for the per-round phase times.
- Checked (5):
  - IL tests with `IL2WASM_IL_CHECKED=1`, which is new in the test harness (718fcaa): planned and verified, naive;
  - the kernels with `IL2WASM_IL_CHECKED=1`, planned and verified, naive.

## 1. Removal share (executed count operations, planned against naive)

Owners: program, containers (System.Collections*), library, runtime (Il2Wasm.Runtime), generated (rt.destroy and the
other generated functions). "Without the runtime" leaves out runtime and the generated functions other than destroy.
Each owner cell is the removed share, then planned/naive operations.

| program | naive ops | planned ops | removed | removed without the runtime | program | containers | library | runtime | generated (destroy etc.) |
| --- |---|---|---|---|---|---|---|---|---|
| hello | 926 | 300 | 67.6% | 67.5% | - | 100.0% (0/12) | 67.1% (296/900) | 71.4% (4/14) | - |
| k_Tree | 15,311,468 | 7,250,066 | 52.6% | 52.6% | 53.4% (5,627,784/12,066,626) | 100.0% (0/12) | 100.0% (310/1,622,848) | 71.4% (4/14) | 0.0% (1,621,968/1,621,968) |
| k_DictionaryInts | 10,866,505 | 2,989,906 | 72.5% | 72.5% | 0.0% (4/4) | 72.5% (2,989,436/10,864,641) | 74.4% (457/1,785) | 94.3% (4/70) | 0.0% (5/5) |
| k_ListSort | 32,939,479 | 18,795,962 | 42.9% | 43.0% | 87.5% (30,006/240,006) | 13.2% (18,315,394/21,100,053) | 97.2% (319,494/11,468,042) | 98.7% (4/314) | 0.0% (131,064/131,064) |
| k_Linq | 26,401,177 | 5,529,966 | 79.1% | 79.1% | 100.0% (48/2,943,880) | 67.3% (2,162,302/6,604,681) | 80.0% (3,367,214/16,852,202) | 70.6% (5/17) | 0.0% (397/397) |
| k_WordCount | 6,828,074 | 1,684,392 | 75.3% | 75.3% | 50.0% (480,273/960,401) | 80.6% (841,572/4,327,285) | 76.4% (362,281/1,537,780) | 99.8% (4/2,346) | 0.0% (262/262) |
| k_Strings | 4,449,008 | 1,640,063 | 63.1% | 63.1% | 43.6% (265,720/471,437) | 0.0% (68/68) | 65.5% (1,368,421/3,971,639) | 71.4% (4/14) | 0.0% (5,850/5,850) |
| k_NBody | 2,497,746 | 469 | 100.0% | 100.0% | 100.0% (66/2,496,348) | 75.0% (4/16) | 74.6% (331/1,304) | 71.4% (4/14) | 0.0% (64/64) |
| k_Shapes | 9,864,402 | 8,533 | 99.9% | 99.9% | 100.0% (4,098/9,850,908) | 75.0% (4/16) | 96.5% (331/9,368) | 71.4% (4/14) | 0.0% (4,096/4,096) |
| k_LinkedNodes | 13,801,278 | 5,000,386 | 63.8% | 63.8% | 48.9% (4,800,043/9,400,043) | 75.0% (4/16) | 100.0% (335/4,201,205) | 71.4% (4/14) | 0.0% (200,000/200,000) |
| Collection | 32,201 | 6,589 | 79.5% | 80.7% | 78.0% (1,044/4,752) | 87.1% (576/4,460) | 81.0% (4,179/21,947) | 71.6% (100/352) | 0.0% (690/690) |
| ComplexMutableCollection | 75,505 | 26,453 | 65.0% | 65.4% | 42.5% (1,001/1,742) | 54.6% (16,275/35,821) | 78.1% (7,977/36,466) | 62.1% (181/477) | -2.0% (1,019/999) |
| Dictionary | 320,762 | 29,241 | 90.9% | 91.2% | 68.1% (630/1,973) | 71.2% (23,787/82,592) | 98.4% (3,674/234,873) | 64.0% (98/272) | 0.0% (1,052/1,052) |
| HashSet | 185,311 | 29,668 | 84.0% | 84.0% | 87.4% (713/5,642) | 84.5% (25,025/161,125) | 79.7% (3,636/17,871) | 77.9% (108/488) | -0.5% (186/185) |
| ImmutableArray | 8,690 | 2,885 | 66.8% | 66.9% | 27.5% (434/599) | 39.8% (286/475) | 72.3% (2,054/7,405) | 61.7% (62/162) | 0.0% (49/49) |
| ImmutableDictionary | 44,333 | 13,315 | 70.0% | 70.1% | 34.0% (581/880) | 47.9% (5,799/11,139) | 79.7% (6,410/31,566) | 62.0% (151/397) | -6.6% (374/351) |
| ImmutableHashSet | 45,795 | 17,188 | 62.5% | 62.4% | 26.5% (633/861) | 47.4% (13,031/24,764) | 84.5% (3,011/19,434) | 66.2% (114/337) | 0.0% (399/399) |
| ImmutableList | 59,117 | 18,220 | 69.2% | 69.6% | 35.0% (759/1,168) | 59.0% (9,771/23,831) | 79.4% (6,826/33,132) | 30.5% (326/469) | -4.1% (538/517) |
| ImmutableQueueStack | 36,360 | 6,659 | 81.7% | 82.1% | 38.7% (415/677) | 71.4% (624/2,185) | 84.4% (5,097/32,650) | 61.5% (217/563) | -7.4% (306/285) |
| ImmutableSortedDictionary | 43,451 | 11,866 | 72.7% | 72.8% | 36.6% (656/1,034) | 63.6% (3,876/10,654) | 78.3% (6,721/30,936) | 62.0% (144/379) | -4.7% (469/448) |
| ImmutableSortedSet | 45,873 | 13,467 | 70.6% | 70.8% | 33.4% (617/926) | 60.7% (6,249/15,906) | 78.8% (5,976/28,218) | 54.6% (182/401) | -5.0% (443/422) |
| LinkedList | 40,913 | 8,865 | 78.3% | 78.7% | 32.1% (563/829) | 62.1% (971/2,563) | 81.4% (6,826/36,697) | 61.8% (210/550) | -7.7% (295/274) |
| List | 19,309 | 3,996 | 79.3% | 79.6% | 31.3% (659/959) | 85.3% (186/1,262) | 82.8% (2,834/16,441) | 68.7% (151/482) | -0.6% (166/165) |
| ObjectModelCollection | 33,256 | 4,346 | 86.9% | 87.6% | 26.8% (619/846) | 76.1% (275/1,149) | 90.8% (2,769/30,022) | 63.0% (330/892) | -1.7% (353/347) |
| OrderedDictionary | 34,448 | 8,068 | 76.6% | 76.8% | 31.2% (607/882) | 78.6% (819/3,822) | 78.6% (6,226/29,106) | 61.9% (150/394) | -9.0% (266/244) |
| Queue | 21,846 | 3,792 | 82.6% | 83.0% | 30.2% (579/829) | 68.1% (221/693) | 86.5% (2,661/19,700) | 65.6% (154/448) | -0.6% (177/176) |
| SortedCollections | 43,572 | 12,381 | 71.6% | 71.7% | 27.0% (869/1,191) | 58.4% (4,225/10,164) | 78.5% (6,765/31,536) | 59.7% (122/303) | -5.8% (400/378) |
| SortedList | 43,348 | 10,190 | 76.5% | 76.9% | 31.4% (613/894) | 63.7% (1,188/3,271) | 79.6% (7,806/38,291) | 62.1% (203/535) | -6.4% (380/357) |
| Stack | 39,519 | 8,583 | 78.3% | 78.6% | 37.9% (586/943) | 60.3% (444/1,117) | 80.7% (7,055/36,629) | 62.1% (216/570) | -8.5% (282/260) |
| BigInteger | 3,810,827 | 2,227,723 | 41.5% | 42.7% | 7.3% (299,472/323,193) | 57.6% (3,321/7,828) | 46.0% (1,878,000/3,475,347) | -1713.8% (44,927/2,477) | -1.1% (2,003/1,982) |
| LambdaIterator | 4,516 | 1,574 | 65.1% | 65.1% | 43.9% (257/458) | 100.0% (0/12) | 67.9% (1,273/3,968) | 68.0% (16/50) | 0.0% (28/28) |
| EnumerableIteratorContract | 24,182 | 5,225 | 78.4% | 78.6% | 35.1% (964/1,485) | 100.0% (0/12) | 82.2% (3,945/22,115) | 64.8% (138/392) | 0.0% (178/178) |
| RegexReplacement | 67,552 | 29,645 | 56.1% | 57.2% | 32.1% (688/1,014) | 55.4% (3,998/8,963) | 58.6% (22,969/55,474) | 35.3% (150/232) | 1.6% (1,840/1,869) |
| collector-retention | 6,262 | 2,175 | 65.3% | 65.3% | 25.2% (484/647) | 100.0% (0/12) | 70.6% (1,609/5,469) | 63.1% (31/84) | -2.0% (51/50) |

- summed over the 34 programs: 64.5% removed (45,402,157 of 128,047,011); without the runtime 64.6%
- mean of the per-program shares: 72.5%; without the runtime 72.8%; programs at 90% or more: 3 of 34

The **mean of the per-program shares is 72.5%** (72.8% without the runtime). The share summed over all operations is
64.5%, because ListSort's and Tree's large runs dominate it.

**A correction to earlier figures.** Before 91452f0, Lower/RcStats.cs gave a specialization variant
(`<method>|ownN`) the owner "generated". il-rcstats then left variants out of "without the runtime". The planned
build's "without the runtime" share was overstated wherever variants ran: k_Tree read 84.1% against a true 52.6%,
hello world 78.5% against 67.5%. The tables above use the fixed build.

### Remaining operations by the lowering's category (mean over the 34 programs, points of naive)

| category | mean points |
|---|---|
| Generic | 7.0 |
| Unwind | 6.8 |
| StoreOld | 2.7 |
| ArgAfterCall | 2.2 |
| Result | 1.9 |
| untagged | 1.8 |
| FlipField | 1.2 |
| Destroy | 0.9 |
| Phi | 0.8 |
| StoreRetain | 0.8 |
| OwnedConvention | 0.6 |
| FlipStatic, FlipVar, FlipElem | 0.5, 0.4, 0.0 |

The tags drift, as row b found: a tag can land on a neighbouring count call. "Unwind" is mostly ordinary last-use
releases, and "Generic" covers field loads that don't borrow. The cause table below reads the sites themselves.

## Top causes of the remaining count operations

From `tools/s1-causes.mjs` and the top sites. "Points" are percentage points of a program's naive count. The first
three each hold one kernel far below 90%; the rest are spread over most programs.

1. **Box/unbox pairs in the generic comparers.** These are not ownership decisions. The importer keeps the box of
   `(int)(object)left` (GenericArraySortHelper\<int\>.LessThan/GreaterThan, BinarySearch, PickPivotAndPartition) and
   the box of a null test on a value-type T (GenericEqualityComparer\<int\>.Equals, GenericComparer\<long\>.Compare).
   That is one allocation and one release per comparison. The JIT folds these: box then unbox.any of the same type is
   the value itself, and box then brtrue of a value type is true.
   - ListSort: 48.9 points of the remaining 57.
   - DictionaryInts 6.9, Linq 6.8, HashSet 3.7.
   - It also costs speed (list_sort 3.55x, dictionary 3.24x).
   - Folding them changes no observable behaviour.
2. **A recursive walk given the owned variant (bug 2, routed to row b).** In `Walk(n) => ... Walk(n.Left) +
   Walk(n.Right)`, the root's last call hands the root over to `Walk|own1`. Every recursive call then goes to own1 too:
   each child is retained to be handed over and released in the callee. That is 3 operations per node, where Lend
   needs 0.
   - Tree: 31.5 points.
   - Regex 2.5, hello 1.9, ImmutableHashSet 1.8; 11 programs above 1 point.
   - Repro: C:\s1b\k\tree_min.cs, 53.7% removed.
3. **Loop-carried reference phis.** `for (var n = head; n != null; n = n.Next)` releases and retains at every
   iteration. The borrow's root changes each iteration, so 4.6's loop-invariant Guaranteed phi doesn't apply. It
   needs a chain borrow (head held, and nothing in the loop writes `.Next`).
   - LinkedNodes: 31.9 points (4M of 13.8M).
   - Strings 7.1 (Number.GetTwoDigitsBytesRef's static), Linq 2.8; 9 programs above 1 point.
4. **Old-value releases at stores (StoreOld).** These include stores into fresh objects, like `n.Left = Build(...)`
   in a constructor-made node, whose old value is null.
   - Mean 2.7 points, 29 programs above 1 point; hello world 8.9.
   - These are the `Init`/`Fresh` store kinds of 5.2/5.3, which are not wired in S1.
5. **Byref getters retaining their array for the returned address.**
   - `MemoryMarshal.GetArrayDataReference<T>(T[])` retains its parameter and releases it at the return: a pair per
     call. A lent array parameter needs no count for an interior address of it.
   - Strings: 10.7 points.
   - BigInteger 4.0, Linq 2.9; 10 programs above 1 point.
   - Next to it are static getters whose results are retained (EqualityComparer\<int\>.Default 4.3 points in
     DictionaryInts; ArrayPool.Shared, Encoding.UTF8, CultureInfo.InvariantCulture; StringBuilder.Append returning
     `this`): 0.9 points on average, 11 programs above 1. Persistent statics and borrowed returns don't cover them
     yet.

Also measured:

- **destroy.** The children's releases when a tree is freed are a floor: 10.6 points of Tree's naive count, which
  caps Tree at 89.4% even with bug 2 fixed.
- **The returned-address keeper ring (Rt.ReturnKeepers.Hold).** 3 operations per hold, 1.2 points of BigInteger.
  It is why BigInteger's runtime count is 18x naive's (44,927 against 2,477).
- **A long tail of about 16 points per program**, by role/category:
  - field loads that don't borrow (field/Generic 1.6);
  - parameters' last-use releases (argument/Unwind 1.2, parameter/Unwind 1.2);
  - results (return/Generic 1.0, return/Result 1.0);
  - FlipField 0.7, FlipStatic 0.55.

### NBody: 14.7x NativeAOT's with no count operations left (profile, planned build)

The planned build removes 100.0% of NBody's count operations, and NBody is still 14.7x slower than NativeAOT-LLVM.

**Profile.** V8 --cpu-prof, IL2WASM_REPEAT=200, 34 ms a call; tools/profsum.mjs gives the self time:

| function | self time |
|---|---|
| Programs.NBody | 56% |
| Vec3.op_Addition | 23% |
| Vec3.op_Subtraction | 19% |

The operators are not inlined. **The code (wasm-dis of the module) is struct copying, not counting.**

- `a.X` on a struct parameter copies the whole 24-byte Vec3 into a frame temporary before it reads one field.
  `op_Addition` makes 6 such copies, 144 of its 176-byte shadow-stack frame.
- The frame is zeroed with memory.fill on every call.
- The body is wrapped in try_table/catch_all_ref/throw_ref, to restore the stack pointer on unwind.
- Each inner iteration has 4 calls (op_Subtraction, op_Multiply, op_Addition, the constructor), and the back end
  inlines none of them, because of the frame and the try.

The causes, in order:

1. `ldarg <struct>; ldfld` lowered as a copy, where reading the field through the argument's address is enough;
2. the frame's zero fill and the unwind try for leaf struct functions;
3. no inlining of small struct operators.

These are the IL front end's struct lowering, not the memory model.

## 2. Leaks: live objects at exit against .NET

| program | .NET live (all types) | naive live | naive program types differing | planned live | planned program types differing | planned against naive, per type | differences |
| --- |---|---|---|---|---|---|---|
| hello | 3431 | 34 | equal (0 types) | 34 | equal (0 types) | equal |  |
| k_Tree | 3430 | 32 | equal (1 types) | 32 | equal (1 types) | equal |  |
| k_DictionaryInts | 3454 | 56 | equal (0 types) | 56 | equal (0 types) | equal |  |
| k_ListSort | 3753 | 362 | equal (0 types) | 362 | equal (0 types) | equal |  |
| k_Linq | 3748 | 558 | equal (1 types) | 558 | equal (1 types) | equal |  |
| k_WordCount | 3455 | 62 | equal (0 types) | 62 | equal (0 types) | equal |  |
| k_Strings | 3775 | 366 | equal (0 types) | 366 | equal (0 types) | equal |  |
| k_NBody | 3454 | 55 | equal (2 types) | 55 | equal (2 types) | equal |  |
| k_Shapes | 3454 | 55 | equal (5 types) | 55 | equal (5 types) | equal |  |
| k_LinkedNodes | 3454 | 55 | equal (1 types) | 55 | equal (1 types) | equal |  |
| Collection | 3564 | 79 | equal (4 types) | 78 | equal (4 types) | [S.P.CoreLib]System.Collections.Generic.Dictionary`2+Entry<int32,int32>[] 0 vs 1 |  |
| ComplexMutableCollection | 3586 | 156 | equal (5 types) | 156 | equal (5 types) | equal |  |
| Dictionary | 3541 | 62 | equal (1 types) | 62 | equal (1 types) | equal |  |
| HashSet | 3648 | 84 | equal (5 types) | 84 | equal (5 types) | equal |  |
| ImmutableArray | 3554 | 60 | equal (2 types) | 60 | equal (2 types) | equal |  |
| ImmutableDictionary | 3635 | 183 | equal (2 types) | 183 | equal (2 types) | equal |  |
| ImmutableHashSet | 3595 | 105 | equal (3 types) | 105 | equal (3 types) | equal |  |
| ImmutableList | 3604 | 166 | equal (2 types) | 166 | equal (2 types) | equal |  |
| ImmutableQueueStack | 3571 | 140 | equal (2 types) | 140 | equal (2 types) | equal |  |
| ImmutableSortedDictionary | 3620 | 167 | equal (3 types) | 167 | equal (3 types) | equal |  |
| ImmutableSortedSet | 3588 | 148 | equal (3 types) | 148 | equal (3 types) | equal |  |
| LinkedList | 3596 | 174 | equal (1 types) | 174 | equal (1 types) | equal |  |
| List | 3646 | 67 | equal (2 types) | 67 | equal (2 types) | equal |  |
| ObjectModelCollection | 3642 | 65 | equal (2 types) | 65 | equal (2 types) | equal |  |
| OrderedDictionary | 3572 | 137 | equal (2 types) | 137 | equal (2 types) | equal |  |
| Queue | 3648 | 62 | equal (3 types) | 62 | equal (3 types) | equal |  |
| SortedCollections | 3590 | 143 | equal (4 types) | 143 | equal (4 types) | equal |  |
| SortedList | 3597 | 162 | 1 of 2 | 162 | 1 of 2 | equal | naive: NetWasm.Correctness.SortedLists.DescendingComparer 1 vs 0 / planned: NetWasm.Correctness.SortedLists.DescendingComparer 1 vs 0 |
| Stack | 3595 | 153 | equal (3 types) | 153 | equal (3 types) | equal |  |
| BigInteger | 3913 | 682 | equal (2 types) | 682 | equal (2 types) | equal |  |
| LambdaIterator | 3470 | 55 | equal (3 types) | 55 | equal (3 types) | equal |  |
| EnumerableIteratorContract | 3549 | 65 | equal (5 types) | 65 | equal (5 types) | equal |  |
| RegexReplacement | 4399 | 636 | equal (2 types) | 627 | equal (2 types) | [S.P.CoreLib]System.Collections.Generic.Dictionary`2+Entry<string,int32>[] 0 vs 9 |  |
| collector-retention | 3447 | 61 | 1 of 6 | 59 | equal (6 types) | int32[] 3 vs 4; [case]NetWasm.Correctness.CollectorLifetime.Payload 0 vs 1 | naive: NetWasm.Correctness.CollectorLifetime.Payload 1 vs 0 |

- **Program types.**
  - Planned equals .NET on every program except SortedList: `SortedList.Keys` caches a KeyList that points back at
    its list, a cycle left to S2's groups. Naive is the same there.
  - collector-retention (`ref new Payload(41).Number`) leaks one Payload under naive. The plan's returned-address
    keeper (7.4) frees it.
- **Planned against naive, per type.** Equal on 31 programs. On the other three the plan frees more, never less:
  - Collection: one `Dictionary<int,int>.Entry[]`;
  - RegexReplacement: 9 `Dictionary<string,int>.Entry[]`;
  - collector-retention: one `int[]` and the Payload.

  All three are **naive's leaks, not early frees in the planned build**. Each shape was reduced to a repro:
  C:\s1b\k\early_array.cs, early_dict.cs and early_regex.cs. tools/il-rcstats.mjs run --live shows the same one-array
  difference per type on each.

  **Evidence that the plan does not free early:**
  - The planned and verified build, compiled checked (`IL2WASM_IL_CHECKED=1`: freed memory poisoned, never reused,
    traps on a retain of a freed object or a release too many), prints the same output as naive and as the shipped
    build on all three: 42, 115, `a#b#c#`. There is no trap and nothing on stderr.
  - .NET's heap walk of the two NetWasm cases (rcs/oracle) has **0** live `Dictionary<int,int>.Entry[]` and **0** live
    `Dictionary<string,int>.Entry[]` at exit. The planned count is 0 too; naive's is 1 and 9.

  **Where naive keeps them.** The cause is the one rule, Ir/Ownership.cs:294 (MEMORY-IR 7.4). At a return of an
  address, a base the function holds is not released when `!HeldElsewhere(base)`. HeldElsewhere (Ownership.cs:101)
  accepts arguments and loads (Arg, LdFld, LdElem, LdSFld, VarLd, LdInd, casts of them). It does not accept a NewArr,
  a call result or a **Phi**. A base outside that list keeps the count it holds forever.
  - **Arrays (collector-retention input 5).** `static ref int ArrayInterior() => ref new[] { 7, 41, 9 }[1];` is
    `v2 = NewArr int32; v5 = LdElemA v2 1; Ret v5`. The NewArr's count is never released, so the array outlives the
    caller's last use of the ref; .NET collects it after that use. This is the deliberate leak MEMORY-IR 7.4
    describes, the same as `ref new Payload(41).Number` (input 6). The planned build's returned-address keeper
    (Rt.ReturnKeepers Hold/Take) hands the count to the caller, which releases it at the ref's last use.
  - **Dictionary entries (Collection's MutateDictionaryThroughReferences; RegexReplacement through
    RegexWriter.StringCode).** `Dictionary<K,V>.CollectionsMarshalHelper.GetValueRefOrAddDefault` returns
    `ref entries[i].value`. On the add path `entries` is `v140 = Phi(v18, v138)`: two loads of `_entries`, before and
    after Resize (IL2WASM_IL_DUMP, naive, the `<int32,int32>` instance). Naive counting retains the phi's copy, and the
    returned address `LdFldA(LdElemA v140 ...)` has the phi as its base. A Phi is not HeldElsewhere, so the copy's
    count is kept. The array keeps one count after its dictionary has released it, so one entries array leaks per
    dictionary whose first entry comes in through GetValueRefOrAddDefault: one in Collection, and 9 in
    RegexReplacement, one per RegexWriter `_stringTable` (`Dictionary<string,int>`, `StringCode` →
    `CollectionsMarshal.GetValueRefOrAddDefault<string,int>`). The planned build's phi is Guaranteed over two loads
    of a field of a lent parameter, so it holds no count to keep.

  The naive build is the baseline only and is not shipped. Its leaks are recorded here and not fixed. A fix in
  Ownership.cs would be to treat a Phi whose inputs are all HeldElsewhere as held elsewhere. That would change the
  naive module's bytes and the removal share's baseline, so it is the coordinator's call.

## 3. Speed against NativeAOT-LLVM's WebAssembly build (homelab, same Node)

bench.mjs's kernels. Best of five repetitions in a process, best of 3 runs, --js-young 32.

| kernel | NativeAOT-LLVM wasm ms | naive ms | naive ratio | planned ms | planned ratio |
|---|---|---|---|---|---|
| dictionary | 5.9 | 22.1 | 3.77x | 19.0 | 3.24x |
| word_count | 2.8 | 7.9 | 2.84x | 2.7 | **0.97x** |
| strings | 2.7 | 10.5 | 3.90x | 5.6 | 2.10x |
| tree | 9.7 | 34.3 | 3.54x | 18.9 | 1.95x |
| list_sort | 42.5 | 176.3 | 4.15x | 150.9 | 3.55x |
| linq | 20.2 | 71.8 | 3.55x | 52.0 | 2.57x |
| nbody | 1.8 | 27.6 | 15.56x | 26.1 | 14.68x |
| shapes | 1.3 | 5.7 | 4.36x | 1.9 | 1.45x |
| linked_list | 3.3 | 16.6 | 4.99x | 9.9 | 2.98x |
| **geometric mean** | | | **4.49x** | | **2.69x** |

| | NativeAOT-LLVM wasm | naive | planned |
|---|---|---|---|
| peak memory | 71.3 MB | 32.0 MB | 32.0 MB |
| code size, no custom sections | 1,328.4 KB | 1,427.2 KB | 1,333.4 KB |

Every checksum is equal.

frames.mjs's scenes. 1,000 warm-up frames, then 6,000 timed frames; the lowest of 3 runs per statistic.

| scene | side | median ms | p99 ms | p99.9 ms | worst ms | mean ms | peak MB |
|---|---|---|---|---|---|---|---|
| churn | NativeAOT | 0.060 | 0.071 | 1.177 | 1.202 | 0.065 | 59.3 |
| | naive | 0.216 (3.59x) | 0.234 | 0.271 | 0.393 | 0.216 | 16.0 |
| | planned | 0.130 (2.16x) | 0.141 | 0.152 | 0.184 | 0.130 | 16.0 |
| collections | NativeAOT | 0.006 | 0.028 | 1.099 | 1.402 | 0.008 | 59.3 |
| | naive | 0.030 (4.98x) | 0.080 | 0.287 | 0.316 | 0.031 | 16.0 |
| | planned | 0.015 (2.57x) | 0.056 | 0.270 | 0.286 | 0.016 | 16.0 |
| strings | NativeAOT | 0.009 | 0.023 | 0.061 | 0.200 | 0.010 | 59.3 |
| | naive | 0.034 (3.93x) | 0.040 | 0.049 | 0.057 | 0.035 | 16.0 |
| | planned | 0.019 (2.23x) | 0.024 | 0.032 | 0.046 | 0.020 | 16.0 |
| tree | NativeAOT | 0.034 | 0.049 | 2.803 | 3.781 | 0.045 | 59.3 |
| | naive | 0.298 (8.91x) | 0.409 | 1.748 | 1.961 | 0.308 | 96.0 |
| | planned | 0.156 (4.65x) | 0.216 | 1.027 | 1.210 | 0.160 | 96.0 |
| bigdrop | NativeAOT | 0.004 | 0.008 | 6.725 | 9.474 | 0.031 | 71.1 |
| | naive, planned | **fails**: RangeError, maximum call stack size exceeded (bug 4) | | | | | |

Over the 4 scenes that run:

| | mean frame | median | worst frame |
|---|---|---|---|
| planned | 2.30x NativeAOT's | 2.75x | 0.22x |
| naive | 4.16x | 5.00x | 0.32x |

In those four scenes there are no spikes above NativeAOT's: p99.9 and the worst frame are below NativeAOT's on both
builds.

## 4. Size and compile cost (desk, shared, interleaved)

| program | naive size | planned size | naive compile s | planned compile s | added | (planned-naive)/planned | naive peak MB | planned peak MB | added |
|---|---|---|---|---|---|---|---|---|---|
| hello | 1,277,136 | 1,204,423 | 13.2, 13.1, 13.8 | 16.5, 17.2, 16.7 | 26% | 20% | 451, 468, 435 | 504, 514, 516 | 10% |
| k_Tree | 1,278,843 | 1,206,101 | 13.9, 14.3, 14.4 | 17.3, 17.0, 17.5 | 22% | 18% | 472, 457, 452 | 512, 510, 501 | 8% |
| k_DictionaryInts | 1,286,882 | 1,213,741 | 14.2, 14.4 | 17.0, 18.1 | 23% | 19% | 477, 454 | 475, 534 | 12% |
| k_ListSort | 1,321,842 | 1,246,103 | 14.3, 14.4 | 17.3, 18.7 | 25% | 20% | 475, 489 | 552, 495 | 13% |
| k_Linq | 1,459,797 | 1,365,069 | 15.5, 15.6 | 20.0, 23.9 | 41% | 29% | 572, 537 | 574, 571 | 0% |
| k_WordCount | 1,292,021 | 1,217,735 | 13.7, 14.8 | 18.0, 21.1 | 37% | 27% | 466, 467 | 544, 509 | 16% |
| k_Strings | 1,286,389 | 1,213,737 | 14.2, 14.0 | 17.9, 15.2 | 17% | 15% | 438, 456 | 522, 529 | 16% |
| k_NBody | 1,286,459 | 1,213,428 | 13.8, 11.6 | 17.7, 14.9 | 28% | 22% | 475, 496 | 467, 491 | -1% |
| k_Shapes | 1,283,641 | 1,210,501 | 14.2, 11.2 | 18.5, 13.1 | 24% | 20% | 472, 472 | 490, 533 | 13% |
| k_LinkedNodes | 1,281,427 | 1,208,604 | 13.5, 11.0 | 18.0, 15.7 | 38% | 27% | 465, 476 | 529, 545 | 14% |

- mean: compile naive 13.8 s, planned 17.7 s (+28%, 22% of the planned build); peak naive 485 MB, planned 533 MB (+10%)

**Hello world's phases per round (IL2WASM_MEM_TRACE, planned).** There are 6 rounds.

| phase | per round |
|---|---|
| reach phase | 0.24-1.8 s |
| solve | 0.06-0.15 s |
| signatures | 0.08-0.29 s |
| Inline (final round) | step 151 ms, Canon 195 ms (2,809 sites) |

The plan's own passes (Signatures, Borrows, Plan, Inline, Canon) run only in the planned build, and the planned build
takes 3.9 s more on average (+28%). Analysis is therefore at least 22% of the planned build, above 3.8's 20%. The
naive build also runs the solve, which is not counted here, so the true analysis share is higher.

Peak memory is +10%, inside the 25%.

## 5. Checked build

The IL tests ran from the repository (`dotnet run -c Release --project tests/Il2Wasm.IL.Tests`, IL2WASM_FRAMEWORK set
to the framework that was built). `IL2WASM_IL_CHECKED=1` compiles every program as the checked build: freed memory
is poisoned and never reused, a release too many traps, and so does a retain of a freed object.

| run | passed | failed |
|---|---|---|
| planned, verified (`IL2WASM_MEM_PLAN=1 IL2WASM_VERIFY=1`), checked | 178 | 0 |
| naive, checked | 161 | 0 |
| planned, verified, shipped | 178 | 0 |

The naive run has fewer checks: the plan-only tests (f19 and the planner's and signatures' own checks) run only with
the plan.

Kernels and hello world, built checked (Il2Wasm.IL.Tests framework mode, `IL2WASM_IL_CHECKED=1`): 20 of 20 builds
run with exit 0, no trap and no checked report on stderr, and each prints the same checksum as its shipped build. The
20 are the planned and verified build and the naive build of each of the ten programs.

| program | checksum (planned = naive = shipped) |
|---|---|
| hello | 42 |
| k_Tree | 46736547756 |
| k_DictionaryInts | 21128325111 |
| k_ListSort | 14929194645 |
| k_Linq | 668180439 |
| k_WordCount | 1833431 |
| k_Strings | 2525666 |
| k_NBody | 94032996 |
| k_Shapes | 23747905 |
| k_LinkedNodes | 99999362620 |

**Verdict: passes.** The checked build has no shadow counts yet (MEMORY-IR 8.2's additions are not built). A borrow
that outlived its object shows up here only if the freed memory is then read through a poisoned header, or retained
or released.

## Bugs found (all reported to the coordinator with repros)

1. **RcStats gave specialization variants the owner "generated".** Fixed here (91452f0, byte-identical with counting
   off). It had overstated "without the runtime" shares.
2. **The planner hands a borrowed field load over to the owned variant in a recursive walk.** 3 count operations per
   node. Repro: C:\s1b\k\tree_min.cs. Routed to row b.
3. **Main(string[]) got null args, and an int Main's exit code was dropped.** Repro: C:\s1b\k\args.cs. Fixed on main
   at 83371bb by the coordinator.
4. **Freeing a long acyclic chain overflows the stack** (destroy recursion: no depth queue or sealed-tail loop in the
   IL front end's GenerateLate). Repro: C:\s1b\k\chain_drop.cs, 200,000 nodes. frames' bigdrop fails on both builds.
   With a new agent on claude/il-destroy-depth.

## Not done or not verified

- The NetWasm suite case by case with the plan on was not rerun: the rows' own reports say 283 passed with none lost.
- The 24 cases were not run under the checked build.
- The kernels' live oracle runs .NET's Release build (`dotnet build -c Release`, not trimmed). Only program types
  are compared.
- bigdrop is not measured (bug 4). bench and frames have not been rerun since bug 3's fix (83371bb); the per-scene
  modules don't depend on it.
- Compile times are from a desk other sessions were also using: runs of the same build differ by up to 4 s.

## Leaks re-measure 2026-11-25

Branch claude/s1-leaks, from origin/claude/span-uaf-merged (58349a0). Release builds of the framework (FW, build-corelib),
the CLI and Il2Wasm.IL.Tests. Measurement only: no code changed.

- Method: section 2's. Desk: `node tools/il-rcstats.mjs run <program> --live --keep ...`, one program at a time, naive
  and as configured. Nothing .NET runs on the desk. The programs are the 34 of section 2: hello, the nine kernels
  (built on the desk from tools/bench/Programs.cs, as run-kernels.sh builds them), and the 24 NetWasm cases (each case's
  built `case.dll` in the desk's NetWasm work cache (the homelab's is the same pinned commit); the case id is the one whose name starts with the program's name,
  e.g. SortedList is `SortedListCompatibilityCorpusTests.SortedListsMatchDesktopAcrossCompilerProfilesAndTargets`).
- .NET oracle, homelab only: `tools/homelab.sh claude/s1-leaks bash tools/il-live-oracle/run-kernels.sh` (hello and the
  kernels) and `... run.sh <24 case ids>` (the homelab's il2wasm\netwasm cache). All 34 returned a heap walk.
- Comparison: il-rcstats' `live` against each oracle, program types only (the program's own assembly).

| program | .NET live (all types) | naive live | naive program types vs .NET | planned live | planned program types vs .NET | planned above naive on any type |
| --- | ---: | ---: | --- | ---: | --- | --- |
| hello | 3431 | 34 | equal (0 types) | 34 | equal (0 types) | no |
| k_Tree | 3430 | 32 | equal (1 types) | 32 | equal (1 types) | no |
| k_DictionaryInts | 3454 | 56 | equal (0 types) | 56 | equal (0 types) | no |
| k_ListSort | 3753 | 362 | equal (0 types) | 362 | equal (0 types) | no |
| k_Linq | 3748 | 558 | equal (1 types) | 558 | equal (1 types) | no |
| k_WordCount | 3455 | 62 | equal (0 types) | 62 | equal (0 types) | no |
| k_Strings | 3775 | 366 | equal (0 types) | 366 | equal (0 types) | no |
| k_NBody | 3454 | 55 | equal (2 types) | 55 | equal (2 types) | no |
| k_Shapes | 3454 | 55 | equal (5 types) | 55 | equal (5 types) | no |
| k_LinkedNodes | 3454 | 55 | equal (1 types) | 55 | equal (1 types) | no |
| Collection | 3564 | 79 | equal (4 types) | 79 | equal (4 types) | no |
| ComplexMutableCollection | 3586 | 156 | equal (5 types) | 156 | equal (5 types) | no |
| Dictionary | 3541 | 62 | equal (1 types) | 62 | equal (1 types) | no |
| HashSet | 3648 | 84 | equal (5 types) | 84 | equal (5 types) | no |
| ImmutableArray | 3554 | 60 | equal (2 types) | 60 | equal (2 types) | no |
| ImmutableDictionary | 3635 | 183 | equal (2 types) | 183 | equal (2 types) | no |
| ImmutableHashSet | 3595 | 105 | equal (3 types) | 105 | equal (3 types) | no |
| ImmutableList | 3604 | 164 | equal (2 types) | 164 | equal (2 types) | no |
| ImmutableQueueStack | 3571 | 140 | equal (2 types) | 140 | equal (2 types) | no |
| ImmutableSortedDictionary | 3620 | 167 | equal (3 types) | 167 | equal (3 types) | no |
| ImmutableSortedSet | 3588 | 148 | equal (3 types) | 148 | equal (3 types) | no |
| LinkedList | 3596 | 174 | equal (1 types) | 174 | equal (1 types) | no |
| List | 3646 | 67 | equal (2 types) | 67 | equal (2 types) | no |
| ObjectModelCollection | 3642 | 65 | equal (2 types) | 65 | equal (2 types) | no |
| OrderedDictionary | 3572 | 137 | equal (2 types) | 137 | equal (2 types) | no |
| Queue | 3648 | 62 | equal (3 types) | 62 | equal (3 types) | no |
| SortedCollections | 3590 | 143 | equal (4 types) | 143 | equal (4 types) | no |
| SortedList | 3597 | 162 | 1 of 2 program types differ | 162 | 1 of 2 program types differ | no |
| Stack | 3595 | 153 | equal (3 types) | 153 | equal (3 types) | no |
| BigInteger | 3913 | 670 | equal (2 types) | 670 | equal (2 types) | no |
| LambdaIterator | 3470 | 55 | equal (3 types) | 55 | equal (3 types) | no |
| EnumerableIteratorContract | 3549 | 65 | equal (5 types) | 65 | equal (5 types) | no |
| RegexReplacement | 4399 | 636 | equal (2 types) | 636 | equal (2 types) | no |
| collector-retention | 3447 | 61 | 1 of 6 program types differ | 61 | 1 of 6 program types differ | no |

- **Program types.** Equal to .NET on 32 of 34 programs, naive and planned. Two differ, the same way in both builds:
  - SortedList: `NetWasm.Correctness.SortedLists.DescendingComparer` 1 vs 0 (the known S2 cycle; naive and planned alike).
  - collector-retention: `NetWasm.Correctness.CollectorLifetime.Payload` 1 vs 0, naive and planned. Section 2 had the
    planned build equal here. On this branch the plan leaves the Payload live at exit, so the returned-address keeper
    no longer frees it. This is a regression against section 2's result and needs a look at the planned build.
- **Planned against naive.** The planned build is never above naive on any type of any program. On this branch the
  live counts of the two builds are equal on every type of every program (the totals in the table are equal in each
  row). Section 2 had three programs where the plan freed more; those are now equal:
  Collection (planned 78 to 79), RegexReplacement (627 to 636), collector-retention (59 to 61).
- **Other types.** Every program also has 127 to 155 types that differ from .NET outside the program's own assembly
  (runtime and library internal objects, which differ by design). The count is the same for naive and planned.
  They are not listed here.
- **Totals.** BigInteger's live total is 670 in both builds here, against 682 in section 2. Not investigated; its
  program types are equal.
- Not done: no fix; the collector-retention regression is reported, not changed.

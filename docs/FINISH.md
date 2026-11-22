# Finishing plan (2026-11-20)

The working list to 100%. Updated as items land: each line says its state and the evidence. Details and history
stay in docs/AUDIT.md; this file is only what is left and how each part is checked.

## What 100% means

Scope as the owner set it on 2026-11-20:

1. **Any C# in, one WASM module out** (not WasmGC), behaving as .NET does, stack traces included.
2. **No GC of any kind**: no collector, no run-time walk of memory; what .NET frees, cs2wasm frees.
3. **Plugins good enough for the Invisible API.**
4. **Every module cs2wasm emits beats its NativeAOT-LLVM WebAssembly counterpart**: faster, smaller, less memory, no
   frame-time spikes, in the same engine. The self-hosted compiler is one such module: cs2wasm's build of the
   compiler against NativeAOT-LLVM's build of it, both compiling the same program.
5. **Self-hosted**: the compiler compiles itself, and that build compiles programs (the game in the browser too).

Not required: the time or memory it takes to *build* the self-hosted compiler (18 to 20 minutes against 80 s is
known and accepted). The compiler only needs to be reasonably fast to compile with (rapid prototyping).

**Owner, 2026-11-22: what counts first.** Close to NativeAOT-LLVM's speed or faster, without frame-time spikes (the
spikes are what ruled the stock .NET runtime out on the web), then the size and memory gains. So requirement 4 is steered
by game-shaped code (`tools/frames.mjs`: each scene's median and worst frame; `tools/bench.mjs`), and the self-hosted
compiler's time (2.37x NativeAOT-LLVM's on main 6e60243) is second to that: a compile allocates millions of objects and
frees none, the case a collector pays least for and counting most.

## The checks

Each requirement is done when its check passes. A check that does not exist yet is part of the work.

| # | Check | Now | Goal |
|---|---|---|---|
| 1 | Suite, checked and as shipped (`node tools/suite.mjs`) | 180/180 | all pass, no `known-gap` |
| 1 | Behaviour differences on record (AUDIT open items) | GetMethod, TargetSite, GetCurrentMethod, FirstChanceException and 6 trace differences closed (stream E's tests); reflection on compiler-made methods closed 2026-11-21 (tests 155, 156: names with Roslyn's ordinals, declaring classes, attributes, MoveNext); open: a capture-free lambda's `Delegate.Target` (`<>c` in .NET), the generated classes' own member lists, generic generated classes as constructed types; .NET's own library frames (not reproducible from source) | none |
| 2 | Leaks: test 139 under load, 127, 131 | 139: none in 400 loaded runs, checked and as shipped (was 2 nodes in ~1 run of 45; fixed 2026-11-20) | none in 200 loaded runs |
| 2 | Test 127 under load (`tools/stress-cycles.sh`) | open: `async 5 / pending 1` in about 1% of loaded runs (main 6e60243: 22 of 2,400; main d4b331e: 4 of 400; main dca1f9c: 3 of 400, the homelab); the 2026-11-21 fix made it rarer, not gone. Test 187 as shipped differs under load at about 2.5% (19 of 800 on main bc06ea9, 20 of 800 on dca1f9c) | none |
| 2 | Test 139 under load, a trap | the cause found and guarded (main 80ae1ce): the engine lets a thread go on with the memory size it last saw after another thread grew the memory, and checks atomic accesses, copies and fills against it; Wasm/MemSync.cs refreshes it before those (test 197, with the engine's stack checks off: 60 of 60 trap without the pass, none with it; test 139 so run 7 of 1,000 without, none of 3,000 with). Open: one of those 3,000 ended with two nodes left (`16 3`), none such in 34,400 with stack checks; not followed | none |
| 3 | Closed-form solver (A4.6), its four examples against running the ticks | pass (API check "plugin v6", version 6) | pass |
| 3 | Invisible API slice (9.4), `samples/invisible-api` (owner, 2026-11-20: a sample here, not in Bazoodle) | pass (suite "invisible-api slice", both modes; interface version 7) | builds and gates one interaction |
| 4 | `tools/bench.mjs`, optimizing tier: time, memory | 0.96x, 16 MB vs 71 MB | < 1.0x, less memory |
| 4 | Frame-time spikes: worst frame of a long run against NativeAOT's (`node tools/frames.mjs`) | 4 of 5 scenes below (worst frames 0.11x to 0.30x NativeAOT's with `--js-young 32`); tree worst 2.77 to 2.84 ms vs 3.11 to 3.22 (0.86x to 0.91x over three runs of 3), p99.9 2.66 to 2.68 vs 2.42 to 2.48 (1.07x to 1.10x), its rebuild (main 80ae1ce, this machine, 2026-11-23; AUDIT A3.5) | worst frame and p99.9 not above NativeAOT's on every scene |
| 4 | Frame medians against NativeAOT's (`node tools/frames.mjs`, the owner's first measure since 2026-11-22: close to the same speed or faster) | churn 1.60x (was 1.77x, 2.16x, 3.47x; what counting a moved reference costs is 14 us of its 80 a frame against the collector's 3.5: AUDIT A3.5, the churn loop, round 3), tree 1.65x (was 2.56x), collections 1.06x to 1.10x (p99 1.34x to 1.37x), strings 0.99x to 1.01x, bigdrop 0.90x to 0.94x (main 01949ae, this machine with other work on it, one run of 3) | close to 1.0x on every scene |
| 4 | Hello world size (test 145) | 27,089 B (main dca1f9c) | < 28,383 B |
| 4 | Self-hosted compiler on `Console.WriteLine(42)`, no threads: time | 9.51 s vs 3.82 s, 2.49x (main ad4c99e, the homelab, quiet, 3 interleaved rounds; this machine measured 1.98x under load, which slows NativeAOT's run more) | < NativeAOT-LLVM |
| 4 | Same: peak memory | 306.0 MB vs 322.2 MB, 0.95x (main ad4c99e, homelab; unchanged by the reflection merge) | < NativeAOT-LLVM's, same measure |
| 4 | Same: module size | 39.5 MB vs 129 MB (main ad4c99e) | met; next waste: code (36.2 MB) |
| 5 | Threaded self-hosted compiler: more threads not slower | 18.7 s on 1, 17.0 on 4, 26.6 on 12, 26.7 on 24 (main before: 16.3, 23.6, 37.7, 39.4); Roslyn's one group of 19,847 types is one lock. Homelab (16 threads), branch threads-scale: 15.5 / 12.9 / 15.1 / 15.9 / 18.2 s on 1 / 4 / 12 / 16 / 24 against main's 15.7 / 13.2 / 16.8 / 17.7 / 18.1 | faster with threads |
| 5 | Game built in the browser by the self-hosted compiler (proof 5 item 1) | met 2026-11-21: `node tools/proof5.mjs toolchain`, `web`, `check`: built in headless Chrome in 220.9 s, its 1,667 log lines (1,669 with `--panic`) equal stock Godot .NET's | plays, log equals the oracle's |
| 5 | Self-hosted output equals the .NET-hosted compiler's, on 1 and 4 cores (`node tools/selfdet.mjs [--self t.wasm] [--cores 1,4] <program.cs> [-r x.dll]`) | met 2026-11-21: 021_threads and the GodotSharp Hello (4,060,330 B) byte-identical, no-threads toolchain and threaded on 1 and 4 cores | byte-identical |
| - | 072_http's rare out-of-bounds | found and fixed 2026-11-20: none in 600 loaded runs per mode (was ~1 in 1,000) | found or not seen in 500 runs |

## Where the self-hosted compiler's gap is (measured 2026-11-20)

Library ready 9.05 s against 3.89 s, compile 2.30 s against 0.46 s; memory 470 MB against 239 MB. Profile flat:
arena (cycle) machinery about a quarter, counting about a tenth, host file calls 6%, the rest slower code
everywhere. Drop stats for one run: 78 million top checks, 37 million of them marked; 30 million checks from
releases. So the two levers, in order:

1. **Fewer cycle-capable stores (7.E, the back-reference proof).** Roslyn's green and red nodes, syntax lists,
   symbols and pooled builders all refer to their own kinds, so every store into them links. A proof that a field
   only ever points up (a parent, set once at construction or from the object that owns it) takes that edge out of
   the cycle graph; most of Roslyn's types then stop being cycle-capable and their stores become plain stores.
   Measure first: which sites are hot (count stores by site in the named no-threads build), then prove those.
2. **Code quality in the baseline tier** (where the compiler runs): calls and instructions per operation. IR
   passes where the WASM passes cannot see (steps 5 and 6), driven by the hot functions of the profile.

Memory (stream L, `CS2WASM_MEMSTATS=1`, 2026-11-21): the heap only grows and at its peak holds the same objects as
.NET's after a full collection (4.67 million, type by type). Round 4: 302 MB against NativeAOT-LLVM's 322 MB; the
memory grows by 16 MiB now, not by an eighth, so the peak follows the heap (a megabyte more of objects is at most
16 MiB more, where it was 38 MB) and the 9.3 MB of data below the heap lost the 4.1 MB of manifest resources. What
is left against NativeAOT is per object and the representation's: the count word (4.67 million times 4 bytes; the
header's third word is the identity hash, as NativeAOT's header word is, and 35% of the objects have one made:
Roslyn's SyntaxNodeCache hashes green nodes' children), and 32 bytes before each of 0.54 million arena members
(17.2 MB, all seven words in use). The library's sources are 110 MB of strings, as in .NET.

**Found 2026-11-20 (CS2WASM_ARENA_DEBUG on the self-hosted build): lever 1 is not a matter of proving a few hot
sites.** One cycle group holds 19,847 types, `System.Object` among them, and with it the parser (LanguageParser),
red nodes, symbols, binders, bound nodes, syntax trees; green nodes are a second group of 268, joined by their
child arrays (`ArrayElement<GreenNode>[]`). What joins the big group are slots of type `object` (or an interface)
that are stored a value whose static type is `object`: a task's state and continuation, AsyncLocal's maps, timers'
state, a list or array of objects, a dictionary keyed by object (`wide site` lines; 100 of them in test 088 alone).
Each such slot can hold anything, so everything that has one is on a cycle with everything. The type of a slot
cannot tell these apart; what can is where the values come from (the allocation sites and parameters that reach
the store, as `Sharing.cs` already follows for threads). That is the structural piece: value sources per site
instead of static types. Tried and without effect: a field that is only cleared after construction counted as
set at construction (the hubs are written for real, or are not private).
Also: a struct copied into a container that cannot be a group's member now stores plainly (8ed3739), but
Roslyn's `BlendedNode` copies go into `SyntaxParser`, which is in the big group; methods marked AggressiveInlining
are inlined everywhere up to 24 (was 16), which takes the runtime's word read (`Rt.PL`, 2.8% of the profile as a
call) into every caller.
**Tried the same day: value sources per site** (`Sharing.SiteTypeIds`, `CS2WASM_ARENA_SOURCES=1`, off by default; a
program with threads only, where the sources are recorded). Each site's targets narrowed to the types its recorded
sources lead to, followed through parameters, results and slots: in test 088, 30 sites narrow (a task's
continuation list 35 types to 4, a thread start's 35 to 2), but every wide `object` slot stays wide and the groups
do not change (876 cycle types either way). Followed far enough, each reaches a value recorded as any object: a
caller further up passes an `object` whose source the recorder does not know (a boxed generic value, a conversion,
an interface call's result). So the walk is right but the recording is too coarse: what this lever needs is sources
recorded precisely at every store, argument and return (and recorded without threads too), which is work across
the whole front end, not a pass of its own.

## Work streams

Each stream is independent of the others' files where marked, so it can run in a session of its own.

| Stream | Items | Files | State |
|---|---|---|---|
| A. Self-hosted speed and memory | 7.E proof on the hot sites; baseline-tier code quality | Compiler/Arenas.cs, Driver, Wasm/* | here, in progress |
| B. Self-hosted size | breakdown of the 64 MB by namespace and kind; cut waste | tools/size-report.mjs, RuntimeGen (names, packing), Driver (data) | first pass done (64.0 to 42.7 MB); code next |
| C. Threads scale | per-group locks for arena operations, runtime scratch per thread | Boundary/GroupLocks.cs, Runtime.cs (arena locking), RuntimeGen | done: groups lock apart (PLINQ trees scale); the self-hosted compiler waits on its one giant group: A's structural piece (value sources per site) |
| D. Proof 5 in the browser | game built by today's self-hosted compiler in the browser | host/web, tools/proof5.mjs, tests/Godot | done 2026-11-21 (two premature frees fixed on the way, tests 179 and 180) |
| E. Behaviour gaps | `StackFrame.GetMethod()`, `FirstChanceException`, the sweep's trace differences | CoreLib Boundary, stack trace code | done 2026-11-20; compiler-made methods' records done 2026-11-21 (tests 155, 156) |
| F. Plugins | A4.6 closed-form solver with its tests | Compiler/Solver.cs, PluginSolver.cs, tests | done 2026-11-20 (version 6) |
| G. Spikes | frame-time check against NativeAOT | tools/frames.mjs, tools/frames, tools/frames-naot-wasm | done: check, foreach borrows; tree scene's rebuild left to I and J |
| H. Flakes | test 139 leak under load, 072_http | Runtime.cs (read first), tests | done: both found and fixed (tests 149, 150) |

**Second fan-out (2026-11-20), for requirement 4's self-hosted compiler** (each in its own session and worktree):

| Stream | Items | Files | State |
|---|---|---|---|
| I. Value sources everywhere | record sources precisely at every store, argument and return (boxed generics, conversions, interface results, conditionals), also without threads; then `Sharing.SiteTypeIds` on by default where it narrows | Compiler/Expressions.cs, Calls.cs, Sharing.cs | done 2026-11-21: on by default, sound (test 171), but the self-hosted compiler's group only 20,717 to 20,636 types; its bound with every source known is 20,555 (Log) |
| J. Owned arrays | an array filled while only its maker knows it and then kept by a new object's readonly field is a node of its own in the type graph (Roslyn's green nodes' child arrays against its builders' arrays) | Compiler/OwnedArrays.cs (new), Arenas.cs | done 2026-11-20: green nodes out of the cycle graph (268 types); self-hosted 14.1 to 11.9 s, 470 to 295 MB |
| K. Baseline-tier code quality | from the side-by-side profile: counted reads whose count can sink to the one path that keeps the value, static-initializer checks, constant-length bounds checks, frame setup, small calls left | Wasm/*, Compiler where a pass needs a fact | done 2026-11-20: six changes (Log); micro 1.876x to 1.847x, self-hosted 2.74x to 2.65x NativeAOT (quiet rounds). Round 2 done 2026-11-21: count fast paths, frames in place, zero-skipping range releases; self-hosted 2.04x to 1.98x. Round 3: those three off by default (no gain on the self-hosted compiler, 9.5 MB), final tidy; hello world -700 B, module 82.3 to 72.4 MB, time the same. Left: typed releases for borrows (branch claude/k2-throw-only-wip), dispatch stubs Round 4: locals coalesced, counts in loops by fast path; compiler 1-2% faster, 70.1 MB. Round 5: shared bodies (functions identical but for i32 constants share one body, traces exact); compiler 70.2 to 56.8 MB, 1.0% faster, hello world 27,067 B; TurboFan inlining diagnosed (the lever is fewer count calls) |
| L. Self-hosted memory | where the 470 MB against 239 go (member words, counts, allocator waste by size class, pages held) and the structural cuts | Boundary/Runtime.cs allocator (after C merges) | first pass done: report `CS2WASM_MEMSTATS=1`; 568.9 to 482.9 MB (members' words inline, sparse words aside, frame names in UTF-8); the rest is per object (Log); round 2 (2026-11-21): 344.2 to 306.0 MB, size steps of 4 bytes, wider above 256, rounded at compile time |
| N. Arena operations | the runtime share of the self-hosted compiler (arena operations on red nodes and symbols): their usual cases without calls | Boundary/Runtime.cs (arenas), RuntimeGen (the slot test) | first pass done 2026-11-21: arena self time 2.08 to 1.64 s of a profiled run, self-hosted 11.09 to 10.54 s (5%); what is left is real work (Log) |

Tools for them: `tools/profcompare.mjs` (two profiles by category and by Roslyn method), `tools/profsize.mjs` (self
time by callee size), `tools/profcallers.mjs`, `tools/selfhost-ab.sh` (interleaved self-hosted builds against
NativeAOT-LLVM's), `tools/roslyn-parse-probe.cs` (a small program over Roslyn's parser: compiles in about 2 minutes,
with CS2WASM_DUMP=name to read a function's code). Heavy runs through `tests/out/heavy.sh`.

9.4 (the Invisible API slice): built as a standalone sample in this repository (owner, 2026-11-20), stream M, done:
`samples/invisible-api` and its check `tests/Cs2Wasm.Tests/InvisibleCheck.cs`.

## Log

- 2026-11-20: plan written. Self-hosted no threads 12.45 s / 470 MB / 64 MB against 4.6 s / 239 MB / 129 MB.
- 2026-11-20: stream F done. Plugin interface version 6: `PluginContext.Solve` (per-tick method or loop body) gives
  the piecewise affine map, guards with how they flip, exactness, and `GenerateAdvance` (repeated squaring; integers
  exact, floating point marked approximate). The four A4.6 examples, integer LCGs, a loop, a throwing Clamp, each
  against running the ticks; two unsolvable cases with reasons. Fixed on the way: generated code calling a program
  method with parameters (`Driver.InputType`). Suite 180/180 checked and as shipped.
- 2026-11-20, stream H: test 139's leak (a lock-free counted read of a member in its group's deferred finalizer
  phase made the check after the finalizers keep the group) and 072_http's out-of-bounds (a compare-exchange into a
  group's slot that did not happen left the offered value owned by the container) found and fixed; tests 149 and
  150 fail before; `tools/stress.mjs` for loaded runs. Details in docs/AUDIT.md.
- 2026-11-20, stream A: the two self-hosted compilers' profiles side by side (both under the same machine load:
  cs2wasm 74.5 s, NativeAOT-LLVM 36.4 s). cs2wasm's runtime 31.4 s (arenas 19.9, counts 4.4, allocation 3.6)
  against NativeAOT's GC and allocation 6.0 s; the same Roslyn methods' own code 2.0x; host file calls 8.0 s against
  17.7 s. Fixed from it: displays of local functions that are only called live in the frame (c1acced; Roslyn's
  ParseAnonymousFunctionModifiers allocated one per call, 948 ms against 33), small bodies inlined up to 12
  instructions and AggressiveInlining ones up to 24 (Hash.Combine, Rt.PL stayed calls), a struct copied into a
  container no group can hold stores plainly. Micro baseline 1.85x to about 1.75x.
- 2026-11-21: reflection on what the C# compiler makes. Lambdas', local functions' and state machines' names now
  follow Roslyn's closure conversion with its ordinals (scope tree, environments, merging in release builds; checked
  against Roslyn's own output with `CS2WASM_CHECK_NAMES=1`), and they have records: `Delegate.Method`, a frame's
  `GetMethod`, `TargetSite` and `GetCurrentMethod` give `<Main>b__9_0` on `Program+<>c`, `MoveNext` on
  `Program+<Numbers>d__5`, with parameters and `[CompilerGenerated]`; kickoff methods carry
  `[AsyncStateMachine(typeof(...))]` / `[IteratorStateMachine]`, `[DebuggerStepThrough]`. TargetSite through
  `ExceptionDispatchInfo.Throw` fixed. Tests 155, 156; suite 198/198 checked and as shipped (homelab);
  `tools/names-check.sh` debug and release clean; hello world 27,297 B unchanged. Left: a capture-free lambda's
  `Target` (AUDIT).
- 2026-11-20, stream E: frames' methods (`StackFrame.GetMethod`, `TargetSite` through `[StackTraceHidden]` throwers,
  `GetCurrentMethod`), `AppDomain.FirstChanceException`, and six trace differences a sweep against .NET found
  (generic definitions' names, merged identical methods, iterator frames and lines in state machines, failed static
  initializers, async traces): tests 151 to 154. Hello world 27,574 to 27,981 bytes measured at one path (+407: the
  static initializers' traces 369, the unmerged frames 24), in the main checkout about 27,908. docs/AUDIT.md, stream E.
- 2026-11-20 (stream B): `tools/size-report.mjs` (sections; code by kind, namespace, type and source method; data
  expanded and split into literals, resources, tables). The 64.0 MB were code 36.1, data 23.3 (12.5 of it the full
  names of 37,841 generic instances, eager `Type.FullName` literals; 4.0 the library sources; the data was not packed
  because packed it did not fit the start's first megabyte of heap), frames 1.2. Now: generic instances' and arrays'
  names made when first asked for (38,412 of them; the Type.GetType table keeps their lengths), id lists shared,
  packed data expanded through a 1 MB window, dead chunks of a pinned layout zeroed. Self-hosted compiler (no
  threads, shipped) 63,966,970 to 42,664,712 bytes (data 23.3 to 2.8 MB in the module, 20.9 to 12.8 MB expanded);
  its hello world byte-identical, time unchanged (14.2 s, interleaved). Hello world 27,541 B (unchanged), bench
  372,676 to 372,166 B (0.91x, 16.0 MB vs 71.3), test 036 528,820 to 512,342 B. Suite 179/179 checked and as
  shipped. Left: code, 36.2 MB (generic instantiations 14.9, System.Linq 7.6, dispatch stubs 2.0; AUDIT).
- 2026-11-20, stream C: per-group locks for arena operations (Boundary/GroupLocks.cs). A store's link, unlink and
  exchange, a member's first and last outside reference, a join, a merge and the drop check lock the groups they
  touch (256 stripes, lowest first, verified), not the runtime lock; a join's objects are found under the locks
  before anything changes; the arena scratch is per thread; the allocator has a lock of its own. Programs without
  threads compile none of it (Rt.ThreadMode): hello world byte-identical, micro and bench the same code. Measured
  interleaved, holding a heavy.sh slot, main before against now: the PLINQ tree program
  (tools/bench-threads/plinq_trees.cs) 0.60 / 3.2 / 6.5 / 7.0 s on 1 / 4 / 12 / 24 threads against 0.79 / 0.46 /
  0.45 / 0.44; the threaded self-hosted compiler on hello world 16.3 / 23.6 / 37.7 / 39.4 s against 18.7 / 17.0 /
  26.6 / 26.7. Left: the compiler still waits on more than 4 threads, on one stripe: Roslyn's types are one group
  (the lexer's and the node cache's stores, BlendedNode copies), which no lock per group can split; one thread is
  15% slower than with the one lock (the stripes' bookkeeping). Suite 187/187 checked and as shipped (070_files failed once in a run beside another session's, its fixed directory shared; passes alone); test 139 200
  of 200 under stress (8 at once).
- 2026-11-20 (G): spikes check `tools/frames.mjs`, five frame loops against NativeAOT-LLVM. Worst frame cs2wasm vs
  NativeAOT: churn 8.6 / 23.1 ms, collections 0.31 / 2.56, strings 0.15 / 0.36, bigdrop (150k objects dropped in one
  frame) 2.8 / 17.5, tree 31.9 / 4.3; peak memory 16 MB against 59 to 71 MB (tree 32). tree's spike is its rebuild:
  a scene tree with children lists is cycle-capable by type, so building it is arena links (15 ms against ~1 ms);
  the fix is 7.E in the compiler (stream A). churn's median is 3.4x NativeAOT's (speed, open).
- 2026-11-20 (G): a `foreach` over a `List<T>` in a field of a stable object borrows the list, and its loop variable
  keeps its borrow across a recursive call (the changed-list MoveNext call and a throw's trace no longer count as
  element stores; Rt.FreeObjectArena is the allocator's). tree's median frame 0.76 to 0.14 ms (NativeAOT 0.03).
  On a quiet machine (heavy lock), worst frame cs2wasm vs NativeAOT: churn 0.90 / 1.96 ms, collections 0.34 / 1.54,
  strings 0.16 / 0.32, bigdrop 1.83 / 9.62, tree 29.9 / 3.8 (the rebuild: streams I and J take types out of the
  cycle graph; measure again after they merge). bench 0.82x.
- 2026-11-20, stream K (baseline-tier code quality): measured against a NativeAOT-LLVM build of the same sources (the
  old `naot-wasm-measure` was an older compiler doing other work), both profiled with `--no-wasm-lazy-compilation`
  (lazily compiled, 5 s of the 25 s profile were samples waiting on first-call compiles, charged to whatever ran next:
  the "2.8 s in SwitchExpressionBinder" was that). Profiled hello world: code 11.8 s against 4.1 (the same Roslyn
  methods 1.84x), runtime 11.7 against 1.8. Changes: `rt.array_clear` word by word up to 256 bytes (memory.fill is an
  engine call; Roslyn clears a list builder's whole array at every reuse); a returned struct made in the caller's
  destination (no frame temporary, no exception region, no copy); `x = default` copied from zeros; an element of a
  static's array borrows, and a type's initializer's own stores do not break a borrow of its statics (152 borrows in
  the parser probe, 138); the inliner's second round takes leaves up to 12 instructions (Hash.Combine) and copies
  lazy getters' fast paths to their sites with the call left on the slow path (SyntaxParser.CurrentToken from 874
  sites; traces unchanged); constant locals fold (`switch (Unsafe.SizeOf<T>())`: SegmentedArray's SegmentShift and
  OffsetMask in StringTable.FindSharedEntry's loop are constants now). Tried and reverted: br_table dispatch stubs
  (the baseline tier compiles a br_table as a binary search over its range: interface stubs 1.2x slower). Micro
  (baseline tier) 1.876x to 1.847x NativeAOT; parser bench (`tools/roslyn-parse-bench.cs`) 375 to 350.5 ms; self-hosted
  compiler (no threads, same sources) 77,718,084 to 77,170,647 bytes, hello world 13.7-14.5 s to 13.3-13.6 s on a quiet machine (NativeAOT-LLVM 5.0 s: 2.74x to 2.65x). Hello world 27,868 to 27,745
  bytes. Suite 188 passed checked and as shipped. docs/AUDIT.md, stream K.
- 2026-11-20, stream L (self-hosted memory): a heap report, `CS2WASM_MEMSTATS=1` at compile time (a line at every
  growth of the memory; the export `cs2wasm_memstats`, which host/run.mjs calls at the end, prints the last growth's
  state and the end's: by size class, by type, members and which of their words they use, large blocks, free runs).
  What it found on the self-hosted compiler, hello world, no threads (569 MB of linear memory): the heap only grows,
  nothing of the compilation is freed before the end, and it holds the same objects as .NET's heap after a full
  collection at the same point (dotnet-dump of the .NET-hosted compiler: 4.74 million objects; String 371K vs 366K,
  SyntaxTrivia 202,090 vs 202,111): no garbage kept. The allocator wastes little (4 MB held free in pages, 1 MB in
  free runs). The arena's words were 3.7 million blocks of 48 bytes (175 MB) and an 8-byte prefix on each member
  (30 MB); 36 MB below the heap is data (frame names of StackFrame.GetMethod's table: 57,000 string literals).
  Cut: the words are in the member's block, before its header; the two that few members use (a waiting container
  6%, a failed drop check's note 5%) are in 8 bytes of their own made when first written, which the block's tag
  points to (a member 56 bytes to 32; threads: 40, with the group's lock word); frame names in UTF-8 in one blob
  (data 35.7 to 29.2 MB). Interleaved (`tools/selfhost-ab.sh`, now with each run's peak), 4 rounds: main 199572c
  14.6 s / 568.9 MB, stream L 14.0 s / 482.9 MB, NativeAOT-LLVM 5.5 s / 322.2 MB. micro --baseline 1.87x both;
  bench 0.95x both, 16.0 MB (its floor); hello world 27,812 B unchanged; arena programs about 2 KB smaller. Suite
  187/187 checked and as shipped. The threaded self-hosted build compiles hello world right on 1 and 12 threads.
- 2026-11-20, stream M: the Invisible API's first slice, `samples/invisible-api`: a game in plain C# (players keyed by
  id with private position, velocity, health and cooldown; coins keyed by player; a claimed grid; a wave timer) and
  a plugin that registers everything from `RunBeforeCompile` (`CompileResult.RecompiledFunctions` 0). It classifies
  the 21 writes (PathConditions with a taint of the input exports' parameters along each path; `Player._hp` unknown:
  a `ref` unsure site), intercepts the three input reads (and replaces one action for the server), marks the
  writes under input with `InstrumentWrite` and the entry writes under input with the player key with the new
  `InstrumentEntryWrite` (version 7: per-site entry hooks; the tick's deterministic `_coins[id] += 2` stays
  unmarked), generates the fire gate from `TryFire`'s own conditions over private fields (`gate_fire`,
  `gate_view`), and solves the wave timer exactly (`server_predict`, `server_advance`). The check plays a script with
  `driver.mjs` and under .NET without the plugin: dumps, gate answers, views, predictions (up to 1,000 ticks) and a
  100-tick catch-up equal; every value an input changed marked; no tick marks anything. Gaps: per-site entry hooks
  (fixed, version 7); no way to see a second compile (`RecompiledFunctions`); open in docs/AUDIT.md: input taint
  and conditions as C# over a receiver (the plugin does both itself), field initializers not in `Accesses`.
- 2026-11-20, stream J: owned arrays (Compiler/OwnedArrays.cs). A field whose arrays are filled while only their
  maker knows them and never written once handed over is *sealed*: in the type graph it leads to a node of its own
  for the array type, whose element edges close no cycle; arrays of that type held elsewhere (the builders') keep
  the type's node. Proven over every compiled body (a fixpoint over fields, parameters, locals, writing callees and
  fresh returns; any other use, a write through a span, Array.Copy into it, a ref to an element, a lambda that
  writes later, an override that keeps it, a caller passing a builder's array, reflection that can call the
  constructor, keeps the field an edge). Roslyn's `SyntaxList.WithManyChildrenBase.children` is sealed: the green
  group (268 types, `ArrayElement<GreenNode>[]`) is gone, no group holds a green node. In the self-hosted build it
  first was not, because reflection could call every program constructor: asking frames for their MethodBase gives
  every program method a record and an invoke thunk, and the compiler's own CS2WASM_STATS aid and
  Exception.SetCurrentStackTrace (through .NET's StackTrace text, which reads each frame's MethodBase) asked; and
  Activator.CreateInstance(Type) on unknown types kept all constructors of the program's types. Now the aids read
  names from the trace text, that StackTrace overload is substituted as its sibling is (it now writes lines as
  .NET does: test 177), CreateInstance(Type) keeps parameterless constructors only, and the analysis asks for an
  invoke thunk, not a record. Self-hosted compiler, named no-threads builds, hello world, interleaved 3 rounds
  against NativeAOT-LLVM: main 14.8 s, the reflection part alone 14.1 s, with the rule 11.9 s, NativeAOT 5.9 s
  (2.50x to 2.01x); library ready 11.6 to 8.8 s; memory 470 to 295 MB; arena self time 3.52 to 1.76 s (21.7% to
  13.0% of the profile); cycle types 39,351 to 38,796; module (named) 78.5 to 72.6 MB; its output byte-identical.
  The analysis takes 1.5 s of the self-host build (3,432 fields asked). Tests 175 (the shape and eleven near misses,
  with a `// cycles:` marker in the runner: exactly these classes are CW0202), 176, 177. Suite 190 passed checked
  and as shipped. Found: a store through a span alone recorded no value at the array's site (fixed 2026-11-21, below).
- 2026-11-21, main 3810a98 (streams C, G, H, J, K, L, M merged), interleaved against NativeAOT-LLVM, 3 rounds: self-hosted
  compiler 12.0-12.8 s / 344 MB / 44.0 MB against 6.2-6.5 s / 322 MB / 129 MB. Time 2.0x (2.7x this morning), memory
  1.07x (1.8x), size 0.34x.
- 2026-11-21, stream I: value sources recorded in every program (not only with threads) and a class field's arena
  targets narrowed by them by default (`Sharing.SiteTypeIds`, once per component of the source graph in
  SharingFlow.cs; `CS2WASM_ARENA_SOURCES=0` for the old way; hello world byte-identical, 27,745 B). Made precise:
  `ref`/`in`/`out` parameters (what the callers' places hold), `Unsafe.Add`/`As` to the place they name,
  Interlocked's generic stores, `Buffer.Memmove`, a struct inside an object (its struct field's node), stores
  through refs only for slots whose address may be kept (an escape analysis of ref parameters), nodes recorded with
  only nulls. Kept sound: array elements are not narrowed (references reach them by the runtime's raw copies:
  Array.Copy, SetValue, Memmove), a ref field reads as any object, reflection's writes to statics and struct fields
  apply. Found on the way and fixed: `FieldInfo.SetValue` into a class field skipped the arena protocol and the type
  graph (a cycle closed that way leaked, narrowing or not). Not fixed (runtime, outside I): an object made by
  `MemberwiseClone` of a cycle-capable type leaks with its cycle. Test 171: 32 ways a value reaches an object field,
  each closing a cycle; all freed, both modes, narrowing on and off. Suite 193/193 checked and as shipped. Numbers: test 088 437 to 388 cycle types
  (largest group 396 to 344); the self-hosted compiler on main with J: 20,841 to 20,763 cycle types, largest group
  20,717 to 20,636, cyclic sites 40,533 to 40,350; its hello world 14.24 s off against 14.29 s on (NativeAOT-LLVM
  5.27 s; interleaved, 3 rounds, before J), the build +24 s (arena graph 49 to 72 s). **The bound:**
  `CS2WASM_FLOW_OPTIMISTIC=1 CS2WASM_FLOW_CAP=1000000` (a measurement, unsound: every unknown source taken as
  nothing, arrays narrowed too) leaves 20,555 types in the group, `System.Object` among them. The hub slots
  (a task's state, AsyncLocal's maps, ParameterInfo's default, Linq iterators' current) keep about 3,800 exact types
  each even then: a context-insensitive flow merges every caller of the shared code they are filled through, and
  Roslyn's precisely typed edges close the rest. So recording precision is not what holds the group together; what
  would split it is an analysis that tells callers apart (per call site, or per allocation site of the container),
  or proofs on typed edges (7.E). CS2WASM_FLOW_CAUSES=1 prints, for each site that stays wide, one source that
  makes it so and the path to it.
- 2026-11-21, stores by address (follow-up of stream J): a cycle closed only by a store through a span's element,
  MemoryMarshal's reference, Unsafe.Add, a reinterpreted ref, Interlocked through a span or a readonly field written
  through Unsafe.AsRef was in no group and never freed (test 178, 50 of each left). Each store by address now records
  its value against its slot type, and every array type of that element type holds it (Compiler/RefStores.cs, AUDIT).
  Self-hosted compiler (main 3810a98, before stream I): groups and cycle types unchanged (61, 38,881), time the same (10.68 s against 10.74),
  output byte-identical. Suite 193 passed checked and as shipped.
- 2026-11-21, stream K round 2 (on main 3810a98): profiled against NativeAOT-LLVM of the same sources (13.1 s against
  5.9 s, eager; the count functions' own time 1.36 s, the largest share left in reach). Kept: the counts' common case
  written out in the functions a loop calls three deep (`Wasm/RcFastPaths.cs`, no threads; parser bench 4%, micro
  0.972; the whole functions inlined everywhere were 7% at twice the size), frames up to 64 bytes taken in place there
  (parser bench 2.8%), range releases skipping all-zero elements. Self-hosted compiler 10.26 to 9.94 s on a quiet
  machine, 8 interleaved rounds, faster in each (2.04x to 1.98x NativeAOT-LLVM), 72.1 to 81.2 MB. Hello world 27,745 to
  27,912 bytes. Suite 195 passed checked and as shipped (rebased on 5f7c98d). Dropped: struct helpers inlined (no gain). Parked: borrows
  across throw-only stores (branch claude/k2-throw-only-wip), which pay only with typed releases. docs/AUDIT.md.
- 2026-11-21, stream L round 2 (branch claude/stream-l2-memory, on 6a346d8): self-hosted compiler 344.2 MB to
  306.0 MB against NativeAOT-LLVM's 322.2 MB, time unchanged (interleaved, 3 rounds: main 10.6-11.1 s, L 10.7-11.1 s,
  NativeAOT 4.9-5.5 s). With J's green nodes out of the cycle graph the report showed 4.6 million objects, 0.54
  million members, and what the size classes cost: blocks rounded to 8 bytes (an object whose fields need 4 took 4
  more: Roslyn's 36-byte tokens 40), and 380 classes above 512 bytes holding 3.4 MB of blocks in 7.8 MB of pages.
  Cut: small sizes in steps of 4 bytes to 128, 8 to 256, eight per doubling above (a class with an 8-byte field a
  multiple of 8, so its block is at one); the smallest block 8 bytes without threads (side words in 8 bytes, an
  object of 12 in 12); a constant size rounded at compile time (Rt.AllocObjectC / FreeObjectC: the rounding in the
  hot path had cost the allocation micro benchmarks 10 to 26% in the baseline tier). micro --baseline 1.83x/1.82x
  against main's 1.87x/1.87x; bench 0.95x/0.96x against 0.95x/0.95x, 16.0 MB both; hello world 27,744 to 28,120 B.
  Suite 192/192 checked and as shipped (in one run of five 070_files and 127 failed in both modes: 127's async leak
  is the intermittent one on main; 070's directory name is the same in every run, the host's random seed being a
  constant, so its checked and shipped runs collide when they overlap, main's too). The report itself: a page on the
  empty list keeps a stale header (read as a size class out of its table, a trap in the self-hosted build).
- 2026-11-21, main 26150c1 (K and L round 2), interleaved against NativeAOT-LLVM, 3 rounds: self-hosted compiler
  10.6-10.8 s / 306.0 MB / 53.9 MB against 5.1-5.6 s / 322.2 MB / 129 MB: time 2.0x, memory 0.95x (met), size 0.42x.
  Then ae57217: stream D's two premature-free fixes (a borrow temporary unwound as a counted one; MemberwiseClone
  linking twice); the Godot probe binds all of GodotSharp clean under --checked.
- 2026-11-21, stream L round 3 (branch claude/stream-l3-memory, on 26150c1): what round 2 cost a program that barely
  allocates, taken back: hello world 28,287 to 27,868 bytes (27,869 before round 2), the self-hosted compiler still
  306.0 MB (interleaved, 3 rounds, against main's build: the same peak, 11.5-13.3 s against 11.5-13.9 s; NativeAOT-LLVM
  5.5-6.3 s, 322.2 MB). The wider size steps above 128 bytes only for a program that constructs 1,000 types or more
  (Rt.WideSizes, a constant written at the end of the build as rt.single_threaded is, its dead branches removed): a
  small program rounds to 4 bytes in two instructions and has no RoundSmall. Round 2's second entries (AllocObjectC,
  FreeObjectC, sizes rounded at compile time: 431 bytes) taken out; the fast paths call RoundSmall only above 128
  bytes; blocks cleared from their end down before the header is written. micro --baseline 1.71x/1.75x against
  main's 1.78x/1.75x; bench 0.95x/0.97x against 0.97x/0.94x, 16.0 MB. Suite 195/195 checked, as shipped 194/195:
  085 once, where .NET's own run (the expected output) had left 96 objects uncollected under load (cs2wasm's
  "gone" is right; 400 of 400 loaded runs).
- 2026-11-21, stream D done: proof 5 met in the browser. `node tools/proof5.mjs toolchain` (cs2wasm without threads,
  Godot's source generators compiled in, their sources fetched into the cache at stock commit ed1daf0bf; 55.2 MB, 14
  min), `web`, `check`: the game built in headless Chrome in 220.9 s (18.2 MB downloaded, 2,160 MB of memory at the
  end), played in the stock 4.7.2 web template, 1,667 log lines equal to stock Godot .NET's (1,669 with `--panic`).
  What stopped it on 11-20: two premature frees (tests 179, 180, on main as ae57217), found with
  tests/Godot/proof5_hello2d/repro/RoslynGodotProbe.cs. The threaded toolchain no longer hangs on GodotSharp: 758 s
  told one core, 624 s told four. Open: the self-hosted output on a GodotSharp program differs by build and by core
  count (data, and code by a byte). host/README.md: the embedders' rule, never start a thread from a busy worker.
- 2026-11-21, stream N: arena operations without the calls their usual cases made (Boundary/Runtime.cs). Measured
  first (CS2WASM_DROP_STATS=1 now counts each arena path): in a parse of 400 classes 96% of links are stores by
  address (struct copies through `ref`, iterators' state), 6.3 million UpdateHold calls of which 5.5 million end at
  its first look, a walk to the top before every inside unlink only to see it was not far, and every helper of a
  store by address a call in the baseline tier. Now: UpdateHold's first look where it is asked (HoldSettled); ArenaOf,
  PLM, Dropped's, TryLeave's, NoteOutside's and TopHeld's first tests where they are asked; that walk gone; SlotOwner
  with ObjectOf, BlockOf and MapGet written out and the block found by a multiplication (exact for every offset and
  block size, checked for all); the per-type slot tests written into the slot test's cases, which also say an
  array's element (no TypeInfo.Rank per store); rt.scc_of inlined. tools/arena-bench.cs (red nodes' shape) 63 to
  48 ms, tools/roslyn-parse-bench.cs 262 to 229 ms under Liftoff; the self-hosted compiler, named no-threads
  builds, hello world, interleaved: 11.09 to 10.54 s (NativeAOT-LLVM 5.27 s), arena self time 2.08 to 1.64 s of a
  profiled run; a later round 11.63 to 11.26 s on a busier machine; output byte-identical, memory the same; micro
  --baseline 1.73x. Tried and left out: first and last outside references written out in ArenaRetain and
  ArenaRelease (they apply to 3% in a parse: red nodes have side words, a waiting container's, or a hold that
  really changes). What is left is real work, not calls: red nodes have up-references below them (their parent
  links), so a node's first and last reference from outside changes its hold on its owner, up the chain
  (0.8 million changes a parse), and the failing checks at the drop (30,000 a parse, walking 260,000 members).
  Test 185. Suite 197 passed checked and as shipped.
- 2026-11-21, stream K round 3 (on main 26150c1): round 2's count fast paths, frames in place and zero-skipping range
  releases measured one by one on the self-hosted compiler (builds with each off, interleaved, the machine's load
  checked before each round): none moved it beyond 1%, and they were 9.5 MB of its 82.3; off by default now. A final
  tidy on the finished bodies (offsets folded into loads and stores, redundant compares and returns). Hello world
  28,288 to 27,580 bytes (27,869 to 27,194 after stream N), the module 82.3 to 72.4 MB, the time the same within the
  noise (10.53 s against 10.47, 12 rounds; NativeAOT-LLVM 5.17 s). Dropped on the way: `unreachable` after throw
  helpers (micro 6% slower: the baseline tier merges block ends), fast paths without a null test (2.3% slower).
  Suite 197 passed checked and as shipped (rebased on 3be4326). docs/AUDIT.md.
- 2026-11-21, main 480a101 (stream N, K round 3), 3 interleaved rounds against NativeAOT-LLVM: self-hosted compiler
  10.7-11.4 s / 306.0 MB / 44.0 MB against 5.44 s / 322.2 MB / 129 MB: time 2.0x, memory 0.95x, size 0.34x.
- 2026-11-21, stream N round 2: holds kept once made, designed (docs/AUDIT.md) and measured: 90% fewer hold changes
  in the self-hosted compiler, its time within the noise, its peak memory 12% more, unexplained; so an experiment
  (`CS2WASM_STALE_HOLDS=1`), not the default. The counters showed where the arena time is instead: 55 million first
  and last outside references through the slow path and 25 million walks to a top. In by default: marks of the
  kind of failed check a member is under (walks 25.3 to 19.6 million), and the first and last outside reference's
  usual case without a call (65% and 62% of them). Self-hosted 10.47 to 10.27 s against NativeAOT-LLVM's 5.38,
  memory the same. Suite 197/197 both modes. Left: the slow first and last references (a member that is under a
  failed check of the held kind, or whose hold changes), and what lifts the 2.0x is no longer one arena path.
- 2026-11-21, self-hosted output: the self-hosted compiler built a different module from the .NET-hosted one for
  threaded and GodotSharp programs (Sharing found 399 types another thread can reach against 1,861). Cause: a
  miscompile, `Folding.KnownNull` folded `x == null` for a local nothing assigns, looked for in the body being
  compiled only, so a local function or lambda saw its enclosing method's captured local as never written (test 186;
  Sharing.Analyze's node keys are such code). Also host-dependent: assembly identities came from the host's
  `Assembly.Load` (now the reference pack's, Reflection.cs) and `Assembly.Load` in a module threw (now finds a
  module's assembly by name). `tools/selfdet.mjs` checks it: equal paths and output name, `--pathmap /=/` on Windows.
  GodotSharp Hello: self-hosted on 1 core 510 s, on 4 cores 953 s (stream 5's threading row). Suite 198/198 both modes.
- 2026-11-21, stream K round 4 (on main 480a101): where the self-hosted compiler's 10.7 s go. Compiling is small (all
  168,600 functions eagerly: +0.5 s; a run compiles 30,000 lazily), the optimizing tier carries the hot code
  (`--liftoff-only` 17.2 s), and Liftoff makes 2.9x NativeAOT's machine code for what a run compiles (our code 1.8x
  theirs per function set, twice the locals and calls per KB). Locals coalesced on the final bodies (locals per KB
  of the parser bench 23.8 to 12.4; the compiler 1.5-2.3% faster); counts in loops by their fast path instead of the
  whole functions (the bench's 0.94x kept, the compiler 72.3 to 70.1 MB). Hello world 27,194 to 27,297 bytes. Suite
  198 passed checked and as shipped (rebased on 05e8cd0). docs/AUDIT.md, stream K round 4.
- 2026-11-21, main 1191d18 (K round 4, N round 2, D's KnownNull fix; self-hosted output now equals the .NET-hosted
  compiler's), 3 interleaved rounds: self-hosted compiler 10.2-10.3 s / 306.0 MB / 41.8 MB against 5.1-5.3 s / 322.2 MB /
  129 MB: time 1.98x, memory 0.95x, size 0.32x.
- 2026-11-21, fewer count operations (branch claude/rc-fewer-counts, on ec13733): where the counts come from, measured
  (CS2WASM_COUNT_RC by cause, `tools/rccauses.mjs`): the self-hosted compiler runs 931 million retain and release calls
  on hello world; by kind, array clears of one-reference structs element by element 15.6%, struct copies into fields
  8.3%, declared locals' scope ends 6.3%, arguments' and receivers' temporaries 4.6% and 4.4%, stores' old values
  7.4%, unproven borrows of receivers 5.0%. Changes: an indirect call reaches only functions of its own wasm type, a
  delegate's Invoke only the targets of its own constructed type or variance's (test 187), a switch expression of
  borrowed arms is borrowed, `f ??= v` breaks no borrow, multi-statement bodies return fields borrowed, Array's
  GetLength/GetLowerBound/Copy without counted handle copies. Parser bench 61.2 to 59.8 million calls, 25.7 to 24.1
  million counts changed (6% fewer); the self-hosted compiler 930.8 to 915.2 million calls, 348.3 to 337.7 million
  counts changed (3.0% fewer); its time on the homelab, the three quiet rounds of six: main
  10.24 s, branch 9.94 s, NativeAOT-LLVM 4.28 s (2.39x to 2.32x; the homelab, shared with other sessions, loaded in
  the other rounds); merged with fbe7688, the bind bench 0.981 of main (2.20x to 2.16x NativeAOT), bench 0.92x, micro
  --baseline 1.70x, suite 197/197 both modes on the homelab, hello world 27,067 to 27,109 bytes. Two thirds of the
  counts left are the lexer's and parser's, 1% the binder's. docs/AUDIT.md, "Fewer count operations".
- 2026-11-21, stream K round 5 (on main 1191d18): shared bodies (`Wasm/ShareConstants.cs`, on by default,
  `CS2WASM_SHARE=0` off). Functions identical except for i32 constants (the type ids, static addresses and table
  indices of a generic method's instantiations over reference types) and for which member of such a group they call
  share one body; the differing constants become its last parameters, the members stubs. Traces stay exact: pass 1
  shares groups of one frame name, pass 2 lends a shared body's lines to each member's frame through a new trailing
  list in the cs2wasm.frames section (host/engine.mjs, host/web). Self-hosted compiler module 70.2 to 56.8 MB, wall
  time -1.0% against the same compiler with sharing off (12 interleaved rounds); hello world 27,297 to 27,067 bytes;
  bench.mjs 0.94x NativeAOT unchanged. TurboFan inlining diagnosed (`tools/k5`): budgets x4 make ours 5.6% faster,
  NativeAOT 3%; most refused inlines are rt.release (6,768) and rt.retain (2,160), so the lever is fewer count calls,
  not inlining. Suite 198/198 checked and as shipped (rebased on 23d5188). docs/AUDIT.md, stream K round 5.
- 2026-11-21, threads past four (branch threads-scale, on ec13733): stripe waiters capped at two spinning (the rest
  sleep, woken only when none spins); no LinkLock walk before the first thread; no lock for a store into a pinned
  plain object; with threads a member stored into a plain object stays a counted reference (no join). Homelab, 3
  interleaved rounds, threaded self-hosted compiler on hello world, 1 / 4 / 12 / 16 / 24 threads: main 15.7 / 13.2 /
  16.8 / 17.7 / 18.1 s, branch 15.5 / 12.9 / 15.1 / 15.9 / 18.2; output byte-identical on every count. Check 5 still
  not met: past four threads still slower than four. What waits is the parse: the lexer's and parser's stores and
  cached tokens' first and last outside references on the one group. Suite at ed1e518 196/196 checked and as shipped on the homelab (198/198 here at 0fcc4fa); tests
  139 (200 runs), 147, 149 under stress. docs/AUDIT.md, "Threads past four".
- 2026-11-21, test 127's leak (`async 5` / `pending 1`, 8-15 of 200 loaded runs): one garbage structure with two
  tops whose trees refer into each other (a pending TaskCompletionSource owns the root and a WhenAll array; the
  array refers to a task result, a top of its own, whose `Parent` refers to the root), so each top's check at the
  drop failed by the other's reference. `Rt.Dropped` now takes in, after what the top owns, the tops it refers to
  directly that no code refers to and nothing owns, with what they own, and stops at the first member under one of
  them that code refers to; on a failure by an outside reference it checks again by what the top owns alone (the
  list's first part). `CS2WASM_NO_TAKE_IN=1` the old check, `CS2WASM_ARENA_REPORT=1` what is left at GC.Collect
  (both at compile time). Test 187. 127: none in 200 loaded runs, checked and as shipped. On main 1191d18: suite
  199/199 both modes; hello world 27,297 bytes (unchanged); self-hosted compiler (no threads, homelab, 8 interleaved
  rounds) 9.54 s against main's 9.46 s (+0.8%, about the noise), memory the same (306.0 MB), output byte-identical.
  Rebased on 047b2b7 (K round 5): hello world 27,067 bytes (main's), suite on the homelab 197/197 checked and as
  shipped (no Godot there); on 78eb489, hello world 27,067 bytes and 127, 187, 133 pass both modes. Open: an
  outside-kind note's number goes stale when a referring member is adopted into the top's own tree (docs/AUDIT.md).
- 2026-11-21, branch tree-spike (on ec13733): the tree scene's rebuild in parts (`tools/tree-parts.cs`, homelab):
  build 6.0 ms, drop of the old tree 4.3 ms. Found and fixed: a third of the group at the drop was subtrees replaced
  long before (a member destroyed at an unlink lost its owner first, its up-references stayed on its container's u,
  and a list's array then refused its Handover and kept all it held); the group's free went round the ring four times
  reading every member's referents (now three passes over a list, counts zeroed first); a tree built from the leaves
  up made each node owned by its first child (a joining object's slot now decides ownership after the links: the
  child's parent field an up-reference); stores into a constructor's own object fresh (and the hole they showed: a
  plain container's field past the sixteenth word unlinked by the counts, test 132); a list's growth copies plainly.
  Build 6.0 to 5.3 ms, drop 4.3 to 1.8 ms. frames.mjs on the homelab, main against the branch (2 rounds of 3 runs):
  tree worst 10.6 / 10.8 to 7.4 / 7.4 ms, p99.9 10.3 / 10.2 to 7.2 / 7.1 (NativeAOT-LLVM 3.4-3.5 and 2.7); churn
  0.64-0.66 to 0.65-0.69, collections 0.21-0.23 to 0.22-0.23, strings 0.03-0.06 to 0.03-0.04, bigdrop 1.28-1.34 to
  1.30-1.34 (all below NativeAOT's). bench 0.98x to 0.97x; suite 196 passed checked and as shipped; hello world
  27,297 bytes. Left: tree's rebuild frame is 7 ms against NativeAOT's 3: the build (5.3 ms against ~1) is per-entity
  arena work (each leaf's join, its parent field's up-reference, a hold flipped round the store), docs/AUDIT.md A3.5.
- 2026-11-21, stream L round 4 (branch claude/stream-l4-memory, on ec13733): self-hosted compiler 306.0 to 302.0 MB
  against NativeAOT-LLVM's 322.2 MB, time the same (homelab, interleaved, 3 rounds: main 9.9-11.3 s, L4 10.1-10.3 s,
  NativeAOT 4.0-4.4 s). The report now gives identity hashes, the header's flags, members and hashes per type, field
  padding (CS2WASM_LAYOUT_DUMP), native memory and the data by maker (CS2WASM_DATA_DUMP). Found: the header's hash
  word cannot go (1.66 million objects, 35%, have a hash made: Roslyn's SyntaxNodeCache hashes green nodes' children;
  a side table would cost what the word does); padding is 0.25 MB; the members' seven words are all in use; what is
  left over NativeAOT is the count word and the members' words. Cuts: manifest resources out of the memory (a packed
  segment, expanded for the stream that reads them and freed with it: 4.1 MB of data); reflection's read-only lists
  shared and the frames' method table expanded when first asked for (programs that reflect: 154's data 955 to 766 KB);
  a program without threads grows its memory by 16 MiB, not an eighth (a growth is 0.15 ms; the eighth's 33 MB tail
  was counted in the peak, and a megabyte more of objects cost 38 MB). Heap top 298.3 to 293.7 MB; data below the heap
  13.9 to 9.3 MB. The 20 MB asked for are not in the heap's layout (docs/AUDIT.md, round 4). Module 41.8 to 42.3 MB;
  hello world 27,297 to 27,290 bytes; bench 0.98x / micro --baseline 1.68x (cs2wasm's times at or below main's own
  run on the same loaded machine, whose NativeAOT side ran slower: 0.90x / 1.50x; nothing changed touches them).
  Suite on the homelab: checked 195/196, as shipped 194/196: 072_http's .NET run could not connect (another
  session's suite held its port) and 088's timer tick under load (the known one); both pass alone, both modes.
- 2026-11-22, main ad4c99e (K round 5, the test 127 fix, threads past four, tree spike, stream L round 4 without its growth
  change, fewer count operations, reflection on generated methods, a self-hosted build twice as fast), on the homelab, quiet,
  3 interleaved rounds: self-hosted compiler 9.27-9.79 s / 306.0 MB / 39.5 MB against NativeAOT-LLVM 3.80-3.84 s / 322.2 MB /
  129 MB: time 2.49x, memory 0.95x, size 0.31x. The self-hosted build itself: 247 s.
- 2026-11-22, frame loops (main 6e60243; the owner's restated bar above). Measured first on main 7dd75a8, this machine,
  `node tools/frames.mjs --runs 3`, medians against NativeAOT-LLVM: churn 3.47x, tree 2.56x, collections 1.36x, strings
  1.21x, bigdrop 0.87x; worst frames below NativeAOT's on every scene but tree (6.6 ms against 3.2). Then the churn loop's
  code read (`CS2WASM_DUMP`): ten count operations a particle where NativeAOT does none. Four ownership rules, each with
  its switch: a static's value held for a call borrows by the static's key, as a local does (`CS2WASM_NO_STATIC_TEMPS`);
  a write nested in an expression through a reference (`--p.Life`) marks no variable written during use
  (`CS2WASM_NESTED_THROUGH_REFS`); a local borrows the value of a getter that returns a field or an element borrowed (the
  indexer of List) from a receiver that stays put or a static (`CS2WASM_NO_GETTER_BORROWS`); an element store of the
  borrowing local's own value, in the function or in a setter that stores only its value, does not end the borrow, never
  for a slot of a cycle, and the check of a type whose static the value was read from is no store
  (`CS2WASM_NO_SELF_STORES`). Churn median 0.168 to 0.105 ms (NativeAOT 0.048: 3.47x to 2.16x), collections 1.36x to
  1.28x, the others as before; bench.mjs 0.95x; hello world 27,067 to 27,031 bytes; the self-hosted compiler (homelab, 3
  interleaved rounds) 8.81-8.88 s against main's 8.85-8.98 and NativeAOT-LLVM's 3.71-4.11, its hello world byte for byte
  the .NET-hosted compiler's (`tools/selfdet.mjs`). Test 190 (callees and later arguments that rewrite the static, another
  value stored over the slot, lists cleared, replaced and shortened, a setter that keeps the old value, cycle slots). Suite
  201/201 checked and as shipped on the homelab. Measured and ruled out for these scenes: inlining (`CS2WASM_INLINE_THROWING=40`,
  its loop form at 60 to 80, `CS2WASM_EXP_INLINE=60`: churn and tree the same or slower, before and after the count rules;
  the self-hosted compiler with `CS2WASM_INLINE_THROWING=40` 8.95-9.02 s against 8.85-8.98); the NaN test on float stores
  (SPEC 7.3) is 5% of churn (`CS2WASM_NO_CANON=1`). Left in churn: the count an element's move to another slot needs (the
  array form of the loop is 0.083 ms), and the indexer's own work. Left in tree: the rebuild (branch claude/tree-rebuild),
  and Walk's frame for the enumerator of a changed list, set up on every call (an indexed loop in its place: median 0.073
  to 0.066 ms). Seen and not followed: Dictionary<string, V>.FindValue counts `_comparer` and `_entries` because the
  comparer's virtual calls are taken to store them.
- 2026-11-22, the tree scene's rebuild (branch claude/tree-rebuild, merged as d4b331e; docs/AUDIT.md A3.5). A list's larger
  array stored over the one it was copied from takes its items in one pass (`Rt.ReplaceArray`, `CS2WASM_NO_ARRAY_REPLACE`);
  List.Add and AddWithResize take a new reference item owned (`CS2WASM_NO_OWNED_ADD`); an object plain until the store that
  links it joins without the walks up its container's chain (`Rt.JoinOwned`, `CS2WASM_NO_FRESH_JOIN`); a member's count word
  without atomic instructions in a program without threads (`CS2WASM_ATOMIC_COUNTS=1` the old way); UpdateHold only where
  HoldSettled says something changes. tools/tree-parts.cs: build 4.27 to 3.05 ms, drop 1.5 to 1.4. frames.mjs here, main
  6e60243 against d4b331e: tree worst 6.65 to 4.81 ms, p99.9 6.10 to 4.51, median 0.072 to 0.061 (NativeAOT-LLVM 3.20, 2.66,
  0.028); the other scenes as before (churn 2.15x, collections 1.27x, strings 1.21x, bigdrop 0.87x at the median; worst
  frames 0.14x to 0.49x). Reevaluate by slot bits is in but off (`CS2WASM_SLOT_WALK=1`): with it the strings scene had one
  0.6 ms frame, frame 3318 or 3349 of every run, also compiled eagerly and without tiering, gone with `--no-liftoff`; cause
  not found; the tree scene the same either way. bench.mjs 0.95x; hello world 27,031 bytes; the self-hosted compiler
  (homelab, 3 interleaved rounds) 8.60-8.87 s against 8.84-8.94 before and NativeAOT-LLVM's 3.70-3.91, its hello world byte
  for byte the .NET-hosted compiler's. Suite 201/201 checked and as shipped on the homelab; tools/stress-cycles.sh there,
  200 loaded runs each mode: 139 the same in every run; 127 differed in 1 and 3 of 200 (the rare `async 5 / pending 1`, at
  main's rate: the row above). Still above NativeAOT: per build, the leaves about 1.5 ms (75 ns each, 31 of it plain
  allocation), a node's own links 0.5 ms, list growth from 4 to 8 items 0.9 ms (the copy's retains still flip holds up the
  owner chain), the drop 1.4 ms. A faster link will not reach parity: that needs fewer group members, or members laid
  out together (7.D, 7.E).
- 2026-11-22, the tree scene's rebuild, round 2 (branch claude/tree-rebuild-2, main dca1f9c; docs/AUDIT.md A3.5). A list of
  group members grows in one step (`Rt.GrowInto` before the copy in List<T>.Capacity's setter, an edit of List.cs in the
  manifest; `Rt.ArenaGrowArray`; `CS2WASM_NO_ARRAY_GROW=1` the old way); ArenaMerge points the members of an arena of 8 or
  fewer straight at the survivor (`CS2WASM_NO_MERGE_REPOINT=1`). tools/tree-parts.cs: build 3.05 to 2.13 ms, drop 1.4 to
  1.35. frames.mjs here against NativeAOT-LLVM: tree worst 4.81 to 3.83 ms (3.15), p99.9 4.51 to 3.57 (2.46), median 0.061
  to 0.055 (0.029); the other scenes as before. The 0.45 to 0.75 ms frame at a fixed frame of churn, collections and
  strings (round 1's "cause not found", the reason given for leaving the slot walk off) is one scavenge of Node's own heap
  a run, filled by the BigInts the host's clock returns (`monotonic_ns` is an i64); inside a timed frame or not by the
  module's size; `--js-young 32` runs both sides without it (worst frames then 0.14x to 0.25x NativeAOT's on the four
  scenes, tree 3.87 against 3.23). The slot walk stays off: no gain. bench.mjs 0.95x; hello world 27,031 to 27,089 bytes;
  the self-hosted compiler (homelab, 3 interleaved rounds) 8.68-8.82 s against 8.70-8.96 before and NativeAOT-LLVM's 3.71,
  its hello world byte for byte the .NET-hosted compiler's. Suite 202/202 checked and as shipped on the homelab (test 191:
  growths of lists in groups); stress there, 200 loaded runs each mode: 139 and 191 the same in every run, 127 differed in
  1 and 2. Seen by the agent on main and not fixed: a derived class's finalizer does not run its base's; a finalizer that
  grows a non-empty list of its own dying group leaks the new objects. Left of the rebuild frame: the objects 0.5 ms, leaf
  links 0.63, nodes with children about 1.0, the drop 1.35 (75,000 members: walk 0.40, clear 0.46, free 0.53).
- 2026-11-22, the churn scene by phase (tools/churn-parts.cs, both sides, main dca1f9c; nothing changed). Mean a frame,
  19,392 particles live, cs2wasm against NativeAOT-LLVM: birth 5.4 against 7.0 us, the loop 102 against 44, the removal
  0.5 against 0.2. The loop without its move (no count, no store): 58 against 30. Of the 58 us between the loops: the
  move about 30 (each survivor stored into another slot is a retain and a release, 1.28 ns for a counted array store
  against 0.66 for NativeAOT's store with its barrier, more through the list's setter; and the 400 dead a frame are freed
  here, where NativeAOT frees in its collections); the order in memory about 12 (the same read loop over particles
  allocated in order 45.5 us: freed blocks are reused where they lie, NativeAOT's stay in order, 29.9 either way); the
  static read again on every use about 9 (the list in a local: 1.91 ns a particle against 2.36; NativeAOT 1.54 either
  way); the NaN test on float stores (SPEC 7.3) 2 to 5. What is under all of it: an array loop with no floats 0.59 ns a
  particle against 0.48 (1.22x). Tried by hand in C# and no gain, so not built: the loop rotated (its test at the bottom),
  the length in a local (0.58 to 0.59 every form). Not built: the static read once before a loop that does not store it
  (8% of churn; needs what a field borrow needs: no address taken, no reflection, threads). No one cause brings churn to
  1.0x: half of the gap is counts on moves and frees in the frame, which is what the scene has in place of the
  collector's pauses (worst frame 0.29 ms against 1.15).
- 2026-11-22, the tree scene's rebuild, round 3 (branch claude/tree-rebuild-3 from main d061dda, not merged when this
  was written; docs/AUDIT.md A3.5). The count a store took on a member is given back at the link where the release after
  the store would only take it off (`Rt.EarlyGiveBack`, `CS2WASM_NO_EARLY_GIVE_BACK=1`: 14,060 hold changes a build to
  none); a pinned object stored into a plain one returns before the lock (`CS2WASM_NO_PINNED_SKIP=1`); a group
  without finalizers is let go in two passes, the clears in the walk, with `Rt.ArenaBeforeFinalizer` before the first
  finalizer (`CS2WASM_NO_FUSED_DROP=1`); the members' blocks go back onto their pages without the dispatch on the type
  (`Rt.ArenaFreeList`, `CS2WASM_NO_FREE_LIST=1`). tools/tree-parts.cs: build 2.14 to 1.91 ms, drop 1.33 to 0.88.
  frames.mjs here (`--runs 3 --js-young 32`) against NativeAOT-LLVM: tree p99.9 3.61 to 2.97 ms (2.40), worst 3.88 to
  3.12 (3.13), median 0.056 to 0.052 (0.029): **the worst frame at NativeAOT's, p99.9 1.18 to 1.24 times it: not
  reached.** A rebuild frame is 2.70 to 3.13 ms (main 3.31 to 4.01); NativeAOT's is 0.26 ms, with a collection of 2.3
  to 2.5 ms (one of 3.1) in four of the twelve, so its p99.9 is a collection and this side's the middle rebuild, which
  has to lose 0.45 ms more. The other scenes as on main; bench.mjs 0.95x to 0.96x either way; hello world 27,089
  bytes. Homelab at aeab011: suite 203/203 checked and as shipped (test 192: groups let go whole); stress, 200 loaded
  runs a mode: 139 and 192 the same in every run, 127 differed in 1 and 2. Tried and not kept, none moved the number
  by itself: null and pinned slots left out of the generated visits, a clear by the slot bits, the waiting container
  out of the side words, the members by type, the ring walked from both ends. Left: a build 1.9 ms (the objects 0.56,
  their joins 0.8, the parent fields 0.45, the growths 0.17), a drop 0.88 (11.6 ns a member). What would reach it is
  fewer members (a leaf's list, a third of them, kept a plain object until it has an item: a change of the runtime's
  invariants, AUDIT A3.5) or step 7.D. Seen, not followed: the tree built with the parent set after the child is in
  its list takes 5.5 ms a build (tools/tree-build.cs, variant 2).
- 2026-11-22, round 3 merged (main 7451b65) and the tree scene's other frames by phase (tools/tree-frame.cs, both sides;
  nothing changed). Checked again before the merge: frames.mjs here three times, tree worst 3.20, 3.37, 3.32 ms against
  NativeAOT-LLVM's 3.59, 3.17, 3.18 and p99.9 3.00, 2.93, 2.90 against 2.69, 2.44, 2.42, the other scenes as before;
  bench.mjs 0.96x; homelab at 7451b65: suite 203/203 checked and as shipped, stress 200 loaded runs a mode: 139, 191 and
  192 the same in every run, 127 differed in 1 of 400; the self-hosted compiler 8.76-8.89 s against 8.85-8.89 before and
  NativeAOT-LLVM's 3.73-3.81, its hello world byte for byte the .NET-hosted compiler's. A frame that is not a rebuild,
  median us, cs2wasm against NativeAOT-LLVM: the new subtree's build (156 entities) 15.9 against 2.3; its store over the
  old one 9.8 against 0.1 (the old subtree, 343 members, destroyed there through its owners, 28 ns a member; in 557 of
  11,461 frames it stays with the group instead, `fail-inside` in `CS2WASM_DROP_STATS=1`); the move 0.6 against 0.1;
  the walk 27.9 against 29.3 (at NativeAOT's; walked again at once, indexed: 17.7 against 10.9). So the median's 23 us
  over NativeAOT's is the build and the destroy of the subtree, the two things the rebuild frame is made of, 102 ns an
  entity built against 15 and 63 destroyed against none in the frame; NativeAOT's mean build is 10.6 us, its
  collections in it. Not followed yet: why one replaced subtree in twenty stays (a tree that is never rebuilt keeps
  them), and the build with the parent set after the add (5.5 ms against 1.9, round 3).
- 2026-11-22, the tree scene, round 4 (branch claude/tree-rebuild-4 from main d62ff76, not merged when this was written;
  AUDIT A3.5, "The rebuild, round 4"). The frames that are not a rebuild, and three things found in them. (1) One
  replaced subtree in twenty-five stayed with its group: the move's `moved.Parent = to`, with `to` the parent of the node
  the child was taken from, was an up-reference through the old array the child still held, and the child held that
  array from its new place for good. A new up-reference asks for a chain of owned links (`CS2WASM_NO_OWNED_CHAIN=1`):
  checks at the drop 557 to 0 in tools/tree-parts.cs, the group at a rebuild 75,000 members to 67,380. (2) A subtree
  destroyed at its store cost 31 ns a member, a group let go whole 5.8: in a group without finalizers it goes through
  `Rt.ArenaDestroyPlain` (`CS2WASM_NO_FAST_DESTROY=1`), its members' references read by their slot bits
  (`CS2WASM_NO_DESTROY_BY_BITS=1`), up-references between dying members left alone (`CS2WASM_NO_DYING_UP=1`): 12.2 ns a
  member, tools/tree-store.cs (new) 10.5 to 4.2 us a store, tools/tree-frame.cs's store 9.8 to 4.0 us. (3) The parent
  set after the add (`e.Children.Add(c); c.Parent = e;`) made every node the property of its first child: 6,875 failed
  checks a build, 5.5 ms for the other order's 1.9, and a subtree of such a tree stored over another was not freed
  before its group went. A plain object that code still holds, stored into a member's own slot, now joins with its
  owner left open until its slots are linked (`CS2WASM_NO_STORE_WAITS=1`): 2.4 ms a build, the subtree destroyed at its
  store. frames.mjs here (`--runs 3 --js-young 32`, main and the branch in turn three times) against NativeAOT-LLVM:
  tree median 0.052 to 0.047 ms (0.029), p99 0.112 to 0.074 (0.049), p99.9 2.94 to 2.99 to 2.67 to 2.71 (2.38 to
  2.44), worst 3.14 to 3.27 to 2.74 to 2.87 (3.09 to 3.21): **the worst frame below NativeAOT's, p99.9 1.11 times it:
  not reached**, a rebuild frame 0.28 ms too long, its build still 1.9 ms. The other scenes as on main; bench.mjs 0.95x
  to 0.96x either way; hello world 27,089 bytes. On the homelab at b3c5175: the suite twice, 203/204 checked and 204/204 as shipped (test 143: the host's `Atomics.waitAsync(...).value.then is not a function`, engine.mjs line 228, a wait that had ended already; by itself 100 loaded runs a mode the same every time), then 204/204 both ways; `tools/stress-cycles.sh` (200 loaded runs a mode, 8 at once): 139 the same in every run, 127 the same in 200 as shipped and differing in 1 of 200 checked (`async 5` / `pending 1`, main's rate), 193 the same in every run of both modes. Not run: the self-hosted compiler. Worked out and not done: a leaf's list kept a plain object
  (what it takes, and 0.1 to 0.2 ms a rebuild by a prototype, which the nodes with children would pay back in the
  scene's order); List<T>'s constructor's store of the empty array as a fresh store (0.1 ms a build). Seen, not
  followed: an array of two dimensions of a cycle-capable class in a member's field keeps its group for good; a
  subtree replaced while code holds a node of it costs 33 ns a member at the store (the check that fails).
- 2026-11-23, round 4 merged (main 6e854e4), checked again before the merge: frames.mjs here three times, tree median
  0.047 to 0.048 ms against NativeAOT-LLVM's 0.028 to 0.029, p99 0.071 to 0.075 against 0.045 to 0.049, p99.9 2.67 to
  2.72 against 2.42 to 2.45, worst 2.88, 3.00, 3.37 against 3.15 to 3.21; the other scenes as before; bench.mjs 0.95x.
  Homelab at 6e854e4: suite 204/204 checked and as shipped; the self-hosted compiler 8.67-8.70 s against 8.75-8.84
  before and NativeAOT-LLVM's 3.69-3.90, its hello world byte for byte the .NET-hosted compiler's; stress, 200 loaded
  runs a mode: 192 and 193 the same in every run, 127 differed in 4 of 400. Test 139 as shipped: one run of the first
  200 ended in a trap (exit 1 with a wasm stack; the tally cut the trace off), then none in 3,600 more as shipped and
  none in 3,800 checked; main 7451b65 none in 800 as shipped. Merged with it open (the row above): at one in some
  thousands the runs so far do not tell the branch from main. Next for it: the stress tool keeping a failing run's
  whole output, a module built with names.
- 2026-11-23, the churn loop (branch claude/churn-1 from main 77b2aad, not merged when this was written; docs/AUDIT.md
  A3.5, "The churn loop"). Measured first, both sides, by part (`tools/churn-move.cs`, `tools/churn-order.cs`, new):
  of the 57.7 us a frame between the scene's loop and NativeAOT-LLVM's (102.7 against 45.0), the order in memory 26
  (the same loop over particles in order: 76.6), the store with its counts 30.6 against 11.6, the static read again
  on every use 9 to 13, the NaN test 2 to 5. Two changes, each with its switch. **A static's reference is read once
  before a loop that nothing in it can store to** (`CS2WASM_NO_STATIC_HOIST=1`): decided with the borrows, from all of
  a function's stores and its callees' (those that only replace a null too), no address taken, no reflection, no
  threads, the type's initialization not in the loop. **Which page of a size is used again**
  (`CS2WASM_NO_RIPE_PAGES=1`, programs without threads): one freed in one go and still in the cache, else one that
  is nearly empty or no longer being emptied, else an empty one, cut in order, while memory need not grow; a page
  still being emptied is left to empty (every frame's new particles had gone nine to a page into all 44 pages).
  frames.mjs, quiet, twice in turn against the switches set: churn median 0.104-0.105 to 0.087 ms (NativeAOT-LLVM
  0.049: 2.14x to 1.78x; the static read alone 0.093, the pages alone 0.098), p99.9 0.17-0.18 to 0.13-0.17; tree
  0.047-0.048 / 2.71-2.86 to 0.048-0.049 / 2.68-2.73; collections, strings and bigdrop within their spread
  (collections' worst frame 0.19 to 0.20 ms both times). bench.mjs 0.94x to 0.95x against 0.95x. Memory: the heap's
  top at the end of churn 2,887 to 3,271 KB (the particles' pages 44 to 68), of tree 9,561 to 9,881 KB, the others
  the same; the memory itself 16 MB in all as before. Hello world 27,089 to 27,426 bytes. Tests 195 (the static read:
  each condition's loop, each condition taken out changes its output) and 196 (the pages: every object read back
  while they die one by one, in runs, pinned, with the memory nearly full). Homelab: the suite at c926ac2 (before the
  look at the back) 206/206 checked and as shipped; at 69e6f4d, on a loaded homelab (903 s for 272), 205/206 checked
  (test 088) and 204/206 as shipped (088 and 135, both tests with timers); again alone: 135 passed six of six, 088
  failed two of twelve with the same line (its first timer tick), and one of eight with both switches set: the
  failure is there without the changes. Stress, 200 loaded runs a mode: 139 the same in every run both times; 127
  differed in 2 of 400 at c926ac2 and 6 of 400 at 69e6f4d (the loaded run; main: about 1 in 100). The self-hosted
  compiler without threads on Console.WriteLine(42) (`tools/selfhost-switch.sh`, new: built as it is and with both
  switches set, eight rounds, the two taking turns to go first; the one that goes second is the slower, whichever):
  going first 10.59 to 10.93 s as it is against 10.45 to 10.77 with the switches, lowest 10.59 against 10.45; peak
  memory 489.9 MB both. That the two wrote the same module was not checked (the tool's comparison did not find the
  files), nor was `tools/selfdet.mjs` run.
  Read and not built: the move itself (the engine inlines the setter and its three count functions: no call is paid;
  what is left of it is the two counts, 12 us a frame, which is what counting costs here; a test for the old value
  being the new one would pay in no frame of the scene; a dead particle's free is 6.6 ns, 2.6 us a frame). Tried and
  not kept: pages used again only when empty (the loop 76.3 for 80.3, the particles' pages 1.6 times); an eighth
  in use or half free and passed over four times (the tree scene's heap 9.6 to 13.3 MB). **Open: with other
  programs at work on the machine the tree scene was a tenth slower with the pages' change as first written (ten
  runs in turn); with the page freed in one go taken first its median is as before (six runs), its p99.9 4.61 against
  4.37 in the one sample taken: the rebuild frame under load is not settled.** What is left of churn's loop, 81.4 us
  against 45.0: the order in memory 17 (more memory buys it, nothing else without moving objects), the two counts
  of a move 12 and the frees 2.6 (what counting costs), the setter's other checks 1, the NaN test 2 to 5, the
  array loop under it 3 to 4.
- 2026-11-23, test 139's trap and the tree scene, round 5 (branch claude/tree-rebuild-5 from main 77b2aad, not merged
  when this was written; AUDIT A3.5, "The rebuild, round 5, and test 139's trap"). (1) The trap is not the cycle
  runtime's. Its whole trace was still on the homelab (stress.mjs kept a failed run's stderr beside the module; it
  printed six lines): `memory access out of bounds` in Rt.LoadRetainSlow, at the atomic load of the count of an
  object whose reference it had just read from a place the threads share. V8 (Node 24.19) keeps, in each thread's
  instance, the size the shared memory had when the thread last passed a stack check, and checks atomic accesses of
  its baseline tier, and copies and fills, against that number: tools/grow-race.mjs (new) traps on a word that is
  there in 20 trials of 20, with a plain load in none. Test 139 grows its memory once a run while its threads start.
  In a program with threads every atomic access, copy and fill now first asks `(address >>> 16) >= memory.size` and
  calls `memory.grow(0)` if so (Wasm/MemSync.cs; no cost seen in test 139 run ten times as long, 6 KB of its 454;
  the same test in the tool: no trap). Shown on the module: with `node --no-wasm-stack-checks` main's test 139 traps
  in 7 runs of 60 at the trace's own two frames, this branch's in none of 300; test 197 (new) so run fails in 60 of
  60 without the pass and in none with it, and the suite's runner runs it that way (`// node-flags:`). Not shown: a
  run with the stack checks made to trap again (1 in 3,800 on main; none in 36,000 of this branch before the fix,
  none in 34,000 after). The host's own failure at the entry's return (`Atomics.waitAsync(...).value.then` of an
  answer that is no promise: exit 1, 4 loaded runs in 20,000 of test 197) is fixed too. Two statics two threads wrote at once, found by reading, are each thread's own now
  (round 4's s_waitSlot and the check at the drop's s_trExt); neither was shown to fail a run. tools/stress.mjs
  keeps every differing run whole under tests/out/stress-fail and prints it; stress-cycles.sh builds with names.
  (2) The tree's build. `_items = s_emptyArray` in List<T>'s constructor is a fresh store: asked of the constructor
  that runs and those it hands over to, not of all the class's (`CS2WASM_NO_CTOR_FRESH=1`); Rt.ArenaLink 42,967 to
  23,436 a build, tools/tree-parts.cs build 2.01-2.04 to 1.92-1.98 ms, the objects alone 0.57 to 0.45. On the way
  a leak, on main too: a constructor's lambda that names a field of `this` (`_next = () => _count++;`) was taken to
  keep `this` in, its store a plain one, and the object was never freed; fixed. The parent set after the add
  (`e.Children.Add(c); c.Parent = e;`): where the store is the local's last use the link gives the local's count back
  before the child would hold its owners for it (`CS2WASM_NO_LAST_CONTAINER=1`): 117,180 hold changes a build to
  none, 2.33 ms to 1.91 (the scene's order 1.81); the scene sets the parent first and has nothing of it. A leaf's list left a plain object: not done, as round 4 found. frames.mjs here (`--runs 3 --js-young 32`, main
  and the branch in turn three times): tree median 0.047-0.048 ms on both (NativeAOT-LLVM 0.028-0.029), p99.9
  2.667-2.690 on main and 2.653-2.689 on the branch against NativeAOT-LLVM's 2.49-2.75 in the same invocations,
  worst 2.80-3.05 against 3.18-3.63: **p99.9 is not told apart from main's (the rebuild's 0.07 ms is inside the
  spread), and is above NativeAOT's in three invocations of six: not reached; the median 1.65x: not reached.** The
  other scenes as on main; bench.mjs 0.95x and 0.97x (main 0.97x); hello world 27,089 bytes. On the homelab at
  e8ba580: suite 206/206 checked and as shipped (tests 194: constructors and last stores; 197: threads that use
  grown memory); stress, 200 loaded runs a mode: 139, 193, 194 and 197 the same in every run, 127 differed in 4 of
  400; test 139 as shipped 30,000 loaded runs and checked 4,000, the same in every one; without the engine's stack
  checks 7 traps in 1,000 without the pass, none in 4,000 with it (one of them printed `16 3`, two nodes left: not
  followed). Not run: the self-hosted compiler.
- 2026-11-23, round 5 merged (main 80ae1ce: claude/churn-1 and claude/tree-rebuild-5 together, the two entries
  above; the threads' test of a grown memory is 197 there), checked again before the merge. frames.mjs here three
  times (`--runs 3 --js-young 32`): churn median 0.086 ms against NativeAOT-LLVM's 0.049 (1.76x to 1.77x; with
  CS2WASM_NO_STATIC_HOIST=1 CS2WASM_NO_RIPE_PAGES=1 on churn-1 0.105 to 0.106), tree 0.047 to 0.048 against 0.028 to
  0.029, p99.9 2.66 to 2.68 against 2.42 to 2.48 (not reached), worst 2.77 to 2.84 against 3.11 to 3.22; collections
  0.007 to 0.008 against 0.006, strings 0.009 against 0.007, bigdrop 0.003 both; memory 16 MB against 59 to 71;
  bench.mjs 0.94x. Homelab at 80ae1ce: suite 208/208 checked and as shipped (274 s); the self-hosted compiler's
  hello world byte for byte the .NET-hosted compiler's, 8.60 to 8.73 s against 8.68 to 8.76 before the round and
  NativeAOT-LLVM's 3.72 to 3.84 (peak memory 306 MB, 344 in two of its three runs and in one of churn-1's: one
  more step of growth, not followed); stress, 200 loaded runs a mode: 139, 192 to 197 the same in every run, 127
  differed in 5 of 400. At f3fe0b4 (churn-1 alone) the suite was 205/206 checked (088, its timer line; 206/206 as
  shipped), which the combined run did not show again.
- 2026-11-22, the collections and strings scenes (branch claude/colstr-1 from main 1f7a807, not merged when this
  was written; AUDIT A3.5, "The collections and strings scenes"). Measured by phase on both sides first
  (tools/colstr-parts.cs with tools/parts.mjs and tools/parts-naot-wasm, new). (1) Collections: with ContainsKey
  for TryGetValue the frame was NativeAOT-LLVM's, so the gap was in what TryGetValue adds: its 28 instructions
  were one shared body for `Dictionary<int, Particle>` and `Dictionary<string, CultureInfo>`, calling each one's
  FindValue through the table, and V8 inlined nothing across it. A family's body under 32 instructions that a loop
  calls is not shared where its members call different functions (`CS2WASM_NO_DIRECT_SMALL=1`): the frame 7.62 to
  6.76 us against 6.10; parser bench +0.29% of its module. (Every short body, the first form, made the tree's build
  read 4% slower on the desk: a list's Grow, direct, let V8 inline the Capacity setter into the build. With the
  loop test tree-parts' module is byte for byte the old one.) (2) Strings: an interpolated line of eight parts was
  three strings, Concat in fours; five parts and more are one call and one string (`CS2WASM_NO_CONCAT_ONCE=1`),
  which also ends a difference from .NET (`a + "" + "" + "" + ""` was `a` itself). The strings of 0 to 299 are kept
  as CoreCLR and NativeAOT keep them (the library is compiled as Mono's, which keeps ten:
  `CS2WASM_NO_SMALL_NUMBERS=1`), another difference from .NET that `ReferenceEquals` shows. The frame 9.30 to 7.94
  us against 7.66. frames.mjs here (`--runs 3 --js-young 32`, three pairs in turn, before by the three switches):
  **collections median 1.24x to 1.25x NativeAOT-LLVM's to 1.09x to 1.15x (7.0 to 7.1 us against 6.2 to 6.4): not
  reached; strings 1.24x to 1.25x to 1.01x to 1.03x (7.9 to 8.0 against 7.7 to 7.9); collections' p99 0.036 to
  0.038 ms against 0.026 to 0.027, unchanged: not reached.** Both scenes' p99.9 and worst as before (collections
  0.19 and 0.21 to 0.27 ms against 1.10 to 1.17 and 1.34 to 1.47; strings 0.021 to 0.023 and 0.048 to 0.054 against
  0.075 to 0.091 and 0.27 to 0.33); churn, tree and bigdrop as on main in the same runs (tree p99.9 2.77 to 2.96 ms
  against 2.59 to 2.65); memory 16.0 MB against 59.3; bench.mjs 0.94x to 0.95x before, 0.93x after; hello world
  27,426 to 27,456 bytes. What is left of collections, 0.67 us, measured and not built: the `out` value of a lookup
  counted in and out of the caller's slot 0.33; the sets 0.21 (TryInsert's count on its `entries` local and the new
  object's count pair 0.11, the objects' allocation against a bump 0.10); removes and queue 0.12. Its p99 is the
  dictionary's resize: `Array.Copy` counts every value in and the old array's end counts them off, 6 us of a 29.5 us
  frame (22.3 on NativeAOT's side). Left of strings: StringBuilder is its own cycle type (`m_ChunkPrevious`), 52% of
  40 appends is the arena's (1.42 us against 0.43); the proof that the chain cannot close is not built. Tests 210
  to 212. On the homelab at bc2ba98 (the last commit that changes code): suite 211/211 checked and 211/211 as shipped (461 s); at ebeaa42, the first form, 209/211 checked and 210/211 as shipped (088's timer line in both; 087 checked the trap named in FINISH.md's entry, which main has too). Not run: the self-hosted compiler, stress-cycles. Found on the way, on main too: test 087 checked ends in a trap in 7 of
  1,200 loaded runs on the homelab at main 1f7a807 (8 of 600 and 2 of 600 on the branch): `operation does not
  support unaligned accesses` in rt.retain, from `Interlocked.Exchange(object, object)` in
  `Task.FinishContinuations`, under `Task<bool>.TrySetResult` in the MoveNext of an async iterator on a pool thread;
  not followed.
- 2026-11-23, claude/colstr-1 merged (the entry above), checked again before the merge: frames.mjs here once
  (`--runs 3 --js-young 32`, the machine shared with three sessions): collections median 0.007 ms against
  NativeAOT-LLVM's 0.006 (1.10x), p99 0.034 against 0.025; strings 0.008 against 0.008 (1.01x); churn 0.088 against
  0.050, tree 0.048 against 0.029 and p99.9 2.73 against 2.42, bigdrop 0.003 both. Homelab at 576dbd4: the suite
  211/211 both ways at bc2ba98 (that session's run); stress, 200 loaded runs a mode: 210, 211, 212 the same in every
  run; the self-hosted compiler's hello world byte for byte the .NET-hosted compiler's (27,398 bytes), its time not
  told from before on the loaded homelab (10.6 to 14.7 s before, 12.1 to 19.2 after, NativeAOT-LLVM's 4.5 to 6.7:
  to be timed again when it is quiet). Seen by that session, on main too: test 087 checked traps in 7 of 1,200
  loaded runs (an unaligned atomic access in rt.retain under Interlocked.Exchange in Task.FinishContinuations);
  given to the session on the tests that differ under load.
- 2026-11-22, the churn loop, round 2 (branch claude/churn-2 from main 1f7a807, not merged when this was written;
  AUDIT A3.5, "The churn loop, round 2"). Measured first, and both leads' figures were wrong. (1) The order in memory
  is 2 to 3 us of the loop, not 17 (tools/churn-layout.cs, new: the particles' addresses in the list's order, and the
  loop over the settled layout): what the last round took for it was the decrement's store (5), the dead and the moves
  (3.5), and 7 that are there only while each frame's new particles land between old ones. They did because the page
  policy's test was fooled: "no longer being emptied" was told between two looks, and a program that allocates a
  frame's objects and then frees has no free between its looks; the nearly empty page was then taken on sight and
  handed its blocks out in the order of death; and a page passed over was found again by the look at the back. Built
  (`CS2WASM_NO_HOLD_PAGES=1`): no longer emptied means no block lost while the size needed four pages; the nearly
  empty page waits for that; the look at the back stops at a page passed over; bounded by the pages with freed blocks
  being three quarters of the heap handed out (past it the old rules) and by no memory grown. Steps of the list's walk
  off the next line 16,246 to 4,270 of 19,000; the particles' pages 68 to 67 (the last round's "1.6 times the pages"
  was against the policy before it: its own policy held as many); heap tops as before but churn's (3,274 to 3,338 KB),
  memory 16 MB in all five scenes. (2) The counted move: written without its counts by hand (tools/churn-raw.cs, new)
  a proved move saves 4 us a frame, not 12 (the store is its instructions: the slot read, two slots written, the
  tests; every simple thing in this loop is a cycle a particle, 5 us a frame); the proof that saves more frees the
  dead where they are found, and is not sound: a particle whose Tag is null makes .NET throw in the middle with the
  dead still readable in the list's slots. Not built. (3) Built (`CS2WASM_NO_SEALED_STORE=1`): the test of the array's
  type at a store was kept in generic code for every reference type; a sealed class's array needs none there either
  (List<Particle>'s setter): 3 us. frames.mjs here (`--runs 3 --js-young 32`, main's copy and the branch in turn
  twice, other sessions at work on the machine): churn median 0.088-0.089 ms to **0.081-0.082** against
  NativeAOT-LLVM's 0.049-0.052 in the same invocations (**1.78x-1.80x to 1.58x-1.59x**, twice more later 1.76x-1.78x
  to 1.60x-1.61x: **not reached**), p99.9 0.150-0.158 and 0.143-0.406 against 1.13-1.22, worst 0.26-0.33 and 0.26-2.61
  (the 2.61 once, the machine's) against 1.18-1.73; tree 0.048-0.050 and 0.051-0.057 against 0.029 and 0.030-0.033
  (1.66x-1.72x and 1.70x-1.73x: **not reached**), p99.9 2.76-2.91 and 3.14-3.71 against 2.43-2.51 and 2.58-3.28
  (1.13x-1.16x and 1.13x-1.22x: **not reached**; the machine was busier in the branch's invocations); collections
  0.008 against 0.006, strings 0.009-0.010 against 0.008, bigdrop 0.003 against 0.003-0.004; memory 16 MB against 59
  to 71; bench.mjs 0.94x-0.95x and 0.95x; hello world 27,482 to 27,612 bytes. The tree scene with other programs at
  work: not told apart from main's, on this machine with the other sessions' builds and timings going on (the load the
  last round's question was about). The modules alone, main's and the branch's in turn: ten runs under that load,
  medians 0.051-0.062 ms on main and 0.052-0.066 on the branch (the branch's at or above main's in nine of the ten
  pairs, by 0 to 0.004 and once 0.010), lowest p99.9 3.22 and 3.11; eight runs four ways (main, the pages' change
  alone, the store's alone, both), medians 0.051-0.060, 0.049-0.062, 0.051-0.063 and 0.051-0.087, the first three
  rounds of it, before the load rose, 0.051-0.052, 0.049-0.052, 0.051 and 0.051-0.052; eight runs with the machine
  quieter, medians 0.047-0.051 and 0.048-0.050, p99.9 2.73-3.08 and 2.65-2.94 (the branch's median the lower or the
  same in six pairs of eight, its p99.9 the lower in six). So: the same when quiet; under load the branch's median was
  up to 4% above main's in one set of ten and not in the other sets, inside what the load itself moves a run by (10 to
  60%). A load made for the purpose (two and six processes walking 64 MB each) moved every run by more than any
  difference (medians 0.06 to 0.7 ms): no use. The homelab has no Emscripten and its frames.mjs does not run: not
  measured there. Not settled further than that. Homelab: at 8a7895e (both changes) the suite 210/210 checked and as
  shipped (284 s); at 840d4be (the code's last commit: the switch's layout) 210/210 checked and 209/210 as shipped,
  the one test 088 at its timer line (line 7, the second of its five answers False), which run alone there passed both
  ways. Stress at 840d4be, 200 loaded runs a mode: 205, 206, 196 and 139 the same in every run of both modes; 127
  differed in 4 of 400 (its "async 5" line: 1 as shipped, 3 checked; main's 5 of 400 in the last round), the other 200
  of that test run before it all the same. Not run: the self-hosted compiler, tools/selfdet.mjs. At f54c2b1 (the code
  as at 840d4be, with this text; the homelab busy with other sessions' runs, 713 s for the 284 before): 209/210
  checked (135, its WhenAny between a 1 ms and a 50 ms timer: 60950 for 61050) and 209/210 as shipped (088's timer
  line again); each of the two, run alone there, passed both ways. What is left of churn's loop, about 73 us against
  45: the store through the list 14 above a bare store (its two counts 4 of them), the NaN tests 2 to 5, the order 2,
  the rest the loop's own instructions, which the engine does not thin (it carries no load across a store) and
  NativeAOT-LLVM's compiler does. Not started: the compiler's own inlining of the list's indexers with their loads
  shared.
- 2026-11-22, the churn loop, round 3 (branch claude/churn-3 from claude/round-6a 19a2035; AUDIT A3.5, "The churn
  loop, round 3"; a tool and text, no change to the compiler). The round was to build what round 2 ended on (the
  list's loads shared across the store by field identity; the header's hash word; the NaN tests), each measured first.
  Round 2's closing sentence was wrong: NativeAOT-LLVM does not share the list's loads either. tools/churn-array.cs
  (new: the scene through the list and by hand over an array and a count), both sides in turn: NativeAOT-LLVM
  53.5-55.8 us a frame through the list and 41.5-44.1 over the array, cs2wasm 85.8-89.2 and 77.0-81.0 (80-82 and
  71.6-73.1 quiet): the list costs NativeAOT 12 us and cs2wasm 8 to 9, and with every list load and check gone cs2wasm
  is at 1.85x, not nearer. The compiler's own inlining of the indexers into the loop (CS2WASM_INLINE_THROWING=1
  CS2WASM_INLINE_THROWING_LOOP=80, with and without the counts' fast paths): 0.078-0.081 ms either way, the engine
  does it already. Where the gap is (tools/churn-raw.cs on both sides): the loop without a store 32 against 24, with
  the store as each side makes it 51 against 31: cs2wasm's counting is 14 us a frame on top of a bare store of the
  reference (0.72 ns a move), the collector's card mark 3.5. The hash word: a particle made 40 and 44 bytes costs 1.5
  to 2 us for each four, so 32 would give 2 of 80; not changed. The NaN tests: 2 to 2.5 us (built without them), left,
  since bits leave memory where no test can be put (pointer and ref casts, spans of bytes, overlapping fields, structs
  by their bytes, the engine's copies of an instance's memory). All three at their most: 80 to 67 us, 1.35x; the pass
  by field identity perhaps 4 of it. **Not built; churn's median stays 1.58x to 1.61x NativeAOT-LLVM's: not reached,
  and not reachable by these three.** The floor measured: 14 us of counting at the store against 3.5. Test 135 under
  the stress tool on the homelab: 60950 for 61050 in 3 of 400 runs on claude/churn-2 and 1 of 400 on main (its timer
  race, on both). Not run this round: the suite, the self-hosted compiler (the compiler is as claude/round-6a's).
- 2026-11-23, claude/churn-2 and claude/churn-3 merged (main 01949ae; the two entries above), checked again before
  the merge. Homelab at 19a2035 (main with churn-2): suite 213/213 checked and as shipped; stress, 200 loaded runs a
  mode: 139, 196, 205, 206 the same in every run, 127 differed in 2 of 400; the self-hosted compiler's hello world
  byte for byte the .NET-hosted compiler's (27,584 bytes), 9.6 to 10.2 s against 9.4 to 13.2 before on a loaded
  homelab (NativeAOT-LLVM's 4.0 to 4.3). frames.mjs here once (`--runs 3 --js-young 32`, the machine shared): churn
  0.085 ms against NativeAOT-LLVM's 0.053 (1.60x), tree 0.053 against 0.032 and p99.9 3.25 against 2.94,
  collections 0.007 both (1.06x), strings 0.008 both, bigdrop 0.003 both.
- 2026-11-22 to 23, the tree's build, round 6 (branch claude/tree-build-6, from main 1f7a807; AUDIT A3.5, "The
  build, round 6"; not merged when this was written). Measured first: reference counting has no floor above
  NativeAOT's in this scene (19,531 entities with their lists made and kept as plain objects 0.19 ms, let go 0.19;
  NativeAOT-LLVM's build 0.26); the other 1.3 ms of a build's 1.75 is the group's bookkeeping, spread over sixty
  functions none above 5%: a leaf's two joins 26 ns and its parent field 14, a node with its first leaf 256 ns in
  the scene's order (its list and array a group of their own, merged; the node joined from below through the leaf's
  parent field, waiting for an owner; its first child-node its parent's owner until the add turns that round). The
  cause is how an object becomes a member: made plain, then every slot gone through again as a general store.
  Done: `Rt.JoinFresh` (`CS2WASM_NO_FUSED_JOIN=1`): a new object stored into a member's slot whose slots hold only
  what fixes the outcome joins with them linked in one step (a leaf with its list and its parent; a node's first
  leaf, the node a plain object that holds the top of the container's chain), anything else refused before a
  write. tools/tree-build.cs, off and on in turn: build 2.04 to 1.74 ms (the machine loaded; 1.77 to 1.62 quiet);
  tools/tree-frame.cs an ordinary frame's build 17.0-18.3 to 15.4-16.6 us, store and walk as they were.
  frames.mjs here (`--runs 3 --js-young 32`, off and on in turn three times, the desk loaded): tree median
  0.049-0.052 and 0.048-0.052 against NativeAOT-LLVM's 0.029-0.031; p99.9 2.70, 2.78, 3.04 off and 2.55, 2.85,
  2.87 on against 2.49-2.74: not told apart there. On the homelab, this side alone (frames.mjs --no-aot, new), off
  and on in turn three times: p99.9 3.28-3.29 to 3.01-3.02 ms, median 0.060-0.061 to 0.058-0.059. **The rebuild
  frame is 0.27 ms shorter (8%), the median 4%: by that ratio the desk's 2.67 ms is 2.45, at NativeAOT-LLVM's 2.42
  to 2.48 and not below it; the median stays 1.6x. Not reached.** The other scenes as on main; bench.mjs 0.94x
  off and 0.95x on, this side's own times the same; hello world 27,426 bytes as on main. What is left is the
  nodes (each one's list and first array a group of their own, its own parent field, its place in its parent's
  list: the general way still, 3,906 a build), and it is not one more path: a node made parent first would have
  to join as its parent's at that store, or the parent field be proven a reference back (7.E); both change when
  something is freed: the owner's call. Found on the way, on main too, not followed: **a program that assigns a
  node's list of children anywhere keeps every subtree stored over until the whole tree goes**
  (tools/tree-assign.cs, new: `121 161 201` for .NET's `121 121 121`; the list is then counted across
  `e.Kids.Add(..)`, holds its owner, and the holds are not found at the drop; the scene has no such line); test
  131 prints `0 5 5` in a run of some thousands under load, with the switch on and off. On the homelab at
  a440778: suite 209/209 checked and as shipped (test 198 new; 088's timer line failed once as shipped at c3b22e0
  beside another session's suite and passes alone); stress, 200 loaded runs a mode: 198, 192 to 197 and 139 the
  same in every run, 127 differed in 5 of 400. Test 198 fails with each of six rules of the step broken by hand.
  Not run: the self-hosted compiler.
- 2026-11-22, the churn loop, round 4 (branch claude/churn-4 from claude/churn-3; AUDIT A3.5, "The churn loop, round
  4"). The read loop (tools/churn-raw.cs, 32 us a frame against NativeAOT-LLVM's 24) taken apart against
  NativeAOT-LLVM's own wasm for it: LLVM's turn has no test of the array, no read of its length, no NaN test, one
  branch (the loop rotated), Vy read once, a pointer stepped where cs2wasm has the index, a particle of 28 bytes; each
  difference written by hand in the tool: the NaN tests 4.2 us of this loop (2 to 2.5 of the frame), Vy read once 3.0,
  the loop rotated with the length read once 1.7, the last two 1 together once the NaN tests are out. Built
  (`CS2WASM_NO_SAME_BASE_LOADS=1`, Wasm/SameBaseLoads.cs): in straight-line code a load repeated from the same local
  plus the same constant, the local not assigned since and every store between through that very local to bytes that
  do not overlap, is the first one's value (no knowledge of fields needed; anything else that writes, any call, any
  branch ends it): the tool's loop 32.9-33.2 to 31.0-32.1; **the churn scene's frame the same (0.083-0.085 ms with and
  without), bench.mjs the same (0.93x-0.94x, 0.94x): certain and small, not a step toward the target**; hello world
  27,612 to 27,642 bytes; test 207, each of the pass's five rules taken out changes a line of it. The NaN tests cannot
  go: WebAssembly gives an arithmetic NaN of canonical operands either sign, V8 on x64 makes the one cs2wasm stores
  and arm64's default has the other sign, so skipping the test would make memory's bits the machine's: left, a floor
  of SPEC 7.3 as written. Not built: the loop's shape (the length before the loop is the field-identity pass's,
  another branch's); the header's hash word (it also holds three flags of any object and the share check's thread, the
  count and type words have no room for them, a table by address would put a test on every free, and an array's and a
  string's length are read at the literal offset 12 in ninety places: for 2 us of churn's 80 by round 3's slope;
  tree's part not measured, the machine too busy to time it). **Churn's median as before, 1.58x to 1.61x
  NativeAOT-LLVM's: not reached.** Homelab: at b6ae0e4 (the code's commit) the suite 214/214 checked and as shipped
  (464 s); test 207 under the stress tool, 200 loaded runs a mode, the same in every run; the self-hosted compiler
  built with the pass and without it (tools/selfhost-switch.sh CS2WASM_NO_SAME_BASE_LOADS=1: 31,816,209 and 31,817,447
  bytes), each compiling Console.WriteLine(42) three times in turn: 10.3 to 10.7 s with, 10.2 to 11.2 s without, exit
  0 and the same peak memory (the script's own comparison of the two modules found no file by the name it looks for:
  not shown there); tools/selfdet.mjs with the toolchain built from this commit (tools/proof5.mjs toolchain,
  32,334,486 bytes, no threads, one core): test 207 compiled by the self-hosted compiler is the .NET-hosted compiler's
  module byte for byte (210,405 bytes). Not run: selfdet on more than that one program or with threads; frames.mjs
  against NativeAOT-LLVM for this round (the machine).
- 2026-11-23, claude/tree-build-6 and claude/churn-4 merged (main ae6e889; the entries above), checked again before
  the merge. Homelab at 8022fbc (main with tree-build-6): suite 213/214 checked (088, its timer line) and 212/214 as
  shipped (088, and 072 whose .NET run could not reach its own port while another suite ran beside it); 088 alone
  three times and 072 alone: passed both ways. Stress, 200 loaded runs a mode: 139, 192, 193, 194, 198 the same in
  every run, 127 differed in 7 of 400. The self-hosted compiler's hello world byte for byte the .NET-hosted
  compiler's (27,584 bytes), 9.4 to 11.2 s against 9.5 to 12.4 before on a loaded homelab. churn-4 (one pass,
  Wasm/SameBaseLoads.cs) by its session's suite, 214/214 both ways; merged here, tests 198 to 207 and 145 passed
  both ways on the merge. frames.mjs here once, the machine loaded by four sessions: churn 0.083 ms against
  NativeAOT-LLVM's 0.052 (1.59x), tree 0.051 against 0.031 (1.66x), p99.9 3.06 against 2.83 (1.08x), collections
  0.007 against 0.006, strings 0.008 against 0.010, bigdrop 0.003 both: to be taken again when the machine is quiet.

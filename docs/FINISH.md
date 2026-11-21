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

## The checks

Each requirement is done when its check passes. A check that does not exist yet is part of the work.

| # | Check | Now | Goal |
|---|---|---|---|
| 1 | Suite, checked and as shipped (`node tools/suite.mjs`) | 180/180 | all pass, no `known-gap` |
| 1 | Behaviour differences on record (AUDIT open items) | GetMethod, TargetSite, GetCurrentMethod, FirstChanceException and 6 trace differences closed (stream E's tests); open: reflection on compiler-made methods (lambdas, state machines); .NET's own library frames (not reproducible from source) | none |
| 2 | Leaks: test 139 under load, 127, 131 | 139: none in 400 loaded runs, checked and as shipped (was 2 nodes in ~1 run of 45; fixed 2026-11-20) | none in 200 loaded runs |
| 3 | Closed-form solver (A4.6), its four examples against running the ticks | pass (API check "plugin v6", version 6) | pass |
| 3 | Invisible API slice (9.4), `samples/invisible-api` (owner, 2026-11-20: a sample here, not in Bazoodle) | pass (suite "invisible-api slice", both modes; interface version 7) | builds and gates one interaction |
| 4 | `tools/bench.mjs`, optimizing tier: time, memory | 0.96x, 16 MB vs 71 MB | < 1.0x, less memory |
| 4 | Frame-time spikes: worst frame of a long run against NativeAOT's (`node tools/frames.mjs`) | 4 of 5 scenes below; tree 29.9 ms vs 3.8 ms, its rebuild (AUDIT A3.5; streams I, J) | worst frame and p99.9 not above NativeAOT's on every scene |
| 4 | Hello world size (test 145) | 27,868 B (main 26150c1 with K round 2 and L round 3; 28,287 with L round 2, 27,869 before it) | < 28,383 B |
| 4 | Self-hosted compiler on `Console.WriteLine(42)`, no threads: time | 10.96 s vs 5.44 s, 2.0x (main 480a101 with N and K round 3; interleaved, 3 rounds; 2.7x on 2026-11-20 morning) | < NativeAOT-LLVM |
| 4 | Same: peak memory | 306.0 MB vs 322.2 MB, 0.95x (main 480a101, same run). Met, thinly: the heap top is 1 MiB under the next growth step | < NativeAOT-LLVM's, same measure |
| 4 | Same: module size | 44.0 MB vs 129 MB (main 480a101, shipped; K round 3 took back round 2's inlining) | met; next waste: code (36.2 MB) |
| 5 | Threaded self-hosted compiler: more threads not slower | 18.7 s on 1, 17.0 on 4, 26.6 on 12, 26.7 on 24 (main before: 16.3, 23.6, 37.7, 39.4); Roslyn's one group of 19,847 types is one lock | faster with threads |
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
.NET's after a full collection (4.6 million, type by type). Below NativeAOT-LLVM's 322 MB now (306 MB), by a thin
margin: the heap's top (290.8 MiB) is 1.0 MiB under the step at which the memory grows by an eighth, so a megabyte more
of live objects is 344 MB again. Per object, what is left against NativeAOT: the 12-byte header (its is 8: 4.6 million
times 4 bytes, about 18 MB), 32 bytes before each of 0.54 million arena members (17 MB; side words of 8 bytes for
half of them), 13 MB of data below the heap.

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
| E. Behaviour gaps | `StackFrame.GetMethod()`, `FirstChanceException`, the sweep's trace differences | CoreLib Boundary, stack trace code | done 2026-11-20 but for compiler-made methods' records (a feature of its own) |
| F. Plugins | A4.6 closed-form solver with its tests | Compiler/Solver.cs, PluginSolver.cs, tests | done 2026-11-20 (version 6) |
| G. Spikes | frame-time check against NativeAOT | tools/frames.mjs, tools/frames, tools/frames-naot-wasm | done: check, foreach borrows; tree scene's rebuild left to I and J |
| H. Flakes | test 139 leak under load, 072_http | Runtime.cs (read first), tests | done: both found and fixed (tests 149, 150) |

**Second fan-out (2026-11-20), for requirement 4's self-hosted compiler** (each in its own session and worktree):

| Stream | Items | Files | State |
|---|---|---|---|
| I. Value sources everywhere | record sources precisely at every store, argument and return (boxed generics, conversions, interface results, conditionals), also without threads; then `Sharing.SiteTypeIds` on by default where it narrows | Compiler/Expressions.cs, Calls.cs, Sharing.cs | done 2026-11-21: on by default, sound (test 171), but the self-hosted compiler's group only 20,717 to 20,636 types; its bound with every source known is 20,555 (Log) |
| J. Owned arrays | an array filled while only its maker knows it and then kept by a new object's readonly field is a node of its own in the type graph (Roslyn's green nodes' child arrays against its builders' arrays) | Compiler/OwnedArrays.cs (new), Arenas.cs | done 2026-11-20: green nodes out of the cycle graph (268 types); self-hosted 14.1 to 11.9 s, 470 to 295 MB |
| K. Baseline-tier code quality | from the side-by-side profile: counted reads whose count can sink to the one path that keeps the value, static-initializer checks, constant-length bounds checks, frame setup, small calls left | Wasm/*, Compiler where a pass needs a fact | done 2026-11-20: six changes (Log); micro 1.876x to 1.847x, self-hosted 2.74x to 2.65x NativeAOT (quiet rounds). Round 2 done 2026-11-21: count fast paths, frames in place, zero-skipping range releases; self-hosted 2.04x to 1.98x. Round 3: those three off by default (no gain on the self-hosted compiler, 9.5 MB), final tidy; hello world -700 B, module 82.3 to 72.4 MB, time the same. Left: typed releases for borrows (branch claude/k2-throw-only-wip), dispatch stubs |
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

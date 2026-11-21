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
| 3 | Invisible API slice (9.4) in the Bazoodle repository | not started | builds and gates one interaction |
| 4 | `tools/bench.mjs`, optimizing tier: time, memory | 0.96x, 16 MB vs 71 MB | < 1.0x, less memory |
| 4 | Frame-time spikes: worst frame of a long run against NativeAOT's | no check | check exists, worst frame not above NativeAOT's |
| 4 | Hello world size (test 145) | 27,825 B | < 28,383 B |
| 4 | Self-hosted compiler on `Console.WriteLine(42)`, no threads: time | 12.45 s vs 4.6 s | < 4.6 s |
| 4 | Same: peak memory | 470 MB vs 239 MB | < 239 MB |
| 4 | Same: module size | 42.7 MB vs 129 MB (was 64.0; `tools/size-report.mjs`) | met; next waste: code (36.2 MB) |
| 5 | Threaded self-hosted compiler: more threads not slower | 18.7 s on 1, 17.0 on 4, 26.6 on 12, 26.7 on 24 (main before: 16.3, 23.6, 37.7, 39.4); Roslyn's one group of 19,847 types is one lock | faster with threads |
| 5 | Game built in the browser by the self-hosted compiler (proof 5 item 1) | not re-tried since 11-17 | plays, log equals the oracle's |
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

Memory: the arena's member words (40 bytes per member) and the counts' words are per object; fewer cycle-capable
objects shrinks both. Then the allocator's waste by size class (measure).

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
| D. Proof 5 in the browser | game built by today's self-hosted compiler in the browser | host/web, tools/proof5.mjs, tests/Godot | to start |
| E. Behaviour gaps | `StackFrame.GetMethod()`, `FirstChanceException`, the sweep's trace differences | CoreLib Boundary, stack trace code | done 2026-11-20 but for compiler-made methods' records (a feature of its own) |
| F. Plugins | A4.6 closed-form solver with its tests | Compiler/Solver.cs, PluginSolver.cs, tests | done 2026-11-20 (version 6) |
| G. Spikes | frame-time check against NativeAOT | tools (new), bench programs | to start |
| H. Flakes | test 139 leak under load, 072_http | Runtime.cs (read first), tests | done: both found and fixed (tests 149, 150) |

**Second fan-out (2026-11-20), for requirement 4's self-hosted compiler** (each in its own session and worktree):

| Stream | Items | Files | State |
|---|---|---|---|
| I. Value sources everywhere | record sources precisely at every store, argument and return (boxed generics, conversions, interface results, conditionals), also without threads; then `Sharing.SiteTypeIds` on by default where it narrows | Compiler/Expressions.cs, Calls.cs, Sharing.cs | to start |
| J. Owned arrays | an array filled while only its maker knows it and then kept by a new object's readonly field is a node of its own in the type graph (Roslyn's green nodes' child arrays against its builders' arrays) | Compiler/Arenas.cs, FreshStores.cs | to start |
| K. Baseline-tier code quality | from the side-by-side profile: counted reads whose count can sink to the one path that keeps the value, static-initializer checks, constant-length bounds checks, frame setup, small calls left | Wasm/*, Compiler where a pass needs a fact | to start |
| L. Self-hosted memory | where the 470 MB against 239 go (member words, counts, allocator waste by size class, pages held) and the structural cuts | Boundary/Runtime.cs allocator (after C merges) | to start |

Tools for them: `tools/profcompare.mjs` (two profiles by category and by Roslyn method), `tools/profsize.mjs` (self
time by callee size), `tools/profcallers.mjs`, `tools/selfhost-ab.sh` (interleaved self-hosted builds against
NativeAOT-LLVM's), `tools/roslyn-parse-probe.cs` (a small program over Roslyn's parser: compiles in about 2 minutes,
with CS2WASM_DUMP=name to read a function's code). Heavy runs through `tests/out/heavy.sh`.

9.4 (the Invisible API slice) lives in the Bazoodle repository: started once F lands, from that repository.

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

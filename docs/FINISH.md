# Finishing plan (2026-11-20)

The working list to 100%. Updated as items land: each line says its state and the evidence. Details and history
stay in docs/AUDIT.md; this file is only what is left and how each part is checked.

Renamed il2wasm on 2026-11-23 (docs/SPEC.md section 12; the IL front end's map is docs/IL-FRONTEND.md). Dated
sections and the log keep the names they were written with: `cs2wasm` is il2wasm, `cs2wasmc` the command line
`il2wasm`, a `CS2WASM_*` switch `IL2WASM_*`, `src/Cs2Wasm*` and `tests/Cs2Wasm.Tests` are `src/Il2Wasm*` and
`tests/Il2Wasm.Tests`.

## What 100% means

Scope as the owner set it on 2026-11-20:

1. **Any C# in, one WASM module out** (not WasmGC), behaving as .NET does, stack traces included.
2. **No GC of any kind**: no collector, no run-time walk of memory; what .NET frees, il2wasm frees.
3. **Plugins good enough for the Invisible API.**
4. **Every module il2wasm emits beats its NativeAOT-LLVM WebAssembly counterpart**: faster, smaller, less memory, no
   frame-time spikes, in the same engine. The self-hosted compiler is one such module: il2wasm's build of the
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
| 1 | Suite, checked and as shipped (`node tools/suite.mjs`) | 264 passed, 0 failed, no known gap, both ways (main 440bb82, the homelab, 2026-11-23 08:45) | all pass, no `known-gap` |
| 1 | Behaviour differences on record (AUDIT open items) | GetMethod, TargetSite, GetCurrentMethod, FirstChanceException and 6 trace differences closed (stream E's tests); reflection on compiler-made methods closed 2026-11-21 (tests 155, 156: names with Roslyn's ordinals, declaring classes, attributes, MoveNext); a capture-free lambda's `Delegate.Target` closed 2026-11-23 (test 270, claude/known-diffs-2); open: the generated classes' own member lists, generic generated classes as constructed types, and what docs/AUDIT.md's "Known differences from .NET, second round" lists as left (a table's value that holds its key, what only a dying finalizable object holds, a root that is a member of its group, spans in the core library's own code); .NET's own library frames (not reproducible from source) | none |
| 2 | Leaks: test 139 under load, 127, 131 | 139: none in 400 loaded runs, checked and as shipped (was 2 nodes in ~1 run of 45; fixed 2026-11-20) | none in 200 loaded runs |
| 2 | Test 127 under load (`tools/stress-cycles.sh`) | open: `async 5 / pending 1` in about 1% of loaded runs (main 6e60243: 22 of 2,400; main d4b331e: 4 of 400; main dca1f9c: 3 of 400, the homelab); the 2026-11-21 fix made it rarer, not gone. Test 187 as shipped differs under load at about 2.5% (19 of 800 on main bc06ea9, 20 of 800 on dca1f9c) | none |. 2026-11-23 (claude/load-flakes-safe): tests 127 and 187 now wait, bounded, for the pool thread that completed the last async method before they count (what they asked right after Task.WhenAll came back .NET does not promise, by its sources; not shown by .NET's output in 400 loaded runs); the leak itself is open as before, about one loaded run of a hundred of 127, 131, 187. Its causes are found and closed on claude/load-flakes-1 (a stale note on a top that code still holds; the top an ownership that turns round leaves; two structures that refer into each other), but the upkeep of the notes there (a8a50fc) kept the self-hosted compiler from ending, and the form that does not (the numbers kept at the events) still leaves test 187's rounds in 3 to 8 loaded runs of 400: not merged
| 2 | Test 139 under load, a trap | the cause found and guarded (main 80ae1ce): the engine lets a thread go on with the memory size it last saw after another thread grew the memory, and checks atomic accesses, copies and fills against it; Wasm/MemSync.cs refreshes it before those (test 197, with the engine's stack checks off: 60 of 60 trap without the pass, none with it; test 139 so run 7 of 1,000 without, none of 3,000 with). Open: one of those 3,000 ended with two nodes left (`16 3`), none such in 34,400 with stack checks; not followed | none |
| 3 | Closed-form solver (A4.6), its four examples against running the ticks | pass (API check "plugin v6", version 6) | pass |
| 3 | Invisible API slice (9.4), `samples/invisible-api` (owner, 2026-11-20: a sample here, not in Bazoodle) | pass (suite "invisible-api slice", both modes; interface version 7) | builds and gates one interaction |
| 4 | `tools/bench.mjs`, optimizing tier: time, memory | 0.96x, 16 MB vs 71 MB | < 1.0x, less memory |
| 4 | Frame-time spikes: worst frame of a long run against NativeAOT's (`node tools/frames.mjs`) | 4 of 5 scenes below (worst frames 0.11x to 0.22x NativeAOT's with `--js-young 32`, quiet); tree: on main cb287fb, quiet, p99.9 2.55 to 2.57 ms vs 2.38 to 2.41 (1.06x to 1.08x), worst 0.87x to 0.88x; with the node path (main 189b8c6, the machine loaded by three sessions) p99.9 0.97x and 1.08x, worst 1.08x and 1.27x; switch on and off in turn on 7d36995, busy: p99.9 1.03x to 1.16x on, 1.15x to 1.22x off; on the homelab alone 2.80 to 2.83 ms on, 3.05 to 3.07 off. To be taken again quiet. Branch claude/tree-3 (round 10 of the build, not merged), the desk loaded, the round's six switches off and on in turn, five pairs: tree p99.9 0.76x to 0.85x NativeAOT-LLVM's with them on (2.00 to 2.46 ms against 2.39 to 3.24), 0.95x to 1.14x off; worst 0.68x to 0.78x in four pairs, 3.340 against 3.328 ms in the fifth | worst frame and p99.9 not above NativeAOT's on every scene |
| 4 | Frame medians against NativeAOT's (`node tools/frames.mjs`). The owner's bar since the evening of 2026-11-22: churn and tree may be slightly slower as long as it is faster overall, the self-hosted compiler is fast enough to be practical, and everything is fully correct and deterministic | churn 1.17x to 1.22x on claude/churn-5 (2026-11-23; its mean frame 1.12x to 1.17x), 1.32x to 1.35x on main 189b8c6 (was 1.58x, 2.16x, 3.47x), tree 1.57x to 1.60x (was 1.64x, 2.56x), collections 0.99x to 1.03x, strings 0.87x to 0.89x, bigdrop 0.88x to 0.94x (main 189b8c6 and cb287fb). The whole run (mean frame, pauses and rebuilds in it; `frames.mjs` prints it): geometric mean over the scenes 0.72x to 0.74x NativeAOT-LLVM's; churn 1.18x to 1.29x, tree 1.32x to 1.38x, collections 0.77x to 0.83x, strings 0.75x to 0.80x, bigdrop 0.20x; the five means summed 0.98x. bench.mjs 0.92x. Branch claude/tree-3 (not merged), the same five pairs: tree 1.27x to 1.43x (0.038 to 0.043 ms; 1.52x to 1.68x with the round's steps off), its mean frame 1.03x to 1.12x; the other scenes as they were; the mean frame's geometric mean over the five 0.70x | faster overall; churn and tree slightly slower accepted |
| 4 | Hello world size (test 145) | 27,783 B (claude/churn-5, 2026-11-23); 27,784 B (claude/round-9 d430594 and claude/tree-3); 27,089 B (main dca1f9c) | < 28,383 B |
| 4 | Self-hosted compiler on `Console.WriteLine(42)`, no threads: time | 7.19 s vs 4.06 s from the start, 1.77x (claude/selfhost-speed-2, 2026-11-23, the homelab, five interleaved rounds; NativeAOT-LLVM's build is of 2026-11-20 and does more work: fifteen libraries with their documentation comments, the self-hosted compiler nine without). A later build: on the host's snapshot 2.65 s vs 4.07 (0.65x; 3.66 vs 4.78 with the machine loaded), in an instance that is kept 0.66 s. Before: 8.20 s vs 3.96 s, 2.1x (claude/selfhost-speed-1, 2026-11-22, the homelab, nine interleaved rounds; NativeAOT-LLVM's build is of 2026-11-20 and reads all fifteen core libraries, the self-hosted compiler nine); before: 9.51 s vs 3.82 s, 2.49x (main ad4c99e, the homelab, quiet, 3 interleaved rounds; this machine measured 1.98x under load, which slows NativeAOT's run more) | < NativeAOT-LLVM |
| 4 | Same: peak memory | 241.8 MB vs 322.2 MB (claude/selfhost-speed-2: the libraries' documentation comments not parsed; NativeAOT-LLVM's figure is the one on record); before: 272.0 MB vs 322.2 MB (claude/selfhost-speed-1, with nine libraries read, 344.2 MB with all fifteen; NativeAOT-LLVM's figure is the one on record, of its build that reads fifteen, not measured again); before: 306.0 MB vs 322.2 MB, 0.95x (main ad4c99e, homelab; unchanged by the reflection merge) | < NativeAOT-LLVM's, same measure |
| 4 | Same: module size | 39.5 MB vs 129 MB (main ad4c99e) | met; next waste: code (36.2 MB) |
| 4 | Programs with threads: frames against NativeAOT's (`node tools/frames-threads.mjs`; NativeAOT-LLVM's WebAssembly with threads does not link here, so against its program without threads) | claude/threads-1, 2026-11-23, median frame: one idle worker 1.65x to 2.8x NativeAOT's (churn 0.100 ms against 0.050; before 1.99), the tree 3.6x (0.120 against 0.034; before 1.21); a worker's private churn 2.2x (before 49x), its private tree 3.6x; work handed between threads 0.8x to 2.6x. Worst frames below NativeAOT's except the tree's rebuild (17 ms against 3.7). Private churn scales 5.3 at eight threads, private trees 3.2 | < 1.0x; open: the groups with threads (build 6x the program without), types reachable only by their type (strings) |
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
with IL2WASM_DUMP=name to read a function's code). Heavy runs through `tests/out/heavy.sh`.

9.4 (the Invisible API slice): built as a standalone sample in this repository (owner, 2026-11-20), stream M, done:
`samples/invisible-api` and its check `tests/Il2Wasm.Tests/InvisibleCheck.cs`.

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
- 2026-11-23, the collections and strings scenes, round 2 (branch claude/colstr-2 from main 2dfc806, not merged
  when this was written; AUDIT A3.5, "The collections and strings scenes, round 2"). (1) Stores that go down a
  structure close no cycle (`Compiler/ChainStores.cs`, `CS2WASM_NO_CHAIN_STORES=1`): the value read out of the
  container, or a new object filled from below it, proven over the method's control flow graph; StringBuilder's
  chunks are plain counted objects (40 appends 1.40 to 0.62 us, NativeAOT-LLVM 0.45), unless the program enumerates
  chunks or sets fields by reflection. (2) A collection that grows moves its items (`Rt.MoveRange`,
  `CS2WASM_NO_ARRAY_MOVE=1`; Dictionary, HashSet, List): where only the collection's field refers to the full old
  array, nothing is counted; TryInsert lets its local go before Resize. (3) An `out` value borrowed
  (`Compiler/OutBorrows.cs`, `CS2WASM_NO_OUT_BORROW=1`): `if (map.TryGetValue(key, out var v))` with a branch that
  only reads counts nothing for v. By phase (tools/colstr-parts.cs, lowest of six runs on a loaded desk, before by
  the three switches): **strings' frame 8.19 to 7.21 us against 7.91 (0.91x); collections' 7.21 to 7.27 to 6.78 to
  6.81 against 6.36 to 6.80 (1.00x to 1.07x: at NativeAOT's in one pass, above it in the other; not settled on this
  machine), its p99 36.5 to 37.0 to 25.9 to 26.0 us against 26.8 to 30.4.** `frames.mjs --runs 3 --js-young 32`, three pairs in turn at a quiet moment, before (the three switches) / after: **collections median 6.9 to 7.2 / 6.4 to 6.6 us against NativeAOT-LLVM's 6.0 to 6.4 (1.13x / 1.07x to 1.08x: not reached), p99 34.5 to 37.9 / 24.2 to 25.4 against 24.9 to 27.0 (at NativeAOT's)**, p99.9 0.175 to 0.193 / 0.174 to 0.183 ms (1.04 to 1.11), **worst 0.21 / 0.36 to 0.37 ms (1.29 to 1.37): one frame, the first clear after the warm-up (frame 200), 0.17 ms longer with the move in every run, the other clears 0.18 to 0.20 as before or below; the code that lets an array's values go ran at every resize before and now runs only at the clears, and is still in the engine's baseline tier at that one: with `node --wasm-tiering-budget=1000000` (the optimizing tier a thirteenth of the calls sooner) frame 200 is 0.21 ms, as the other clears (0.38 to 0.39 by default, three runs each; tests/out timing, the module unchanged). An engine tier-up moved, not work added; the worst frame it makes is 0.37 ms against NativeAOT's 1.3**; **strings median 7.5 to 8.0 / 6.7 to 6.9 us against 7.6 (0.99x / 0.88x to 0.91x)**, p99 12.0 to 13.0 / 10.3 to 10.5 (18.3 to 20.0), p99.9 0.018 to 0.021 / 0.017 (0.059 to 0.080), worst 0.04 to 0.05 / 0.04 to 0.06 (0.24 to 0.26); churn 1.75x to 1.81x / 1.76x to 1.80x, tree 1.63x to 1.66x / 1.61x to 1.67x with p99.9 2.64 to 2.77 / 2.66 to 2.74 ms (2.41 to 2.55: not reached, not this round's), bigdrop 0.87x to 0.88x / 0.84x to 0.88x; memory 16.0 MB against 59.3 (71.1). bench.mjs 0.93x to 0.94x
  before and after; hello world 27,456 bytes. Where an object's cost is (tools/alloc-parts.cs, new): 100 objects
  made and kept 0.41 us against NativeAOT's 0.54, 100 let go 0.81 against 0.15: the allocation is faster than
  NativeAOT's, the whole difference is the free, 6.6 ns an object (the allocator's rounds'). **Not built:** the
  sets' counts in TryInsert (its `entries` local and the new value's pair, 0.11 us), the removes and the queue
  (0.12), `Join` (0.07 of the strings frame, which is below NativeAOT's without it), the move for Queue. **Found,
  on main too, not fixed:** a Span over a List's items or a `ref` to a dictionary's value held across a growth
  reads the freed array (as shipped the bytes are still there; a checked build traps). Tests 213, 214, 220, 221.
  On the homelab at 11b6497 (the last commit that changes code): suite 214/215 checked (088's timer line; alone it passes both ways) and 215/215 as shipped; at c3c4867, 215/215 both ways; stress, 200 loaded runs a mode: 213, 214, 220 and 221 the same in every run; the self-hosted compiler (tools/selfhost-switch.sh, which now compiles test 145's hello world: its own one-line program was top-level statements, an error in a library, and no module was ever written, on main too) with and without the round's three switches: the same module from both (27,393 bytes), 9.06 to 9.18 s as it is and 9.07 to 9.30 switched, peak memory 344 MB both. The module differs from the one the compiler run by .NET writes for the same file at byte 24,844 (a comparison added to the script this round, each given the file under another path: not followed, and not known for main).
- 2026-11-22 to 23, the tree's build, round 7 (branch claude/tree-build-7, from claude/round-6b 8022fbc; AUDIT
  A3.5, "The build, round 7"; not merged when this was written). **Fixed: a program that stores a node's list of
  children in a second place, or holds the list by a local or a static across the Add, kept every subtree stored
  over until its tree went** (tools/tree-assign.cs `121 161 201` for .NET's `121 121 121`; a leak in any game that
  resets a node's children). Cause: the list, counted across `e.Kids.Add(Tree(e, ..))`, waits for its owner as the
  node does when the first child joins, and Rt.LinkWaiting took (child, node) before (node, list): the child
  became the node's owner and the list the top of the structure. `Rt.WaitingTop`
  (`CS2WASM_NO_WAITING_TOP=1`) settles (node, list) first; test 199 fails without it. Measured for the rest of the
  round's list, nothing built: the scene tree with an array of the node's own in place of the list (one member
  fewer a node and a leaf) builds in 1.53-1.59 ms for the list's 1.70-1.75 and drops in 0.18-0.20 for 0.42; with
  first-child and next-sibling fields (one member an entity) in 3.5 ms. **The members' number is not the build's
  cost, the links' kind is; the exclusive part (the list and its array no members of their own) is worth 0.2 to
  0.4 ms of a build and half the drop, and was not built in this round, nor the node's own path, nor the parent
  field's proof.** Entity cannot leave the groups at compile time in this scene (its type is on a cycle through
  its own list, and the move's target is picked at run time), so a member an entity and an up-reference a child
  stay: by the parts measured, 25 to 30 ns an entity, 0.5 to 0.6 ms a build against NativeAOT-LLVM's 0.26 (an
  estimate from parts, not a measurement); today's 1.6 ms is three times that and no floor. frames.mjs here, the
  fix off and on in turn twice (the scenes do not take its path): tree median 0.047 and 0.048-0.049 against
  NativeAOT-LLVM's 0.029-0.030, p99.9 2.52-2.62 and 2.67-2.88 against 2.44-2.57 (the desk's spread; with round 6's
  step the rebuild frame is 0.08 to 0.11 ms above NativeAOT-LLVM's in the quiet pairs: not reached); the other
  scenes as on main; bench.mjs 0.98x and 0.94x. On the homelab at 1c7f1c4: suite 214/215 both ways
  (088's timer line, other sessions' jobs beside it); stress, 200 loaded runs a mode: 199, 198, 192 to 197 and 139
  the same in every run, 127 differed in 2 of 400; the self-hosted compiler builds with and without the switch
  and writes hello world byte for byte as the .NET-hosted one (selfdet.mjs, 9.1 s); hello world 27,642 bytes.
- 2026-11-23, claude/colstr-2 and claude/tree-build-7 merged (main 8577d9e; the entries above), checked again
  before the merge. Homelab at 20cb183 (main with colstr-2): suite 219/219 checked and as shipped; stress, 200
  loaded runs a mode: 139, 198, 207, 213, 214, 220, 221 the same in every run, 127 differed in 7 of 400; the
  self-hosted compiler's hello world byte for byte the .NET-hosted compiler's (27,584 bytes), 12.0 to 14.4 s
  against 12.6 to 14.3 before on a loaded homelab (NativeAOT-LLVM's 5.1 to 5.9). tree-build-7 (Rt.WaitingTop, the
  replaced subtrees a counted list kept) by its session's suite, 215/215 both ways; on the merge here tests 199, 21x,
  22x and 145 passed both ways. frames.mjs here twice with the machine quiet (`--runs 3 --js-young 32`): churn
  0.077 ms against NativeAOT-LLVM's 0.049 (1.58x), tree 0.047 against 0.029 (1.63x to 1.64x), p99.9 2.56 and 2.70
  against 2.44 and 2.47, worst 2.81 and 3.00 against 3.14 and 3.16; collections 0.006 both (1.03x), p99 0.024
  against 0.025, worst 0.32 to 0.33 against 1.31 to 1.34 (0.21 before colstr-2: the first clear after warm-up, the
  engine's tier-up); strings 0.007 against 0.008 (0.89x); bigdrop 0.003 both; memory 16 MB against 59 to 71.
- 2026-11-23, references into a collection's storage (branch claude/colstr-3 from claude/round-6c 20cb183, not
  merged when this was written; AUDIT A3.5, "round 3"). A span over a list's items (`CollectionsMarshal.AsSpan`)
  or a `ref` to a dictionary's value (`GetValueRefOrNullRef`, `GetValueRefOrAddDefault`) held while the collection
  grew read the freed array (a checked build trapped; `list.AddRange(CollectionsMarshal.AsSpan(list))`): the call
  site now counts the object the reference points into (`Rt.ObjectOf`) into a local of the calling function, let
  go at its return (`Compiler/StorageKeeps.cs`, `CS2WASM_NO_STORAGE_KEEP=1`; a method or property of the program's
  that wraps one of the calls likewise, one level). As .NET, checked and as shipped; a program without such a call
  is compiled as before. Test 222. Collections' worst frame with round 2's move (0.21 to 0.37 ms, the first clear
  after the warm-up): the engine's tier-up, shown (`node --wasm-tiering-budget=1000000`: 0.21 ms, as the other
  clears); steady state 0.18 to 0.20 ms. Round 2's difference between the self-hosted compiler's module and the
  .NET-hosted one's was the output's name (tools/selfhost-switch.sh now asks tools/selfdet.mjs). On the homelab tools/selfdet.mjs: self = net, byte for byte (27,591 bytes). **Not
  done:** the free path taken apart (6.6 ns an object), TryInsert's counts, the removes and the queue, the queue's
  growth as a move, `Join`: collections' median is where round 2 left it (1.07x to 1.08x). On the homelab at 0aef607 (9db608e's code and the script): suite 220/220 checked and 219/220 as shipped (088's timer line; alone it passes both ways); at 9db608e 218/220 checked and 219/220 as shipped: 072_http in both (the oracle's own connection refused, the port taken by another run) and 131 checked (5 left, the async kind that differs under load; here it passes both ways with the keep and without, and the second run did not show it); stress, 200 loaded runs a mode: 222 the same in every run; the self-hosted compiler with and without the keep: the same module (27,579 bytes), 9.4 to 10.0 s, peak memory 344 MB. Hello world 27,642 bytes on this base. Not measured this round: frames.mjs and bench.mjs (no change to any scene's code: none makes such a call).
- 2026-11-23, claude/colstr-3 merged (main a15be27 with this entry after it; the entry above: a reference into a
  collection's storage keeps the storage), checked again before the merge. Homelab at a15be27: suite 220/221 checked
  and as shipped, the one 088's timer line in both (the homelab shared with four sessions); stress, 200 loaded runs
  a mode: 139, 199, 220, 222 the same in every run, 127 differed in 6 of 400; the self-hosted compiler's hello
  world byte for byte the .NET-hosted compiler's (27,584 bytes), 14.4 to 15.3 s against 14.6 to 16.2 before on the
  loaded homelab (NativeAOT-LLVM's 6.1 to 6.3). Not covered yet, by its session's report: a span or ref returned up
  through two frames (one level of wrapper is); given to the session on the free path.
- 2026-11-23, a corruption of the heap that claude/colstr-2's move brought to main (20cb183 to 3dbef69), found by
  the session on claude/field-loads-1 and fixed (main 4781226: its commits 601c0e1 and 7393522 picked). A collection
  that grows by moving left its old array retyped as byte[], to be freed without counting anything off; an array of
  a type that can be on a cycle has the arena's 32 bytes before its header and is freed with them only by its own
  type's destruction, so as byte[] it was freed 32 bytes into its block and 32 short. It takes such an array that
  is still a plain object when the collection grows (made with a capacity, filled past it with items that refer to
  no member): the inliner's `new List<Instr>(n)`. The self-hosted compiler then trapped, looped (one such run held
  the homelab's slot for half an hour) or failed on every program larger than hello world; the suite did not show
  it, and the check made before each merge compiled hello world only. Rt.MoveItems now refuses those arrays (they are
  copied as before; CS2WASM_ARRAY_MOVE_CYCLE=1 for the old move); test 240 traps in the allocator with the old move
  under the .NET-hosted compiler and passes with the fix, here and by that session. Homelab at 4781226: the
  self-hosted compiler's module byte for byte the .NET-hosted compiler's for hello world and for tests 207, 220,
  222, 240, 193 and 088 (1,613,207 bytes); its hello world in 10.1 to 10.8 s against 9.8 to 10.0 before and
  NativeAOT-LLVM's 4.6 to 5.0. From here every merge's self-host check compiles those larger tests too. Not done: the
  full suite and frames.mjs at 4781226 (the fix is four lines in ArrayMoves.cs; tests 22x and 240 passed here).
- 2026-11-22, the free path taken apart (branch claude/free-path-1 from claude/round-6d a15be27, not merged when
  this was written; AUDIT A3.5, "The free path taken apart"). A free was about twenty loads, ten stores, twenty
  branches, a jump through the table of types and a call: 4 ns over NativeAOT-LLVM's store on a quiet machine.
  Measured parts: the nesting depth 1 ns; what the engine inlines decides the rest (V8 inlines no call it has not
  counted before it compiled the caller, and keeps a small function in baseline code for a long time: a release
  that called its class's destruction directly was slower than the dispatch, 568 to 757 ns for 100 objects, where
  the loop was compiled before the first object died). Built: flat destructions (`CS2WASM_NO_FLAT_DESTROY=1`: a
  class that lets go of leaves and frees itself; rt.destroy counts no nesting for it), written in place in loops
  and in functions of at most 60 instructions (`CS2WASM_NO_DESTROY_IN_PLACE=1`, `CS2WASM_IN_PLACE_SMALL=n`) with
  the free of a known size as `Rt.FreeFixed`; the class named at more releases, among them a store in generic code
  by the type it is compiled for, for a sealed class that looks flat (`CS2WASM_NO_STORE_HINT=1`: List<T>'s and
  Dictionary<K,V>'s stores, where the scenes' frees are); Dictionary takes a fresh value's count
  (`CS2WASM_NO_OWNED_INSERT=1`: `map[key] = new Item()`, Add, TryAdd). Without threads only.
  `tools/alloc-parts.cs`, ns, NativeAOT-LLVM / before / now: made over old ones 226 / 600 / 300, let go 100 / 500 /
  233. `tools/colstr-parts.cs`: the collections frame 6,405 to 6,101 against 5,961 (1.07x to 1.02x). frames.mjs,
  three quiet pairs at the last commit, before / now: collections 1.03x-1.05x / 1.00x-1.03x, churn 1.57x-1.58x /
  1.56x-1.58x (**not moved, not reached**), tree 1.62x-1.63x / 1.62x-1.65x (**not moved, not reached**), strings
  0.89x-0.91x / 0.86x-0.87x, bigdrop 0.90x-0.94x / 0.90x-0.97x; bench.mjs 0.92x-0.93x both; hello world 27,642 to 27,757 bytes.
  Tried and not kept: the dispatch's cases in place (no scene moved, hello world 30,177 bytes); a string's or an
  array's class named at stores in generic code (the self-hosted compiler 3% slower). Tests 235 (two break
  switches) and 237.
  **References into a collection's storage, complete** (`Compiler/StorageKeeps.cs`): any number of frames by a
  fixed point over the calls, every way out of a call (the result, an `out`/`ref` ref struct, a constructor's ref
  struct, the ref struct a method is called on, virtual and interface methods, delegates), the library's methods
  as the program's: LINQ's First, Last and Any over a list whose predicate grew it read the freed array, on main
  too. Not covered: a function pointer. `CS2WASM_STORAGE_KEEP_SHALLOW=1`; test 222 extended.
  **A queue that grows moves its items** (`Rt.MoveRotated`, an edit of Queue.cs): 4,096 enqueues with ten growths
  31 us to 21; test 236.
  **Not done:** the count on TryInsert's `entries` local (collections is 1.02x to 1.03x NativeAOT-LLVM's by
  colstr-parts, 1.00x to 1.02x by frames.mjs: not below it), the lookups' `out` value in that tool's loop,
  `String.Join`'s counted reads (read, not built), Dequeue's and Pop's count pair (seen, not built). **Found, not
  fixed, on main too:** a ConditionalWeakTable's values are not let go when their keys die while the table lives.
  On the homelab at 66374eb (the last commit that changes code): suite 224/224 checked and as shipped (343 s); 088 alone both ways (at 97f3772 the suite was 222/223 both ways, 088's timer line; at 9e254f8 221/224 both ways, tests 055, 058 and 107 not compiled, "consumed struct parameter not moved in Dictionary<string, ResourceLocator>.Add": the owned insert for a value that is a struct, fixed in 66374eb, test 237 has the case; the earlier stages 222/222 at a7918b9, c5395a0 and 0e981fa). Stress, 200 loaded runs a mode: 235, 222 and 236 at 9e254f8 and 237 at 66374eb the same in every run of both modes; 139 the same in every run and 127 differing in 7 of 400 at 9b4639b (its "async 5" line, main's rate; 4 of 400 at a671855). The self-hosted compiler at 66374eb, built as it is and with `CS2WASM_NO_FLAT_DESTROY=1 CS2WASM_NO_OWNED_INSERT=1` (tools/selfhost-switch.sh: 32,558,663 and 32,292,082 bytes, 0.8% larger): both compile hello world to the same module (27,709 bytes), 9.18 to 9.48 s as it is and 9.22 to 9.47 s switched, three rounds in turn, peak memory 344 MB both; tools/selfdet.mjs: self = net, byte for byte (27,721 bytes); the same at 0e981fa and 97f3772. Not run on the homelab: frames.mjs (it has no Emscripten); nothing was measured with threads.
- 2026-11-23, claude/field-loads-1 (merged with main 3dbef69; not merged into main; docs/AUDIT.md A3.5 "Field loads
  by the field's identity"): a field of an object in a local read once before a loop that nothing in it can store,
  an array's length likewise, a field read a second time with nothing between that can store it, and a callee
  that reads fields of an object it is given inlined into loops (`CS2WASM_NO_FIELD_HOIST=1`; tests 230 to 234).
  Things found and fixed on the way: a field of a field of a null was read through whatever address 0 holds
  ("memory access out of bounds" from a loop that takes no turn), and a loop that takes no turn paid for what its
  turns read (the tree scene's median 5% to 7% up on the homelab): what a loop reads only after its test is now
  read behind a copy of the test; and the suite found test 207's reference to one field walked to the next
  (Unsafe.Add): a field's address taken now takes every field of that object out. The churn scene on the desk, with and without in turn: 0.069 to 0.071 ms, 1.33x
  to 1.35x NativeAOT-LLVM's, against 0.083 to 0.086, 1.56x to 1.60x (main: 1.58x): **not reached, two fifths of the
  distance closed**; on the homelab before the second fix 0.089 to 0.093 against 0.100 to 0.103. Tree after the second fix, the two
  builds in turn on the desk: 0.0480 to 0.0487 ms against 0.0471 to 0.0494, level (against NativeAOT-LLVM on the
  loaded desk 1.68x to 1.80x and 1.60x to 1.70x; main: 1.63x; on the loaded homelab at the end 0.069 against 0.065, below: not explained); collections, strings, bigdrop and bench.mjs: nothing (the homelab's
  geometric mean 1.006x without the pass). Hello world 27,657 bytes (27,642 without); the self-hosted compiler
  0.56% larger, built in the same time, as fast (9.0 to 9.3 s against 9.1 to 10.3). Each condition that takes a
  field out has a line of the tests that changes without it (tools/field-hoist-proof.sh), but `volatile` and
  `machine`, which stay as guards. **Found by the way and fixed here, main's: collections that grow by
  moving (claude/colstr-2) broke the self-hosted compiler on every program larger than hello world**
  (Rt.MoveItems left an array whose type can be on a cycle an array of bytes, which is freed 32 bytes into the
  block such an array has; with the pass and without it: tests 207, 220, 222, 230, 232 trapped, one run did not
  end): such an array is copied now (`CS2WASM_ARRAY_MOVE_CYCLE=1`: the old move), test 240 shows it under the
  .NET-hosted compiler, and the self-hosted compiler compiles 145, 207, 220, 222, 230 and 232 byte for byte as
  the .NET-hosted one (homelab, 601c0e1). Checked on the homelab at 7393522: suite 226 of 227 checked (088's
  timer line; alone it passes) and 227 of 227 as shipped; stress, 200 loaded runs a mode: 230 to 234, 240, 139
  the same in every run, 127 differed in 4 of 400. The homelab at the last commit, with and without the pass in turn (five rounds, other sessions' jobs beside: the rounds spread by a third): churn's median 0.095 ms against 0.107; **tree 0.069 against 0.065, the pass's higher in four rounds of five, where the desk's two sets were level: not explained** (the scene's Build and Walk are the same code in both modules, 345 and 356 bytes, Tree(int) 18 bytes shorter with the pass; tree-frame.cs there: build 17.9 to 19.2 us against 17.5 to 18.0, walk 37.3 to 40.3 and once 43.8 against 38.2 to 40.4, the indexed walk 24.0 to 25.4 and once 29.0 against 25.9 to 27.7); collections 0.0076 against 0.0070 and strings 0.0085 against 0.0084 at their lowest, inside the rounds' spread; bench-ab's geometric mean 1.004x without the pass; churn-array's list 94.8 to 99.1 us (once 109.8) against 104.9 to 111.2. The self-hosted compiler there: built in 259 s with the pass and 249 s without, 32,298,942 bytes against 32,189,404 (0.34%), hello world in 9.4 to 9.5 s against 9.4 to 10.0, peak memory 344 MB both, and built either way it compiles 145, 207 and 220 (with the pass 230 too) byte for byte as the .NET-hosted one. Not done: a test
  with a host that calls back; main's own checkout with the fix.
- 2026-11-22, the node path of a tree built parent first (branch claude/tree-node-path from main 4ac4519, not
  merged; AUDIT A3.5, the build, round 9). The three runtime calls a node makes each written out for the shape it
  finds, with the general path's end state and a refusal before any write otherwise (`CS2WASM_NO_NODE_PATH=1` for
  the general way): Rt.JoinPair (a list's first array into a plain list: the two a group of their own, no visit),
  Rt.NodeUp (the node's parent field linked as the node joins at its first child: the merge, the count and the
  parent's ownership in order, no waiting list, no side words), Rt.AddNode (the node into its parent's list: one
  walk for four). tools/tree-build.cs variant 0, off and on in turn three times: build 1.66 to 1.70 ms to 1.40 to
  1.48 (NativeAOT-LLVM 0.26), variants 1, 2, 3, 5 each 0.1 to 0.25 ms shorter, 4 the same; tools/tree-frame.cs
  build 15.3 to 15.6 us to 13.5 to 13.8; frames.mjs on a loaded desk: tree median 0.049 to 0.050 ms off, 0.047 to
  0.049 on (NativeAOT-LLVM 0.029 to 0.032), p99.9 2.91 to 3.09 off, 2.65 to 2.99 on (NativeAOT-LLVM 2.98 to 3.24
  in the same invocations); bench.mjs 0.94x and 0.93x; hello world 27,642 bytes. **Not reached: a node does not
  cost what a leaf costs**; what is left needs the intermediate states changed (the list and array no group before
  the node joins, the node provisionally under its parent), not attempted. Test 201 (the cases of the brief,
  against .NET, the same with the switch off); of the seven rules left out by hand three change its lines (1, 2,
  6), three are reached and do not (3, 4, 5), one is not reached (7).
  Checked. On the homelab at 1e71501 (the three steps; the commit after it, 38e971c, adds test 201 and takes one unused test of the break's number out, which a constant folds away): the suite 222/222 checked and 222/222 as shipped (326 s); the self-hosted compiler built with and without the switch (`tools/selfhost-switch.sh CS2WASM_NO_NODE_PATH=1`: 234 s each, 32,014,434 and 32,012,829 bytes; hello world in 9.2 to 9.5 s either way, peak 344 MB, the two the same module, and self = net byte for byte, 27,597 bytes); `node tools/selfdet.mjs --self <as-is> tests/Conformance/220_collections_grow_by_moving.cs`: self = net byte for byte (271,882 bytes, 18.4 s), and the same for test 193 (252,953 bytes, 18.9 s). On the desk: test 201 and tests 190 to 199 checked and as shipped; test 201 with the switch off and with each break (above). **Not finished when this was written: selfdet for test 088 and `tools/stress-trees.sh` (127, 139, 201, 192 to 199, 200 runs a mode) were started on the homelab and had not come back (their output: tests/out/np/hl-selfdet.txt and hl-stress.txt in the branch's worktree on the desk); the suite was not run again at 38e971c (test 201 passed on the desk both ways); frames.mjs on a quiet desk.** Threads: none of the three steps runs in a program with threads (test 197 and 139 take the general way).
- 2026-11-22, the node path, second part (branch claude/tree-node-path-2 from claude/tree-node-path 35ddb45, not
  merged; AUDIT A3.5, the build, round 9, second part). The first part's queued checks came back clean (selfdet
  for 088 byte for byte; stress, 200 runs a mode, every test the same in every run). Test 201 proves each rule of
  the steps now (`tools/node-path-breaks.sh`): rule 3 is no rule any more (Rt.NodeUp takes a parent whose last
  count from outside is the field's, as LinkMembers does it), 1, 2 and 6 change the test's lines, 4, 5 and 7
  (what the check at the drop frees later) change them with that check off on both sides, where on and off still
  agree. The owner stopped the tree's speed work during the round. Built and left on a side branch, not checked
  on the homelab (claude/tree-node-path-2-plain-parents, a3ce95f): Rt.PlainParents, the first leaf of a node whose
  own parent is a plain object still, 597 of 625 a build, 0.03 to 0.08 ms. Built, measured as no gain and taken out: (c), a list and its
  first array without a group of their own (1.44 to 1.51 ms and 1.49 to 1.52). **Not built: direction (a)** (its
  small form saves two writes and pays side words; its large form, a node owned by an array that does not hold
  it, breaks what the growth, the handover and the destroy rest on: a change of the model, argued in AUDIT), the
  lists' growth. **The build is 1.40 to 1.50 ms against 1.67 to 1.72 without the node path and NativeAOT-LLVM's
  0.26: the target is not reached**, and by the variants it is the leaf's own cost (the objects 0.47 ms, the
  19,530 up-references 0.35, the growth 0.1 to 0.2) that keeps it there. frames.mjs, quiet desk, three pairs on
  the side branch (the branch itself: median 0.048 to 0.050 off, 0.045 to 0.048 on; p99.9 2.92 to 3.09 off, 2.69
  to 2.90 on, against 2.47 to 2.61): tree median 0.049 to 0.050 ms off, 0.047 to 0.048 on (NativeAOT-LLVM 0.030); p99.9 3.04 to 3.12
  off, 2.62 to 2.72 on (2.58 to 2.64: 1.01x to 1.05x, still above); worst 3.29 to 3.69 off, 2.69 to 2.86 on
  (3.32 to 3.38). The first part's worst-frame spikes were the machine: on the homelab the worst tree frame is
  lower with the step in every pair. bench.mjs 0.94x and 0.93x; hello world 27,642 bytes.
  Checked, on the homelab at 88f2a6e (the branch's code: the first part, NodeUp's last count, test 201's new cases, the script): the suite 223/223 checked and 223/223 as shipped (435 s); the self-hosted compiler built with and without the node path (`tools/selfhost-switch.sh CS2WASM_NO_NODE_PATH=1`: 32,014,763 and 32,013,104 bytes; hello world in 14.1 to 15.4 s either way on a loaded machine, peak 344 MB, the two the same module, self = net byte for byte, 27,599 bytes); `selfdet` with that compiler for tests 220 (271,925 bytes), 193 (252,996) and 088 (1,613,340): self = net byte for byte each; `tools/stress-trees.sh`, 200 loaded runs a mode: 139, 192 to 199 and 201 the same in every run of both modes; **127 differed in 4 of 200 as shipped and 5 of 200 checked (`async 5` for 0: the kind and the rate earlier rounds give for main, 1 to 4 in 200; it starts threads and takes none of these steps; in the first part's run at 1e71501 it differed in none; not followed here)**. On the desk: `tools/node-path-breaks.sh` exits 0 (on and off the same with and without the check at the drop; each of the six breaks changes the lines). Not checked: the side branch on the homelab; the suite with `CS2WASM_NO_NODE_PATH=1` for every program (the switch is checked by test 201, the two trees' tools and the self-hosted compiler); frames.mjs on the homelab at this commit (the four pairs above are the first part's code).
- 2026-11-23 (the evening of 2026-11-22 by the owner's clock), four merges and the owner's bar restated. The bar:
  churn and tree may be slightly slower as long as (1) it is faster overall, (2) the self-hosted compiler is fast
  enough to be practical, (3) everything is fully correct and deterministic; cross-platform determinism is a must
  (the NaN test at float stores stays). Merged, each checked on the homelab with the suite both ways, stress, and
  the self-hosted compiler's module byte for byte the .NET-hosted compiler's on hello world and on larger tests:
  claude/free-path-1 (main 3f32acf: suite 224/225 both ways, 088's timer line; selfdet on 207, 220, 222, 240, 193,
  088; here the queue's move, Rt.MoveItemsRotated, was given the refusal of a cycle-capable array that Rt.MoveItems
  has, and test 240 a queue: it trapped the same way); claude/field-loads-1 (main cb287fb: suite 230/230 both ways,
  stress of 230 to 234, 240, 207, 235 to 237, 222 the same in every run, 127 differed in 5 of 400; selfdet on 230,
  232, 207, 220, 222, 240, 193, 088; the self-hosted compiler 11.7 to 12.3 s against 10.4 to 10.9 without it on the
  loaded homelab, level by its session's runs: to be timed again quiet; frames here, quiet: churn 0.065 ms against
  0.049, 1.33x to 1.34x from 1.58x); claude/tree-node-path (7d36995: suite 230/231 both ways, 088's timer line;
  stress of 201, 192 to 194, 198, 199, 240, 230 the same in every run; selfdet on 201, 230, 232, 207, 220, 222,
  240, 193, 088) and claude/tree-node-path-2 (main 189b8c6: by its session suite 223/223 both ways, selfdet on 220,
  193, 088; here tests 19x, 201 and 145 on the merge). Not merged: claude/tree-build-8 (exclusive parts: no gain as
  built, wrong in two cases, off). frames.mjs now prints the mean frame; on main 189b8c6 with three sessions at
  work: churn 0.071 ms against 0.053 (1.32x to 1.35x), tree 0.049 to 0.051 against 0.031 to 0.032 (1.57x to
  1.60x), p99.9 3.14 and 3.36 against 3.25 and 3.10; the whole run 0.72x to 0.74x. Open against the bar, with
  sessions on each: the tests that differ under load (claude/load-flakes-1: causes and fixes for 187, 131, 127, 087;
  a racing List.Add that leaves objects and damages a group's ring, open); the known differences from .NET
  (claude/known-diffs-1); the self-hosted compiler's time, 9 to 10 s for hello world against the .NET-hosted
  compiler's 4 to 5 (claude/selfhost-speed-1).
- 2026-11-22, the self-hosted compiler's phases and the libraries a program is compiled with (branch
  claude/selfhost-speed-1 on main cb287fb, not merged when written; docs/AUDIT.md, "The self-hosted compiler's phases
  on the three sides"). Phases of one hello-world compile, self-hosted / NativeAOT-LLVM's wasm / .NET on one
  processor, all fifteen core libraries: library ready 5.9 / 3.0 / 5.8 s, first drain 2.0 / 0.45 / 1.1 s, encode
  0.19 / 0.06 / 0.38 s, startup 0.07 s; the engine's tiers and its start are not the gap (flags within 5%). The
  profile: counting 1.8 s and the arena functions 1.8 s of 14 against NativeAOT-LLVM's collector 1.5 s of 6.8; the
  first drain is Roslyn's binding (`GetOperation` 59%). **Changed: `UpstreamSources.Needed` takes CoreLib and the
  core libraries up to the last whose reference assembly the program uses, not all fifteen**
  (`CS2WASM_ALL_CORE_LIBS=1`: as before): hello world reads 9 libraries, 27.8 of 35.3 million characters. The
  homelab, nine interleaved rounds: self-hosted 9.11 to 8.20 s (medians), its peak memory 344.2 to 272.0 MB; .NET on
  one processor 7.72 to 6.69 s; NativeAOT-LLVM's build of 2026-11-20 (all fifteen) 3.96 s. Every module is what it
  was: `tools/corelibs-check.mjs`, 208 programs compiled both ways, the same bytes. The self-hosted compiler built at ffc44b3 (32,886,996 bytes, built in 244 s; the build at 66fd2b1 likewise) writes hello world, 220, 193 and 088 byte for byte as the .NET-hosted compiler (`tools/selfhost-verify.sh`: 27,741, 273,319, 255,397 and 1,616,434 bytes; hello world below the 28,383 of test 145, which the suite runs). The suite on the homelab at ffc44b3: 230 of 230 checked and 230 of 230 as shipped (349 s). Not run: tools/frames.mjs and tools/bench.mjs (their programs' modules are byte for byte what they were). **Not done:** a
  program that uses System.Linq still reads 22 libraries (Roslyn reports the assemblies of an extension call's
  candidates); test 088 is as slow as it was (82.7 s against 30.9 s under .NET); NativeAOT-LLVM's build of today's
  compiler; frames.mjs was not run (no module changed).
- 2026-11-22, claude/known-diffs-1 (from main cb287fb; not merged into main; docs/AUDIT.md A3.5 "Known differences
  from .NET, taken in turn"): seven differences earlier sessions left. Closed, each with a test that fails without
  the fix: a span or `ref` into a collection's storage that comes out through a function pointer is kept (241;
  and, found by the test, a keep made by any call in an inner scope ended with that scope: the sites are found
  before the body now; generic function pointer types; `ref x = ref fp()`); a ConditionalWeakTable does not keep
  its keys and a value goes with its key (243; DependentHandle is a weak slot of the runtime's with a counted
  dependent); a derived finalizer runs the base's (242); the weak references to all members of a group that is
  going are cleared before its first finalizer (245: what .NET guarantees for short weak references); arrays of
  two and more dimensions of a cycle-capable class are group members (246). **Left**, each with a test the suite
  shows as a known gap: a finalizer that grows a list of its own dying group (244: the additions refer back, which
  reads as a member that came back; telling them apart needs a walk of plain objects), and a member that refers
  back to itself through an array's element or a far field (247). Left without a test of their own: a
  ConditionalWeakTable value that holds its own key keeps both while the table has the entry; a finalizer of a
  plain object reads a weak reference to an object only it holds as alive; the items of a replaced array that
  refer back to the array's holder stay until the group goes. Checked: each closed item's test fails without its fix (241 with `CS2WASM_NO_FNPTR_STORAGE_KEEP=1` and before the scan, the generic and the `ref` fixes; 246 with `CS2WASM_NO_RANK_SITES=1`; 242, 243 and 245 on the commit before theirs) and passes checked and as shipped. The branch was merged with main 7932c93 (the node path; no conflict) at 14779aa, and what follows is of that commit unless said. On the homelab: the suite 236 passed, 0 failed and the 2 known gaps, checked and as shipped (460 s; the job was queued at a46bd11 and its first line names that commit, the checkout was at 14779aa when it ran: tests 201, 244 and 247 are in its count); the self-hosted compiler (`tools/selfhost-switch.sh CS2WASM_NO_RANK_SITES=1`): built in 240 s, 32,886,612 bytes, hello world in 9.06 to 9.17 s, peak memory 344 MB, the same module with and without the switch, and `tools/selfdet.mjs`: self = net byte for byte on 145 (27,738 bytes), 220 (276,626), 193 (258,680) and 088 (1,617,408); before the merge, at a63e075 (item 1 alone) the suite 231/231 checked and as shipped, and at dbae95a (items 1 to 3) the self-hosted compiler byte for byte on 145 (9.3 to 10.2 s, 344 MB). **Not run: `tools/stress-cycles.sh`** (127 and 139, and the new tests, 200 runs a mode): queued on the homelab behind other sessions for forty minutes and not started when this was written; the desk refuses heavy work. On the desk, the merge: tests 241 to 247 (244 and 247 as known gaps), 030 to 039, 190 to 207, 220 to 222, 020, 086, 087, 125, 127, 139, 143, 180 and 187 and the five of 22x pass both ways; hello world 27,784 bytes as shipped (the limit 28,383; the same number at the first commit of the branch). `node tools/frames.mjs --runs 3 --js-young 32 --no-aot`, main and the merge built in turn, two rounds each (main first): tree median 0.048 and 0.048 ms against 0.047 and 0.048, p99 0.073 and 0.070 against 0.070 and 0.070, p99.9 2.78 and 2.83 against 2.70 and 2.66, worst 3.33 and 3.14 against 2.96 and 2.97; churn median 0.066 and 0.067 against 0.068 and 0.067, p99 0.076 and 0.076 against 0.090 and 0.082 (main's own in a full run half an hour before: 0.140; the scene has no group, no weak reference and no finalizer, and none of its code changed), p99.9 0.116 and 0.100 against 0.124 and 0.124; bigdrop median 0.003 both, p99.9 1.09 against 1.08 to 1.11: level. With NativeAOT-LLVM beside it, before the merge (bd6177b): churn 1.34x its median, collections 0.97x, strings 0.89x, tree 1.69x (p99.9 1.27x, the node path not yet in), bigdrop 0.94x. Nothing was measured with threads beyond tests 139, 143 and 197 in the suite.
- 2026-11-23, claude/known-diffs-2 (from claude/round-9 d430594, with claude/known-diffs-2-reflection merged; not
  merged into main; docs/AUDIT.md A3.5 "Known differences from .NET, second round"). **The suite has no `known-gap`
  test.** Closed, each with a test that fails without its fix or switch: a finalizer that grows its own dying
  group, and a real resurrection, which is now freed when it is let go (244, 248: Rt.FinRevive,
  `CS2WASM_NO_FIN_REVIVE=1`); a member that refers to itself through an array it owns (247:
  `CS2WASM_NO_ADOPT_CHECK=1`); a ring of cells kept by a cell of an array that is then let go (249:
  `CS2WASM_NO_DYING_NOTE=1`); weak handles that track resurrection and ConditionalWeakTable entries stay until the
  object is freed, and a table's value that is its own key is freed (265, 266, 248); a capture-free lambda's
  `Delegate.Target` (270, the reflection branch). Found by the sweep and closed: **a span or a `ref` did not keep
  the object it points into** (`Make().AsSpan()`, a span over a field's array that is replaced, a local assigned
  again, a span returned over the function's own array, a place passed by `ref` whose object the callee lets go:
  34 of 38 small programs read freed memory; now kept at the site, in the program's own functions, 271, 272, 268:
  `CS2WASM_NO_SPAN_KEEP=1`); a container let go while one of its items is held from elsewhere kept all its
  items (274: Rt.ShedHolds, `CS2WASM_NO_SHED_HOLDS=1`). Found and left: a static's initializer pins its object,
  which is then never freed from a cycle when the static is set again; unpinned (`CS2WASM_UNPIN_STATIC_INIT=1`,
  not the default) tests 248 and 249 fail: **what tests 247 to 249 prove holds for a root that is a plain pinned
  object; with a root that is a member of its group, cells that refer to each other and back to the root still
  stay until the group goes** (247 holds either way). Sweep tests with no difference left in them: 267, 269.
  Test 139 with the engine's stack checks off: 6,000 runs as shipped, 24 at once, all `16 1` (not reproduced).
  **Left** (AUDIT, each with its smallest program): a table's value that holds its key through a field; what only
  a dying finalizable plain object holds reads alive to a weak reference in its finalizer and is finalized when it
  is freed, not with its holder; GC.ReRegisterForFinalize twice; `Type.IsFunctionPointer`, the compiler-made
  classes' member lists, generic compiler-made classes as constructed types, Invoke on a compiler-made method;
  FinRevive with threads; spans and by-reference places in the core library's own code; compositions of the
  held-item shape. Seen and not this branch's (the same with every switch of it off): test 108 as built checked
  traps in about 1 run of 100 when four run at once on the desk (`operation does not support unaligned accesses`
  in rt.retain under Interlocked.Exchange(object, object) in Task.FinishContinuations); 088's timer line and
  085's oracle under load.
- 2026-11-23, the self-hosted compiler's build time, second round (branch claude/selfhost-speed-2 on
  claude/round-9 d430594, not merged when written; docs/AUDIT.md, "The self-hosted compiler's build time, second
  round"). Measured first, both sides profiled by phase (`tools/selfprof-report.sh`): one hello-world compile
  7,740 ms self-hosted against 4,073 NativeAOT-LLVM's, of it compiled code 4,858 / 2,548, the runtime's memory
  management 2,424 / 923, the host 329 / 539; the 230 methods both name 1.16x; the parse 3,291 / 2,923 at unequal
  work, the first drain 2,370 / 488. So the gap is the memory management and the long tail of small calls, in no
  one function. **Changed, with no change to any module:** (1) the libraries' documentation comments are passed
  over (`CS2WASM_NO_LIB_DOC_SKIP=1`): CoreLib's parse 3.7 to 2.3 s self-hosted, 30 MB less; (2) a library Roslyn
  reports as used only for a candidate is left out at the start and the program is compiled again with all when
  it meets a type one left out may hold (`CS2WASM_NO_LIB_RESTART=1`; test 250): System.Linq as extensions reads 9
  libraries where it read 22; (3) the Node host keeps a module's state from one process to the next
  (`CS2WASM_SNAPSHOT=<file>`, `host/snapshot.mjs`, SPEC 7.2 and 7.6): a later build of hello world 2.34 to 3.01 s
  where a build from the start is 7.2 and NativeAOT-LLVM's 3.9 to 5.1; (4) the host reads a module's debug
  sections when a stack asks (the compile's one throw 232 to 143 ms), and reads files through one buffer. Hello
  world from the start 8.45 to 7.19 s (medians, five interleaved rounds; NativeAOT-LLVM's build 4.06), memory
  272.0 to 241.8 MB; the second to fourth build in one instance 0.64 to 0.68 s; test 220 27.2-28.7 to 23.8-29.8 s
  (NativeAOT-LLVM 10.2-10.5; a later build in one instance 6 to 7 s), memory 435.6 to 344.2 MB; test 088
  79.9-87.5 and 78.2-91.0 s, NativeAOT-LLVM's build 92.9-94.9 (the machine loaded for both). Tried and taken
  out: compiling for threads at once (no faster under .NET; not measured self-hosted). **Checked:**
  on the homelab, the suite at 713fdb0 237 passed, 0 failed, 2 known gaps, checked and as shipped (493 s); the
  self-hosted compiler built at d4ac9d6 (32,934,974 bytes, 252 s) writes hello world, 220, 193 and 088 byte for byte
  as the .NET-hosted one, from the start, on a snapshot and as the fourth build in one instance; stress, 200 loaded
  runs a mode: tests 250 and 139 the same every run, test 127 with its open `async 5 / pending 1` in 5 of 200 as
  shipped and 3 of 200 checked; `tools/snapshot-check.sh` as expected. Queued when written:
  `tools/libs-rules-check.sh` over every program (some twenty run here: the same modules), thirty more programs on
  a snapshot, a profile of a build on a snapshot. **Not done:** the ratio at equal work (the counts, the arena functions, the cost of a call:
  unchanged; leads in the note); NativeAOT-LLVM's build of today's compiler (about 2.8 s for hello world by its
  profile, not measured); a snapshot for the browser's host and for the threaded compiler; GodotSharp programs
  gain nothing from (2); tools/frames.mjs and tools/bench.mjs not run (no module changed).
- 2026-11-22 to 23, the build, round 10 (branch claude/tree-3 from claude/round-9 d430594, not merged when this was
  written; docs/AUDIT.md A3.5 "The build, round 10"). The tree scene, measured by phase and by step before anything
  was changed. **In:** `foreach` over a List<T> with the size's test first, the list's fields read once before the loop
  and the changed list's throw on an enumerator made on the spot (the walk is three fifths of a frame: no frame,
  no handler and no reads in its loop any more; test 203, `tools/list-foreach-breaks.sh`); a new member's own
  empty object (an entity's list) left a plain object and linked to its holder when it joins later
  (`Rt.PartHolder`, test 202, `tools/plain-parts-breaks.sh`); round 9's `Rt.PlainParents` from its side branch;
  Rt.ArenaUnlink not called for a store over a slot that held nothing unless the link kept a count to give back
  (test 204, `tools/unlink-skip-breaks.sh`: the largest step for the build); Rt.Reevaluate by slot bits the
  default again (`tools/slot-walk-breaks.sh`). Each has its switch (`CS2WASM_NO_LIST_TEST_FIRST`,
  `_NO_LIST_THROW_PATH`, `_NO_PLAIN_PARTS`, `_NO_PLAIN_PARENTS`, `_NO_UNLINK_SKIP`, `_NO_SLOT_WALK`). **Numbers**
  (the desk against NativeAOT-LLVM, the six switches off and on in turn, five pairs): tree median 0.045 to 0.052
  ms off and 0.038 to 0.043 on (1.52x to 1.68x, then 1.27x to 1.43x); **p99.9 2.26 to 3.26 off and 2.00 to 2.46
  on: 0.76x to 0.85x NativeAOT-LLVM's, below it in every pair** (0.95x to 1.14x off); worst frame 0.68x to
  0.78x in four pairs and 3.340 against 3.328 ms in one; the tree's mean frame 1.03x to 1.12x; the other four
  scenes as they were; the mean frame's geometric mean over the five 0.70x. tools/tree-build.cs: build 1.52 to
  1.63 ms off, 1.29 to 1.31 on; drop 0.37 to 0.39 and 0.32 to 0.37. bench.mjs 0.89x to 0.92x both ways. Hello
  world 27,784 bytes. **Not reached: the tree's median** (1.3x to 1.4x: the build and the store of a frame are
  15 us against NativeAOT-LLVM's 2.4, and this side's walk, now the faster of the two, does not make up for
  it); the audit item says where an entity's 71 ns are and what was measured and left.
  Under heavy load on the desk later (fourteen pairs) the tree's median is 1.28x to 1.46x and its p99.9 0.71x to
  1.50x NativeAOT-LLVM's (0.92x to 1.79x with the steps off): below NativeAOT-LLVM's in every run only on the
  quieter desk. Checked (the code is cd93b54): the homelab's suite at 5fb8575 239 passed, 0 failed, 2 known
  gaps, both modes; at 1d4abf2 238 the same; the self-hosted compiler at e92a608 (before the unlink's step and
  the slot walk) built with and without four switches, hello world in 8.2 to 8.6 s either way, 272 MB, the same
  module, self = net byte for byte on test 145; on the desk the five scripts of breaks and tests 12x, 13x, 19x,
  20x both ways. **Queued on the homelab at 25b5f6b and not run when this was written:**
  `tools/overnight-check.sh` (the suite again, the self-hosted compiler with all six switches and selfdet for
  220, 193, 088, 202 to 204, and `tools/stress-trees.sh`); the scene's frames by step on the homelab were not
  measured.

- 2026-11-23, 00:00, claude/selfhost-speed-1 and claude/known-diffs-1 merged (main d430594; their entries above),
  checked again before the merge. Homelab at d430594: suite 236 passed, 0 failed, 2 known gaps, checked and as
  shipped; the self-hosted compiler built (350 s, 32,920,709 bytes), its module byte for byte the .NET-hosted
  compiler's on hello world and on tests 243, 245, 127, 201, 230, 232, 207, 220, 222, 240, 193, 088; hello world in
  17.0 to 21.4 s against 18.9 to 19.3 for the build before and NativeAOT-LLVM's 6.9 to 12.0 on a homelab under
  several sessions' load (its session: 8.20 s against 9.11, quiet medians), peak 272.0 MB against 344.2. Not run:
  stress of 241 to 246. NOT merged: claude/load-flakes-1 (causes and fixes for tests 187, 131, 127, 087 under load
  and a racing List.Add; its suite 237/237 both ways and every stress count clean on the merge e1cb12a): with it
  the self-hosted compiler runs hello world for minutes (two builds here, each stopped after four minutes and more
  at full load; its own session saw the same), which the suite cannot show; its session is to find which of its
  changes it is. From here a merge's self-host check times the self-hosted compiler too. The owner gave until the
  morning (about 08:45) and standing leave to stop stuck jobs of the sessions on the homelab: used once, for this
  session's own runs of the slow builds (selfhost-ab on t17 and t18, t16's build). Sessions through the night, one
  for each goal of the spec not met: claude/selfhost-speed-2 (proof 4: the parse at 2x, the binding at 4.4x, library
  work not needed), claude/tree-3, claude/churn-5 (the header word among it), claude/known-diffs-2 (no known gap
  left), claude/threads-1 (a first comparison with threads; what is off with threads), claude/load-flakes-1.
- 2026-11-23, 03:20, claude/load-flakes-safe merged (main 68f3eb5 with this entry after it): the part of
  claude/load-flakes-1 that touches nothing of the cycle runtime's checks and notes. An async iterator's promise
  counted while it is completed (8577829: test 087's trap under load, 7 of 1,200 checked runs before; test 217
  trapped 24 of 24 without it); the out-of-memory exception not made under the heap's lock (a80a658); the statics
  laid out by cache lines in a program with threads (5837122: test 139 at ten times its length 3.26 to 2.84 s);
  test 072's server on a free port and each suite runner in its own directory (f75de80); tests 088, 135, 127, 187
  with bounded waits where .NET promises no point in time (by .NET's sources: .NET itself gave the usual output in
  400 of 400 loaded runs of each as they were), test 218, tests/Load, tools (oracle-load.sh, selfhost-time.sh, which
  times the self-hosted compiler with a limit, selfdet-tests.sh). By its session, homelab at 68f3eb5: suite 238
  passed, 0 failed, 2 known gaps, both ways; the self-hosted compiler's hello world in 12.7 and 14.1 s on the
  loaded homelab, its module byte for byte the .NET-hosted compiler's on 145, 220, 193 and 088 (112 s). Here on the
  merge: tests 21x, 087, 127 and 145 both ways. Open as before, on main: tests 127, 131 and 187 differ in about one
  loaded run of a hundred. Their fixes (claude/load-flakes-1: a stale note's upkeep) made the self-hosted compiler
  run for minutes: a full check again at every ownership taken or let go under a noted top. A queue that put those
  checks off and paid them down later (NoteDebt) was refused here as deferred work; its session is building the
  number kept right at the one event that changes it, and test 219 still leaks in 2 to 4 loaded runs of 300 to 400
  with that so far.
- 2026-11-23, 05:00, claude/tree-3 merged with main eba0f0d (37c6b15) and checked there (docs/AUDIT.md A3.5,
  round 10, the last two items). Homelab at 37c6b15: suite 241 passed, 0 failed, 2 known gaps, checked and as
  shipped; the self-hosted compiler built (286 s, 33,203,338 bytes), hello world by it in 8.6 to 9.0 s, 272.0 MB,
  the same module with the six switches off; self-1 = net byte for byte on 145, 220, 193, 088, 202, 203, 204;
  stress 200 loaded runs a mode: 139, 192 to 199, 201 to 203 the same in every run, 127 `async 5` / `pending 1` in
  3 and 4 of 200 (the open item 2 of the table; not compared with main the same hour). Frames, main against the
  branch, nine pairs: the tree's median 1.49x to 1.65x NativeAOT-LLVM's on main and 1.30x to 1.50x on the branch
  (1.30x to 1.37x on a quieter desk), its p99.9 below NativeAOT-LLVM's in every pair on the branch (0.70x to
  0.92x), its worst frame above in four of nine on a loaded desk; the other four scenes as on main; bench.mjs
  0.91x to 0.93x on both. Not reached: the tree's median at NativeAOT-LLVM's. Not merged into main by this session.
- 2026-11-23, claude/load-flakes-safe (from main 00d78fe): of claude/load-flakes-1, what is sound and costs the
  self-hosted compiler nothing. Async iterators' promise counted while it is completed (test 087's trap in
  rt.retain: test 217 without it traps in 24 runs of 24, with it in none of 1,200; test 087 checked 2,400 of 2,400,
  7 of 1,200 trapped on main); the out-of-memory exception not made under the heap's lock (test 217 hung in 3 to 5
  runs of 480); with threads the statics laid out by cache lines (test 139 at ten times its length 2.7 to 2.8 s
  against main's 3.1 to 3.3 on the desk, where it had been 3.0 or 3.45 s by how long the program's strings were);
  test 072's server on a free port and each suite runner in a directory of its own. Tests: 088 waits for the
  timer's first tick; 135's far task is 2 s off and the first to complete is checked (2,400 of 2,400 loaded
  runs); 127 and 187 wait, bounded, before they count; 218 (test 139 in rounds). These four changes rest on
  .NET's sources, not its output: .NET gave the usual output in 400 loaded runs of each test as it was
  (tools/oracle-load.sh, tests/Load). On the homelab at 68f3eb5: suite 238 passed, 0 failed, 2 known gaps,
  checked and as shipped; the self-hosted compiler (tools/selfhost-time.sh, new: its time on hello world with a
  limit, since a change that makes it never end does not show in the suite) 12.7 and 14.1 s on hello world with
  the homelab loaded, and byte for byte the .NET-hosted compiler's module on tests 145, 220, 193 and 088 (112 s).
  Not on this branch, open: the leaks of tests 127, 131 and 187 under load (the row above); test 139's two nodes
  left without the engine's stack checks, not seen again in 4,000 runs, cause not shown.
- 2026-11-22 to 23, the churn loop, round 5 (branch claude/churn-5, from claude/round-9 d430594; docs/AUDIT.md A3.5
  "The churn loop, round 5"). Asked for: churn's median at or below NativeAOT-LLVM's, or what is left shown to be a
  floor. **`frames.mjs` churn 1.35x-1.36x to 1.17x-1.22x (0.068-0.070 ms to 0.063-0.064 against 0.050-0.054; by
  `frames-ab.mjs`, eight rounds, 0.0652-0.0681 to 0.0589-0.0631), its mean frame 1.24x-1.25x to 1.12x-1.17x; not at
  NativeAOT-LLVM's.** The other scenes and `bench.mjs` as they were (0.91x and 0.93x, `bench-ab.mjs` 0.991x of before).
  In: (1) a list's setter goes into loops as its getter does (`CS2WASM_INLINE_READERS` 80 to 96: it was 81 to 90
  instructions and stayed a call; `churn-move`, the store through the list 55 us a frame to 46 against
  NativeAOT-LLVM's 40.5); (2) a value stored and read back is the stored value (`CS2WASM_NO_STORE_FORWARD=1`,
  Wasm/SameBaseLoads.cs: `--p.Life > 0` read the field twice after storing it; test 209, and
  `CS2WASM_SAME_BASE_WITHOUT=...` takes each of the pass's rules out for tests 207 and 209). Built and **off by
  default** (`CS2WASM_COLD_EXITS=1`, Wasm/ColdExits.cs, test 208, `tools/cold-exits-proof.sh`): the calls of a
  count's other path written after the loop. Found with the engine's own listing (`node --print-wasm-code`): a
  call that comes back, on a path never taken, makes V8 write every value that is alive across it to the stack
  where the value is made, five stores a turn in the counted store's loop; without them the store is 40.5 us a
  frame for 46 (`tools/churn-count.cs`), but each turn that leaves the loop pays 5 to 7 ns, so a loop whose other
  path is taken once in 4 to 50 turns is up to 17% slower (`tools/drop-range.cs`), list_sort 3 to 4% slower, and
  the churn scene, where one particle in 48 ends in the loop, the same with and without. Tried and not kept (branch
  claude/churn-5-free-inline): an object's end in the loop without a call (the frees' usual cases written out, the
  rare calls outside): the tool's loop 2 us shorter, the frames scene not; the mark's bit tested before
  rt.on_destroy: nothing; the NaN test as a select on the bits: slower. **What is left, by measurement** (us of a
  63 against 51 to 54): the particles that end inside the loop 6 (`tools/churn-deaths.cs`: 15 ns each there, 7 after
  the loop, under 1 where every object ends: an unpredicted branch and a path run once in 48 turns; NativeAOT-LLVM
  pays it in its pauses, 5.5 to 6 us a frame of its mean); the NaN tests 2.8 to 3.1 (the owner's ruling); the two
  counts of a moved reference 5.5, 2.4 above the card mark (no sound proof of a move); their tests 1.5 and the
  engine's stores for the calls 2 to 3.7; the header's third word 1.5 to 2, **not built**: the pinned bit is read
  on fifteen arena paths before the type is known, the mark bit at every end, the count's sign and the type word
  have no bit to spare, SPEC 7.3's hash is a counter kept in the word (AUDIT has the design that is left: a
  two-word header for hierarchies without a cycle-capable or finalizable class). The loop's shape: the read loop
  rotated by hand is slower now on both sides, and the scene's loop without its move is level with NativeAOT-LLVM's
  (29.4 against 29.7): nothing to build. Hello world 27,783 bytes. **Checked** on the homelab: the suite
  at ba966f6 239 passed, 0 failed, 2 known gaps, checked and as shipped; the self-host check at cb43eb9 (new:
  `tools/selfhost-check.sh`): built in 246 s, 32,970,943 bytes, hello world in 8.29-8.34 s (8.25-8.35 with the two
  changes switched off), peak 272.0 MB, and 145, 220, 193 and 088 each "self-1 = net, byte for byte"; stress:
  208, 209, 260 and 139 the same in every one of 200 loaded runs a mode, 127 differed in 3 of 400 (its "async 5 / pending 1", as on main); `tools/field-hoist-proof.sh` with the setter inlined: as before. Not built: the header's word. Not
  checked: the suite with ColdExits on at the last commit (tests 190 to 209 on the desk, the whole suite at its
  first commit).
- 2026-11-23, 06:30, claude/known-diffs-2 merged with main 4cf95fe (1cae0b2; the entry above, docs/AUDIT.md A3.5
  "Checked at the merge"). Homelab at 1cae0b2: the suite 255 passed, 0 failed, no known gap, checked and as
  shipped (1,088 s); the self-hosted compiler built (251 s, 33,041,673 bytes), hello world in 13.1 to 13.6 s on the
  loaded homelab, peak 272.0 MB, byte for byte the .NET-hosted compiler on 145, 220, 193 and 088. Against main
  4cf95fe on the desk, in turn, five rounds: 9.68 to 12.12 s for 9.80 to 12.19, the same module: level. The last
  session's uncommitted DrainKills change: parked untested on claude/known-diffs-2-drainkills. Stress of the new
  tests and 127, 139 (200 loaded runs a mode, the homelab, at 1cae0b2): 244, 247 to 249, 265 to 274 and 139 the
  same in every run of both modes; 127 checked 1 of 200 "async 5 / pending 1" (the load flake on record), as
  shipped none.
- 2026-11-23, 05:40, main 4cf95fe checked whole on the homelab (claude/main-check-1): suite 241 passed, 0
  failed, 2 known gaps, checked and as shipped; tools/selfhost-check.sh: the self-hosted compiler built (247 s,
  32,978,189 bytes), hello world in 8.32 to 8.50 s (peak 272 MB), its module byte for byte the .NET-hosted
  compiler's on 145, 220, 193 and 088. The churn merge was checked on its own branch by its session and on the
  merge only by tests here until this run. The standing leave to stop stuck jobs on the homelab was used three
  times in the night: twice for this session's own leftover self-host runs of branches that were not merged, once
  (04:25) for four queued jobs of the load-flakes session that it had superseded and was not allowed to stop.
  At about 03:44 the overnight sessions' model began to fail (overloaded, then its limit): tree-3,
  selfhost-speed-2, load-flakes-1, known-diffs-2 and threads-1 were taken over in their worktrees by sessions on
  another model; churn-5 had handed back first.
- 2026-11-23, 06:30, claude/tree-3 merged with main again (e682484: main 4cf95fe, claude/churn-5's steps) and
  checked there (docs/AUDIT.md A3.5 round 10, its last items). Homelab at e682484: suite 244 passed, 0 failed, 2
  known gaps, both modes; the self-hosted compiler byte for byte on 145, 220, 193, 088, 202, 203, 204, 208, 209,
  hello world by it in 10.2 to 11.4 s on a loaded homelab (8.6 to 9.0 s at 37c6b15 before; the build without
  the branch's six steps as slow; not timed against main side by side), 272.0 MB; stress of 139, 192 to 199,
  201 to 203 the same in every run, 127's open line in 3 and 1 of 200 (main 4cf95fe the same hour: 3 and 4 of
  400). Desk: the five scripts of breaks exit 0; tests 19x, 20x, 26x, 145 both modes. Frames, main 4cf95fe
  against the merge, six pairs on a loaded desk: tree median 1.53x and 1.54x on main against 1.33x and 1.35x in
  the two quiet pairs, lower on the branch in five of six; churn as on main. Not merged into main by this
  session; the tree's median is not at NativeAOT-LLVM's.
- 2026-11-23, 07:10, claude/known-diffs-2-main2: claude/known-diffs-2 merged with main 4a42328 (claude/tree-3).
  Merged so, tests 248, 265 and 266 failed (objects a finalizer brings back and objects behind weak handles stayed
  alive); of tree-3's switches only `CS2WASM_NO_PLAIN_PARTS=1` put them right, so **the plain parts are off by
  default** (`CS2WASM_PLAIN_PARTS=1` turns them on; the cause not found: docs/AUDIT.md A3.5, "Known differences
  from .NET, second round", the merge with main 4a42328). On the desk at that commit: the tests matching 19, 20,
  24, 26, 27, 145, 127, 139, 187 and 088 pass checked and as shipped; hello world 27,786 bytes as shipped; frames,
  off and on in turn, two rounds: tree median 0.038/0.037 ms off for 0.039/0.038 on, p99.9 2.01/2.03 for
  1.97/1.93, the other scenes the same. The homelab's suite, self-host check and stress are those of 1cae0b2 (the
  entry above); nothing of this merge was run there.
- 2026-11-23, programs with threads (branch claude/threads-1 from claude/round-9 d430594; not merged into main;
  docs/AUDIT.md A3.6). The measurement that was missing: `node tools/frames-threads.mjs`, the frames scenes in a
  program with threads (one idle worker; N workers with private worlds; work handed between threads; the churn's
  and the tree's parts alone), against the same work without threads, cs2wasm's and NativeAOT-LLVM's
  (NativeAOT-LLVM's WebAssembly with threads does not link here: its runtime libraries are built without atomics).
  Found: having one idle thread cost the churn scene 29x (1.99 ms a frame against 0.068), a worker's private churn
  33x, because the thread escape analysis (Compiler/Sharing.cs) answered "every type" for any program with a
  thread. Seven places that stood for any object now say where their values come from (struct refs that keep their
  type, slots only ever stored null, stores by address into arrays, captured locals and parameters, arrays of no
  element, statics only the engine thread's code names, delegates by the lambda they were made of), and three
  things the types then allowed are on (a list's foreach and a getter borrow from objects no other thread can
  reach; the runtime's own lists are out of the store summary, which had every release count as a store of an
  element). Median frame, before -> now (NativeAOT without threads): churn with one idle worker 1.99 -> 0.100 ms
  (0.050), collections 0.065 -> 0.012 (0.007), strings 0.032 -> 0.013 (0.008), tree 1.21 -> 0.120 (0.034),
  bigdrop 0.031 -> 0.009 (0.003); a worker's private churn 2.33 -> 0.107 (0.048), four at once 3.12 -> 0.116,
  eight 3.15 -> 0.158; a private tree 0.160 -> 0.022 (0.006), eight 0.246 -> 0.056; work handed to one, two, four
  workers 0.170 -> 0.113, 0.231 -> 0.081, 0.405 -> 0.264 (0.102 done by the main thread itself). Worst frames
  below NativeAOT's except the tree's rebuild (17.4 ms against 3.7; 35.3 before) and the private trees. Programs
  without threads are compiled byte for byte as by the base (145, 220, 193, 201, tools/frames; hello world 27,784
  bytes by the command line): `node tools/frames.mjs --runs 3 --js-young 32` gives the mean frame 0.74x NativeAOT's
  over the five scenes (medians churn 1.35x, collections 1.02x, strings 0.88x, tree 1.57x, bigdrop 0.97x; worst
  frames 0.12x to 0.81x), `node tools/bench.mjs` 0.95x, 16 MB against 71.3, 335.5 KB against 1,328 + 170.
  Tests 275 to 279; `bash tools/thread-escape-breaks.sh`: each with CS2WASM_SHARE_CHECK=1 prints the same, each
  with its rule weakened is stopped by the check (seven weakenings), each with its rule off prints the same. A
  fault of the analysis found by test 278 on the way (a pointer store's types taken with the first visit's filter).
  Measured and taken out again: field loads read once by a loop and plain counts written out in loops (no gain);
  the groups' stripes left out when no cycle-capable type is reached (never the case: Thread is one).
  Open, by cost: the groups with threads (a tree's build 6x the program without threads; the node path is off;
  the runtime lock at a member's first and last outside reference; private trees scale 3.2 at eight threads);
  types reached by their type only (the churn's drop is the atomic release of each particle's string, 316 us
  against 40); a delegate with no tag is any of its type; the threaded self-hosted compiler (check 5) not
  touched. `tools/thread-board-hang.cs` hangs one run in six, with the base's compiler too.
- 2026-11-23, claude/threads-1 merged with main 4cf95fe (612cd56; docs/AUDIT.md A3.6, "On the merge"). Programs
  without threads byte for byte main's (145 27,786 bytes, 220, 193, 201, 208, 209, tools/frames). frames-threads,
  main's compiler -> this branch's, median ms (NativeAOT-LLVM without threads): idle churn 2.02 -> 0.096-0.100
  (0.050-0.052), idle tree 0.94-0.99 -> 0.122-0.126 (0.033-0.035), private churn at eight 3.09-3.27 -> 0.160
  (0.049), private tree at eight 0.243-0.259 -> 0.056 (0.006), shared at four 0.26-0.30 and 0.18-0.33 from run to run on either (0.105). The desk on
  the merged code: thread-share-check 61 of 61 the same, thread-escape-breaks as before, 275 to 279 300 loaded runs
  each all the same, 139, 218, 215b, 131, 127 200 each all the same with this branch's compiler and with main's,
  187 3 of 200 with each. The homelab on the merged code (612cd56 and its docs commit c1f0859): suite 246 passed,
  0 failed, 2 known gaps, checked and as shipped; `tools/selfhost-check.sh`: the self-hosted compiler built in
  431 s (33,455,890 bytes), hello world in 8.3 to 8.5 s, peak 272.0 MB, self-1 = net byte for byte on 145, 220,
  193 and 088; 139 without the engine's stack checks 200 loaded runs a mode all the same; 276 200 a mode the same.
  Not merged: main 4a42328 (claude/tree-3's foreach over a List<T>, which meets this branch's list loops), since
  this check had started. Not run on the merged code before the hand-back: the threaded self-hosted compiler
  (`tools/thread-scale.sh`), the homelab's stress of 127, 218, 215b, 187, 131, 275, 277 to 279 (the desk's above).
- 2026-11-23 morning, claude/selfhost-speed-2 checked again after main was merged (4746983 on main 4a42328;
  docs/AUDIT.md, the entry's item "Checked again after main was merged"). One homelab turn
  (`tools/selfhost-speed-2-verify.sh`, others beside). The suite 245 passed, 0 failed, 2 known gaps, checked and
  as shipped. The self-hosted compiler built (253 s, 33,265,837 bytes): hello world 6.77 to 7.20 s, 241.8 MB
  (8.11 to 8.15 s, 272.0 MB with both switches; the same module); NativeAOT-LLVM's build 3.76 to 3.96 s;
  `tools/selfdet.mjs` self = net byte for byte on 145, 220, 193 and 088. The snapshot (`CS2WASM_SNAPSHOT=<file>`,
  off unless set): `tools/selfsnap.mjs` every module on a snapshot byte for byte the .NET-hosted compiler's (145
  five times, then 220, 193, 088, 139 and 197 twice each, hello world again on the snapshot 088 left);
  `tools/selfsnap-stale.mjs` all eleven steps as expected (a new version of the source read anew; a source that
  needs System.Linq; another build of the compiler, another reference directory, a reference assembly's time of
  change and a file added to that directory each let the snapshot go); `tools/snapshot-check.sh` as expected.
  Not covered: a file rewritten with the same size and time of change (by design), `-r` references (not tried).
  Hello world in turn, five rounds, loaded: from the start 9.61 s median (9.39 to 10.47), on a snapshot 3.54
  (3.41 to 3.83), NativeAOT-LLVM's build 5.24 (5.15 to 5.92). **Not finished when written** (still running in
  the same turn): test 088 timed from the start and on a snapshot beside NativeAOT-LLVM's; stress of 250, 127 and
  139; `tools/libs-rules-check.sh`.
  Later in the same turn (ended 08:15): test 088 in turn, three rounds: from the start 75.19 to 77.38 s (median
  76.44), on its own snapshot 55.11 to 55.31 (55.30), NativeAOT-LLVM's build 91.13 to 105.17 (94.72), every module
  byte for byte; stress of test 250, 200 loaded runs a mode, 0 bad both ways (the lines for 127 and 139 were cut
  by the chain's `tail -30` and are not known); `tools/libs-rules-check.sh` passed: 225 programs the same module
  against both switches, 0 differ, and the host's snapshot rules as expected.
- 2026-11-23, 08:45, main's last code commits checked whole on the homelab. f549fb0 (known-diffs-2 with tree-3,
  plain parts off): suite 258 passed, 0 failed, no known gap, both ways; self-host check all the same (hello world
  10.2 to 10.7 s on the loaded homelab, 272 MB; byte for byte on 145, 220, 193, 088). 440bb82 (with threads-1 and
  selfhost-speed-2): suite 264 passed, 0 failed, both ways (896 s); self-host check all the same: built in 275 s
  (33,809,524 bytes), hello world 7.30 to 7.56 s at 241.8 MB, the switched build the same module, byte for byte on
  145, 220, 193, 088. The morning report is docs/REPORT-2026-11-23.md.
- 2026-11-23 afternoon, claude/netwasm-suite (d508471): the harness that runs NetWasm's suite against il2wasm with .NET
  as the oracle (`tools/netwasm-suite.mjs`; docs/AUDIT.md, the entry "NetWasm's suite as il2wasm's main suite").
  Homelab: 474 cases (78,073 inputs) extracted from the pinned NetWasm d0cecda45cc8, 473 built, and 473 stable over
  three .NET win-x86 runs. The `dotnet-standin` run passed 473 of 473 checked and as shipped, 4 at once each
  (`all` takes about 2 minutes). Not run: il2wasm itself (the IL front end cannot compile these yet). The
  runtime, compiler and library are unchanged, so the suite, stress and self-host checks do not apply. Desk: the
  .NET runs opened 4 JIT-debugger dialogs (library:System.Runtime.Tests calls Debugger.Launch/Break). Since then the
  .NET runs of the cases are refused off the homelab.
- 2026-11-23, 17:10, claude/il-frontend (IL front end steps 2 and 3): NativeAOT's type system for wasm32; IL to an
  SSA IR with exception regions; naive counting as an IR pass; structuring and lowering to the existing back end.
  tests/Il2Wasm.IL.Tests 67 of 67 (8 programs equal to .NET, 0 blocks live at the end, optimization on and off), on the
  stub CoreLib. The IL CoreLib's hello world compiles only leniently (335 gaps: Type objects, 28 intrinsics, ldftn).
  Not run: the homelab suite, stress, self-host check, frames and bench (the old front end and the back end are
  unchanged apart from host/run.mjs's IL2WASM_PRINT_LIVE; 145_size_hello passes on the desk).
- 2026-11-23, claude/il-corelib: il2wasm step 1, the core library as IL (docs/CORELIB.md, AUDIT "il2wasm step 1").
  - CoreLib builds ahead of time to IL in about 10 s, byte-identical on rebuild.
  - The stock pack chosen is Mono.browser-wasm 11.0.0, built from runtime deae2e32 = v11.0.0.
  - The stock libraries reference 12,172 types and 29,032 members of CoreLib, and none is missing or not public.
  - The stock libraries have 28 native entry points.
  - Hello world reaches CoreLib and System.Console only.
  - Old-compiler hello world: byte-identical before and after the one runtime rename.
  - Not run: the homelab suite and the self-host check (the rename only).
- 2026-11-23, claude/il-corelib: the runtime side of the stock libraries' native calls (docs/CORELIB.md section 7). All 56 stock P/Invokes and internal calls bound (BuildCoreLib bindings). Deflate is a C# port of zlib-ng 2.2.5 and is identical to .NET's shipped zlib-ng on 30,690 cases (611,069,290 bytes, every return code and position). Inflate is the managed inflater: same output and result on 3,000 streams, per-call positions differ in 737. errno is now Emscripten's. Desk suite 145, 070, 088, 152, 220, 193 pass both ways; hello world unchanged. Homelab suite and self-host check not run (left to the coordinator).
- 2026-11-23, claude/il-corelib: zlib-ng 2.2.5's inflate ported to C# (Natives/ZLibNgInflate.cs), replacing the managed inflater. Against .NET's native zlib-ng: 72,240 inflate runs identical (2.57 GB output, 833,572 calls: return codes, positions, 18 error messages), including truncated, corrupted and chunked streams. Deflate still identical on 30,690 cases. Homelab suite and self-host check not run on this commit.
- 2026-11-23, claude/il-frontend-2 (IL front end on the IL CoreLib): Console.WriteLine(42) on claude/il-corelib's
  framework compiles strictly and prints 42 (1,948,641 bytes; no leak per call, 44 objects live at exit). NetWasm suite,
  desk, shipped mode: 161 passed, 217 failed, 71 did not compile, 1 timed out (the IL front end's first number). Stub
  tests 69 of 69. Not run: homelab suite, stress, self-host check (the back end, runtime and old front end are
  unchanged; the new code is a separate project).
- 2026-11-23, claude/il-generics (IL front end: reachability and reflection records; docs/AUDIT.md, the entry
  "reachability to a fixed point, substitutions, assemblies' reflection records"): SCCP with a second import, ILLink
  substitutions, pure calls, whole-program rounds (unwritten statics, constant arguments, return values, field values,
  virtual-call values), assembly records with attributes and manifest resources, reflected arrays. Hello world
  1,949,547 -> 845,943 B (before merging main); 970,075 B on merged main (2,145,227 B with every rule off). Tests 85
  of 85. NetWasm suite, desk, shipped, with the merged 4d29bf0 (before the assembly records): 224 passed, 214 failed, 11 did not compile, 1 timed out (main reported 186). Not run: homelab suite, stress, self-host check (the old front
  end, the back end's passes and the runtime are unchanged; the one Wasm/ change is the IL2WASM_SIZES_FILE report; 145
  passes both ways). Not done: generic sharing, Delegate.Method/DynamicInvoke/DelegateArity.
- 2026-11-23, claude/il-runtime (the IL front end's runtime semantics; AUDIT "IL front end: the runtime's
  semantics"): finalizers and weak references on the destroy path, static constructors with .NET's triggers and
  TypeInitializationException, Activator.CreateInstance, two-pass filters, stack traces from the PDB,
  [UnmanagedCallersOnly] exports with exception reporting, and a checked build that differs from the shipped one
  (poisoned freed memory, traps on a release too many). The checked build found three use-after-frees, fixed:
  pinned locals, the runtime's int addresses of objects, rethrow. NetWasm suite on the desk: 257 passed in both
  builds at a04d832 (main had 186). Homelab: old suite 264/264 both modes; self-hosted compiler byte for byte on
  145, 220, 193, 088, hello world 7.1 s. Not done: threaded static constructors (no threads in the IL front end),
  group weak references (no groups). Not run: stress, frames.mjs, bench.mjs (old front end and back end unchanged).
- 2026-11-23 evening, claude/il-codegen (IL importer and lowering correctness, AUDIT "IL front end: importer and
  lowering correctness"): NetWasm suite, desk, shipped mode, each run whole: 163 passed at main 179edef, then 186,
  226, 255 (d2ce0aa, now on main), 262 (with claude/il-runtime a04d832), 263 (f96d770), 265 (162ff60). In: generic virtual
  methods, multi-dimensional arrays, calli with structs, float remainder, [UnsafeAccessor], initblk/cpblk, array
  covariance and ldelema checks, Nullable boxing, delegate fixes, arrays' generic interfaces, the double release
  of a boxed struct's references, string.Empty, addresses and spans keeping their objects. tests/Il2Wasm.IL.Tests
  101 of 101 (18 new programs on the real CoreLib); TypedReference. Not run: homelab suite, stress, self-host check, frames, bench
  (the old front end and back end passes are unchanged; the one library change, Boundary/CompilerHelpers.cs, is
  reached only by the IL front end).
- 2026-11-24, claude/il-codegen-speed (IL codegen for speed, AUDIT "IL code generation for speed"): box folds in the
  importer, struct field reads in place, frames given back at a catch so frame-only functions have no handler, the
  back end's frame promotion for the IL front end, the IR inliner taking reference-free struct arguments and
  results, and the CLR's unbox rules for enums. Homelab il-speed, bench geometric mean against NativeAOT-LLVM wasm:
  2.71x → 1.37x planned (nbody 15.1x → 1.45x, list_sort 3.51x → 0.72x), 4.44x → 2.71x naive. Frames mean
  2.33x → 1.88x planned, no spikes above NativeAOT. NetWasm suite, desk, shipped, all recompiled: 284 (none lost
  against 283). IL tests 167/167 naive, 187/187 planned and verified. Not run: self-host (the IL front end does not
  self-host yet), stress, homelab suite.
- 2026-11-24, claude/plan-2 (planner second stage, AUDIT "Planner, second stage"): Init stores, the returned-span
  keeper's count moved into its hold, takes per entry, Unsafe.As forwarding, persistent statics and their getters
  borrowed, frame objects; a terminator-borrow use-after-free fixed and the verifier made to see it. Removal share over
  S1-BARS's 34 programs: mean 78.8% (was 72.5%), summed 71.2% (64.5%). IL tests 188/188 planned and verified (and
  checked), 166/166 naive; NetWasm suite 283/160/7, none lost. Not run: speed, homelab suite, stress, self-host check.

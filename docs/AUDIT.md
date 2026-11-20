# cs2wasm: audit findings and plan

Audit of 2026-11-18 against `docs/SPEC.md`, the Invisible API design (`bazoodle/docs/invisible-api.md`) and the three
production goals:

1. The self-hosted compiler and Roslyn are each faster than their original counterparts.
2. Any output is spikeless and more memory-efficient than NativeAOT's, with speed at least equal.
3. The plugin system supports every stated and potential need of the Invisible API.

Each problem below gives its **source** (where in the code it comes from, and why) and the **fix**. Evidence is
labelled: **[run]** reproduced by running, **[read]** found by reading the code, **[inferred]** reasoned from the code
and not yet reproduced. Part B is the ordered plan.

State at the audit: suite 139 passed, 0 failed; `tools/bench.mjs` 0.95x time (5 of 9 kernels slower), 32.0 MB against
71.3 MB, 340 KB against 1,328 KB.

---

## Part A. Problems and their sources

### A1. Memory safety (blocks everything else)

**A1.1 Use-after-free when a local is rewritten during its own call. [run]**
`Use(x, x = y)` prints 50606 (expected 50303); `UseRef(p, ref p)` with the callee clearing the ref prints 606
(expected 404); `UseOut(q, out q)` prints 606 (expected 707).
- Source: `LoadVar` returns `Own.Stable` for a wasm local and for an address-taken frame local
  (`Compiler/Expressions.cs:310-315`, `Compiler/FunctionCompiler.cs:417`), and `ToCallSafe` adds no count for a
  `Stable` value (`Compiler/Calls.cs:384-395`). "Stable" assumes nothing writes the local while the borrow is live,
  but a later argument of the same call, or the callee through `ref`/`out`, can.
- Fix: a local is `Stable` for a call only if no later argument assigns it and it is not passed by `ref`/`out` in the
  same call and its address has not escaped. Otherwise retain it for the call (SPEC 4.1 "nothing is rejected": a
  run-time count where Lobster would report an error).

**A1.2 A finalizer that stores `this` leaves a dangling reference. [run]**
- Source: after the finalizer the count is forced to 0 and the block freed (`Compiler/RuntimeGen.cs:637-651`).
- Fix: after the finalizer returns, read the count again; if something now refers to the object, keep it alive and
  mark it finalized (as .NET does: no second finalization unless re-registered).

**A1.3 Weak slots can keep a freed address under threads. [read]**
- Source: `ObjectMarks.OnDestroy` returns early when `s_busy != 0`, read without the lock
  (`Upstream/System.Private.CoreLib/Boundary/ObjectMarks.cs:58`). A dying target then skips clearing its weak slots.
- Fix: take the lock in `OnDestroy` (or make the busy test and the clearing one atomic step).

**A1.4 Arena unlink guesses inside/outside from counts. [read]**
- Source: slot bits cover only the first 16 words; array elements and later fields are guessed
  (`Boundary/Runtime.cs:1821-1845`, `SlotIndex` 1368-1373). `ArenaLinkCore` returns early for busy arenas and pinned
  objects (1750-1757), leaving a counted reference between two members.
- Fix: record inside/outside per slot for every slot (a side bitmap for arrays and long objects); make link and
  unlink symmetric by construction, not by the `t_prevSlot` hand-off.

**A1.5 "Written only by constructors" edges. [read]**
- Source: the ref-return case is dead code (`&& false`, `Compiler/Arenas.cs:192`), so a private field that escapes by
  ref-return counts as immutable; a cycle through it is never put in an arena and leaks.
- Fix: treat any field whose address escapes as written after construction.

**A1.6 No stack limit check. [run + read]**
2 MB of recursive `stackalloc` on a 1 MB stack traps only after running down through the statics.
- Source: the frame prologue subtracts from the stack pointer with no comparison (`Compiler/FunctionCompiler.cs:205-206`);
  `stackalloc` likewise (`Compiler/Expressions.cs:3196-3224`). The stack sits directly above the statics.
- Fix: compare against the stack's low limit in the prologue of functions with a frame and at `stackalloc`; on
  overflow, trap (SPEC 3: "stack overflow ends the instance"). Keep a guard gap. Honour `Options.StackSize` in
  `__thread_entry` (`RuntimeGen.cs:875`).

**A1.7 The suite cannot see any of the above. [run]**
- Source: the runner compares console output only (`tests/Cs2Wasm.Tests/Program.cs:85-88`); nothing poisons freed
  memory or counts live blocks; the WASM validator runs only under `CS2WASM_VALIDATE=1` and never fails a build
  (`Wasm/WasmModule.cs:86-96`); there is no internal assertion anywhere.
- Fix: plan step 1.

### A2. Speed (goals 1 and 2)

**A2.1 The premise.** Removing the GC removes pauses and memory overhead, not mutator cost. A generational GC
allocates by moving a pointer and pays nothing for a dead object; counting pays at each retained copy and touches
every object it frees. Speed has to come from doing less counting and from better code, not from the absence of a
collector. **[reasoning]**

**A2.2 No IR between the operation tree and stack code. [read]**
- Source: `FunctionCompiler` emits `List<Instr>` directly (`Wasm/Ir.cs:99,129`); the optimizer is about 15 passes
  that pattern-match stack code (`Wasm/WasmModule.cs:159-250`), some on fixed instruction shapes
  (`Wasm/InitChecks.cs:22`) and on function names (`"rt.release"`, `Inliner.cs:158-161`).
- Consequence: no bounds-check removal, no hoisting of loads out of loops beyond `LoopLoads`, no counting removal by
  dataflow, inlining only of callees that cannot throw (`Wasm/Inliner.cs:12-20`).
- NativeAOT's module has been through LLVM before the same engine runs it, so the engine cannot close this gap for us.

**A2.3 Devirtualization is local. [read]**
- Source: only sealed receivers (`Compiler/Calls.cs:210`) and the exact type of a `new` (`EscapeAnalysis.cs:14`);
  everything else calls a dispatch stub (`Calls.cs:564`). `shapes` is 1.25x.
- Fix: class-hierarchy analysis over the constructed types (the reachability pass already knows them): a slot with
  one implementation is a direct call; with two or three, a type test and direct calls.

**A2.4 Counting removal is at 53-57%, against Lobster's 95%. [read; last recorded figure, commit 56e4ff3]**
- Source: ownership is decided per operation while lowering, with whole-program store summaries by field key (type
  based, not per object: `Compiler/FieldBorrows.cs:344-448`). Ownership specialization covers only "consumed"
  parameters on direct calls (`Compiler/OwnedParams.cs:7-36`). Getters return borrowed, callers re-retain
  (`Driver.cs:570`).
- No current figure is in the repo; `docs/STATUS.md` (cited at `Arenas.cs:17`) was deleted.

**A2.5 Thread mode is one switch for the whole program. [read + measured in commit 75e0f72]**
- Source: `SharedCounts.cs:99` ("no object is marked shared"), `RuntimeFns2.cs:60-127`, `Driver.cs:143-149`: if a
  thread start is reachable, every count is a compare-and-swap loop and every heap reference read is
  `rt.load_retain`; the heap stops using pages and never joins freed blocks (`Runtime.cs:412-414, 697`).
- Cost: the self-hosted compiler takes 19.7 s threaded on one thread, 12.6 s built without threads, NativeAOT 6.8 s.
- SPEC 4.5 asks for atomic counts only on objects escape analysis finds shared.

**A2.6 Every free touches the object. [read]** `linked_list` 1.16x, `dictionary` 1.13x.
- Source: inherent to counting; made worse by no reuse of a just-freed block for the next allocation of the same
  size, and by the count-word sign test on every retain/release once any cyclic site exists (`RuntimeGen.cs:661-674`).

**A2.7 The compile is done twice for a threaded program. [read]**
- Source: `Driver.ThreadsSeen` restarts with a new `Driver` (`Api.cs:82-90`). Also: the static-initializer loop
  recompiles to a fixpoint (`Driver.cs:1145-1168`), 49 `Descendants()` re-walks, string keys built on hot paths,
  linear `Functions.FirstOrDefault(f => f.Name == …)` scans, about 8 whole-module sweeps of Peephole and Locals.
  These are the compiler's own speed, which is goal 1.

**A2.8 The measurement is too weak to steer by. [read + run]**
- Source: nine kernels in 213 lines (`tools/bench/Programs.cs`); NativeAOT side is net10.0 with a floating
  `10.0.0-*` package (`tools/bench-naot-wasm/bench-naot-wasm.csproj:6,21`) against .NET 11; "peak memory" is the
  final linear-memory size (`tools/bench.mjs:75-79`); the self-host comparison is one program
  (`Console.WriteLine(42)`), with size and memory unrecorded; numbers live only in commit messages.

### A3. Spikes and memory (goal 2)

**A3.1 A store can walk a graph. [read]**
- Source: when a plain object joins an arena, everything it transitively holds is visited and linked
  (`ArenaLinkJoined`, `Runtime.cs:1305`; `ArenaLinkContents` 1953).

**A3.2 Arenas only merge. [read]**
- Source: `ArenaMerge` (`Runtime.cs:1322`); the only split is a single member with no inside references leaving
  (`TryLeave` 1510). So the last release visits every member, in several passes with finalizers
  (`ArenaFree`/`ArenaDestroyFrom` 1991-2092), and a detached sub-cycle stays until the arena dies
  (`Runtime.cs:1073-1076`), for the program's life if the arena hangs off a static.
- SPEC 4.3 asks for arenas "scoped to things that end", with inside references as uncounted indices; the built
  design is a run-time union structure with counted pointers (`Runtime.cs:1055-1063, 1772-1808`).

**A3.3 Freeing a large graph is one synchronous cost. [read]** Inherent to immediate release; predictable, unlike a
GC pause. Whether it counts as a spike is the owner's decision (deferring is ruled out by the owner).

**A3.4 Memory under threads only grows. [read]** No pages, no joining of blocks, 256 KB cached per thread
(`Runtime.cs:412-414, 697`). The self-hosted compiler reaches the 1 GiB limit on a real input
(`tests/Godot/proof5_hello2d/README.md:95-97`).

### A4. Plugins (goal 3)

**A4.1 Generated code cannot reach private members. [read]**
- Source: `AddSource` builds a separate compilation that references the game (`Compiler/Plugins.cs:552-559`), with
  default access checks; field `[UnsafeAccessor]` is refused (`DynamicDelegates.cs:103`).
- Needed by: gates, solvers, per-tick steps, typed get/set (Invisible API 3.1, 7, 8.8).

**A4.2 No hook sees or changes a value. [read]**
- Source: hooks take `()`, `(int)`, `(object)` or `(object, int)` (`Instrumentation.cs:106-119`).
- Needed by: sending a branch outcome and following the told outcome (3.3), redirecting a draw to the private
  random stream (3.2), treating an `await` result as private (4), wire rounding (3.4), field limits (17.3).

**A4.3 Write hooks are per member, not per write. [read]**
- Source: `WriteHooks` is keyed by member symbol with one tag (`Compiler/Plugins.cs:561-568`). The design classifies
  each write statement (3.3). `InstrumentBefore` is per site but runs before and has no receiver.

**A4.4 Collection entries are invisible. [read]**
- Source: `Accesses` records field, property and event references only (`Compiler/Plugins.cs:193-204`);
  `Scores[p] = 1` is a read of `Scores` and a call. `Dictionary<Player, T>` is the design's per-player mechanism
  (5, 6, 8.2).

**A4.5 Analysis and instrumentation disagree. [read]**
- Source: `KindOf` counts `ref`/`out` arguments and deconstruction as writes (`Compiler/Plugins.cs:216-232`);
  `WriteTarget` hooks only `=`, compound, `++`/`--`, `??=` (`Instrumentation.cs:24-31`). A write classified as an
  interaction can go unmarked: a wrong game, not just bandwidth.

**A4.6 Closed forms are too weak. [read]**
- Source: one affine rule per symbol with exactly one write, outside branches, no calls
  (`Compiler/Plugins.cs:428-509`); loops found by block ordinal. Fails the design's own examples: the spawner timer
  (two writes), velocity/position (coupled), `Lerp` (a call), piecewise solving. The formula is a string.

**A4.7 Suppressed clones leak effects and cannot be called. [read]**
- Source: virtual calls go to the unsuppressed stub before the clone test (`Calls.cs:564`); lambdas are not cloned;
  the clone exists only as a named export (`Compiler/Plugins.cs:579-584`).

**A4.8 Conditions, aliasing, unsure spots. [read]**
- Conditions are syntactic and per method (`ConditionsOf`, `Compiler/Plugins.cs:235-257`): no early returns, no
  call-path conditions.
- `MayAlias`/`PointsTo` are type compatibility (`Compiler/Plugins.cs:512-548`), not the ownership analysis.
- Delegate calls are absent from the call graph; reflection warnings never reach the plugin.

**A4.9 Plugins run after code generation. [read; soundness part inferred]**
- Source: `Driver.cs:1134-1136`; hooked methods are recompiled (`Compiler/Plugins.cs:63-76`). Escape analysis never
  sees hooks and its memo is not invalidated, so a hook that stores its receiver may be given a frame-allocated
  object from a caller that was not recompiled.

**A4.10 "Potential needs" cannot be met by hooks.** Each new need is a new hook. SPEC 7.1 describes an LLVM-style
pass over the compiler's lowered form; no lowered form is exposed because none exists (A2.2).

**A4.11 Almost untested.** One test plugin; before-hooks, read hooks, statement hooks, `MayAlias` and the
control-flow graph are never called (`tests/Cs2Wasm.Tests/ApiChecks.cs:390-479`).

### A5. Spec conformance

| # | Problem | Source |
|---|---|---|
| A5.1 | "Every program compiles" is not met | about 156 `throw new CompileException` sites become CW0101 (`Api.cs:132`, `Driver.cs:1298-1309`); CW0100 for missing library members (`FunctionCompiler.cs:121-123, 2551`). Await inside `?:`, `??`, `?.`, `&&`, `\|\|` is one (`StateMachine.cs:1066`) |
| A5.2 | Missing capabilities are build errors, not run-time exceptions (SPEC 8) | `[MissingCapability]` makes reaching a member CW0003 (`Driver.cs:783-789`, `FunctionCompiler.cs:2530-2551`); about 190 uses |
| A5.3 | A silent trap with no diagnostic | `FunctionCompiler.cs:2544` |
| A5.4 | Two-pass filters depend on the program's own source | `FindFilters` scans `Inputs` only (`Filters.cs:22-30`) |
| A5.5 | Generic sharing is merge-after-the-fact only | `Types.cs:252-288`, `WasmModule.cs:470` |
| A5.6 | Stack traces assume V8's stack text | `host/engine.mjs:141-186`; each throw captures and parses a JS stack |
| A5.7 | Secure randomness is a PRNG from a 32-bit seed [run] | `Boundary/Interop.Sys.cs:27-50`; host returns 12345 (`host/engine.mjs:177`) |
| A5.8 | String hash seed comes from the host import (SPEC 7.3: fixed seed) | upstream `Marvin.cs` seeded through `random_seed` |
| A5.9 | Identity hash depends on scheduling under threads, unmarked | `Runtime.cs:845-855, 1030-1046` |
| A5.10 | Output depends on about 60 environment variables | A6.1; `CS2WASM_NO_CANON` turns NaN canonicalization off (`FloatStores.cs:16`) |
| A5.11 | Pipeline order differs from SPEC 2 | `Driver.Run` (`Driver.cs:1118-1185`): reachability, lowering, analysis and emission are one drain |
| A5.12 | A program with `Main` and no export compiles to an empty module, silently [run] | roots are exports only (`Driver.cs:1344-1346`) |
| A5.13 | Keep-whole attribute is type-level only; CW0201 suppressed after `Assembly.GetTypes` | `Reflection.cs:168-176` |
| A5.14 | Export parameter types unchecked; diagnostics without locations | `Driver.cs:1344-1346`; CW0101 throw sites that pass no location |
| A5.15 | Hand-written code beyond the runtime boundary | 63 modified Mono runtime files; 18 `edit` lines on non-boundary upstream files (`CoreLib manifest.txt:40-56`); `ManagedZLib.cs`, `EnumFormat.cs`, `TextScan.cs` |
| A5.16 | No Wasmtime path; no check that self-hosted output equals .NET-hosted output | host needs JS imports and workers; `ApiChecks.cs:101` compares same-host only |
| A5.17 | Proof 4 and proof 5 not met | self-host: traps, 1 GiB, hang on more than one core (`proof5_hello2d/README.md`, "What stops proof 5"); browser build of the game fails; hello world 23-30 s in the browser. Proof 5's engine half met 2026-11-19 (step 12.5 status) |

### A6. Structure

**A6.1 Configuration is ambient.** 84 `CS2WASM_*` names read by about 105 `GetEnvironmentVariable` calls in 30
files; 43 frozen in `static readonly` fields; 34 `NO_*` kill switches and 3 `EXP_*`. `CompileOptions`
(`Api.cs:35-54`) exposes none, not even `NO_THREADS` (`Driver.cs:142`). Source: each optimization was added with its
own switch for bisection and none was retired.

**A6.2 The library is not reentrant.** `Inliner.Module` (`Inliner.cs:27`), `Emitter.RcSites`/`RcCauses`
(`Emitter.cs:50-54`), `[ThreadStatic]` ambient parameters in `Locals` (`WasmModule.cs:178-193`), `Driver.s_libModels`
never evicted (`Driver.cs:872`), `Library.s_built` and public static sets (`Library.cs:65-71, 128-150`). The library
also shells out to `git` and a Godot editor (`Library.cs:405-532`) and writes to `Console.Error`.

**A6.3 Two god classes.** `FunctionCompiler` is 34 partial files (about 924 KB), `Driver` 19. Analyses have no
result objects: 73 string-keyed dictionaries on `Driver`, `HashSet<SyntaxNode>` (`OwnedParams.cs:16`), placeholder
calls patched later (`Arenas.cs:52-53`).

**A6.4 Passes recognise functions by name.** `"rt.nonnull"`, `"rt.release"`, `"static init "`,
`"Cs2Wasm.Runtime.Rt.AllocObject("` (`WasmModule.cs:195`). A rename silently disables an optimization.

**A6.5 Name special-casing.** 426 `"System.…"` literals and 121 `.Name == "…"` comparisons in `Compiler/`; no symbol
table. Workarounds among them: `foreach` over `List<T>` reading private fields (`FunctionCompiler.cs:1503`),
`EqualityComparer<T>.Default` replaced by name (`Calls.cs:46-50`), thread starters by name (`Scheduling.cs:38-47`),
capability guessed from substrings of type names (`FunctionCompiler.cs:2493-2567`).

**A6.6 Hand-assembled runtime glue.** Positional locals and literal offsets (`RuntimeGen.cs:703-715`); 45 literal
offset loads beside `Types.ObjectHeader`; `WasmModule.Encode` is 298 lines of pipeline, switches and encoding.

**A6.7 Stale comments.** Limbo and the shared bit are gone but still described (`FieldBorrows.cs:383-388`,
`SharedCounts.cs:7-13`, `Runtime.cs:1079-1081`); `Rt.Share` calls compile to drops.

### A7. Tests, evidence, repository

- End-to-end only: no unit test for any analysis or pass; none of the 34 kill switches is ever exercised.
- No Roslyn or dotnet/runtime test programs (SPEC 10); no program for stack overflow, out of memory or
  `SynchronizationContext`; Godot checks SKIP, not FAIL, when packages are missing (`GodotChecks.cs:36, 41`).
- No CI. No LICENSE file (SPEC: MIT), and no notice for the vendored upstream and fdlibm code.
- README describes `src/Cs2Wasm/Lib`, which no longer exists; prerequisites (emsdk 3.1.56, Godot editor, the git
  fetch of dotnet/runtime) are undocumented; `tools/selfhost.mjs:68` depends on untracked `tests/out/up/bin`.
- `host/web/cs2wasm-web.mjs` re-implements the import surface of `host/engine.mjs`.
- Commit subjects are paragraphs (median 290 characters).

---

## Part B. Plan

### Progress

- **Step 1: partly done (2026-11-18).** `CompileOptions.Checked` (`--checked`; the suite's default, `--unchecked`
  for the shipped form): freed memory is filled with a pattern and never used again, and every function is
  type-checked by the validator, whose findings are build errors. The validator learnt the 128-bit instructions.
  Still to do: the count of blocks left at exit (1.2; tests 119 and 121 count live objects through finalizers
  instead), CI (1.5).
- **Step 2: done except A1.4 (2026-11-18).**
  - A1.1 fixed: a local or parameter assigned inside an expression, or whose address is taken, is no longer a stable
    borrow (`_nestedWrites`, `WrittenDuringUse`). Test 118.
  - A1.2 fixed: an object its finalizer stored somewhere lives on and is not finalized twice (`Rt.FinalizedBit`,
    cleared by `GC.ReRegisterForFinalize`). A member of a dying group brought back keeps the whole group, which is
    then never freed: step 7 replaces this. Test 119.
  - A1.3 fixed: the weak table's busy mark is per thread.
  - A1.5 fixed: a field returned by reference counts as written after construction.
  - A1.6 fixed: a function takes its frame through one small function, `rt.frame`, which traps when the stack has
    not that much left and clears the frame (`Wasm/Frames.cs`: the last rewrite before a module is encoded, of the
    twenty-odd bytes each such function began with); `stackalloc` checks its size. For every thread, with the
    stack where it was and 1 MiB as before. Modules are 1 to 4% smaller for it (hello world: 27,629 bytes; the
    bench: 333 KB, 341 before) and no slower (bench 0.95x, micro in the baseline tier 2.29x). An earlier form of
    this fix put the stack under the data and shrank it to keep addresses short: the owner called that a hack, and
    it is gone. Worker stacks are still 1 MiB whatever `StackSize` says. Test 120.
  - A1.4: no fault found (2026-11-18). Test 132 makes 70,000 stores chosen by a fixed pseudo-random sequence
    among objects with twenty-two reference fields (more than a member keeps bits for), arrays, lists, a tuple, a
    dictionary and an object field, a third of them in a group that statics keep alive: what is reachable sums
    to what .NET's does all the way, checked and as shipped, and nothing is left at the end. Where there are no
    bits, `ArenaUnlink` goes by the counts, and that is right as long as every reference between two members of
    one group in a slot a cycle can close through is an inside one: a store links it, and an object that joins
    has its slots linked. Bits for every slot (7.B2) would let a parent link be an array's element or a late
    field; it is not needed for correctness.
  - **Found by the same test, and fixed: a finalizer that stores a reference into its own object while its group
    is destroyed** (`F19 = F2;` in `~Wide()`). The members had count zero then, so the stored one looked like a
    new plain object and the two were joined into a new group over the words the destruction was using: a hang,
    or a read out of bounds, checked and as shipped. Now a dying member joins no group; while the finalizers run
    every reference to a member is counted (those put back in their slots too) over a bias that keeps a release
    from destroying it twice; afterwards the references the dying members hold to each other come off the counts
    (their slots are visited, as when they were let go), and a count that is left is a reference from where the
    program can reach it: then they all live on, as before, with their fields as the finalizers left them
    (`Rt.FinPhase`).

- **Step 4, first part (2026-11-18): which types another thread can reach, decided when compiling; their counts
  atomic, the others plain.** Measured first: a program is in thread mode as soon as a thread start is reachable,
  and `await Task.Yield()` or `await Task.Delay(..)` makes one reachable (the thread pool), so most async code
  pays it. The bench compiled so (a `Task.Run` it never runs) took 18.4, 61.0, 50.1, 52.2, 12.2, 22.8, 5.4, 45.0,
  12.9 ms (dictionary, linked_list, linq, list_sort, nbody, shapes, strings, tree, word_count) against 7.1, 3.5,
  12.9, 37.8, 1.6, 1.0, 2.8, 8.2, 2.5 without threads; with every count plain it would still take 32 ms for tree.
  - `Compiler/Sharing.cs`: at every store into a field, an element, a static, a struct's field, at every argument
    and every `return`, where the value comes from (a new object of a type, a parameter, a result, a field, an
    element, a local's assignments, an `out` parameter's stores, a lambda's delegate's invocations, a
    reflection thunk's member), else its static type. The types another thread can reach are what the statics and
    the thread a thread start hands over lead to, through what those types' slots hold: a slot holds its declared
    type's objects only; a `ref T` lands in a T slot (Unsafe.As recorded); an element read is of the arrays its
    expression can be. A parameter's sources count only if every call to the method is one made from recorded
    arguments (marked on the call instruction); generated code, stubs, delegates, the host make it its type.
  - `Compiler/TypedCounts.cs`: retain, release, the read that counts and the store take the static type at about
    a hundred places; once every function exists each becomes the atomic function or a plain one (rt.retain_plain,
    rt.release_plain with a leaf's release written in place, rt.store_ref_plain; a read from an object no other
    thread can reach is a plain read). `CS2WASM_SHARE_CHECK=1` makes the plain counts trap on an object another
    thread made: the 33 tests that use threads, tasks or Parallel pass with it, and with every type taken as
    thread-local (`CS2WASM_SHARING_NONE=1`, a test of the check) they trap.
  - The bench in thread mode now: 18.1, 33.3, 36.0, 51.1, 2.7, 3.4, 5.4, 25.5, 11.6 ms. Of 413 types, 239 are
    reachable by another thread, none of the bench's own. Without threads nothing changes (0.94x NativeAOT,
    16.0 MB, hello world 27,943 bytes).
  - What is left of thread mode's cost, measured: `string` is a type another thread reaches (the culture data is),
    so every string count is atomic (word_count, dictionary); `List<int>` too (the runtime's weak-reference table
    holds one): a per-type answer cannot separate an object from others of its type. Field borrows, the
    allocator's single-thread path and the loop that frees a list are still off in thread mode, and a store into a
    cycle-capable object takes the runtime lock. Next: borrows for objects no other thread can reach (they are
    decided while compiling, before the answer is known: a second pass or deferred borrows), the runtime's own
    structures in types of their own, and the allocator.
  - **Second part (2026-11-18): a program with threads is compiled again, knowing the answer.** Whether a value is
    counted or borrowed shapes the code around it, so it cannot wait for the end of the compilation that finds out
    which types another thread can reach. `Cs2WasmCompiler.Compile` compiles such a program once more with the first
    answer (`Driver.SharingPrior`); where that says no other thread can reach an object of the static type, a field
    or element of it is read as without threads (no counting read), and borrowed from: locals that borrow a field
    (FieldBorrows.cs, LocalBorrows.cs), a field held for a call; the borrow's resolution needs no look at other
    threads' stores (`Driver.ThreadLocalBorrows`). Statics are never the thread's own (a thread's own static is).
    The new compilation's own answer must name no shared type the one it used did not, else it compiles again with
    both (three rounds; then once taking every object as shared). The answer goes from one compilation to the next as type keys, each with whether an object another thread can reach can be one: each compilation has symbols of its own (one that adds generated code, dynamic call sites or a Godot game's, builds its own program compilation). Cost: a threaded program's compile time, the
    bench's 18.3 s to 23.1 s. `CS2WASM_NO_SECOND_PASS=1` compiles once.
  - The bench in thread mode now: 17.2, 8.8, 15.0, 47.1, 1.6, 1.1, 3.7, 20.3, 7.1 ms (nbody and shapes as fast as
    without threads). The 33 threaded tests pass under the share check. Left: string and List<int> are shared types
    (dictionary, list_sort, word_count), and tree's 20 ms against 8.2: the allocator's thread path and the loop
    that frees a chain, still off in thread mode.
  - **A deadlock found on the way, older than step 4 (2026-11-18).** Test 102 timed out once in the suite; run in a
    loop it hung every time, also built with all of step 4 off (about once in five hundred runs of the test as it
    is). Paused through the inspector protocol: the engine thread held the runtime lock while it freed a group
    of objects that refer to each other, and one of them, a SafeHandle, ran its finalizer, which takes the wait
    subsystem's lock; the timer thread held that lock and waited for the runtime lock (a read that counts, of a
    group's member). .NET runs no finalizer under a lock of its runtime. Now, with threads, a group's
    destruction takes its members out under the lock and leaves the rest (finalizers, clears, frees) to a list of
    the thread's own, run when its outermost RuntimeUnlock gives the lock back (`Rt.RunDeferredDestroys`); the
    references cut for the finalizers go with it, in a block of their own, and a part's arena record is dealt with
    while still under the lock (it may be merged or freed by others before the rest runs: test 111 caught that).
    Test 135 is the loop: 150 rounds, every one hung before. `CS2WASM_DEADLOCK_DUMP=1` makes a long wait for the
    runtime lock throw, naming its holder.
  - **Thread mode's allocator and releases (2026-11-18).** Measured first: tree in thread mode spent 37% in the thread
    caches' exchange with the shared lists (a flush walked each list to its tail, a refill walked the shared list a
    block at a time: 2 MB of nodes freed, then built again). A shared list is now a stack of the batches threads gave
    back whole, so a batch goes either way in a few stores. AllocObject and FreeObject have the one-piece path on the
    thread's own list that single-thread has on its own. The exact releases (a sealed type's count test and a direct
    call of its destruction) are written out for the types no other thread can reach, after the sharing is known.
    The optimizer's count passes (Peephole, LocalsRc, Fresh) knew rt.retain and rt.release by name only and so missed
    every count of a program with threads (still the per-type placeholders then): they know them all now (Wasm.Rc).
    `rt.lock_exit` no longer runs the deferred destructions before the thread has its thread statics (AllocFirst:
    it read a word of memory that was not the list). Thread mode: tree 20.3 to 12.0 ms (7.8 without threads),
    linked_list 8.8 to 5.8, the rest a little faster; without threads unchanged (0.96x NativeAOT).
  - **The runtime's own types, and a thread's own pages (2026-11-18).** CS2WASM_SHARING_DEBUG named why the
    program's Dictionary<int, int> and List<int> were shared: the runtime's tables of object marks and weak slots
    were a Dictionary<int, int> and a Dictionary<int, List<int>> in statics. They are now maps in memory of the
    runtime's own (IntMap, ObjectMarks.cs), which share no type with the program. A thread now cuts its small blocks
    from pages that are its own and puts a block it frees back on its page, which counts its blocks out, and is cut
    again in order once they are all back, as the pages without threads (the lists by thread handed blocks out in
    the order they were freed: a tree made again after one was freed lay all over memory). A block another thread
    frees takes the old way. The inliner puts the plain counts into loops as it does rt.retain and rt.release; a
    list's node is freed in a loop for a type the last compilation found no other thread reaches. Thread mode:
    dictionary 17.0 to 7.3 ms, word_count 7.0 to 5.0, tree 12.0 to 9.5, linked_list 5.8 to 4.4. What is left is
    strings: string is a shared type in every program that has a static string (CultureInfo's, a program's own
    `static readonly string[]`), so every string's count is atomic; deciding that per object rather than per type
    needs the allocation site and the thread that reads a static, not yet done.
  - **Found by the share check, and fixed: two types' destructions taken for the same code (2026-11-18).** Test 088
    under `CS2WASM_SHARE_CHECK=1` failed one run in three: a plain count of the thread pool's TaskScheduler on the
    timer thread, or a hang. The destruction dispatch gives types whose destroy functions are the same code one
    case (`WasmModule.BodyKey`), and a call was keyed by its callee's order in the module; the per-type count
    placeholders are in no module, all of order 0, so a continuation's destruction and that of a thread-local type
    with references at the same offsets shared a case, and the continuation's scheduler was released with the
    plain count. A callee with no order is now keyed by its name. In the shipped build it was a count that could
    go wrong when two threads let go of the scheduler at once. 088 under the share check: 40 of 40 runs pass.
- **Step 7.1, isolated values (2026-11-19).** A store of a structure that a call has just built of new objects, none
  of which refers to anything but each other, into an object that existed before the call, closes no cycle: the
  structure reaches nothing older (C# evaluates the target of `a.F = M()` first). `Driver.IsolatedValue` finds such
  values: null, a `new` whose constructors bring no reference in, or a call of a static method with no parameter that
  can bring one in, whose locals holding its new objects get only isolated values and are used only as store targets,
  for their plain fields, in comparisons and in returns, which stores into its new objects only isolated values and
  stores them nowhere else (a recursion is taken as closed while it is decided). Such a store links through
  `Site.LinkIso`, which does not make its site general; on a site that is cyclic through other stores it is the
  general link. `CS2WASM_NO_ISOLATED=1` turns it off. Test 136: a subtree replaced under a long-lived tree, beside
  four near misses that must stay cyclic (a structure that refers back, one a static held while it was built, one
  whose field was handed out, one stored where parent links are), each in a class of its own: only the first class
  loses its cycle note, and every kind is freed as under .NET. Micro `tree_replace`: 193 us to 21 (NativeAOT 7, which
  leaves the old subtree to its collector).
- **Step 6.4, in part (2026-11-19): stores into a new object.** A field an object initializer sets (`new T { F = v }`,
  not `with`) on a new object whose constructors never write F (`Driver.CtorsNeverWrite`) is a plain store: no
  exchange and no release of an old value; on a site that can close a cycle the link of a store into a new object
  stays (a constructor's own store can have made the object a group's member: tests 090, 127 and 128 leaked
  cycles without it). Test 137 covers the fields a constructor, a base constructor or an initializer set, and `with`;
  it found that a field's initializer was not seen (`GetOperation` of a declarator is not its `= v`), in this and in
  the isolated values' check of constructors (`MemberInitializer`; test 136 has the near miss now). And `Wasm/Fresh.cs` follows the object its allocation's `local.tee`
  pushes (it began one instruction later, so whatever took that first push went unseen) and knows a field store whose
  address is in a local. Micro: `llb_build` 720 us to 642 (a list of 100,000 nodes of a cycle-capable type made and
  freed), `link_build_free` 297 to 279.

- **Step 3: answered from measurements (2026-11-17 and 2026-11-18); the bench fixes (3.1, 3.2) are still to do.**
  - Optimizing tier (what a game runs in), `tools/bench.mjs`: 0.95x NativeAOT's time, 32 MB against 71 MB.
  - Baseline tier (what a short run over many functions stays in: the self-hosted compiler), `tools/micro.mjs
    --baseline`: 2.32x on 2026-11-18. The engine runs the module as written there, and NativeAOT's module was
    optimized by LLVM first. Worst: replacing a subtree 38x, `foreach` through an interface 11x, dictionary set and
    get 4x, allocating and freeing a node 3 to 4x, a walk over a list or tree 1.7x.
  - The self-hosted compiler on `Console.WriteLine(42)` (2026-11-17, interleaved runs): NativeAOT's WebAssembly
    build 6.8 s; built without threads 12.6 s (1.86x); threaded 19.7 s on one thread, 8.0 s on 24. In the
    no-threads profile counting is 2.2 s of 14.6 s; the rest is flat over Roslyn's parser.
  - So for the self-hosted compiler: code quality in the baseline tier first (about 1.6x), thread mode second (12.6 s
    to 19.7 s), counting third (about 15%). The order of steps 4 to 7 stands. The build with counting and freeing
    switched off (3.3 c, d) was not made: the profile already bounds what it could show.
  - What the generated code showed (`CS2WASM_DUMP`): a walk over a list held by a static counted every node inside
    an exception region (fixed, below); a store of a new subtree into a new node goes through the cycle machinery
    (a check, `rt.store_ref`, a release of the old value that is known to be null) because any class that refers to
    its own type is cycle-capable.
- **Step 5.1: the spike is done (2026-11-18); the IR is built from the operation tree, not from Roslyn's graph.**
  Over the conformance sources (1,129 bodies) and the compiler's own (1,360 bodies) `ControlFlowGraph` built for
  every body with no failure, in 7 to 12% of the time the operation trees take.
  - (a) Flattened: blocks and declarations, loops, `if`/`?:`, `switch` and `switch` expressions, `try`/`catch`
    (regions), `?.`, `??`, `??=`, `using`, `lock`, `with`, object and collection initializers, interpolated string
    handlers, local functions.
  - (b) Left as operations: patterns, lambdas, `await`, `yield return`, tuples and deconstruction, compound
    assignment and increments, `dynamic`, collection expressions, ranges.
  - (c) Lost: `foreach` over an array or a string becomes `IEnumerable.GetEnumerator`, `MoveNext` and `Current`
    (64 of the conformance loops), and every `foreach` loses the fact that its element borrows from the collection.
  - Decision: (c) rules the graph out as the IR's source. The IR's own blocks give the same flattening (an
    `await` inside `?:` is then a block boundary like any other), with `foreach` lowered by the compiler as today.
- **Step 6, begun (2026-11-18).** A local borrows from a static as from a field (the stores of statics are noted
  for the scope question): `ll_walk` in the baseline tier 1.68x to 0.99x. Test 122.
- **Step 7.1, begun (2026-11-18).** A field only ever stored into objects that nothing else can reach yet
  (`Site.General` false: its general link is called nowhere) closes no cycle, like a field filled at construction:
  a component made of such edges alone is not cycle-capable (`CS2WASM_NO_FRESH_SITES=1` for the old rule). It
  takes one type out in the micro program (`Link`) and one of 40 in the bench. Trees and lists that are rewired in
  place, and anything that holds its own kind through a `List<T>` (an array site), stay cycle-capable: those need
  the isolated-value rule (a new structure that refers to nothing older, stored into an old one), the
  back-reference proof, and step 7.D.
- **Found on the way, not fixed:** a reference inside a struct-typed field of a class is stored without any link
  (`StoreToAddressFromStack`, the struct branch), so a cycle through such a field (`class A { (A, int) pair; }`) is
  counted plainly and never freed.

- **Step 8.1: done (2026-11-18).** Field `[UnsafeAccessor]` (an instance's, a struct's, a static's field: test 123),
  and code a plugin adds with `AddSource` is bound ignoring accessibility, so it names the program's internal types
  and private members directly (API check "generated code reads and writes private fields"). The second rests on
  setting a non-public property of Roslyn's options by reflection: it is not yet tried in the self-hosted compiler,
  where field accessors are the way that does not depend on it. Steps 8.2 and 8.3 are still to do.
- **Step 13.1: done (2026-11-18).** `LICENSE`, `THIRD-PARTY-NOTICES.md`, a README that matches the tree and names
  what a build needs.
- **Step 12.1, begun (2026-11-19): the compiler compiled by itself, on today's runtime.** Its build without threads
  took 24 minutes, 17 of them in one place: the arena visitor of every cycle-capable type asked all 51,000 sites
  which of them its type has (`Arenas.CyclicFieldOffsets`, a subtype test per site and type); the sites are now
  looked up by container, along the type's bases: 7.4 minutes. The threaded build ran out of memory (14 GB) in the
  sharing analysis of step 4, after 94 minutes: its "why" text grew along every path (now kept only for
  `CS2WASM_SHARING_DEBUG`), and its states, a node and the type that filters what it holds, were unbounded; a node
  reached with more than four filters is now visited once with none, which lets through whatever any filter would
  (more types taken as shared, never fewer); and which types may be subtypes of a type is asked of an index
  (`SubtypeIndex`, as Arenas does) instead of every type id, in `TopIds` and `MayBeShared` (checked against the old
  answers over the threaded tests and the bench: the same). The threaded build: 18.8 minutes, sharing 110 s and
  the counts' resolution 33 s per compilation (two). The isolated values' check of a method (step 7.1) went over
  the whole body for each local: now once.
- **Found by the same run, and fixed: the check at the drop (7.C) made the self-hosted compiler hang.** Every
  self-hosted build ran past 300 s on hello world (it took 13 s on 2026-11-17). Paused in the inspector: Roslyn's
  sorts and pooled builders, each move or clear a loss of a reference from a member, each one a check that went
  through all a structure owned (`CS2WASM_DROP_STATS=1`, a count at compile time: 82,000 checks had gone through
  950 million members, 11 freed anything; `CS2WASM_NO_DROP_CHECK=1`: 13.8 s). A check fails for one of two reasons,
  and its top's note now counts which: how many of what it owns outside code holds (TopHeld, as before), or how many
  references from members outside what it owns were left into it (`NoteOutside`: one more at such a store, one
  fewer at such a loss; a reference from inside changes nothing). It is looked at again when that number is zero,
  or when it loses its owner, not at every loss. The note has a word of its own (`MarkWord`, a ninth member
  word). Self-hosted hello world: done in 18.8 s with the statistics on (114,000 checks through 22 million members,
  1,594 of them freeing a structure). Test 138: a sort over references to 2,000 structures whose checks fail.
- **Open, being worked on (2026-11-19): the threaded self-hosted compiler on many threads.** Hello world, timed
  against NativeAOT's WebAssembly build (5.5 s): built without threads 19.0 s (3.4x; 12.6 s on 2026-11-17), threaded
  on one thread 39.5 s, on 8 or 24 threads no end in 300 s, or a crash: `rt.retain` on a reference that is no object
  (unaligned). Bisected over the self-hosted builds of 2026-11-18: commit 9070946 (every container in the graph of
  what can refer to what; a struct's copy and Interlocked's exchanges of references, with threads, as stores by
  address) is the first with it, and the first at 27 s on one thread (14.9 s before). Found in it and fixed: an
  Interlocked exchange or compare-exchange of a reference into a slot of a cycle-capable object linked the value
  before the atomic exchange and unlinked after, under no lock in common, so two threads' exchanges of one slot left
  its bits saying the other's value and its counts wrong; now the three are one step under the runtime lock, with an
  atomic exchange (the place's lock is taken before the runtime lock elsewhere: not inside it). Not enough: 24
  threads still do not end; a checked build of the compiler (freed memory never used again) is to say where.
  Also seen: 072_http once out of bounds in a worker in a checked suite run, then 366 runs passing.
  Then, found with a checked build of the compiler (a node of Roslyn's read after it was freed) and two small
  programs that reproduce it (test 139: sixteen threads storing into the same slots of shared objects, a shared
  array of structs among them, as Roslyn's node cache does; 14 runs in 40 failed before):
  - Any store into a slot that can close a cycle, in an object another thread may reach, is one step under the
    runtime lock (`Site.StoreLock`, `Arenas.StoreLockAt` for stores by address and a struct's copy): its link sets
    the slot's bits, its unlink reads them, and two threads' stores of one slot between them left them saying the
    other's value. Not a store into an object the last compilation found no other thread reaches.
  - An object whose destruction begins while its thread holds the runtime lock waits in the thread's list until
    the lock is given back (rt.destroy, rt.lock_exit): its finalizer runs program code, as with the groups before.
  - The finalizers' phase of a group destroyed later puts the cut references back while the lock is still held, at
    the deferral (`FinPrepare`): put back afterwards, one could be to an object another thread had freed.
  - A placeholder for a local's borrow was made in the module before its first use: a local whose every value was
    owned left one with no body, which only a checked build's validator saw.
  Test 139: 40 runs of 40 pass checked, 30 of 30 as shipped. The price: every such store takes the one runtime
  lock, also into a plain object (unlocked, another thread can make the container a member mid-store and link the
  old value, which the plain unlink then leaves counted: two nodes leaked in 320,000). The threaded self-hosted
  compiler: 73 s on one thread, and on 24 it is all waiting for that lock. **Open, the next structural piece:** the
  runtime lock replaced by one per group for arena operations (a store, a link, a join lock the groups they touch,
  in address order), with the runtime's scratch state per thread; until then a program that shares cycle-capable
  objects among many threads is serialized on them.
  `node tools/suite.mjs` runs the suite in shards side by side, both modes: 7 minutes instead of 25.
  **Then (2026-11-20): the threaded self-hosted compiler ends on any number of threads.** Hello world: 23 s on one
  thread, 25 on four, 64 on twelve, 70 on twenty-four (it did not end on eight or more). Found on the way, each with a
  test that failed before:
  - A thread's new page of small blocks walked, under the runtime lock, a run of exactly a page left free for each page
    before it (quadratic; half the pages' memory lost): now an aligned window of a free run or of the heap's top.
  - The thread pool's limit was the browser's 10 threads (TARGET_BROWSER): pool threads blocked on work queued behind
    them waited forever (test 142). Now a 32-bit target's 1023.
  - Monitor.Pulse could be taken by the thread that pulsed and then waited (test 144): a queue of waiters now.
  - Two types whose static initializers use each other, started by two threads at once, waited forever (test 146):
    a thread follows the owners of the initializers waited for, as .NET does.
  - The cause of the hang on twelve threads: a Node worker started by another worker needs its parent's event loop,
    which a thread running the module never gives back; PLINQ's producers started that way never ran. Every thread is
    now started by host/spawner.mjs, a worker of the main thread that runs no module code.
  Still open: more threads make it slower, not faster (every arena operation takes the one runtime lock; the work
  item below). Tools for such hunts: tools/stacks.mjs (every thread's stack under --inspect), CS2WASM_WATCHDOG=1 (the
  pool's counts every 5 s), CS2WASM_DEADLOCK_DUMP=1 (a wait of 20 s for the runtime lock says who holds it).
  **Code quality for the baseline tier, 2026-11-20** (where the self-hosted compiler runs): `tools/micro.mjs
  --baseline` 2.18x to 1.85x NativeAOT's time (null checks taken out again on the final bodies and the rest written
  out in loops and what they call; AggressiveInlining honoured for methods no exception leaves; tee chains, constant
  arguments of inlined calls; the walk to a member's top only for members under a failed check). The no-threads
  self-hosted compiler on Console.WriteLine(42): 16.0 s to 12.45 s, against 4.6 s for NativeAOT-LLVM's build of it
  (2.7x). Its profile is flat now: the arena machinery about a quarter of the program's time (stores into slots of
  Roslyn's cycle-capable types: proving them away is step 7.2's), counting about a tenth, the host's file calls 6%.
  **Open (2026-11-20): test 139 leaks two nodes about one run in 45 under heavy load** (15 runs at once, checked;
  none in 107 runs at lighter load): the same rate before and after the TopHeld change below, so older. Not found yet.
  **Build cost, measured 2026-11-20 (tools/measure-build.ps1: wall time, peak memory of every process a build
  starts), the compiler with Roslyn:** `dotnet build` to IL 9 s, 0.73 GB; NativeAOT-LLVM published to WebAssembly
  80 s, 8.7 GB summed (ilc 2.8 GB, Emscripten's clang and wasm-ld the rest), a 129 MB module; cs2wasm 18 to 20
  minutes near 14 GB (one build ran out of 14 GB). cs2wasm is slower and heavier to build than NativeAOT-LLVM.
  Separately, and older: a program with threads whose cycle-capable objects are made on many threads at once gets
  slower with each thread (a list of 3,000 trees of 200 nodes, made in a PLINQ query: 2.9 s on one thread, 10.7 on
  four, 21.7 on 24; the same with the compiler of 2026-11-17): every arena operation takes the one runtime lock.
- **Step 13.5: done (2026-11-19).** `docs/UPSTREAM.md`, generated by `node tools/upstream-report.mjs` from the
  manifests and dotnet/runtime v11.0.0: every exact edit and replaced file with the reason tools/CoreLibManifest
  gives for it (18 edits, 3 replaced files, in four libraries), and System.Private.CoreLib's runtime half file by
  file: 63 changed (lines added and removed, and the comments marked cs2wasm that say why), 47 as upstream, 1
  cs2wasm's own.

- **Step 12.5, the engine's half: done (2026-11-19); the build in the browser: open.** The game
  (`tests/Godot/proof5_hello2d`, two C# scripts on a `CharacterBody2D` and a `Label`) compiled by the .NET-hosted
  cs2wasmc (3,449,802 bytes) plays in a browser in the **stock** Godot 4.7.2 web template (the build without threads,
  `v4.7.2.stable.official.ed1daf0bf`; the eight stock `web_*.zip`, taken by range requests from the official
  `Godot_v4.7.2-stable_export_templates.tpz`, replaced the mislabeled copies in the editor's template folder), through
  the JS bridge (`host/godot-bridge.mjs`, `host/web/godot-web.mjs`, `host/web/godot/*.gd`). Its engine-call log equals
  stock Godot .NET's on the desktop line for line: 1,667 lines with the fixed input, 1,669 with the key that makes
  the script throw (`node tools/proof5.mjs check --module tests/out/proof5/game-net.wasm [--panic]`, headless Chrome
  157); the same in the Claude app's browser pane (Chrome 156) with `node tools/proof5.mjs serve --build` and
  `?module=prebuilt.wasm&drive=1`, the page comparing its log with the oracle's itself; played by keys it moves and
  jumps (`--play`). The headless check without a browser is `node tools/proof5.mjs stub [--panic]`: the same module
  on Node with the oracle's answers replayed, equal too. The oracle, run again, gave the same bytes. Open: (1) the
  game built in the browser by the self-hosted compiler (`tests/Godot/proof5_hello2d/README.md`, "What stops
  proof 5", items 1 to 4: last tried 2026-11-17 with the self-hosted compiler of that day; not re-tried on today's);
  (2) `StackFrame.GetMethod()` is null in a module (`repro/StackTraceFrames.cs`: .NET prints `A`), a behaviour
  difference the game's log does not show; (3) a host without JavaScript (Wasmtime) for the `godot.*` imports.

- **Steps 8.2, 8.3: done (2026-11-18).** `MemberAccess.Instrumentable` says which accesses a hook runs at (not a
  `ref` or `out` argument, a deconstruction's target, an event's handlers); every hook form and failure path of the
  interface has an API check.
- **Step 9, in part (2026-11-18): plugin interface version 4, without waiting for the IR.** The measurements put the
  IR's first use in the optimizer, and what the Invisible API was blocked on turned out to need only the front end's
  one entry point for expressions:
  - `InterceptValue(expression, hook)`: the value of any one expression goes through a hook that returns the value
    the program goes on with: a branch's outcome seen and replaced (A4.2), a draw redirected, the value a write
    stores rounded or limited, an awaited answer. Works in async methods. A hook on something that is not computed
    as a value fails the build.
  - `InstrumentWrite(write, hook)`: a write hook at one write (A4.3).
  - `SuppressibleImports(imports)` and `Cs2Wasm.Effects.Suppressed`: effects are left out by a switch the generated
    code sets around a run, so it holds for virtual calls, delegates, lambdas and the library, and nothing is
    cloned (A4.7). `CloneWithSuppressedImports` stays as it was.
  - Generated code names the program's internal types and private members (step 8.1).
  - Version 5 (2026-11-19), collection entries (A4.4): `Accesses` reports an access to the entries of a collection
    a field or property holds (`MemberAccess.IsEntry`, `Key`): an element read or stored, a compound store, a library
    method that changes it (`Add`, `Remove`, `Clear`, `Sort`, `Array.Fill`, ...) or reads it (`TryGetValue`, `Count`,
    LINQ, a `foreach`), on the member, on an element of it (`Grid[x][y] = 1` is entry x of Grid), a field of a struct
    element, or a local whose every reaching definition is the member. `InstrumentEntryWrites` and
    `InstrumentEntryReads` call a hook after each, with the receiver and the key as the program computed them
    (`(object receiver, TKey key, bool keyed, int tag)`). One function (`Compiler/Entries.cs`) decides the sites for
    the analysis and for the code, so they agree. Plugins of versions 1 to 4 see the accesses they saw. A collection
    handed to the program's own method, or a `ref` to an element, is not followed (the list of unsure sites).
  - `InstrumentAfter(statement, hook)` (version 5): a hook where a statement's code ends, so it runs when the
    statement completes normally (an `if` whose branch did not return, a loop left by its condition or a `break`)
    and not when it is left by a `return`, a `throw` or an exception; a jump statement is refused. In an async
    method or an iterator (compiled from Roslyn's lowered graph) it runs after the statement's last lowered
    operation, and only expression statements and declarations can be hooked there.
  - Conditions and aliasing (A4.8, version 5): conditions count early returns (`if (c) return;` and a branch that
    always leaves put the statements after it under `c` false, up to a label). `CallersOf(method)` gives each call
    with its conditions (a virtual or interface call counts for every override and implementation; a property's
    accessor, a constructor), and `PathConditions(operation)` every path of calls from a method no source code
    calls (or from a delegate's invocation, a recursion, the cut) with the conditions along it, outermost first.
    `MayAlias` follows where references come from through the def-use chains: two different `new`s, a `new` and a
    parameter's incoming value, or a `new` its method owns alone (`IsOwned`, from the escape analysis: not stored,
    captured, returned or handed to code that keeps it) and anything from outside do not alias; then types.
    `AllocationSites(reference)` gives the `new`s a reference can name.
  - Plugins before the first compile (A4.9, version 5): a plugin that implements `ICompilerPluginBeforeCompile`
    runs before the first drain as well; what it adds there is compiled once, with every analysis knowing its
    hooks. Before the compile the reach is not known, so it sees every source method with a body, the source's
    direct calls and every source type. The soundness part: the escape analysis and the fresh, isolated and
    constructor analyses (`ParamEscape`, frames, `FreshLocalBefore`, `ReturnsFresh`, `IsolatedValue`,
    `CtorsKeepThis`, `ClosedCtors`, `IsoMethod`, stores into fresh objects) take a method with a hook as one that
    lets everything out, since a hook may be given its receiver, a value or a key. A hook added after the compile
    makes the compiler forget those answers and compile again what it instruments and every function (static
    initializers too) that used one of them. Test: a hook that keeps its receiver was given an object a caller had
    put in its frame (printed 20 for 2), now in both phases. Not looked at for hooks: the threaded sharing analysis
    (`Sharing.cs`) and the arena analysis; both run over compiled code that includes the hooks' own bodies, but not
    over the call edge from an instrumented site to its hook.
  - The list of unsure sites (version 5, `UnsureSites`): a delegate invoked whose target is not known there
    (`DelegateTargets` gives the lambdas and methods where the def-use chains know them), reflection (calls into
    `System.Reflection`, `Activator`, `Type.GetField` and the like, and the compiler's own CW0201 warnings),
    `dynamic`, a member or an entry used through a reference (`ref`, `out`, `in` arguments, `ref` locals and
    returns), a collection that leaves where its entries are followed (handed to the program's own method, stored in
    another place, returned, made a writable span or memory, or reached through a local that is not always the
    member), and addresses and reinterpreted memory (`&`, `Unsafe`, `MemoryMarshal`). A library method other than
    the known changing ones (`Entries.cs`) is taken to read a collection it is given.
  - Still open: the closed-form solver (A4.6) and the first slice of the real plugin in the Bazoodle repository
    (9.4). What they need:
    - A4.6, closed forms as a solver. Input: a per-tick method or a loop body, as Roslyn's graph now (the IR's
      later). Execute the region symbolically: every state variable written (fields of `this` and statics, loop
      locals, several writes each, in order) becomes an affine expression of the old state vector, `x' = A x + b`,
      whose coefficients are invariants of the region (constants, parameters such as `dt`, members it does not
      write); this covers the spawner timer's two writes and velocity/position (`v *= d; p += v * dt` is a
      triangular A). Known pure functions expand: `Min`, `Max`, `Clamp`, `Abs` into pieces, `Lerp` into its affine
      form; a program method inlines when it writes nothing and calls only such functions. A branch on the state
      makes pieces: within one the rule is affine, and the tick a guard flips is solved from the affine-in-n value
      (for one variable, `n = ceil((limit - x0) / step)`); a guard over a component that is not monotone is
      reported as not solvable, with the reason. Results are values, not strings: the variables, A and b as
      operations or constants, the pieces with their guards and flip ticks, and a generated C# method
      `Advance(ref state, long n)` the plugin can add with `AddSource`. Exactness is part of the result, because
      behaviour must not differ: integer rules (unchecked, wrapping) are exact through repeated squaring of the
      affine map (`log n` steps; the geometric sum's division has no inverse mod 2^32, so no formula with
      `(a^n - 1) / (a - 1)`); floating-point closed forms round differently from running the ticks, so they are
      marked approximate (a display or a prediction may use them; a simulation must run the ticks or call the
      per-tick code n times). Tests: the four failing examples in A4.6, each compared with running the ticks.
    - 9.4, the Invisible API's first slice, in the Bazoodle repository against version 5: classify each write
      (`Accesses` with entries, `PathConditions` for whether it runs under a value from input, `InterceptValue` on
      the input reads, `UnsureSites` as unknown), mark the interactions changed (`InstrumentWrite`,
      `InstrumentEntryWrites` with the player key), one gate (generated C# with `AddSource`, reading private
      fields, exported for the server), registered from `RunBeforeCompile` so nothing compiles twice; the solved
      timer waits for A4.6. Each gap it finds is a compiler issue here.

- **Step 11.1, in part (2026-11-18): programs written to use every form, compared with .NET.** Tests 124 to 126
  removed three refusals (`await` in conditional expressions was already handled; lifted compound assignment
  through an operator method; a spread with an element conversion) and found five wrong results that no refusal
  announced: `x ??= await f()` and `a[i] += await g()` lost the assignment; `x++` on a nullable struct with its own
  operator; relational and constant patterns on a `decimal` (compared as addresses); a NaN constant pattern; an
  empty nested initializer read its member. Writing such programs finds more than reading the refusal sites does.
- **Step 11.2: done (2026-11-18).** What the sandbox or the library lacks is a warning and
  `PlatformNotSupportedException` at run time (A5.2, A5.3).
- **Found 2026-11-18, open, and it comes before speed: most cycles are never freed (A1.8).** Test 127 holds objects
  every ordinary way, lets go, and counts what is left by finalizers. Freed: trees with parent links through a
  list, rings, parts cut off a living structure, replaced values, iterators, exceptions, weak references. Never
  freed: a cycle through
  - a closure or a delegate (an event's subscriber that the publisher refers to; a callback that captures its
    owner; LINQ over objects with a lambda that captures one),
  - a task or an async method's state,
  - a reference inside a struct: a tuple field, a dictionary's entries (`Dictionary<string, Node>` in a `Node`), a
    `List` of structs, a boxed struct.
  - Source: the type graph and its link sites (`Arenas.cs`) know class fields of reference type and arrays of
    references only. Display classes and synthesized types are left out of the graph on purpose
    (`Arenas.Analyze`: `!D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id)`), a captured variable
    is stored plainly (`StoreCaptured`), and a struct is copied with plain counts (`StoreToAddressFromStack`, the
    struct branch; `rt.CopyAssign`).
  - Fix (step 7, first): every reference a heap object holds is a site, whatever holds it: a class field, an
    element, a struct inside either, a box, a display, a delegate, a state machine. The suite shows the test as a
    known gap at every run until then (`// known-gap:` in a program).

- **Step 7.A: done (2026-11-18).** The heap is handed out in granules of 1 KiB with a table word for each: a page
  of small blocks, a block of its own, or a free run. `Rt.BlockOf(address)` says which block an address is in, for
  programs with and without threads (with threads a page keeps its kind and size for good, and a freed large block
  is kept for one of its size). Without threads a freed run is joined with the free runs beside it, where freed
  large blocks used to be split and never joined: the bench's memory is 16.0 MB (32.0 MB before; NativeAOT
  71.3 MB), its time unchanged (0.95x), hello world 27,840 bytes. A checked build asks the table about every block
  it frees.
- **The suite runs twice from now on, checked and as shipped** (`--unchecked`; CI does both). A checked build never
  uses freed memory again, so it cannot see a fault in how memory is reused: the allocator's first version passed
  it and crashed the bench. The 153 programs pass both ways.
- **Step 7.C: done but for one shape (2026-11-18).** Every container is in the graph of what can refer to what,
  in programs with and without threads: a reference inside a struct that is a class's field, an array's element or
  boxed (tests 129, 130); a closure's captured variables and its enclosing closure, a state machine's variables,
  a delegate's target, an anonymous type's properties (a closure's object has no type of the program's: its sites
  are by its type id); a field-like event's field (its accessors store by address); a slot whose address is taken
  holds whatever its type can. Test 127 went from ten kinds of objects left to two.
  - **Two faults in the runtime, found by these shapes, both about the moment a store is made.** `ArenaLink` gives
    back the count the store took, before the storing code writes the slot. When that count was the value's last
    outside one, (a) a group nothing outside referred to any more was never freed (`self.Slots[0] = self` with the
    last reference: nothing looked at the group's number then), and (b) a value that became unreachable was
    destroyed at once, and its container with it, before the slot was written and its old value read
    (`t.Tag = new object[] { t }`: a write to freed memory, which the checked build showed). Now such a count is
    kept until the store is written and given back then as any release (`Rt.GiveBackLater`, `AfterStore`; every
    link is followed by an unlink, an array initializer's elements too). An object that joins its group for the
    store gives the count back at once as before, so building a structure costs what it did (bench 0.96x,
    unchanged; 16.0 MB against 71.3 MB; hello world 27,943 bytes).
  - **What is left of test 127 (`async 5`, `pending 1`), and why it is not a missing container.** With a task in a
    cycle (a continuation that captures what holds its task), tasks, state machines and delegates are of one
    component with the program's objects, and every object an async method touches ends in one group with the
    task machinery, which lives as long as the program. Inside it only what is shaped like ownership is freed when
    let go, and an async method breaks the shape: the object it makes is first held by the state machine (its
    owner then), so its reference to its parent is a counted one, and when the machine goes and the parent's list
    becomes the owner, the reference to the parent stays counted (it is looked at again only if the new owner is
    the direct one at that moment: here the result array took the object first). A counted cycle in a group that
    lives: it stays. This is the limit the model states ("a part of a group that is let go while the group lives
    may stay"), met by ordinary async code, so it is the owner's to weigh: steps 7.D and 7.E do not remove it. Ways
    that keep the decisions (no search for garbage): keep the task machinery out of the program's groups (a
    continuation's target linked only while it waits), or look at an object's counted references again whenever
    any object above it gets an owner (a visit of what that object owns, at that store).
  - **Both ways were tried the same day; neither is what fixed it.** Keeping the task machinery out has no rule
    that is automatic and sound: the group that lives on holds the thread pool's queue, its per-thread parts, the
    threads and the timer queue (measured: 39 members, 12 referred to from outside), their storage is ordinary
    arrays and `ConcurrentQueue` segments shared with the program's own, and an object that never joins a group
    (pinned) is never freed as part of a cycle, which a fired `Task.Delay`'s promise and timer are. The visit of
    what a re-owned object owns changed nothing. What the counts showed instead: the object an async method makes
    refers to its parent first and so *owns* the parent; when the state machine that held it goes, the next
    owner was the latest other holder (the task's result, gone by then) or was refused because the object was
    above it. Now the first other holder is remembered, and an adoption turns the ownership round as a first
    store does (`Rt.Adopt`, `LinkMembers`): constant time, no visit. Test 131: one child and four through
    `Task.WhenAll` are freed (5 left each before). Suite 157 passed checked and as shipped, 1 known gap; bench
    0.96x, 16.0 MB against 71.3 MB, unchanged.
  - **Closed (2026-11-18): the check at the drop, the owner's choice.** A member left with no reference from
    outside and no owner, kept only by holds from what it owns, has what it owns gone through once at that store
    or release: the counted references among them come off, and if none of them is left with any reference, they
    are destroyed together (`Rt.Dropped`). Otherwise the counts go back, and the top keeps how many of them outside
    code holds; it is looked at again when that number reaches zero. Chosen over keeping a kind for every slot,
    which every store would pay for. Its cost is at the drop only: a subtree of 7,381 nodes indexed twice is
    released in 4.2 ms at worst against 1.9 ms for the same tree indexed once; when something outside still holds
    a part, the failed check adds about 20% (1.27 against 1.08 ms). Tests 127 (no known gap any more) and 134;
    suite 161 passed checked and as shipped; bench 0.96x, 16.0 MB against 71.3 MB; hello world 27,943 bytes.
  - **What test 127 left before that, exactly:** children held by two containers of one tree (`root.Children` and
    `root.Tag = results`). A member that something besides its owner refers to holds its owner, since that other
    holder may be outside the tree; here it is inside, the tree holds itself, and in a group that lives on it
    stays. Not specific to async; freed whenever the group goes (a program without a task on a cycle: at once).

### Direction (revised 2026-11-18 with the owner)

What the tool must do, at its core:

1. Any C# in, one WASM module out (not WasmGC).
2. Plugin support good enough for the Invisible API.
3. The same speed as NativeAOT or better, lower memory, no frame-time spikes, no runtime.
4. The same for the self-hosted compiler and Roslyn.

How the plan serves them:

- **The input stays the operation tree.** It keeps what the ownership analysis and the plugin need and IL has
  already thrown away: captured variables are still locals, async and iterator locals are still locals, `foreach`,
  `using` and `lock` still have their shape, and every operation maps to the developer's source. IL input was
  considered and rejected for these reasons.
- **One mid-level IR with explicit ownership is the centre** (step 5). The optimizer (goal 3), the plugins (goal 2)
  and self-hosting (goal 4) all work on it. It is the only structural change.
- **The IR is built from Roslyn's `ControlFlowGraph` for every method**, not only async ones as today, so Roslyn does
  the flattening of control flow and the compiler's own lowering shrinks to closures, state machines and patterns
  (goal 1). To be confirmed by step 5.1 before anything depends on it.
- **Cycles: prove the common ones away, group the rest, release groups without visiting them** (step 7). A dropped
  part of a live group staying until the group goes is a memory cost, not a spike, and is accepted.
- **Build time comes from caching the library's IR and analysis summaries**, not from precompiled IL (step 12.4).

The order is by dependency: nothing built on an unsafe base, measure before changing, the IR before what needs it.
Each step ends with the suite, `tools/bench.mjs` against NativeAOT's WebAssembly build, and a commit.

### Step 1. Make the tests able to see memory errors (A1.7)

1. In `host/run.mjs` and the runtime, add a checked mode: a freed block is filled with a poison pattern and its count
   word set to a trap value; retain, release and field loads on a poisoned block trap with the block's type.
2. At exit of each conformance program, report blocks still allocated that nothing static reaches (a leak count, per
   type). Record the expected figure per program; a change fails the test.
3. Run the WASM validator in every test build and in Debug; a failure is an error diagnostic.
4. Run the suite in checked mode by default. Fix what it finds before going on.
5. Add CI (build, suite, checked mode) on every push.

### Step 2. Fix the known memory-safety bugs (A1.1-A1.6)

1. Add the probes as conformance programs first (`118_args_rewritten.cs`, `119_finalizer_resurrects.cs`,
   `120_stack_limit.cs`), each failing.
2. A1.1: `Stable` only when the local cannot be written during the call; else retain for the call.
3. A1.2: re-read the count after the finalizer.
4. A1.3: lock in `ObjectMarks.OnDestroy`.
5. A1.4: full per-slot inside/outside record; a stress test that links and unlinks through arrays and wide objects.
6. A1.5: address-escaping fields count as written.
7. A1.6: stack limit check and guard gap; `StackSize` honoured for workers.
8. Sweep every other `Own.Stable` producer for the same assumption, with a test per case found.

### Step 3. Measure where the time goes (A2.1, A2.8)

1. Fix the measure: NativeAOT side on the same .NET version, pinned; true peak memory (sample `memory.buffer`
   growth and the allocator's high-water mark); results written to `docs/measurements/<date>.json` by the script.
2. Replace the kernels with real programs: the self-hosted compiler over a fixed set (hello, the conformance
   sources, a GodotSharp game), a JSON round trip, a text-processing program, the game simulation.
3. Run four builds of the self-hosted compiler on that set, in the same engine:
   - (a) as shipped, threads; (b) no threads;
   - (c) counting off, nothing freed (the upper bound of what memory management can cost);
   - (d) (c) with allocation as a bare pointer bump.
4. Read the result: NativeAOT's time against (c)/(d) is the code-quality gap; (b) minus (c) is counting and freeing;
   (a) minus (b) is thread mode. Count executed retains, releases, retained loads, allocations, frees and arena
   operations in each.
5. Write the split down. Steps 4 to 7 are ordered by it; the order below assumes what the audit expects (thread
   mode first, then code quality, then counting).

### Step 4. Static thread escape (A2.5, A3.4, SPEC 4.5)

1. Per allocation site, decide at compile time whether its objects can be reached by a second thread: captured by a
   delegate handed to a thread starter, stored in a static, or stored into an object already shared. Whole-program,
   flow-insensitive, on the type-and-field graph the arena analysis already builds.
2. A type all of whose allocation sites are thread-local gets plain counts and plain loads; a shared type gets
   atomic ones. Where a value's static type covers both, the operation is the atomic form (correct for both). No
   run-time shared bit (owner's rule).
3. Thread-local objects go back to the paged heap; shared ones use a heap that joins and reuses blocks.
4. Replace the name list of thread starters (`Scheduling.cs:38-47`) with the analysis's own sources.
5. Remove the `ThreadsSeen` restart: thread use is known from reachability before code generation.
6. Target: the threaded self-hosted compiler on one thread within 5% of the no-threads build.

### Step 5. A mid-level IR with explicit ownership (A2.2, A6.3, A6.4, A4.10)

This is the one structural change; steps 6, 7 and 9 need it.

1. Spike first, a day at most: build Roslyn's `ControlFlowGraph` for every method of the conformance sources and
   record (a) which constructs it flattens (expected: `?:`, `??`, `?.`, `&&`/`||`, `using`, `lock`, `foreach`,
   loops, `switch`), (b) which it leaves as operations (expected: patterns, lambdas, `await`, `yield`), (c) where it
   fails or loses a fact the ownership analysis uses today (a `foreach` element's borrow, a local that is never
   reassigned), and (d) its cost in build time. If (c) or (d) rules it out, the IR is built from the operation tree
   directly and the rest of this step is unchanged.
2. Define the IR: typed, block-structured, values in SSA form; C#-level operations already lowered (no patterns, no
   `foreach`, no closures) but above WASM (field load/store by symbol, array access with its check as a separate
   operation, call with a resolved target or a slot, `retain`, `release`, `move`, `borrow` as instructions). Captured
   variables and state-machine locals stay variables in the IR until a late pass gives them their storage, so the
   ownership analysis sees them as locals. Every instruction keeps its Roslyn operation and location.
3. Split `FunctionCompiler` in two: operation tree (through the control-flow graph, if 5.1 holds) to IR, IR to stack
   code. Move one construct family at a time behind a switch that falls back to the old path, with the suite passing
   at each move; delete the old path at the end. Each refusal site of A5.1 that the control-flow graph flattens goes
   with its family, with a conformance program.
4. Move the analyses onto the IR as classes with an input and an immutable result: escape, field borrows, owned
   parameters, fresh stores, arena membership. The string-keyed dictionaries on `Driver` go with them.
5. Give `WasmFunction` a role enum and the runtime functions typed identities; remove every `Name.StartsWith`.
6. Keep the WASM passes that are about encoding (locals packing, merging, ordering, data packing); retire those the
   IR passes replace.
7. Unit tests per pass on small IR inputs.
8. Give the IR a stable serialized form (needed by step 12.4).

### Step 6. Optimizations on the IR (A2.2, A2.3, A2.4, A2.6)

In the order step 3's numbers give; expected order:

1. Class-hierarchy devirtualization; guarded direct calls for two or three implementations.
2. Inlining on the IR, including callees that can throw (the inline table already exists: `cs2wasm.inline`).
3. Bounds-check and null-check removal by dominance and induction ranges; hoisting of invariant loads.
4. Counting removal by dataflow: cancel retain/release pairs across blocks, sink releases, borrow across calls that
   provably do not write the source.
5. Ownership specialization as SPEC 4.1 states it: per function, for the ownership its callers need, virtual groups
   sharing one borrow-first form; returns owned, with a borrowed-return variant where every caller borrows.
6. Reuse: a release that frees immediately followed by an allocation of the same size class takes the block
   directly.
7. Report the removed-count percentage from the build on the step 3 programs, in `docs/measurements`.
8. Target: no program in the set slower than NativeAOT's. Faster is welcome, not required. Stop optimizing a
   program once it is at parity and go to the next slowest.

### Step 7. Cycles: groups (redesign decided 2026-11-18: "redesign first")

The owner chose to redesign before extending today's machinery to closures, tasks and structs (A1.8). This is the
design; 7.A to 7.E are its stages, each ending with the suite green.

**What is kept from the decisions so far:** no collector, no search for garbage at run time, no list of things to
free later, no annotation; cycles that can be proven away are (7.E); a part of a group that is let go while the
group lives may stay until the group goes, when its objects still refer to each other.

**The model.**

- The type graph covers every heap object that holds references: classes, arrays, boxes, closures' display
  objects, delegates, async and iterator state machines; and every reference slot in them, also inside a struct
  that is a field or an element. Its strongly connected components are the cycle-capable types, as today.
- An object of a cycle-capable type is counted like any other object: one count word, every reference to it
  counted, freed when the count is zero. So everything that is not on a cycle at run time goes at once, in a group
  or not (a node popped off a list, a child removed from its parent).
- **A group** is a set of such objects that refer to each other. It has one number of its own: the references to
  its members from outside it (locals, statics, objects that are not members). When that number is zero and
  members are left, they are a cycle nothing can reach: they are all destroyed (finalizers first, then each as
  any object, references among them not counted down).
- **The rule that needs no walk:** a reference in a slot of object C to object V is *inside* exactly when C and V
  are of the same component of the type graph; and whenever such a reference is stored, C's and V's groups become
  one (a union of two records: constant time). So two objects of one component that are in different groups never
  refer to each other, an object that is in no group yet has only outside references (its count is its group's
  number the moment it gets one), and joining needs no look at what the objects hold. Today's join walk, owner
  chains, up-references, slot bits and kill queue all go.
- A store into a slot is then: the new value's group joined with the container's when the two are of one
  component, and the reference it had counted as an outside one no longer is; the old value let go as an inside
  reference when it is of the container's component, as an outside one otherwise. Whether a reference is inside
  is a fact about the two objects' types, not about how it was stored: nothing has to be remembered per slot.

**The constraint that shapes the rest: the container must be known at every store.** A reference is stored into a
heap object through its field, but also through a `ref`, a span, a pointer, a struct's own method (`this` is the
struct inside some object), or a block copy. The code there has the slot's address and nothing else. Counting such a
store as an outside reference while another path later lets it go as an inside one loses a count; the reverse frees a
group that is still referred to. Today's runtime has this hole (A1.4 is one face of it). Two ways close it:

- a mark in the stored reference itself (its low bit): safe, but every read of a reference from a field of a
  cycle-capable type pays a mask;
- **the allocator can say which object an address is in** (chosen): small blocks already lie in pages of one size
  (the block is arithmetic on the address); blocks above 2 KiB get the same in larger pages, and blocks above
  16 KiB a table entry per 16 KiB. Reads cost nothing; a store whose container the code does not know asks the
  allocator. It also gives threads the paged heap they lack (A3.4) and is what a group needs to keep its members
  in pages of its own and release them by returning the pages (7.D).

**Stages.**

- **7.A The allocator answers "which object is this address in".** One paged heap for programs with and without
  threads: small pages (to 2 KiB), medium pages (to 16 KiB), large blocks with a table. `Rt.OwnerOf(address)`:
  the object, or none for the stack, the statics and memory that is no object. A checked build tests it on every
  store through an address. The rule threads need for reads that count stays: a block keeps its kind for good.
- **7.B The group runtime, under the interface the compiler has today** (`Link`, `Unlink`, the count word's
  sign for a member), for the sites it has today: union of two group records, the group's number, a group's
  destruction, a finalizer that brings a member back (the destruction is called off: the finalizers ran before
  anything was let go). A store through an address resolves its container. Tests 016, 017, 082 to 085, 090, 119,
  121 and the fixed-memory API checks are the measure; test 127's `tree`, `ring`, `cut`, `kept`, `self`, `array`
  lines must hold or improve.
- **7.C Every container.** Sites for references inside structs (a field, an element, a box), for display objects'
  captured variables, delegates' targets, state machines' fields; block copies that say where they copy to;
  `MemberwiseClone`. Test 127 passes whole and loses its `known-gap` line.
- **7.D Groups that keep their members together.** Where the compiler sees at an allocation which group the
  object is for (it is stored into a member before anything else can see it), the object comes from that group's
  pages. A group whose types have no finalizer and refer to nothing counted outside it is released by returning
  its pages. The longest single runtime call per program is measured against NativeAOT's longest pause (the
  bench's latency figure).
- **7.E Cycles proven away.** A reference back to the object that owns the holder (a child's parent) is a borrow
  where the compiler proves the holder cannot outlive it: the types leave their component, and such objects are
  never in a group. A structure built new and attached whole (the isolated-value rule) likewise. Each proof takes
  types out of the graph; nothing else changes.

**What a program pays.** An object of a cycle-capable type: a few words before its header (its group, its place
among the members). A store that may join two objects: a look at two types, and a union when their groups differ.
Counting a member from outside: its count and its group's number. Letting a group go: its members, one after the
other, at that moment (7.D removes the visit where it can). With threads the group's number is shared state: step 4
decides which groups more than one thread can touch.

**Refinements for speed, taken up when the owner asked whether anything would make the structure faster
(2026-11-18).** None changes the structure.

- A member keeps the number of inside references to it beside its count, and its group counts the members that
  have a reference from outside, not the references. Counting a member that outside code already holds then
  touches the member alone, as for any object; the group is looked at only when a member gets its first outside
  reference or loses its last.
- A `ref` to a slot of an object the method itself names (`ref var x = ref a[i]`) knows its container: only a
  store through a reference that came from elsewhere asks the allocator.
- "Written only while nothing else can reach the object" is asked of display objects, delegates and state machines
  as of classes, so most of them are in no component at all.
- A program with threads locks the group it changes, not the whole runtime.
- The block of an address in a page of small blocks is a multiplication by the size's reciprocal kept in the
  page, not a division.

**One thing found while starting 7.B, which changes one sentence of the model (2026-11-18).** "Joining needs no
look at what the objects hold" is true only if every store of one component's object into another links at once.
Today a store into an object that nothing else can reach yet is a plain counted store (it cannot close a cycle), and
that is what makes building a tree or a list of a cycle-capable type cost what building any objects costs. Giving
it up makes every such object a group member as it is built: by the instructions it takes, about half as much
again for an object that is allocated, linked and freed (not measured). Keeping it means that the first time such
an object is stored where a cycle can close, what it holds is linked then: a visit of the structure built so far,
each object once in its life, at the moment it is attached. 7.B builds the runtime so that both can be measured
(the compiler emits the plain store or the link; the runtime is the same), and the numbers decide: time on the
bench's `tree` and `linked_list`, and the longest single runtime call.

**What it does not do.** A cycle that is let go inside a group that lives on stays until the group goes (a build
notice names the types, as today). The component that `object` fields make (anything that can hold anything) is one
component: objects of it stored into each other share a group. Both are measured in 7.B and 7.C before 7.D and 7.E
are ordered.

**Correction, 2026-11-18, on reading the runtime whole before replacing it.** The audit's summary of it ("counted
raw pointers", "arenas only merge", walks) was mine and was wrong in what mattered. `Boundary/Runtime.cs` already
is the model above: an object of a cycle-capable type starts as a plain counted object; a store that can close a
cycle puts container and value in one group (a union of two records, constant time, whatever their sizes); a member
has its count of inside references and its count of outside ones, and its group counts the members that have
outside ones; a member nothing refers to goes at once, a group nothing outside refers to goes whole.

And it has one thing the model above gives up: **a reference from a member to one that holds it (a child to its
parent) is not counted at all, decided at run time along the chain of owners**, so a subtree with parent links that
is cut from a living tree is destroyed at the store that cuts it. The fixed-memory checks of the suite ("a
long-lived tree that keeps dropping subtrees stays in 16 MB") depend on it, and so does any game that removes nodes
from a scene. The model above would have kept such a subtree until the whole tree goes. The compile-time proof of
7.E cannot stand in for it: a child may outlive its parent in ways a compiler cannot rule out, and the run-time
chain handles those.

So nothing is replaced. What was wrong with the runtime is narrower, and it is what step 7 now is:

- **7.B (done 2026-11-18): a store that has only the slot's address is the store through the object.** Through a
  `ref`, a span, a struct's own method, `Interlocked`, a block copy of references. The allocator says which object
  the slot is in (7.A); the object's type says whether the slot is one a cycle can close through (`ArenaIsSlot`,
  generated). Before, a slot whose address the program took was left out of the graph for good (cycles through it
  were never freed: test 128 left four objects), and a store through a span into a slot that was not left out
  counted the old value as an outside reference whatever it was.
- **7.B2: what a slot holds is known for every slot.** A member keeps two bits for each of its first sixteen words
  (nothing, an inside reference, an uncounted one to its owner); for an array's elements and later fields
  `ArenaUnlink` goes by the counts (A1.4). Bits for every slot, in a block of their own for an object that has
  more than sixteen: no guess, and a child's link to its parent may then be an element or a late field too.
- **7.C: every container** (unchanged): references inside structs, boxes, display objects, delegates, state
  machines. Test 127.
- **7.D, 7.E** as above, after the measurements of 7.C. The store into an object nothing else can reach stays a
  plain store, and what such an object holds is linked when it is first stored where a cycle can close, as today:
  the paragraph above that proposed to measure the other way is moot.

### Step 8. Plugin fixes that do not need the IR (A4.1, A4.5, A4.11)

1. `AddSource` compiles with access checks off for the game's assemblies (Roslyn's `IgnoresAccessChecksTo` on the
   generated compilation), and the compiler honours it; support field `[UnsafeAccessor]` as well.
2. Make the write hook cover every form `Accesses` reports as a write (`ref`/`out`, deconstruction, initializers),
   or report those forms as "cannot instrument" so the plugin treats them as unsure.
3. Tests for every existing interface member, including the rejection paths.

### Step 9. The plugin interface on the IR (A4.2-A4.10, goal 3)

1. Interface version 4: a plugin receives the IR of every reachable method after the analyses and before the
   optimizer (SPEC 7.1's place), and may rewrite it: insert, replace or remove instructions; add functions built as
   IR or as C#; clone a function and edit the clone. This is the LLVM-pass model, and it is what makes "potential
   needs" answerable.
2. On top of that, the conveniences the Invisible API needs today, each written against the IR:
   - a hook at any single write, read, call or branch, receiving the receiver, the old and new value, and for a
     collection the key; and able to replace the value (branch outcomes, random streams, rounding, limits);
   - accesses with collection entries (indexer and mutating calls on a field's collection resolved to the field),
     through locals by the real alias analysis;
   - path conditions from the IR's control flow, across calls;
   - the ownership-based alias query SPEC 7.1 names;
   - the call graph with delegate targets where known and an explicit list of unsure sites (unknown delegate,
     reflection, `dynamic`);
   - clones that follow virtual calls and lambdas, callable from generated code, with suppression by import set.
3. Closed forms as a real solver over the IR: linear recurrences in several coupled variables (matrix form), known
   pure functions (`Lerp`, `Clamp`, `Min`, `Max`) expanded, guarded pieces with the tick at which each guard flips;
   results as IR or C# the plugin can emit, not strings.
4. Write the Invisible API plugin's first slice in the Bazoodle repository against version 4 (classification,
   "mark changed", one gate, one solved timer) as the acceptance test; each gap it finds is a compiler issue.
5. Keep versions 1 to 3 running through an adapter over the IR.

### Step 10. Configuration and reentrancy (A6.1, A6.2, A5.10)

1. One `CompilerFlags` record on `CompileOptions`; the CLI parses the environment into it; no other
   `GetEnvironmentVariable` in the library. Diagnostics output goes to a writer the embedder supplies.
2. Delete each kill switch whose optimization has passed checked mode; the few that stay are options, recorded in
   the module's version section so "same inputs, same bytes" is checkable.
3. Statics become fields of a per-compile context; library caches become a `LibraryCache` object the embedder owns.
4. Fetching sources (git, Godot editor) moves out of the library into a tool; the library takes a directory.
5. A test that runs two compilations concurrently and compares each with its sequential result.

### Step 11. Spec conformance (A5)

1. A5.1: the refusal sites that are control-flow shapes go in step 5.3. Work through the rest by frequency in real
   code; each fixed site gets a conformance program. Add Roslyn's and dotnet/runtime's own test programs where they
   fit, to find the sites nobody has hit yet. The remaining count is reported by the build of the suite.
2. A5.2: a missing capability compiles to a throw of .NET's exception at run time; only platform invoke stays an
   error. A5.3 goes with it.
3. A5.4: two-pass filters decided per `try` from what its filters can observe, library included.
4. A5.7-A5.9: secure randomness from a host import that fills bytes; string hash seed fixed in the module; identity
   hash from a per-instance counter that scheduling cannot reorder, or marked scheduling-dependent.
5. A5.12: a notice when a program has `Main` and no export; the CLI roots `Main` for console programs.
6. A5.6: stack frames from the module's own tables for engines without V8's stack text.
7. A5.13-A5.14: assembly-level keep attribute; export signature check; a location on every diagnostic.
8. A5.5: share reference-type instantiations in the IR where the body does not depend on the type.

### Step 12. Self-hosting and the proofs (goal 1, A5.16, A5.17)

1. With steps 2, 4 and 7 done, re-run the self-hosted compiler on the step 3 set in checked mode; fix the trap, the
   memory growth and the multi-core hang it shows.
2. An automated check: the self-hosted compiler's output equals the .NET-hosted compiler's, byte for byte, for every
   conformance program.
3. Proof 4 as a script: time, size and peak memory of the self-hosted toolchain against NativeAOT's WebAssembly
   build over the fixed set; goal 1 is met when every row is better.
4. Compiler speed itself (A2.7): cache the library's IR and analysis summaries between builds, keyed by the library
   version and the compiler flags, so a build parses and binds only the program (SPEC 7.6 allows it); ship that cache
   in the toolchain download in place of the library sources. Then one static-initializer pass, hashed keys instead
   of display strings, indexed function lookup. Target: the browser build budget of a few seconds for a small game.
5. Proof 5 in the browser on stock Godot; a headless path without JS (Wasmtime needs the import surface in a second
   host).

### Step 13. Repository (A7, A5.15)

1. LICENSE and a third-party notice. README matching the tree, with prerequisites.
2. `docs/STATUS.md` restored as the generated measurement summary; stale comments removed (A6.7).
3. One shared import implementation for the Node and web hosts.
4. A `WellKnown` symbol table replacing name literals; each remaining by-name special case either becomes a general
   rule or is listed with its reason (A6.5).
5. Record each modified upstream file and why, so the boundary is auditable (A5.15).

### What can run in parallel

Steps 1, 2 and 3 first, in that order. After them: step 4, step 8, step 10 and step 13 are independent of the IR and
of each other. Step 5 gates steps 6, 7 and 9. Step 11 can proceed alongside throughout, except 11.8. Step 12 closes.

### Open decisions for the owner

None are open. All three were decided by the owner on 2026-11-18:

1. A large free is handled by a release that does not visit the objects (step 7.D): regions and groups released by
   returning their pages. The price is accepted: a member dropped early stays until its region or group goes. This
   supersedes the 2026-11-16 ruling against retention, for regions and groups only; the growth is measured and
   reported (steps 7.B, 7.C), never hidden.
2. The back-reference proof goes ahead (step 7.E), with compile-time scoping (the groups of step 7) for what stays cyclic. The
   run-time owner-chain counting built on 2026-11-16 (`Runtime.cs`, "Owners") is the fallback where the proof fails,
   and goes with step 7.B: the redesign of 2026-11-18 replaces it.
3. Decided by the owner, 2026-11-18: the bar is **the same speed as NativeAOT, lower memory, no frame-time spikes**.
   Faster is welcome, not required. Step 6's target is therefore "no program in the set slower than NativeAOT's";
   step 6.6 (reuse) is done only if step 3's numbers call for it.

What was agreed for decisions 1 and 2:

- Decision 1: the alternative to a large synchronous free that the owner's rules allow is a free that does not visit
  the objects. An arena whose member types have no finalizer and hold no counted reference to anything outside it
  can be released by returning its pages. The compiler knows both facts per type. The same applies to an owned tree
  with no cycle (a list of nodes, a parsed document): allocate it in a region of its root and release the region.
- Decision 2: compile-time scoping does not replace the back-reference proof; they do different jobs. The proof
  removes the commonest cycle (a child's link to its parent) so the child stays a plain counted object and is freed
  the moment it is dropped. Scoping makes what is still cyclic cheap. Without the proof, a long-lived parent with
  children that come and go (a world and its enemies) is one arena that only grows.

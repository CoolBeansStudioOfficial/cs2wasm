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
| A5.17 | Proof 4 and proof 5 not met | self-host: traps, 1 GiB, hang on more than one core (`proof5_hello2d/README.md:85-100`); browser build of the game fails; hello world 23-30 s in the browser |

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
  - A1.6 fixed: the first thread's stack lies under the data, so a frame past its end traps at its first write
    with no check in the code; a program with threads compares each frame with its stack's end; `stackalloc`
    checks its size. The default stack is 960 KiB (hello world: 28,089 bytes). Worker stacks are still 1 MiB
    whatever `StackSize` says. Test 120.
  - A1.4 open: test 121 (cycles through arrays and far fields, stored while a static holds the container) passes
    in checked mode with no object left; the guess in `ArenaUnlink` is still there and goes with step 7.

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
  back-reference proof, and step 7.2.
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
  - Still open: collection entries as accesses (A4.4: a plugin can hook the key and the store itself, but
    `Accesses` does not report them), a hook after a statement, the closed-form solver (A4.6), conditions across
    calls and the ownership-based alias query (A4.8), the list of unsure sites, plugins before the first compile
    (A4.9), and the first slice of the real plugin in the Bazoodle repository (9.4).

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

### Step 7. Arenas that end (A3.1, A3.2, SPEC 4.3)

Three parts, in this order. The first removes cycles, the second makes the remaining ones cheap, the third makes
releasing a group cost nothing per object.

1. **Prove back references away** (needs the owner's decision, open since 2026-11-16). Where the compiler proves a
   reference points back at an object that owns the holder (a child's link to its parent) and cannot outlive it,
   the reference is a borrow, the types are no longer on a cycle, and the child is a plain counted object freed the
   moment it is dropped. Where the proof fails, the reference stays counted and the types stay cycle-capable. A
   long-lived parent with children that come and go (a world and its enemies) depends on this part: without it, it
   is one group that only grows.
2. **Scope the remaining groups at compile time** from the IR: a group per owning root of a cycle-capable component
   (the object whose release frees it), members allocated into it at their allocation site. Inside references are
   not counted, so a store inside a group is a plain store and joining needs no walk.
3. A store that would link two groups is the one remaining run-time case: keep the merge, record it, and report each
   such site as a notice so the common shapes can be given a static scope.
4. **Release without visiting.** A group whose member types have no finalizer and hold no counted reference to
   anything outside it is released by returning its pages; the compiler knows both facts per type. Apply the same to
   an owned structure with no cycle (a list of nodes, a parsed tree): allocate it in a region of its root and release
   the region. Nothing is postponed, so this is within the owner's no-deferred-free rule.
5. Accepted cost: a dropped part of a live group stays until the group goes. Measure it per program in checked mode
   (step 1.2) and report it in the build notices, so a group that grows without bound is seen.
6. Add a latency measure to the bench: the longest single runtime call per program, against NativeAOT's longest GC
   pause. "No frame-time spikes" is met when no runtime call exceeds a stated bound on the measured set.

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

1. A large free is handled by a release that does not visit the objects (step 7.4): regions and groups released by
   returning their pages. The price is accepted: a member dropped early stays until its region or group goes. This
   supersedes the 2026-11-16 ruling against retention, for regions and groups only; the growth is measured and
   reported (step 7.5), never hidden.
2. The back-reference proof goes ahead (step 7.1), with compile-time scoping (step 7.2) for what stays cyclic. The
   run-time owner-chain counting built on 2026-11-16 (`Runtime.cs`, "Owners") is the fallback where the proof fails,
   until step 7.2 covers the case; it is removed when nothing reaches it.
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

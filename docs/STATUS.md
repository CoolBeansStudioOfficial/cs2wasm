# cs2wasm: implementation status

What exists today, measured against [SPEC.md](SPEC.md). Claims here are what the test suite checks; anything not
tested is labelled so. Run `dotnet run --project tests/Cs2Wasm.Tests` to reproduce the results.

## Results

- **Conformance (proof 1, partial):** 17 C# programs (arithmetic and math, classes and virtual/interface dispatch,
  structs and enums, strings and formatting, exceptions with stack traces, collections, LINQ and closures,
  generics and pattern matching, nullable value types, tuples, iterators, async/await on one thread, `typeof` and
  seeded `Random`, records, reference cycles) produce output identical to .NET 11's, byte for byte.
- **API checks:** 13 checks of the module shape, embedder API and memory pass (boundary imports, sandbox errors,
  custom and version sections, shared memory import, reproducible output, exception reporting at exports, plugin
  passes, source map, cycles bounded in a fixed 16 MB memory, no arena code without cycle-capable types).
- **Module size (gzip -9, measured):** "hello" 5.0 KB (12.7 KB raw); the LINQ/closures test 43 KB (156 KB raw);
  the async test 20 KB (71 KB raw). The Rust 2D game bar in the spec is 24 KB; no size work has been done beyond
  reachability and the final cleanup pass, and the library's number formatting and exception machinery is in
  every module.
- **Build time:** 150–800 ms per test program on the .NET runtime (desktop), after the first compile. Not measured
  in the browser.
- **Run-time speed vs NativeAOT:** unmeasured.
- **Reference-count operations removed at compile time:** unmeasured (no counter yet).

## Section by section

### 1–2. Pipeline and API — done
Roslyn `Compilation` in, module bytes + diagnostics + source map out (`Cs2WasmCompiler.Compile`). The compiler reads
the operation tree (`IOperation`), not IL. Several compilations are accepted (`CompileOptions.AdditionalCompilations`).
A command-line wrapper (`cs2wasmc`) exists for tests.

### 3. Language coverage — partial
Done: classes, structs (inline values, copy semantics), inheritance, virtual/abstract/override, interfaces with default
methods, static abstract interface members, generics (monomorphized per instantiation), properties (auto, `field`),
indexers, operators and user conversions, enums (incl. `[Flags]` formatting), delegates (multicast), lambdas and local
functions with captures (per-iteration capture as in C#), events, static constructors (lazy, precise vs
beforefieldinit), `checked`/`unchecked`, integer semantics (wrap, overflow and divide-by-zero exceptions,
saturating float→int as .NET 9+), exceptions (`try`/`catch`/`finally`/`when`, rethrow, `using`, `using var`),
pattern matching (type, constant, relational, `and`/`or`/`not`, property patterns, `var`), switch statements and
expressions, string interpolation (alignment and formats), nullable value types (lifted operators, `??`, boxing,
patterns), tuples (literals, names, conversions, equality, deconstruction incl. `foreach`), collection expressions for
arrays and lists, `ref`/`out`/`in` parameters, ref locals, pointers (`*p`, `p[i]`, pointer arithmetic).

Also done: iterators (`yield return`/`yield break`, inside `try/finally` and `foreach`, `IEnumerable<T>` and
`IEnumerator<T>`), `async`/`await` (`Task`, `Task<T>`, `ValueTask`, custom awaiters through the awaiter pattern
such as Godot's `ToSignal`, `try/catch/finally` around awaits, `await` inside expressions with left-to-right spilling,
`SynchronizationContext.Post` for captured contexts), records (class and struct: primary constructors, positional
properties, value equality, `ToString`/`PrintMembers`, `with`, copy/clone, `==`, `Deconstruct`, inheritance),
`typeof`/`GetType()` (`Name`, `FullName`, `Namespace`, `IsValueType`...), array-backed `Span<T>`/`ReadOnlySpan<T>`
(enough for `params ReadOnlySpan<T>` overloads and collection expressions).

Iterators and async methods are lowered like Roslyn's: a state machine object holds every local; its MoveNext is a
dispatch loop over Roslyn's control-flow graph, one resumable segment per basic block and per suspension point, with
a single exception handler that finds the catch or finally region of the segment that was running.

Not done yet (a program using them gets a `CW0101` error naming the construct):
- async and iterator lambdas and local functions; async iterators (`IAsyncEnumerable`, `await foreach`);
  exception filters inside iterators and async methods; `await` inside `?:`, `&&`, `||`, `??` sub-expressions;
- lambdas inside loops of an async method or iterator share one variable per loop instead of one per iteration;
- non-record primary constructors (C# 12 classes), positional and list patterns;
- multi-dimensional arrays, `stackalloc`, `fixed`, spans over stack or native memory;
- reflection beyond `Type` names, `dynamic`, the interpreter for runtime code generation (section 8);
- `goto`;
- exception filters run in one pass (a filter runs after inner `finally` blocks, not before as in .NET).

Async on one thread: continuations run when the awaited task completes (or are posted to a captured
`SynchronizationContext`); `Task.Run` work, `Task.Yield` and `Task.Delay` timers are queued and run when code blocks on
a task (`.Result`, `Wait`, `GetAwaiter().GetResult()`), in posting order and timer order. `Scheduler.RunPending`
exists for the engine to call each frame but is not exported yet. Timers do not wait for real time when a blocking
wait drains them.

### 4. Memory — partial
Done: every heap object is reference counted with compile-time ownership: locals and fields own, reads of locals and
parameters borrow for free, borrowed values that a call could invalidate are retained for the call, temporaries are
released at the end of their statement, owned locals are released at scope exit, on `break`/`return` and during
exception unwinding. Structs are inline values carrying their references' ownership. Finalizers run when the last
owner releases (checked: deterministic order). Freed memory is reused (size-class free lists): a 300 000-iteration
allocation loop stays inside the initial 16 MB.

Cycles (4.3): done for class fields and arrays of references, see the design below. Checked: a loop building
1 000 000 two-object cycles and a long-lived tree that keeps dropping subtrees (100 000 rounds) both run in a fixed
16 MB memory (no growth allowed); without arenas they need 505 and 432 pages and fail. Two conformance programs
(`016_cycles`, `017_cycle_stress`: 60 000 random mutations of a cyclic graph) match .NET. A program whose types cannot
form cycles contains no arena code (checked).

#### Cycle design (section 4.3)

**Which types are cycle-capable: field-target analysis.** Every store of a reference into a class field or an array
element is a *site* (`Compiler/Arenas.cs`). The compiler records, per site, the static types of the values stored
there, before conversion (`a.Tag = node` records `Node`, not `object`; `null` records nothing). Generic code is
monomorphized, so `List<Node>._items` is a `Node[]` site that only ever receives `Node`. When the whole program is
lowered, the type graph has an edge from every constructed type that has the slot to every constructed type that is
a subtype of a recorded value type (a covariant array only to its own element type). Its strongly connected
components with a cycle are the cycle-capable groups. This is what keeps `object`, interface and delegate fields from
merging everything into one component: an `object` field only points at what the program puts in it. A site whose
edges stay inside one group is *cyclic*; all other sites compile to the plain counted store. Sites are emitted as
calls to placeholder functions and resolved once the graph is known (`Arenas.Resolve`), so non-cyclic stores cost
nothing, and a program with no cyclic site gets no arena runtime at all. A slot whose address is taken (`ref`/`out`,
`ref` returns, spans over the array) is written outside the protocol, so its site is kept plain.

**Arenas at run time** (`Lib/Runtime/Runtime.cs`, `Arena*`). Objects start as plain counted objects. A store at a
cyclic site, when the value and the container belong to the same group, puts both into one arena: a plain object
joins the container's arena (or a new one), two arenas merge (the smaller one's members move, so merging is
O(n log n) overall). The stored reference is then not counted. A member's header holds `-arena` in the count word
(values below -1; -1 stays immortal) and its index in the arena's member table in the hash word (the hash code moves
to the table). The table keeps, per member, its *outside* count: references from locals, statics, other objects and
other arenas. `rt.retain`/`rt.release` take the arena path only for count words below -1, so plain objects pay one
never-taken branch. The arena is freed when the sum of outside counts reaches zero; member-to-member slots are cut
first, then finalizers run and outside references are released, then the memory is freed.

**Scoping and splitting.** An arena is the set of objects linked by cyclic-site stores: an object graph, not a type
and not the program. Objects stored in statics are *pinned* and never join an arena (statics never end, and shared
singletons such as `Array.Empty<T>()` would otherwise join every graph). Arenas never split into components; instead
an arena **sweeps** itself: overwriting a member-to-member reference, or a member's outside count falling to zero,
is a dirty event, and after as many dirty events as half the arena's size the arena marks from its members with
outside counts, through member-to-member slots only, and frees the unmarked members. The cost is proportional to the
one arena and amortized over the operations that produced the garbage; nothing else is scanned. This is what keeps a
long-lived graph (a scene tree whose children come and go, a `List<T>` whose old arrays are replaced) bounded.
*Owner review:* the sweep is a mark of one arena triggered by the program's own stores, which is how this design
answers "when they split"; it is not a heap scan, but it is a tracing step.

**What is not covered** (references there stay counted, so a cycle closed only through them still leaks):
- references inside structs embedded in heap objects (`Dictionary` entries, `KeyValuePair`, tuples in fields);
- closure display slots and delegate targets, so an event-handler cycle (`node.Clicked += () => node.Foo()`) leaks;
- iterator and async state machines; slots whose address is taken.
- The analysis is per type, not per allocation site: chained LINQ iterators (`Where` over `Select` over `Where`) are
  cycle-capable and pay the link cost though no instance ever forms a cycle.
- Finalizers of members run when the arena is freed or swept, in table order; a finalizer that stores references
  into its arena while the arena is being freed is not supported (the store stays counted). Arenas are
  not thread-aware beyond the runtime lock: arena operations take it, but a cycle shared across threads is untested.
- Size: the arena runtime adds about 4 KB (raw) to modules that need it.

Also fixed on the way: freeing a large block at the top of the heap moved the bump pointer back without clearing
the block, so later objects could start with stale fields (found by the sweep's temporary buffers).

Not done:
- ownership specialization of functions, retroactive owners, the counting-elision statistics (4.1);
- weak references, `ConditionalWeakTable` (4.4);
- threads (4.5), see below: per-object sharing analysis, `AsyncLocal<T>`, parking (7.2).

### 4.5 Threads — partial
Done: `System.Threading.Thread` runs on a real worker: each thread is a new instance of the module on the shared
memory, started through the engine import `cs2wasm.thread_spawn(stackTop, tls, id, arg)` at the per-thread export
`__thread_entry`, which sets that instance's stack pointer, thread-local block and thread id (globals) before running
the delegate. Each thread gets a 1 MB shadow stack and a thread-local block holding `[ThreadStatic]` fields. The WASM
atomic instructions are emitted for `Interlocked`, `Volatile` and the runtime; `lock` is `Monitor.Enter`/`Exit` in a
`try`/`finally`, and `Monitor` is a real reentrant lock per object with `Wait`/`Pulse`/`PulseAll`. The allocator and
the lock table share one reentrant runtime lock. `Join`, `Sleep` and lock waits block with `memory.atomic.wait32` on
workers and spin on the engine thread, which never blocks. An exception escaping a thread is reported like one
escaping an export. The Node host (`host/worker.mjs`) runs each thread as a `worker_threads` Worker; once a worker
exists, output is written through immediately so it stays in order.

Counts: when the program can start a thread, `rt.retain`/`rt.release` become compare-and-swap loops for every
object (immortal and arena count words are kept). This is program-wide, not the per-object analysis the spec asks for
(only objects reached from more than one thread should pay for atomic counts): still to do. Programs that never start
a thread keep plain counts, and the lock functions are empty.

Thread pool: background workers, one per core (`cs2wasm.processor_count`, at least two), started on first use and
taking work from one queue. `Task.Run`, `ThreadPool.QueueUserWorkItem`, `Parallel.For`/`ForEach`/`Invoke` (chunks
claimed with `Interlocked`, the caller works too) and `await Task.Yield()` off the engine thread run there. Tasks
complete under the runtime lock, so a continuation added while another thread completes the task runs exactly once;
`SynchronizationContext.Current` is per thread. Blocking waits (`Task.Wait`/`Result`, `WaitAll`/`WaitAny`,
`SemaphoreSlim`, `ManualResetEventSlim`, `CountdownEvent`) run the engine thread's posted work and timers while they
wait. Timers keep virtual time (the earliest fires at once) until a worker exists, so single-threaded programs stay
deterministic; after that they fire when due. `ThreadLocal<T>` keeps values by thread id. Background threads do not
keep the Node host alive, as in .NET.

Not done: `AsyncLocal<T>` (no execution context flows with continuations), the build notice for blocking waits on the
engine thread, concurrent collections, freeing a finished thread's stack and thread-local block, static constructors guarded across threads (two threads can both run a type's static initializer), the
scheduling-dependence marks for the plugin (7.3), and parking workers for snapshots (7.2).

### 5. Optimization — minimal
Done: reachability from exports (only reached methods are emitted), devirtualization when a virtual call has one
implementation or the receiver type is sealed, a final pass over the WASM (identical-body merging, `local.set`/`get`
→ `local.tee`, dead code after unconditional branches, call targets ordered by use for short indices).
Not done: inlining, escape analysis (stack allocation of non-escaping objects), constant folding beyond Roslyn's,
local packing, data segment packing.

### 6. The module — done
32-bit core module, imported shared memory (`env.memory`, size from options), passive data segment initialized by the
`init` export, exports from `[UnmanagedCallersOnly(EntryPoint)]` wrapped so no exception crosses them (reported
through `cs2wasm.report_exception`, export returns 0), boundary-table imports (one typed import per allowed method;
unlisted bodiless methods are `CW0002`), custom sections copied as given, `cs2wasm.version` (compiler and ABI
version), `name` section, `cs2wasm.lines` (code offset → file and line), `cs2wasm.frames` (.NET frame names), a v3
source map. The stack pointer global is exported (`__stack_pointer`, section 7.2); the other mutable state is in memory.
Stack traces: `Exception.StackTrace` and `ToString()` give C# frames with file and line in .NET's format
(`   at Program.Depth(Int32 n) in C:\...\x.cs:line 25`), captured through the engine at `throw` only (no cost on
paths that do not throw) and cut at the catching frame as in .NET. Lambda and local-function frame names
approximate Roslyn's generated names. Per-thread start export: `__thread_entry` (section 4.5), with `__tls_base`
exported beside the stack pointer.

### 7. What Bazoodle needs — partial
- 7.1 plugin passes: `ICompilerPlugin` (interface version 1) runs after lowering and before emission, sees the
  reachable method instances and the call graph, and can add custom sections and diagnostics (checked). The
  analyses it should expose beyond that (def-use, CFGs per lowered method, aliasing, closed forms of loop-carried
  values, instrumentation, cloning with suppressed imports, generated functions) do not exist yet.
- 7.2 snapshot/restore: state is in memory plus the exported stack pointer; parking threads: not done.
- 7.3 determinism: no relaxed SIMD (no SIMD at all), fixed-seed string hashing, allocation-order object hash codes,
  math computed in WASM (fdlibm ports, `Exp` in double-double), reproducible output (checked), NaN canonicalized
  where bits are read (`BitConverter`), `Random()`'s seed from the engine and `Random(seed)` giving .NET's
  sequence (checked). Not done: `Unsafe.As`-style bit reads, scheduling-dependence marking.
  Transcendental functions are not guaranteed to round like .NET's (UCRT/glibc) in every case; the tested inputs
  match.
- 7.4 the library: **cs2wasm's own library, not upstream `dotnet/runtime` trimmed** (the spec's decision). It is a
  self-contained corlib written for this project (about 6 500 lines: object model, strings, number formatting and
  parsing with .NET's exact rounding rules, math, exceptions, collections whose enumeration order matches .NET's,
  LINQ with deferred execution, StringBuilder). Moving to upstream sources is open work. Invariant globalization
  semantics are followed (culture comparisons are ordinal, as in .NET's invariant mode). Case mapping covers
  ASCII, Latin-1, Greek and Cyrillic only. Present since: `Task` and friends, `Random`, `Stopwatch`,
  `BitConverter`, `Type`, spans over arrays. Missing: `DateTime`, `TimeZoneInfo`, concurrent collections, `GodotSharp`.
- 7.5–7.6: no GodotSharp, no browser build, budgets unmeasured.

### 8. Sandbox — partial
Files, sockets, processes, environment and registry members, and platform invoke, are build errors naming the
capability (`CW0003`, `CW0004`). `unsafe` code and pointers compile. Not done: reflection metadata, the interpreter.

### 9. Hosting and self-hosting — not started
The compiler runs on .NET. It has not been run on the browser .NET runtime, and it does not compile itself
(it uses LINQ, iterators, generics over Roslyn's API — all of Roslyn would have to compile first).

### 10. Testing — started
Proof 1 is partially met (the programs listed above). Proofs 2–5 are not started (they need GodotSharp sources,
the Node2D wrapper generator, `hello2d`, and self-hosting).

## Known divergences from .NET

- `Environment.NewLine` is `\n` (as .NET on Linux and in browsers); the test runner normalizes .NET-on-Windows output.
- `IntPtr.Size` is 4 (32-bit WASM).
- Exceptions thrown inside the library show the library's own frames in stack traces, not CoreLib's.
- `ValueType.Equals`/`GetHashCode` for structs that do not override them compare addresses / return 0.

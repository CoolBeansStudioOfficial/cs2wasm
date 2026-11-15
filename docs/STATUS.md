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
a single exception handler that finds the catch or finally region of the segment that was running. Async iterators
(`IAsyncEnumerable<T>`, `IAsyncEnumerator<T>`) use the same machine: `MoveNextAsync` makes a fresh promise and runs
MoveNext, which completes it at the next `yield return`, at the end, or with the exception. `await foreach` and
`await using` (also with `WithCancellation`/`ConfigureAwait`) come from the control-flow graph's own lowering.
Disposing a suspended iterator (`Dispose`, `DisposeAsync`, leaving a `foreach` early) runs the `finally` blocks
around its `yield return`, innermost first, as .NET does. `await` works inside `?:`, `&&`, `||`, `??` and `?.` (the
graph's flow captures get machine slots before any segment is emitted). Variables that lambdas or local functions
of an iterator or async method capture live in displays held by the machine, as in Roslyn: one for the method and
one per loop iteration, so each iteration's lambdas see their own variable and a lambda stored in a local makes no
cycle with the machine.

Anonymous types: a slot per property, read-only properties, value `Equals`/`GetHashCode` (Roslyn's hash) and
`ToString` `{ A = 1, B = x }`, registered as object overrides since Roslyn exposes no symbols for them.

Fixed on the way: `string == string` (the predefined operator) compared references; it now compares values, as C#
defines it. Generic variance (`out`/`in` on interfaces and delegates) holds in conversions and interface dispatch;
lifted compound assignment (`int? x; x += 1`) and relational patterns on nullable values work.

`goto` (to a label in the same or an enclosing block, forward or backward, out of loops and `try`) compiles each
labeled block as a dispatch loop over its segments; a backward `goto` releases what the block declared after the
label. `goto case` and `goto default` put the switch sections in a dispatch loop. `fixed` holds the array, string
or object for the statement and gives its data address (nothing moves without a collector); `GetPinnableReference`
is used for spans and user types.

Exception filters keep .NET's two-pass order: in a program with a filter, each `try`/`catch` keeps a record on a
per-thread handler chain while its body runs, and `throw` (and `throw;`) first asks each record's test function
(type tests and filters, in order; filter variables live in a display as for lambdas) which catch takes the
exception, before unwinding runs any `finally`. An exception thrown inside a filter makes it false. Programs without
filters pay nothing.

Not done yet (a program using them gets a `CW0101` error naming the construct):
- spans over stack or native memory (`stackalloc` into a span allocates an array);
- reflection beyond `Type` names, `dynamic`, the interpreter for runtime code generation (section 8);
- filters inside iterators and async methods run in one pass (when the exception reaches their catch, after the
  `finally` blocks inside the try); an exception thrown by such a filter is not swallowed; the first pass of a throw
  does not see an iterator's or async method's own catch regions, so a filter further out can run for an exception
  such a body then catches; and an export called back from the engine does not hide outer frames' filters.

Async: continuations run where the awaited task completes, or are posted to the captured `SynchronizationContext`;
blocking waits (`.Result`, `Wait`, `GetAwaiter().GetResult()`) run the engine thread's posted work and timers. See
section 4.5 for the thread pool and timers. The engine runs posted work and due timers each frame through the
`cs2wasm_run_pending` export (present when the program posts work or uses timers).

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
  into its arena while the arena is being freed is not supported (the store stays counted). Arena
  operations take the runtime lock; a cycle among shared objects is not collected (shared objects stay out of arenas).
- Size: the arena runtime adds about 4 KB (raw) to modules that need it.

Also fixed on the way: freeing a large block at the top of the heap moved the bump pointer back without clearing
the block, so later objects could start with stale fields (found by the sweep's temporary buffers).

Weak references (4.4): `WeakReference`, `WeakReference<T>` and `ConditionalWeakTable` keep handles to their
targets; a target carries a mark (a side table by address, consulted by the destroy path only while marks exist), and
when its count reaches zero its handles are cleared before its finalizer runs; a table's entry, and its value, go
with the key. `GC.SuppressFinalize`/`ReRegisterForFinalize` are marks too; `GC.Collect` has nothing to do (objects
are freed when their count reaches zero). Programs without marks pay nothing.

Not done:
- ownership specialization of functions, retroactive owners, the counting-elision statistics (4.1);

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

Counts (section 4.5): atomic only where an object is shared. A shared object has bit 30 of its count word set; its
retains and releases are compare-and-swap loops, every other object's stay plain. An object is marked, with
everything it reaches (iteratively, with a per-thread worklist), when it is published: stored into a static field,
into an object or array that is already shared (containers known at the store: class fields, array elements,
captured variables, state machines), handed to another thread (`Thread.Start`, the pool), or stored where the
compiler cannot see the container (struct fields through addresses, struct copies) unless the address is on the
storing thread's own stack. So an object another thread can reach is marked before it can, and an object one thread
owns keeps plain counts (checked under stress with a per-thread allocation assertion during development).
Programs that never start a thread contain none of this, and the lock functions are empty.

Deferred destruction (upstream library): .NET lets one thread read a reference from a shared field while another
overwrites it, and upstream's threading code does so (benign races in tasks, the pool, `Parallel`). With counting,
the overwriting thread's release could destroy the object between the reader's load and its retain. So a shared
object whose count reaches zero waits in limbo (count word: shared bit, limbo bit, count 0) and is destroyed once
every thread running program code has passed a safe point since (a wait, the end of a pool work item, the start or
end of a thread or of an export), where a thread holds only counted references (arguments read from memory are
retained before calls); a count taken meanwhile keeps it alive. Epoch-based: per-thread epochs, quiescent threads
(blocked, idle, or the engine thread between exports) hold nothing back (`Boundary/Runtime.cs`, `Limbo`). A thread
that computes for a long time without a safe point delays the destruction of shared garbage, not its correctness;
a collection that cannot free anything returns at once, and collections run when the list has grown by half, so
their cost stays linear.
Shared objects never join arenas (their counts are atomic, arena counts are under the runtime lock), and sharing an
arena member marks its arena shared: it takes no more members and what its members reach is shared too.

Thread pool: background workers, one per core (`cs2wasm.processor_count`, at least two), started on first use and
taking work from one queue. `Task.Run`, `ThreadPool.QueueUserWorkItem`, `Parallel.For`/`ForEach`/`Invoke` (chunks
claimed with `Interlocked`, the caller works too) and `await Task.Yield()` off the engine thread run there. Tasks
complete under the runtime lock, so a continuation added while another thread completes the task runs exactly once;
`SynchronizationContext.Current` is per thread. Blocking waits (`Task.Wait`/`Result`, `WaitAll`/`WaitAny`,
`SemaphoreSlim`, `ManualResetEventSlim`, `CountdownEvent`) run the engine thread's posted work and timers while they
wait. Timers keep virtual time (the earliest fires at once) until a worker exists, so single-threaded programs stay
deterministic; after that they fire when due. `ThreadLocal<T>` keeps values by thread id. Background threads do not
keep the Node host alive, as in .NET. Concurrent collections (`ConcurrentQueue`, `ConcurrentStack`, `ConcurrentBag`,
`ConcurrentDictionary`) are the plain ones behind a lock, callbacks run outside it; `ConcurrentDictionary` enumerates
in insertion order rather than .NET's bucket order.

`AsyncLocal<T>`: an immutable chain of values per thread, captured where work is handed on (continuations, the
pool, `Task.Run`, `Thread.Start`, `Task.Yield`) and put back after each async method's synchronous part, so what an
async method sets stays inside it, as in .NET; programs without `AsyncLocal` do not capture. Static constructors
are thread-safe: a type's state word is claimed with compare-and-swap, other threads wait until it is done, and the
thread running it sees its own partial initialization (recursion), as in .NET.

A blocking wait the engine thread can reach (an export and what it calls directly; not lambdas handed to other
threads) is build notice `CW0200` naming it (`Task.Wait`, `.Result`, `GetResult`, `WaitAll`/`WaitAny`, `Join`,
`Sleep`, `SemaphoreSlim`/`ManualResetEventSlim`/`CountdownEvent` waits, `Monitor.Wait`; checked). An exception from a
static initializer becomes `TypeInitializationException`, thrown again at every later access, as in .NET.

Not done: a contended `lock` on the engine thread is not reported; freeing a finished thread's stack and thread-local block,
and the scheduling-dependence marks for the plugin (7.3).

### 5. Optimization — minimal
Done: reachability from exports (only reached methods are emitted), devirtualization when a virtual call has one
implementation or the receiver type is sealed, a final pass over the WASM (identical-body merging, `local.set`/`get`
→ `local.tee`, dead code after unconditional branches, call targets ordered by use for short indices), and inlining
of leaf functions (no calls, no throws: getters, setters, accessors, thunks) where the copy is about as small as the
call, or the function has a single call site; stack traces are unchanged because inlined code cannot throw.
Not done: inlining of functions that call or throw, escape analysis (stack allocation of non-escaping objects), constant folding beyond Roslyn's,
local packing, data segment packing.

### 6. The module — done
32-bit core module, imported shared memory (`env.memory`, size from options), passive data segment initialized by the
`init` export, exports from `[UnmanagedCallersOnly(EntryPoint)]` wrapped so no exception crosses them (reported
through `cs2wasm.report_exception`, export returns 0), boundary-table imports (one typed import per allowed method;
unlisted bodiless methods are `CW0002`), custom sections copied as given, `cs2wasm.version` (compiler and ABI
version), `name` section (the program's functions, by .NET frame name), `cs2wasm.lines` (code offset → file and
line for the program's own files, delta-encoded; the library's frames have none, as .NET's framework frames show
none), `cs2wasm.frames` (a bit per function index for .NET frames, only for functions that can be on the stack at a
throw or catch, and the library's frame names as type, method and parameters over one string table; packed: a format
byte, 0 plain, 1 LZ77 with LEB128 lengths and distances, as the data segment, which the start code expands), a v3
source map. The stack pointer global is exported (`__stack_pointer`, section 7.2); the other mutable state is in memory.
Stack traces: `Exception.StackTrace` and `ToString()` give C# frames with file and line in .NET's format
(`   at Program.Depth(Int32 n) in C:\...\x.cs:line 25`), captured through the engine at `throw` only (no cost on
paths that do not throw) and cut at the catching frame as in .NET. Lambda and local-function frame names
approximate Roslyn's generated names. Per-thread start export: `__thread_entry` (section 4.5), with `__tls_base`
exported beside the stack pointer.

### 7. What Bazoodle needs — partial
- 7.1 plugin passes: `ICompilerPlugin` (interface version 2; version 1 plugins still run) runs after lowering and
  the whole-program analyses, before optimization. It sees the reachable method instances and the call graph; every
  read and write of fields, properties and events with the branch conditions it runs under; per method, the
  control-flow graph (Roslyn's lowering), reaching-definition def-use chains, and update rules with closed forms
  (x' = a·x + b per loop iteration or per call: `x += v * dt`, `v *= 0.9`, counters); the object types a field can
  hold and a may-alias query (type-based, with the program's constructed types). It can add generated C# (compiled
  against the program; its `[UnmanagedCallersOnly]` methods are exported), call hooks at member writes (before or
  after), member reads and statements (methods with instrumented sites, their generic instances, lambdas and state
  machine bodies are compiled again with the calls), export a clone of a method in which chosen imports do nothing
  (its direct callees and lambdas are cloned too), and add custom sections and diagnostics (checked). Everything is
  in Roslyn's terms (operations, symbols, locations). Limits: a clone's virtual calls reach the original
  implementations; instrumented sites in static constructors and field initializers are not compiled again; getters
  are assumed to have no effect when judging a rule's step invariant; alias answers are type-based.
- 7.2 snapshot/restore: state is in memory plus the exported stack pointer (and `__tls_base`). With threads, the
  module exports `cs2wasm_park`, `cs2wasm_resume`, `cs2wasm_parked_count` and `cs2wasm_live_workers`: workers stop at
  their next safe point (a wait, `Sleep`, `Join`, the end of a pool work item, which is also where an `await`
  returns) and wait until resumed; the engine snapshots once the parked count equals the live count (checked).
- 7.3 determinism: no relaxed SIMD (no SIMD at all), fixed-seed string hashing, allocation-order object hash codes,
  math computed in WASM (fdlibm ports, `Exp` in double-double), reproducible output (checked), NaN canonicalized
  where bits are read (`BitConverter`), `Random()`'s seed from the engine and `Random(seed)` giving .NET's
  sequence (checked). Not done: `Unsafe.As`-style bit reads, scheduling-dependence marking.
  Transcendental functions are not guaranteed to round like .NET's (UCRT/glibc) in every case; the tested inputs
  match.
- 7.4 the library — **moving to upstream, in progress.** The upstream library is built and used when
  `CS2WASM_UPSTREAM=1`: dotnet/runtime v11.0.0's System.Private.CoreLib (Mono's project, for a 32-bit browser
  target with multithreading and without event tracing) and eleven libraries above it (Collections, Linq,
  Console, Numerics, Concurrent, Parallel, RegularExpressions, ...), each its own compilation, from the files
  tools/CoreLibManifest evaluates from their projects (`src/Cs2Wasm/Upstream/<Library>/manifest.txt`; sources from
  the cache `%LOCALAPPDATA%/cs2wasm/runtime-v11.0.0` or `CS2WASM_RUNTIME_SOURCES`). Hand-written code is only the
  runtime boundary: Mono's runtime half (`Runtime/`, its internal calls implemented over cs2wasm's runtime),
  `Boundary/` (memory, counting, exceptions, threads, engine imports, the System.Native calls that are the
  engine's: clock, seeded randomness, low-level monitor, console output), generated SR strings, and the
  platform invokes the sandbox lacks (build errors when reached). Upstream files replaced: the tz database
  reader (no database; the engine's offset) and the browser pool's JS-loop yield. The compiler folds what the
  target fixes (`IsHardwareAccelerated`/`IsSupported` false, no event tracing, invariant globalization) and
  compiles only the side of a branch taken. The conformance suite passes on it (59 of 59). Reflection over members reads the compiler's reflection records through Mono's icalls
  (`Boundary/Meta.cs`): methods, constructors, fields, properties, invocation, attributes. Upstream's own types make
  every program carry the arena runtime (exception chains, `object[]` holding `object`, StringBuilder chunks, and
  encoders with their fallback buffers, which do form cycles); the program's own types pay only if they can form
  cycles (`CompileResult.CycleCapableTypes` lists the cycle-capable types). Not yet: module size
  (123 KB for `Console.WriteLine(42)`, from 844 KB; the goal is below NetWasm's 28,383 B). Done for size, besides
  the earlier items: the console writes UTF-16 to the engine directly (`System.Console/Boundary/EngineConsole.cs`);
  no gen-2 GC callbacks and no pool trimming tables (no GC); no environment variables and no processor numbers in
  the sandbox; resource keys off (`SR.UsingResourceKeys()` false); unread static fields lose initializers without
  effect (a second compilation of the static initializers once the program is known); function-table entries and
  data chunks are kept only when live code refers to them; types count as constructed only where live code
  constructs them (a fixpoint before the dispatch stubs, destructors and type tests are emitted)). Done for size: constant-argument
  specialization of `[AggressiveInlining]` methods and short forwarders (Specialization.cs); `string.Format` with a
  constant format lowered to concatenation (FormatLowering.cs; resource strings are constants); interface calls on
  values of known primitive or readonly-struct type call the type's method instead of boxing and dispatching; enum
  formatting reads the compiler's enum tables (`Boundary/EnumFormat.cs`, upstream's algorithm over one value
  instead of a copy per storage type); no environment variables in the sandbox, so the runtime's configuration
  reads fold (a local set once from a known null is null). What remains large: number formatting (still reached
  through `string.Format` with item formats such as `{0:X4}` and with a provider), `AppContext` configuration
  parsing, dictionary and encoding code that Console really uses and build time (about
  10 s against 2 s), `Delegate.CreateDelegate`/`DynamicInvoke`,
  reflection on generic instantiations, `Array.CreateInstance` of array types the program never makes.
  Until the suite passes on it, the default is still **cs2wasm's own library** (a deviation from the spec). It is a
  self-contained corlib written for this project (about 6 500 lines: object model, strings, number formatting and
  parsing with .NET's exact rounding rules, math, exceptions, collections whose enumeration order matches .NET's,
  LINQ with deferred execution, StringBuilder). Moving to upstream sources is open work. Invariant globalization
  semantics are followed (culture comparisons are ordinal, as in .NET's invariant mode). Case mapping covers
  ASCII, Latin-1, Greek and Cyrillic only. Present since: `Task` and friends, `Random`, `Stopwatch`,
  `BitConverter`, `Type`, spans over arrays, `SortedDictionary`/`SortedList`/`SortedSet`, `PriorityQueue` (.NET's quaternary heap, so ties come out in the same order), `LinkedList`, `BigInteger`, `Chunk`, `Join`/`GroupJoin`/`ToLookup`, `Enum`'s static API (`GetValues`, `GetNames`, `Parse`/`TryParse` with flags, `IsDefined`, `GetName`, format strings, `HasFlag`, `CompareTo`). Static abstract interface members resolve to the type argument's implementation; for the primitive types the generic math interfaces (`INumber<T>`: `Zero`, `One`, operators, comparisons, `Abs`/`Max`/...) lower to primitive code. `decimal` has .NET's representation and arithmetic (scale kept on addition, summed on multiplication, half-even rounding when a result does not fit, 28-29 digit quotients, conversions from double to 15 digits), formatting, parsing, `Math`/LINQ overloads (checked against .NET). Missing: `TimeZoneInfo`, `DateTimeOffset`, date parsing, `GodotSharp`. `DateTime` and `TimeSpan` follow .NET's tick arithmetic and the invariant culture's standard and custom formats; the clock and the local offset come from the engine.
- 7.5–7.6: no GodotSharp, no browser build, budgets unmeasured.

### 8. Sandbox — partial
Files, sockets, processes, environment and registry members, and platform invoke, are build errors naming the
capability (`CW0003`, `CW0004`). `unsafe` code and pointers compile. Reflection metadata is kept for what the
program's reflection can be proven to reach (a trimming analysis): `typeof(X).GetMethod("M")` keeps X.M,
`GetMethods()`/`GetProperties()`/`GetFields()` keep that kind of X's members, `GetConstructors`/`Activator` keep
constructors, a type marked `[DynamicallyAccessedMembers]` keeps everything, and reflection on a Type the compiler
cannot see is warning `CW0201`. Kept members get records in the data segment and thunks that unbox arguments, call
and box the result: `Type.GetMethods/GetMethod/GetProperties/GetProperty/GetFields/GetField/GetConstructors/
GetConstructor/GetMembers` with `BindingFlags` (inherited members, overrides, `DeclaredOnly`), `MethodInfo.Invoke`
(`TargetInvocationException`, `TargetParameterCountException`), `PropertyInfo`/`FieldInfo` `GetValue`/`SetValue`,
`ConstructorInfo.Invoke`, `Activator.CreateInstance` (also with arguments and `<T>`), custom attributes with
constructor and named arguments (`GetCustomAttribute<T>`, `IsDefined`), `BaseType`, `GetInterfaces`,
`IsAssignableFrom`, and .NET's `ToString` forms and member order (checked against .NET). Not done: generic methods
and `ref` parameters through reflection, `MakeGenericType`, `Type.GetType(string)`, events and nested types, the
interpreter (`Reflection.Emit`, `Expression.Compile`) and `dynamic`.

Also: a readonly field (or get-only auto-property) set only in constructors that keep `this` in is not an edge of
the cycle analysis (section 4.3), so an exception's inner exception no longer brings the arena runtime into every
program.

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

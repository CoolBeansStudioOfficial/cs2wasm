# cs2wasm: implementation status

What exists today, measured against [SPEC.md](SPEC.md). Claims here are what the test suite checks; anything not
tested is labelled so. Run `dotnet run --project tests/Cs2Wasm.Tests` to reproduce the results.

## Results

- **Conformance (proof 1, partial):** 50 C# programs (arithmetic and math, classes and virtual/interface dispatch,
  structs and enums, strings and formatting, exceptions with stack traces and filters, collections, LINQ (methods and
  query syntax) and closures, generics (generic virtual methods) and pattern matching (list patterns), nullable value
  types, tuples, iterators, async/await (on one thread, async streams, captures, filters), threads, the thread pool
  and concurrent collections, `typeof` and seeded `Random`, records, reference cycles, weak references and
  finalizers, reflection, decimal, date and time, enum formatting, spans and `stackalloc`, `ref struct`, ref returns,
  C# 14 extension members, value-type equality, expression trees and `Expression.Compile`, array ranges) produce output identical to .NET 11's, byte for byte, on upstream's library; so does the
  GodotSharp math proof (proof 2).
- **API checks:** 17 checks of the module shape, embedder API and memory pass (boundary imports, sandbox errors,
  custom and version sections, shared memory import, reproducible output, exception reporting at exports, plugin
  passes, source map, cycles bounded in a fixed 16 MB memory, no arena code without cycle-capable types).
- **Module size (raw, measured):** `Console.WriteLine(42)` is 26,738 bytes on upstream's library (code 20.5 KB,
  LZ-packed data and frame names about 6 KB); NetWasm's build of the same program is 28,383 bytes. The conformance
  programs are 30–300 KB raw (`tests/Cs2Wasm.Tests` prints each size). The Rust 2D game bar in the spec (24 KB
  compressed) is for a game with GodotSharp, which is not built yet.
- **Build time (desktop .NET, measured):** a fresh process takes 3.5–4 s to parse the library first; after that a
  conformance program builds in 0.4–2.1 s (1.35 s on average over the suite), the library's semantic models and
  bound bodies being kept between builds (section 7.6 allows caching the library's analysis). `CS2WASM_TIMES=1`
  prints the phases. Not measured in the browser.
- **Run-time speed vs NativeAOT:** unmeasured.
- **Reference-count operations removed at compile time (static, `CS2WASM_STATS=1`):** a scheme without ownership
  analysis pays an increment and a decrement for every reference transfer (assignment, initializer, argument,
  return); 53% are removed for `Console.WriteLine(42)`, 55% for the LINQ program, 57% for the game simulation, 54%
  for the text and generics program. Lobster reports about 95%. What remains: owned call results the caller
  releases, values from elements, properties and parameters held in locals (retained, released at scope end), and
  stores' old values. Fields of objects that stay put (`this`, a parameter or local never reassigned) are borrowed,
  for a local never reassigned or a call's receiver or argument, when nothing that can run meanwhile stores the
  field (a whole-program summary of the fields each function can store, with what it calls; programs without
  threads).

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
- `Reflection.Emit` (section 8); in `dynamic` code, `&&`/`||` with a static left operand, `ref`/`out` arguments of
  dynamic calls and the value of a dynamic member's postfix `++`/`--`;
- in expression trees, the closure's type name in a tree's text (`value(<>c__DisplayClass).x`: Roslyn numbers its
  closure classes, cs2wasm's are unnamed) and lifted (nullable) user-defined operators; captured variables (a field of
  the closure, read when the tree runs), `this`, generic method calls and user-defined operators and conversions work
  (`056_expression_trees_captures`, with `AsQueryable()` over upstream's System.Linq.Queryable), and
  `Expression.Compile` runs on upstream's interpreter (`046_expressions`);
- filters inside iterators and async methods run in one pass (when the exception reaches their catch, after the
  `finally` blocks inside the try; an exception thrown by such a filter makes it false, as in .NET); the first pass of a throw
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

**Arenas at run time** (`Upstream/System.Private.CoreLib/Boundary/Runtime.cs`, `Arena*`). Objects start as plain counted objects. A store at a
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
- ownership specialization beyond methods that return `this` (StringBuilder.Append: their body returns it
  borrowed to direct callers, an owned-return wrapper serves indirect ones), retroactive owners (4.1);

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

A `lock` on the engine thread whose object worker code also locks (a field locked in a delegate handed to `Thread`,
`Task.Run`, the thread pool or `Parallel`, or in what it calls) is notice `CW0200` too. A finished thread's stack and
thread-local block are reused by the next thread (`__thread_entry` marks them free when it is done), and its
`[ThreadStatic]` references are released at its end: 200 threads run one after another in a fixed 16 MB memory
(checked). Not done: the scheduling-dependence marks for the plugin (7.3).

### 5. Optimization — partial
Done: reachability from exports (only reached methods are emitted), devirtualization when a virtual call has one
implementation or the receiver type is sealed, constant-argument specialization (null/empty/string/bool/`typeof`
arguments that decide a branch get their own copy of the callee), folding of what the target fixes, and the final
pass over the WASM (Wasm/): inlining of leaf functions, identical-body merging, a liveness-based local optimizer
(dead stores, single-use values kept on the stack, constant and copy propagation, unused locals removed and grouped
by type), count traffic with no effect removed (releases of locals that are still zero, retain/release pairs of one
local, borrows released before anything could drop the owner's reference, stores into a fresh object skip the old
value, cleanup handlers no exception can reach), branches on constants known only for the whole program folded
(`Rt.SingleThreaded`), dense type ids with per-type tables as narrow data arrays, the data segment and frame-name
section LZ-packed, call targets ordered by use. Not done: inlining of functions that call or throw, escape analysis
(stack allocation of non-escaping objects).

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
  where bits are read (`Unsafe.As<float or double, T>`, so `BitConverter` and `BitCast`: to .NET's `NaN` bits,
  0xFFF8000000000000 and 0xFFC00000; a NaN with another payload is the one place output differs from .NET, as
  section 7.3 asks), `Random()`'s seed from the engine and `Random(seed)` giving .NET's
  sequence (checked). Not done: scheduling-dependence marking.
  Transcendental functions are not guaranteed to round like .NET's (UCRT/glibc) in every case; the tested inputs
  match.
- 7.4 the library — **upstream, done.** The library is dotnet/runtime v11.0.0's System.Private.CoreLib (Mono's
  project, for a 32-bit browser target with multithreading and without event tracing) and eleven libraries above it
  (Collections, Linq, Console, Numerics, Concurrent, Parallel, RegularExpressions, ...), each its own compilation,
  from the files tools/CoreLibManifest evaluates from their projects (`src/Cs2Wasm/Upstream/<Library>/manifest.txt`;
  sources fetched on first use into `%LOCALAPPDATA%/cs2wasm/runtime-v11.0.0`, or `CS2WASM_RUNTIME_SOURCES`). There is
  no other library: the hand-written corlib the project started with is deleted. Hand-written code is only the
  runtime boundary: Mono's runtime half (`Runtime/`, its internal calls implemented over cs2wasm's runtime),
  `Boundary/` (memory, counting, exceptions, threads, engine imports, the System.Native calls that are the engine's:
  clock, seeded randomness, low-level monitor, console output), generated SR strings, and the platform invokes the
  sandbox lacks (build errors when reached). Upstream files replaced: the tz database reader (no database; the
  engine's offset) and the browser pool's JS-loop yield. Cuts for a garbage-collector-free, single-module target are
  compiler rules and manifest substitutions/edits, not a fork: no gen-2 GC callbacks or pool trimming, no
  environment variables or processor numbers in the sandbox, resource keys off, static initializers of unread fields
  dropped, constructed types pruned to live code, invariant globalization folded. Reflection over members reads the
  compiler's reflection records through Mono's icalls (`Boundary/Meta.cs`). `Delegate.DynamicInvoke` (a thunk per
  delegate type the program constructs) and `Delegate.CreateDelegate`/`MethodInfo.CreateDelegate` from a
  `MethodInfo` (closed, open instance, static) or a method name work (`049`, `050`). `Delegate.Method` and `Target`
  (`052`): when the program asks for `Method`, the methods its method-group delegates name keep reflection records,
  and the delegate's function maps to its method's record; delegates CreateDelegate made report the method they were
  made from. `delegate + delegate` and `-` combine and remove. Missing: `Delegate.Method` of a lambda or local
  function (.NET reports the C# compiler's generated names), `Array.CreateInstance` of array types the program never makes. Build time: see Results (the library's models and bound bodies are kept between builds in one process).
- 7.5–7.6: GodotSharp's math types compile and match (proof 2); the generated wrappers (Node2D) and the boundary
  table are Bazoodle's (7.5) and do not exist yet, so proofs 3 and 4 wait on them. No browser build (the browser
  .NET runtime's workload is not installed here); budgets: module size and desktop build time measured (Results),
  call cost and run-time speed against NativeAOT unmeasured.

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
`IsAssignableFrom`, and .NET's `ToString` forms and member order (checked against .NET). Also (`048_reflection_types`):
`Type.GetType(name)` over a table of the program's and CoreLib's types the module numbers (as .NET looks in the
calling assembly and CoreLib); generic type definitions (`typeof(List<>)`: `IsGenericTypeDefinition`, its parameters
as `GetGenericArguments()`), `GetGenericTypeDefinition`, and `MakeGenericType` (folded at compile time when the
definition and the arguments are `typeof` constants, so the instance's code exists; otherwise the instances the
program makes); `GetNestedTypes`/`GetNestedType`; events (`GetEvents`/`GetEvent`, `AddEventHandler`,
`RemoveEventHandler`: an event is its kept accessor pair); reflection in the library's own code, such as
System.Linq.Expressions asking a delegate type for `Invoke` (a member named by a constant on a type the compiler
cannot see is kept on every type the program names that has it). `Expression.Compile` runs on upstream's
interpreter. Generic methods (`051`): a generic definition's record lists its parameters and its instances,
`MakeGenericMethod` with `typeof` arguments keeps that instance (its code exists), `IsGenericMethod(Definition)`,
`GetGenericArguments`, `GetGenericMethodDefinition`; member objects of the same member and reflected type are equal
(CoreCLR caches them). `ref`/`out`/`in` parameters (`053`): by-ref types (`MakeByRefType`, `IsByRef`, `Int32&`,
`IsOut`/`IsIn`) are their element's id with a bit; the thunk passes a value's box data (the invoker's copy, written in
place) or a frame slot copied back for a reference; a null argument for a value gets a default box
(`RuntimeHelpers.GetUninitializedObject`/`Box`). Assemblies (`054`): `Type.Assembly`, `Assembly.FullName`/`GetName`,
`GetExecutingAssembly` (the calling code's, known where it is compiled), `GetEntryAssembly`, from a table of the
assemblies' names (a library's version and key token are the running .NET's, the program's as the C# compiler
emits it); `FullName`/`AssemblyQualifiedName` qualify generic arguments with their assemblies, and nested types of
generic types carry the outer arguments as .NET's do. Not done: `MakeGenericMethod` with arguments the compiler cannot
see, `Assembly.GetTypes`/`GetType(name)`/`GetCallingAssembly` and `Reflection.Emit`.

`dynamic` (`055_dynamic`, matching .NET's output and binder errors) runs on upstream's C# runtime binder
(Microsoft.CSharp) as Roslyn lowers it: each dynamic operation is a `CallSite<D>` made once from `Binder.InvokeMember`,
`GetMember`/`SetMember`, `GetIndex`/`SetIndex`, `Invoke`, `InvokeConstructor`, `BinaryOperation`, `UnaryOperation`
or `Convert` with Roslyn's flags (`&&`/`||` as an `IsFalse`/`IsTrue` test, then the logical operator), argument infos and delegate type, written as C# in a generated class of the program
(`Compiler/DynamicLowering.cs`) and called where the operation is; compound assignments and `++`/`--` on members get,
operate and set. The binder binds against reflection: the member names the sites use are kept on every type the
program names (and on generic definitions, which the binder reads), with what the binder asks of metadata now kept
too: operators and conversions (`SpecialName`, `Final`, `NewSlot` attributes), indexers (`[DefaultMember]` as C#
emits it), optional parameters' default values, `init` and `in` modifiers, one metadata token per definition (shared by
a generic type's instances), generic parameters (position, attributes, constraints, declaring method), modules. Its
expression trees run on upstream's interpreter. A program using `dynamic` carries the binder (about 3 MB, a minute
to build).

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
- `ValueType.GetHashCode` for structs that do not override it hashes the value's bytes (FNV-1a), not .NET's
  per-process randomized hash; both are legal, and cs2wasm's is the same on every machine (section 7.3).
  `ValueType.Equals` is .NET's: bytes when the struct can be compared bit by bit, else field by field.

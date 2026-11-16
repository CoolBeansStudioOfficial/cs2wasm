# cs2wasm: specification

cs2wasm is a compiler that takes a C# program, as Roslyn understands it, and emits one small, fast WebAssembly module with no
garbage collector and no .NET runtime. It is the owner's own project, in its own repository. Its first user is
Bazoodle, a browser game platform whose games are C# written against Godot's C# API; this document states the design
and everything Bazoodle needs from it. It is self-contained: a building session needs nothing else to start, apart from
the sources it names.

Words used throughout: **the compiler** is this project; **the embedder** is the program that calls it (Bazoodle's
editor); **the engine** is Bazoodle's Godot build, which loads and calls the module; **the module** is the compiler's
output.

**Licence:** MIT. Bazoodle's own plugin (section 7.1) is not part of this project.

---

## 1. Goals and fixed decisions

| Topic | Decision |
|---|---|
| Input | A Roslyn `Compilation` (C# 15, .NET 11 library surface), read through Roslyn's **operation tree** (`IOperation`) and semantic model. Not IL: the operation tree keeps the source-level facts the memory analysis needs |
| Output | One 32-bit **core** WASM module with imported memory. Not a component (browsers do not run the component model), not WasmGC |
| Pipeline | Roslyn, then the compiler. No intermediate language, no second compiler (no Rust, no LLVM, no C) |
| Correctness | **The same observable output as the program under .NET.** Observable output is the sequence of calls the module makes to its imports, and their arguments. How the compiler gets there is free |
| Input scope | **Every program .NET accepts compiles.** Where the compiler cannot prove something cheap, it pays a run-time cost; it never rejects a program for being hard to analyse. No program or use case is treated specially: every module gets the same platform (section 8), and platform invoke into native libraries, which cannot exist in WASM, is the only refusal |
| Memory | No garbage collector, no background collector, no whole-heap scan. Compile-time reference counting copied from Lobster (section 4) |
| Speed | Speed and module size are the priorities, reached by real translations of C# concepts, not by imitating the .NET runtime |
| Against NativeAOT | **Anything cs2wasm compiles is faster, smaller and uses less memory than the same program under .NET NativeAOT.** Run time, binary size and peak memory are each measured against NativeAOT on real programs; Roslyn, compiled by cs2wasm and compiling programs, is the main measure (section 10, proof 4) |
| Self-hosting | The compiler is written in C# and compiles itself to WASM (section 9) |
| Whole program | All source is present at the final build. Whole-program analysis is allowed and expected |
| Threads | Real threads for game code (owner): they are how the engine gets the most out of a device (section 4.5) |
| Hosting | It runs in the browser, in a Web Worker, as part of the embedder's build (section 9) |

**Philosophy.** Find real translations of C# concepts into WASM, not workarounds. Runtime machinery that imitates
what .NET does is a sign the real translation has not been found yet. Copy designs that someone has already refined
(Lobster for memory, Roslyn's own lowering rules for language features) rather than invent new ones. When a design hits
a hard case, look for a different translation before accepting a loss, and say plainly when none has been found.

---

## 2. Architecture

```
C# sources ──> Roslyn (parse, bind, source generators, diagnostics) ──> Compilation
   Compilation ──> the compiler:
       1. reachability from the roots (section 6.1)
       2. lowering of language features on the operation tree (section 3)
       3. whole-program analyses: call graph, ownership and aliasing, def-use, closed forms (sections 4, 7.1)
       4. plugin passes (section 7.1): read the analyses, instrument, add functions, emit sections
       5. specialization and optimization (sections 4, 5)
       6. WASM emission, line tables, custom sections (section 6)
   ──> module.wasm (+ diagnostics, + source map)
```

- **One compilation feeds everything.** The embedder may read the same `Compilation` (Bazoodle's moderation string
  extraction does), and plugs its own whole-program passes into the compiler (section 7.1).
- **An in-process API first.** The compiler is a library called with a `Compilation` and options, returning the module
  bytes, diagnostics and a source map. A command-line wrapper is for tests.
- **The program is several compilations:** the game, GodotSharp, the `Bazoodle` namespaces and the .NET library are
  each a Roslyn compilation; the compiler treats them as one program.

---

## 3. Language coverage and lowering

The compiler does its own lowering on the operation tree. Where Roslyn's own emitter defines a lowering (async state
machines, iterator state machines, closure classes, pattern-matching decision trees, string interpolation handlers,
`using`, `foreach`, `lock`, records, tuples, LINQ query syntax), the compiler **follows the same semantics**, because
those semantics are what programs observe. Every feature of C# 15 is in scope, including:

- classes, structs (including `ref struct`, `readonly`), records, interfaces with default methods, generics with
  constraints (including static abstract members), nullable value types, enums, delegates and events, lambdas and
  local functions with captures, properties, indexers, operators and conversions, extension members;
- `async`/`await` with the full awaiter pattern (`GetAwaiter`, `IsCompleted`, `OnCompleted`/`UnsafeOnCompleted`,
  `GetResult`), so custom awaiters work (Godot's `await ToSignal(...)` uses one), plus `ValueTask`, async iterators
  and `await foreach`;
- iterators (`yield return`), pattern matching of every kind, `switch` expressions, `checked`/`unchecked`,
  exceptions with `try`/`catch`/`finally`/`when`, `using` and `await using`, `stackalloc`, `Span<T>`;
- generics: instantiate per type where it pays (value types always), share code between reference-type instantiations
  where it does not; the choice is the compiler's;
- static constructors and static field initialization with .NET's rules (`beforefieldinit`, first access, once per
  program, thread-safe);
- `dynamic`, through the C# runtime binder on the reflection metadata kept (section 8) and the interpreter.

**Async follows .NET's threading.** A continuation runs where the current `SynchronizationContext` posts it, as in
.NET: GodotSharp installs its own, so script code returns to the engine thread after `await`. `Task.Run`, the thread
pool, `Parallel` and `Thread` run on real worker threads (section 4.5).

**Exceptions** use native WASM exception handling (owner; the `exnref` form, shipped in all major browsers; the
version floor is checked by the owner). An exception that escapes an exported entry point must reach the engine as a catchable failure carrying the
exception's type, message and C# stack trace with file and line (section 6.3). Exception filters (`when`) keep .NET's
two-pass order where it is observable: a filter runs before the `finally` blocks of the frames it would unwind.

**Stack overflow ends the instance**, as it ends the process under .NET; the engine reports it. **Out of memory** throws
`OutOfMemoryException` when the engine's memory limit is reached, as .NET does.

**Integer and floating-point semantics** are C#'s: unchecked arithmetic wraps, `checked` throws, integer division by
zero throws, float behaviour is IEEE 754 as WASM gives it. Determinism rules are in section 7.3.

---

## 4. Memory: compile-time reference counting

The model is copied from the Lobster compiler: <https://aardappel.github.io/lobster/memory_management.html>. Read it
first. In Lobster's tests the analysis removes about 95% of reference-count operations at compile time. Garbage
collection is ruled out because it was measured at 47 ms pauses native and 64 ms in Chrome with a 64 MB heap, which
is unacceptable for games on throttled devices.

### 4.1 Ownership and borrowing

- **Every heap allocation has a single owner**, usually the first variable, field or element it is stored in. Every
  other use is a **borrow**, with no run-time cost.
- **Each operation declares** whether it owns or borrows each operand and whether its result is owned or borrowed.
  Where the two sides disagree, the compiler inserts a count increase, or holds a temporary until the end of its
  scope.
- **Variables.** An assigned variable owns its value. A variable that is never reassigned, and a `foreach` element,
  borrow from their source for their scope.
- **Retroactive owners.** If a borrowing variable's source is written while the borrow is live, the variable becomes
  an owner instead. Behaviour never changes, only cost.
- **Specialization by ownership.** A function is specialized for the ownership its callers need. Large functions and
  virtual-dispatch groups get one shared, borrow-first specialization. Return values are owned.
- **Nothing is rejected.** Where Lobster reports an ownership error (changing a value while it is borrowed in the
  same call), the compiler inserts a run-time count increase instead.

### 4.2 Values

- **Structs are inline values**, stored in their parent (a field, an array, a local, the stack), copied on
  assignment, and mutable, as C# defines them.
- A struct containing references carries their ownership with it.
- Boxing allocates an owned object like any other.

### 4.3 Cycles: arenas (owner)

- The compiler builds the graph of which types can hold references to which. **Types on a cycle in that graph are
  cycle-capable.**
- Objects of cycle-capable types are allocated in an **arena**. References between objects in the same arena are
  indices and are not counted, so they may point back at each other freely.
- **The arena is counted as one unit** from outside. It is freed when no outside reference remains.
- An object that becomes unreachable while its arena is still alive is freed with the arena. Arenas are therefore
  scoped to things that end (an object graph rooted in one owner), never one arena for the whole program. How arenas
  are scoped and when they split is the compiler's design.
- Types that cannot form cycles never pay for arenas.

### 4.4 Finalizers and weak references

- **Finalizers run** when an object's last owner releases it (or with its arena). GodotSharp's `GodotObject` releases
  an engine object in its finalizer; a finalizer that never runs is a leak in the engine. Any timing C# allows is fine.
- **`WeakReference` and `WeakReference<T>` work**: a weak reference does not keep its target alive and reads as empty
  once the target is freed. `ConditionalWeakTable` likewise.
- `GC.Collect`, `GC.KeepAlive`, `GC.SuppressFinalize` and similar keep their observable meaning.

### 4.5 Threads (owner)

Threads are how Bazoodle gets the most out of a device, so game code has real ones.

- **Each thread is a Web Worker running an instance of the module on the same shared memory** (WASM threads, with
  `SharedArrayBuffer`; the site is already cross-origin isolated). The engine thread runs script callbacks; `Thread`,
  the thread pool, `Task.Run` and `Parallel` run on a pool sized to the device's cores.
- **Counts are atomic only where an object is shared.** Escape analysis finds the objects (and arenas) that can be
  reached from more than one thread: captured by work handed to another thread, stored in a static, or published
  through a shared object. Those get atomic counts; everything a single thread owns keeps plain counts. Most objects
  never cross a thread.
- **.NET's memory model and synchronization apply**: `lock`, `Monitor`, `Interlocked`, `volatile`, `SemaphoreSlim`,
  concurrent collections, `Task` joins. Correctly synchronized code behaves exactly as under .NET; racy code stays
  memory-safe and gives some outcome .NET allows.
- **No thread ever blocks the engine thread.** A blocking wait on the engine thread (`Task.Wait`, `.Result`, a
  contended `lock` held for long) is allowed by the language but reported as a build notice where the compiler can see
  it; the engine thread must keep its frame budget.
- **Thread-local state:** `[ThreadStatic]`, `ThreadLocal<T>` and `AsyncLocal<T>` work. Each worker's instance keeps its
  own stack pointer and thread-local block in memory.
- **Engine calls from worker threads follow Godot's own rules**: thread-safe servers and resources may be called, the
  scene tree may not (Godot's thread guards report it, as in stock Godot).

### 4.6 All state in linear memory

**Everything a module instance knows lives in its linear memory and its mutable globals.** Nothing lives in JavaScript,
in WasmGC objects, or in tables that change after start-up. Bazoodle's networking relies on this: rewinding and
re-simulation snapshot a module's state by copying its memory and restore it by copying it back (section 7.2). Keep the
heap compact (reuse freed space) because a snapshot's cost is proportional to the memory in use.

---

## 5. Optimization

- Optimization is the compiler's own: inlining, devirtualization, escape analysis (an object that never escapes needs
  no heap allocation), ownership and specialization (section 4), dead-code removal, constant folding.
- **A final pass over its own WASM output** cleans up what lowering produced: merging identical function bodies (type
  and ownership specialization makes many), packing short-lived locals, removing redundant `local.set`/`local.get`
  pairs, simplifying branches, ordering functions and globals so the most used get the shortest indices, packing data
  segments. No external optimizer is required.
- The browser's optimizing tier does register allocation, so the compiler does not need to.
- **Size matters as much as speed**: players download every module. Emit only what the roots reach (section 6.1).

---

## 6. The module

### 6.1 Shape

- 32-bit core WASM, **shared memory imported** from the engine (the engine creates it, with the maximum it allows;
  shared because threads need it; start with a large initial size, because growing memory is slow in Chrome: about
  2.1 s to build a 256 MB heap). The module has its own memory, separate from the engine's.
- **Imports: the host's functions and nothing else.** There is no WASI layer. Console output, time, randomness, files,
  HTTP and environment variables come from host imports that the library calls (sections 7.4 and 8), the same for every
  module.
- **Roots.** A game has no `Main`. The roots of reachability are the exported entry points (section 6.2). Objects live
  between calls; the engine holds some of them through handles.
- **Initialization.** An exported `init` runs once before any other export (memory layout, statics that are not lazy,
  the thread pool). Worker instances have their own per-thread start export.
- **Exports.** A static method marked `[UnmanagedCallersOnly(EntryPoint = "name")]` becomes a WASM export with that
  name. Its parameters and result are WASM numbers (and pointers into the module's memory).
- **No exception crosses an export.** Each export is wrapped: an exception that escapes it is reported through the
  engine's exception-report import (type, message, stack trace) and the export returns its failure value. The engine
  never has to catch a WASM exception itself.
- **Custom sections supplied by the embedder** are written into the module as given (Bazoodle writes a
  `platform.scripts` record that its editor reads without running the module).
- **A version section** records the compiler version and the ABI version, so the engine can refuse a module it does
  not understand.

### 6.2 The engine boundary

The module talks to the engine only through a **boundary table** that the embedder supplies (section 7.5): methods
with no C# body (`extern`, or marked by an attribute the table names) that become WASM imports, with their WASM
signatures. There is one typed import per allowed engine method. A method the program calls that is not in the table,
and has no body, is a build error naming it. Marshalling (which values are copied through memory, which are engine
handles) is Bazoodle's C# code on top of these imports, so the compiler needs only numbers and pointers at the
boundary.

### 6.3 Debug information

- **Source positions survive.** Every emitted instruction can be mapped back to the C# file and line that produced it.
  The compiler emits the WASM `name` section and a mapping from code offsets to file and line (a source map), so stack
  traces name the creator's code.
- **Stack traces at run time.** An exception's `StackTrace` gives C# methods with file and line, as .NET does with
  portable PDBs present. How the compiler records frames is its own design; the cost on paths that do not throw should
  be near zero.
- Release and play builds carry line tables only (not full local-variable information).

### 6.4 Diagnostics

Compiler diagnostics are reported like Roslyn's: severity, a stable id, message and C# location. The embedder shows
them beside Roslyn's.

---

## 7. What Bazoodle needs

### 7.1 Plugin passes (owner)

Bazoodle's networking (the "Invisible API") is a whole-program analysis of the same kind the compiler already does. It
needs, for every property: its readers and writers, the full condition each read runs under, which writes depend on
player input or hidden values (statement-level taint through the call graph), which per-tick code can be solved into a
closed formula of time, and which spots it cannot be sure about (aliases, unresolved delegates, reflection). It then
changes the program: a "mark this property changed" at each such write, small generated functions (solvers, per-tick
steps, gates the server runs), variants of per-tick code that run with effects suppressed, debug-build checks at
reads, and a manifest. Doing that as a separate tool would repeat the whole-program analysis and double build time, and
would lose the compiler's precise aliasing. So the compiler has a **plugin pass interface** and stays general-purpose;
the Invisible API is Bazoodle's plugin, like an LLVM pass.

A plugin pass, run after lowering and the whole-program analyses and before specialization and optimization, can:

- **read the analyses:** the call graph (with devirtualized targets), reachability, ownership and aliasing (which
  references can name the same object, from section 4's single-owner analysis), def-use chains and control-flow graphs
  per method on the lowered tree, and closed forms of loop-carried values (induction-variable analysis: a value
  updated by a fixed rule per iteration, as `x += v * dt` or `v *= 0.9`, with its formula in the iteration count);
- **map everything back to source:** every lowered operation knows the Roslyn operation, symbol and C# location it
  came from, so a plugin reports in the developer's terms;
- **instrument:** insert calls or inline code at chosen operations (before or after a field or property write, at a
  read, at a branch), with their cost visible to the optimizer;
- **add functions:** generated methods, written as C# operations or as the compiler's lowered form, which may be
  exported;
- **clone with changes:** a copy of a method (and what it calls) in which calls to chosen imports are suppressed;
- **emit custom sections** and **diagnostics** (errors fail the build; notices are listed).

Plugins are C# libraries written against the compiler's plugin interface. While the compiler runs on the .NET runtime
they are assemblies passed in-process; once it compiles itself (section 9) they are compiled into the toolchain module
with it. The interface is versioned. A program built with no plugins is ordinary C# compiled to WASM.

### 7.2 Snapshot, restore and partial runs

- Section 4.6: a module's whole state is its memory plus its mutable globals. The compiler exports the mutable globals
  it uses (or keeps them in memory), so the engine can save and restore an instance completely.
- The engine re-runs code from a restored state and discards the result (prediction and drawing between ticks). That
  only needs section 4.6 and imports with no hidden state on the module side.
- **With threads running,** a running thread's own stack is not in memory, so a snapshot or restore of live memory
  happens only with worker threads parked. The module supports **parking every worker thread at a safe point** (an
  `await`, the end of a work item, a wait) on the engine's request, and resuming them. How often the engine needs it is
  the networking design's concern; re-running on a copy of memory in a separate instance needs no parking.

### 7.3 Determinism

Every peer in a session runs the same module, and Bazoodle's networking relies on it computing **bit-identical
results on every machine**. The compiler must not introduce variation:

- **No relaxed or non-deterministic WASM instructions** (no relaxed SIMD). Deterministic SIMD is fine.
- **NaN payloads never become observable.** Where a float's bits are read (`BitConverter`, `Unsafe.As`, hashing a
  double), NaNs are canonicalized first.
- **Hash codes are deterministic.** `string.GetHashCode` uses a fixed seed (any seed is legal C# behaviour, and a
  fixed one makes `Dictionary` and `HashSet` iterate the same on every machine). `object.GetHashCode` and
  `RuntimeHelpers.GetHashCode` come from a per-instance counter in allocation order, never from addresses that vary.
- **Nothing depends on memory addresses** in observable output.
- **Math functions are computed in WASM, never imported from JavaScript.** `Math.Sin`, `Math.Exp`, `MathF.Pow` and the
  rest come from the library compiled into the module (upstream's managed code or a portable implementation), because
  browsers' `Math` functions differ between engines. Only operations WASM defines exactly (`sqrt`, the basic
  arithmetic, `min`, `max`, rounding) use WASM instructions directly.
- **The output is reproducible:** the same inputs give a byte-identical module.
- Time and randomness come only from engine imports (section 7.4); the engine decides what they return.
- **Thread timing is not hidden from the plugin.** The compiler's analyses mark every value that can depend on
  scheduling (a race, a float sum accumulated by several threads, the order of a concurrent collection, whichever task
  finishes first). Bazoodle's networking plugin treats a write of such a value as one the server computes and sends
  (owner), so racy code costs bandwidth, never a desync. Order-independent parallel work (`Parallel.For` writing
  separate elements, tasks joined by `await` in program order) is deterministic and needs nothing.

### 7.4 The library

- **GodotSharp is reused as upstream wrote it** (`godot/modules/mono/glue/GodotSharp`, Godot 4.7.2): its core types,
  its source generators (`Godot.SourceGenerators`, run inside Roslyn) and its class wrappers. Only `NativeInterop` and
  `Bridge`, which assume C# and the engine share one address space, are replaced by Bazoodle with the boundary table
  (section 6.2). GodotSharp has 89 uses of pointers, `GCHandle` and function pointers outside those two
  (`Callable.generics.cs`, `DelegateUtils.cs`, `GodotObject.base.cs`, `Array.cs`): the compiler translates them, or
  Bazoodle patches them.
- **The .NET library** is C# compiled by the compiler like any other code, with hand-written code only at the runtime
  boundary (memory, counting, exceptions, the engine imports). **It is upstream's library, trimmed** (owner): the real
  `dotnet/runtime` sources (`System.Private.CoreLib` and the libraries above it), as .NET's own WASM build uses them,
  with only what the program reaches emitted. The parts that assume a garbage collector, threads or native interop are
  replaced at that boundary; everything else (formatting, collections, LINQ, text) is upstream's code and behaves
  exactly as .NET does.
- **Invariant globalization (owner).** Programs run in .NET's invariant globalization mode: formatting, parsing,
  comparison and casing are culture-invariant on every machine, so peers compute the same strings, and no ICU data is
  shipped. Localized text is the embedder's concern (Bazoodle's translation layer). `TimeZoneInfo.Local` takes its UTC
  offset from the engine's clock import; no time-zone database is shipped.
- **Clock and randomness.** `DateTime.Now`, `Stopwatch` and `Environment.TickCount` read the engine's real-time
  import. `System.Random`'s default seed comes from the engine's seeded import (so it matches across peers).
  `Console.WriteLine` and `GD.Print` go to the engine's log import.

### 7.5 What Bazoodle supplies

- The boundary table (section 6.2), generated from Godot's `extension_api.json` with a blocklist; the engine side
  of each import already exists.
- Patched GodotSharp sources, Bazoodle's source generators and the `Bazoodle` namespaces.
- Custom sections (section 6.1) and its networking plugin (section 7.1).
- The engine that loads the module, with the entry-point ABI it calls: create an instance, call a method, get and set
  a property, raise a signal, serialize and restore state, typed field get and set by index (generated as ordinary C#
  with `[UnmanagedCallersOnly]` exports, so the compiler sees nothing special).

### 7.6 Budgets

- **Build time:** a few seconds from editing a small game to playing it, in the browser, on an ordinary laptop. The
  program is analysed whole on every build; caching the analysis of library compilations that did not change is
  allowed.
- **Module size:** as small as the code allows, and smaller than the same program under NativeAOT (section 1). A small
  2D game compiled from Rust was about 24 KiB compressed; that is the bar to measure against (GodotSharp's reachable
  part comes on top).
- **Call cost:** calls between the module and the engine cost about 3 to 5 ns each in Chrome. Keep the boundary thin.
- **Run-time speed:** faster than the same C# under .NET NativeAOT (section 1).
- **Memory:** a lower peak memory use than the same C# under .NET NativeAOT (section 1).
- **Toolchain download (owner):** as small as possible; the ballpark aim is **20 to 30 MB** for everything a creator
  downloads to build (Roslyn and cs2wasm as self-hosted WASM, plus the library sources or their cached analysis). It is
  fetched lazily on the first build and cached, measured and reported with every release. Self-hosting (section 9) is
  the main lever: no .NET runtime in the download.

---

## 8. Sandbox: the platform every module gets

The module runs in the browser's WASM sandbox with only its host's imports. **Every module gets the platform .NET's own
browser target gets, through host imports, the same for every program**: files over the host's file system, HTTP
through the host's fetch, environment variables, console, time and randomness. What .NET's browser target does not
support (processes, raw sockets, the registry) behaves as it does there: it fails at run time with .NET's exception.
**Platform invoke into native libraries** (`DllImport`, `LibraryImport`, COM), which cannot exist in WASM, is the only
build error, naming what was called. The compiler never cuts functionality for a kind of program: an embedder that
wants a module to have less (Bazoodle's games: no files, no network) does it in its host, by what its imports do, or in
its own code.

What does exist, though .NET programs might expect it not to here (owner):

- **`unsafe` code and pointers compile.** A pointer is an offset into the module's own linear memory, which the WASM
  sandbox already confines, so unsafe code cannot reach anything outside the game. A pointer into an object (`fixed`,
  `&field`) keeps the object alive and in place for the pointer's scope. `AllowUnsafeBlocks` is honoured.
- **Runtime code generation runs on a bundled interpreter.** `Reflection.Emit`, `DynamicMethod`, `Expression.Compile`
  and the like work through a small interpreter that is linked in only when the program uses them; programs that do not
  pay nothing.
- **Reflection metadata is kept for what code reaches.** The compiler keeps the types, members and attributes the
  program's reflection calls can be proven to reach (the trimming analysis NativeAOT does), and warns at each spot it
  cannot follow. A project-level attribute keeps a type's metadata whole.

---

## 9. Hosting and self-hosting

- **In the browser.** The embedder runs Roslyn and the compiler in a Web Worker. Downloads are fetched on the first
  build and cached; players never download the compiler.
- **Until it compiles itself,** the compiler runs on the browser .NET runtime next to Roslyn.
- **Self-hosting, Roslyn included (owner).** The compiler is written in C#, so it compiles itself to WASM and runs as a
  module, and it compiles Roslyn too, so the editor needs no .NET runtime at all; Roslyn is the largest and most
  demanding test the memory model will get.
- Desktop builds use the same browser toolchain; there is no native build path to keep in step.
- **Headless too.** The same toolchain module runs outside a browser, in a WASM runtime (Wasmtime or a headless browser),
  for an embedder's server-side builds: Bazoodle rebuilds every published game itself and compares the result with the
  creator's build. Output is byte-identical wherever it runs (section 7.3), and the build-time code it runs (source
  generators, analyzers) stays inside the WASM sandbox.

---

## 10. Testing and evidence

- **The oracle is .NET.** A conformance suite of C# programs (starting from Roslyn's and `dotnet/runtime`'s own test
  programs where they fit) runs under .NET and through the compiler; their output
  (console output, or the logged sequence of import calls) must match exactly. Every language feature in section 3 has
  programs in the suite, including the hard cases: cycles, finalizers, weak references, exceptions through async,
  custom awaiters, generic virtual methods.
- **The Godot oracle is stock Godot with .NET.** A C# script using only allowed APIs must make the same sequence of
  engine calls under stock Godot 4.7.2 with .NET as under the compiled module.
- **Evidence rules:** claim nothing that has not been tested; label anything unmeasured as unmeasured; report numbers
  and failures as they came out, including results that hurt the design. Measure the reference-count operations
  removed, build time, and module size, run-time speed and peak memory against NativeAOT, on real programs.
- **Smallest proofs, in order:**
  1. Pure C#: a set of console programs (math, collections, strings, LINQ, async on one thread, exceptions); output
     equals .NET.
  2. Upstream GodotSharp's math types (`Vector2`, `Basis`, `Transform3D`, `Color`) with a test program; output equals
     .NET. Needs no engine.
  3. One wrapper: `Node2D` from Bazoodle's patched generator, a 20-line script, upstream's source generators, and a stub
     boundary that logs calls; the log equals the one from stock Godot with .NET.
  4. The compiler compiles itself, Roslyn included, and the self-hosted toolchain is faster, smaller and uses less
     memory than the same code under NativeAOT (section 1), measured on Roslyn compiling a fixed set of programs.
  5. One game: Bazoodle's `hello2d` in C#, built in the browser, played in the engine.

---

## 11. Not in scope

- A .NET runtime, a JIT, a garbage collector of any kind, WasmGC, the WASM component model.
- Hot reload of individual methods (Bazoodle may swap whole modules and carry state across; the compiler need not help).
- Native (non-WASM) targets.
- A language subset of its own.

## Sources

- Lobster memory management: <https://aardappel.github.io/lobster/memory_management.html>
- Roslyn operation tree: <https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.ioperation>
- WebAssembly exception handling: <https://github.com/WebAssembly/exception-handling>
- Godot 4.7.2 `modules/mono` (GodotSharp, `Godot.SourceGenerators`, `bindings_generator.cpp`)
- The engine side (Bazoodle's private repository) is not needed to build the compiler: proofs 1 and 2 need no engine,
  and proof 3 uses a stub boundary that logs calls

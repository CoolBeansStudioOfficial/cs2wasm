# il2wasm: specification

il2wasm is a compiler that takes a .NET project, as `dotnet build` produces it, and emits one small, fast WebAssembly
module with no garbage collector and no .NET runtime. It is the owner's own project, in its own repository (formerly
cs2wasm, which compiled C# source; see section 12).

il2wasm is a general compiler, tied to no engine or product. Engines, frameworks and platforms are ordinary .NET
packages that a project references:

- **libgodot**, through 2dog: a NuGet package that embeds libgodot (Godot built as a library) into a .NET project and
  ships its C# bindings. A game is a .NET project that references it.
- **Bazoodle**: its API and its web UI are packages that the game project and the libgodot package use.
- **A web framework**: a package that gives C# code the page (DOM, events, HTML and CSS), so a site or a web app is a
  .NET project compiled to WASM plus vanilla HTML/CSS, with no runtime and no garbage collector. A GUI editor for it is
  a product on top, not part of the compiler.

This document is self-contained: a building session needs nothing else to start, apart from the sources it names.

Words used throughout: **the compiler** is il2wasm; **the project** is the .NET project being compiled, with its
package and project references; **the host** is whatever loads and calls the module (a browser page, a WASM runtime,
an engine package's loader); **the module** is the compiler's output; **a native library** is code that is not .NET
(libgodot), shipped in a package as WASM.

**Licence:** MIT.

---

## 1. Goals and fixed decisions

| Topic | Decision |
|---|---|
| Input | **A .NET project**: its `.csproj` (or any MSBuild project that produces .NET assemblies), built as `dotnet build` builds it, with every `PackageReference` and `ProjectReference`. The compiler reads the resulting **IL assemblies** (ECMA-335) and their metadata, with portable PDBs where present for source positions. Any .NET language that produces IL works; C# is the main one |
| Output | One 32-bit **core** WASM module (plus the native libraries' WASM it links, section 8), with imported memory. Not a component, not WasmGC |
| Pipeline | MSBuild and the language compilers produce IL; il2wasm compiles IL to WASM. No other compiler in the path (no LLVM, no C) for .NET code |
| Correctness | **The same observable output as the project under .NET.** Observable output is the sequence of calls the module makes to its imports and native libraries, and their arguments. How the compiler gets there is free |
| Input scope | **Every project .NET builds compiles**, packages included. Where the compiler cannot prove something cheap, it pays a run-time cost; it never rejects a project for being hard to analyse. P/Invoke into a native library shipped as WASM works (section 8); a P/Invoke into a library with no WASM build compiles to the `DllNotFoundException` .NET throws when the library is missing, with a build warning naming the library and the package |
| Memory | No garbage collector, no background collector, no whole-heap scan. Compile-time reference counting copied from Lobster (section 4) |
| Determinism | **A hard requirement.** Bit-identical results on every machine, every run, under any load; byte-identical modules from the same inputs (section 7) |
| Speed | Speed and module size are the priorities, reached by real translations of .NET concepts, not by imitating the .NET runtime |
| Against NativeAOT | **Anything il2wasm compiles is faster, smaller and uses less memory than the same project under .NET NativeAOT**, measured against NativeAOT-LLVM's WebAssembly build in the same engine. Run time, frame times (no spikes), binary size and peak memory are each measured on real projects |
| Threads | Real threads (section 4.5). **A program that uses threads must never run slower than the same program without them**, and work split over N threads must finish faster than on one |
| Self-hosting | The compiler is written in C#; it compiles itself, and the build toolchain it needs, to WASM (section 10) |
| Whole program | Every assembly the project references is present at the final build. Whole-program analysis is allowed and expected |
| Hosting | It runs in the browser, in a Web Worker, and headless in a WASM runtime (section 10) |

**Philosophy.** Find real translations of .NET concepts into WASM, not workarounds. Runtime machinery that imitates
what .NET does is a sign the real translation has not been found yet. Copy designs that someone has already refined
(Lobster for memory, ECMA-335 and NativeAOT for IL semantics, NetWasm for project, package and P/Invoke handling)
rather than invent new ones. When a design hits a hard case, look for a different translation before accepting a
loss, and say plainly when none has been found.

**NetWasm is the reference (owner).** NetWasm (github.com/zion-sati/NetWasm) already compiles .NET projects with
packages and P/Invoke to WASM. Its code may be read directly while writing il2wasm; il2wasm's code is its own,
written for il2wasm's design, and NetWasm's code is not pasted into this repository. NetWasm is a bounded profile
(one thread, ordinal-only text, rewritten library parts): its mechanisms are studied, its deviations from .NET are
not adopted. Its design is summarised in `docs/netwasm-design.md`.

---

## 2. Architecture

```
.NET project (.csproj, packages, project references)
   ──> restore and build (MSBuild, Roslyn, source generators, analyzers) ──> IL assemblies + portable PDBs
   ──> il2wasm:
       1. load: the project's assemblies, every referenced package assembly, the compiler's own core library (section 9)
       2. reachability from the roots (section 6.1)
       3. IL to the compiler's IR: stack to SSA, exception regions, value types, generics
       4. whole-program analyses: call graph, ownership and aliasing, thread escape, def-use, closed forms (sections 4, 11)
       5. plugin passes (section 11)
       6. specialization and optimization (sections 4, 5)
       7. WASM emission, line tables, custom sections (section 6)
       8. linking of native libraries' WASM (section 8)
   ──> module.wasm (+ native libraries' WASM, + a loader, + diagnostics, + source map)
```

- **An in-process API first.** The compiler is a library called with a set of assemblies and options, returning the
  module bytes, diagnostics and a source map. A command line (`il2wasm <project or assemblies> -o out`) wraps it; an
  MSBuild target lets `dotnet publish` produce the module.
- **The IR is the compiler's own**, below IL: SSA values, explicit control flow, explicit ownership. Everything after
  step 3 works on it. The IL front end is the only part that knows IL.
- **The type system is NativeAOT's (owner)**, the MIT-licensed one in dotnet/runtime (`ILCompiler.TypeSystem`), over
  System.Reflection.Metadata: types, layouts, generics, virtual and interface resolution, as NativeAOT computes them.
- **Source facts come from IL and PDBs.** What the memory analysis needs to know (that a loop walks a list, that a
  getter returns its field, that a method is an async state machine) is recovered from IL patterns, compiler-generated
  attributes and names, and the PDB where present, never by decompiling to a source language.

---

## 3. Coverage and semantics

The compiler implements **ECMA-335 as .NET 11 runs it**: everything IL can express, as the CLR defines it. Language
features (async, iterators, lambdas, pattern matching, records, LINQ) arrive already lowered by the language compiler,
so they need no lowering of their own; the compiler recognises the shapes the C# compiler produces where that lets it
do better (section 4).

- types: classes, structs (including byref-like and `readonly`), interfaces with default and static abstract members,
  enums, delegates, generics with constraints, nullable value types, function pointers, fixed buffers, inline arrays;
- generics: instantiate per type where it pays (value types always), share code between reference-type
  instantiations where it does not; generic virtual methods and the full set of constraints;
- exceptions: `try`/`catch`/`finally`/`fault`/filters, with .NET's two-pass order where it is observable (a filter runs
  before the `finally` blocks of the frames it would unwind), using native WASM exception handling (`exnref`);
- static constructors with .NET's rules (`beforefieldinit`, first access, once per program, thread-safe);
- `unsafe` code, pointers, `Unsafe`, `MemoryMarshal`, spans over any memory (section 6.5);
- reflection over the metadata kept (section 6.6), and `dynamic` through the C# runtime binder on it;
- runtime code generation (`Reflection.Emit`, `DynamicMethod`, `Expression.Compile`) on a bundled IL interpreter,
  linked in only when the project uses it.

**Async follows .NET's threading.** A continuation runs where the current `SynchronizationContext` posts it, as in .NET
(an engine package installs its own, so script code returns to the engine thread after `await`). `Task.Run`, the thread
pool, `Parallel` and `Thread` run on real worker threads (section 4.5).

**An exception that escapes an exported entry point** reaches the host as a catchable failure carrying the exception's
type, message and stack trace with file and line (section 6.3). **Stack overflow ends the instance**, as it ends the
process under .NET. **Out of memory** throws `OutOfMemoryException` when the host's memory limit is reached.

**Integer and floating-point semantics** are the CLR's: wrapping and checked arithmetic, division by zero throws,
IEEE 754 as WASM gives it. Determinism rules are in section 7.

**Pointers are 32-bit (owner).** The module is wasm32: `IntPtr.Size` and `sizeof(nint)` are 4, as on 32-bit .NET.
Where a test's output depends on the pointer size, the oracle is 32-bit .NET (x86).
Fields of 8-byte types (`long`, `ulong`, `double`) are 8-byte aligned in classes and structs (owner), as in
NativeAOT's WASM build and 32-bit ARM .NET, because WASM atomics need natural alignment; where a struct's size or
field offsets are observable, the expected value is that layout's, not 32-bit x86's.

---

## 4. Memory: compile-time reference counting

The model is copied from the Lobster compiler: <https://aardappel.github.io/lobster/memory_management.html>. Read it
first. In Lobster's tests the analysis removes about 95% of reference-count operations at compile time; that is the
bar. Garbage collection is ruled out because its pauses (measured at 47 ms native and 64 ms in Chrome with a 64 MB
heap) are unacceptable for games and interactive pages on throttled devices.

### 4.1 Ownership and borrowing

- **Every heap allocation has a single owner**, usually the first local, field or element it is stored in. Every other
  use is a **borrow**, with no run-time cost.
- **Each operation declares** whether it owns or borrows each operand and whether its result is owned or borrowed.
  Where the two sides disagree, the compiler inserts a count increase, or holds a temporary until the end of its
  scope.
- **Locals.** An assigned local owns its value. A local that is never reassigned borrows from its source for its live
  range. In SSA this is a property of the value, so the analysis is per value, not per IL local slot.
- **Retroactive owners.** If a borrowing value's source is written while the borrow is live, the value becomes an owner
  instead. Behaviour never changes, only cost.
- **Specialization by ownership.** A function is specialized for the ownership its callers need. Large functions and
  virtual-dispatch groups get one shared, borrow-first specialization. Return values are owned.
- **Calls (owner).** A call is the statement that ends a last-use argument's life: by default the caller lends it
  and releases it right after the call returns. When the callee keeps running after its own last use of that
  argument (a loop or further calls follow that use), the caller hands its ownership over instead, so the object is
  freed at the callee's last use (an object a long-running call such as a main loop stopped using is not kept).
- **Nothing is rejected.** Where Lobster reports an ownership error, the compiler inserts a run-time count increase.
- **The analysis is compile-time only.** No run-time "is this shared" checks decide a borrow.

### 4.2 Values

- **Structs are inline values**, stored in their parent, copied on assignment, and mutable, as the CLR defines them.
- A struct containing references carries their ownership with it.
- Boxing allocates an owned object like any other.

### 4.3 Cycles (owner)

- The compiler builds the graph of which types can hold references to which. **Types on a cycle in that graph are
  cycle-capable**; others never pay for cycle handling.
- Objects of cycle-capable types join **groups** when stored into each other. References inside a group follow the
  ownership structure (an owner chain, references back up it uncounted), so a subtree cut from a living structure is
  destroyed **at the store that cuts it**, and a group nothing outside refers to goes whole.
- What a structure cannot be decided about at the store is decided at the release that makes it garbage, in work
  bounded by what changed, never by visiting what a living structure owns.
- **No run-time walk of the heap, no deferred frees, no work moved to a later time.** Nothing is ever freed later than
  the statement that makes it unreachable.
- Compile-time proofs (a back reference to the owner, an exclusive part, a store that goes down a structure) take
  types or stores out of the group machinery where they hold; the run-time structure is the fallback where they do
  not.
- **The proofs are the core, the fallback is measured (owner).** No run-time scheme can free an arbitrary cut-off
  mutable cycle at the cutting statement in bounded work (Sotoudeh 2025; docs/research/rc-literature-review.md), so the
  owner-chain proofs carry the design. How many stores they cover and what the fallback costs are measured on the
  self-hosted compiler and the game, and the cases left are brought to the owner with those numbers before any other
  rule is adopted for them.

### 4.4 Finalizers and weak references

- **Finalizers run** when an object's last owner releases it (or with its group), exactly once. Any timing the CLR
  allows is fine; a finalizer that never runs is a leak.
- **Finalizers run inline (owner)**, at the store or release that frees their object, never on a separate thread or
  later. Inside a finalizer, `Thread.CurrentThread` and `ManagedThreadId` report a finalizer thread, and locks the
  interrupted code holds are not the finalizer's own; a finalizer that would block on such a lock (it would wait under
  CoreCLR) throws or is reported. In a group, the order is: clear the group's short weak references, run all its
  finalizers, keep the group if a finalizer gave it an outside reference again, otherwise free it.
- **Weak references** (`WeakReference`, `WeakReference<T>`, weak and resurrection-tracking `GCHandle`s,
  `ConditionalWeakTable`, `DependentHandle`) keep .NET's observable meaning: a short weak reference reads empty before
  any finalizer of the objects that became unreachable with its target runs.
- `GC.Collect`, `GC.KeepAlive`, `GC.SuppressFinalize`, `GC.ReRegisterForFinalize` and similar keep their observable
  meaning.

### 4.5 Threads (owner)

Threads are how a program gets the most out of a device, so programs have real ones.

- **Each thread is a Web Worker running an instance of the module on the same shared memory** (WASM threads with
  `SharedArrayBuffer`; the page must be cross-origin isolated). `Thread`, the thread pool, `Task.Run` and `Parallel` run
  on a pool sized to the device's cores.
- **Threads must pay for themselves (owner).** Two bars, both required: a program that uses threads runs its
  single-threaded code **no slower than the same program built without threads**; and work split over N threads
  finishes faster than on one, near N times for independent work.
- **What a thread cannot share costs nothing extra.** A static thread escape analysis, at compile time, finds the
  objects, groups, fields and allocation sites another thread can reach. Everything else keeps plain counts and every
  single-thread path and optimization. Shared data pays for synchronization only where it is shared, and as little as
  the hardware allows (one lock or atomic per operation, never per member).
- **.NET's memory model and synchronization apply**: `lock`, `Monitor`, `Interlocked`, `volatile`, `SemaphoreSlim`,
  concurrent collections, `Task` joins. Correctly synchronized code behaves exactly as under .NET; racy code stays
  memory-safe, never hangs, finalizes every object exactly once, and gives an outcome .NET allows.
- **The main thread is never blocked by the runtime.** A blocking wait on it is allowed by the language and reported
  as a build notice where the compiler can see it.
- **Thread-local state** (`[ThreadStatic]`, `ThreadLocal<T>`, `AsyncLocal<T>`) works.
- **Calls into a native library from worker threads** follow that library's own rules (an engine package states which
  of its calls are thread-safe).

### 4.6 All state in linear memory

**Everything a module instance knows lives in its linear memory and its mutable globals.** Nothing lives in
JavaScript, in WasmGC objects, or in tables that change after start-up. A host can snapshot an instance by copying its
memory and restore it by copying it back (section 11.2). Keep the heap compact (reuse freed space): a snapshot costs in
proportion to the memory in use.

---

## 5. Optimization

- Optimization is the compiler's own, on its IR: inlining, devirtualization, escape analysis (an object that never
  escapes needs no heap allocation), ownership and specialization (section 4), redundant-load elimination by field
  identity, dead-code removal, constant folding and evaluation, reachability reduced to a fixed point.
- **A final pass over its own WASM output** cleans up what lowering produced: merging identical function bodies,
  packing locals, removing redundant pairs, simplifying branches, ordering functions and globals by use, packing data.
  No external optimizer is required.
- The browser's optimizing tier does register allocation, so the compiler does not need to.
- **Size matters as much as speed**: users download every module. Emit only what the roots reach (section 6.1).
- **Every optimization has a switch and a test.** Each can be turned off by a compile-time switch, and each has a
  conformance test whose output changes when the rule it relies on is deliberately broken.
- **No optimization may change behaviour**, including error paths, exception types and messages, `GetType()` and
  reflection results.

---

## 6. The module

### 6.1 Shape

- 32-bit core WASM, **shared memory imported** from the host (shared because threads need it; started large, because
  growing memory is slow in browsers).
- **Imports: the host's functions and the native libraries' exports, nothing else.** There is no WASI layer: console,
  time, randomness, files, HTTP and environment variables come from host imports that the core library calls (section
  9), the same for every module.
- **Roots.** `Main` when the project has one; otherwise the exported entry points (section 6.2). Objects live between
  calls; a host or a native library holds some of them through handles.
- **Initialization.** An exported start function runs once before any other export (memory layout, statics that are not
  lazy, the thread pool). Worker instances have their own per-thread start export.
- **A version section** records the compiler version and the ABI version, so a host can refuse a module it does not
  understand. **Custom sections supplied by packages or the project** are written into the module as given.

### 6.2 Exports

- A static method marked `[UnmanagedCallersOnly(EntryPoint = "name")]` becomes a WASM export with that name. Its
  parameters and result are WASM numbers and pointers into the module's memory.
- **No exception crosses an export.** Each export is wrapped: an exception that escapes it is reported through the
  host's exception-report import (type, message, stack trace) and the export returns its failure value.

### 6.3 Debug information

- **Source positions survive.** Every emitted instruction maps back to the source file and line, from the portable PDB.
  The compiler emits the WASM `name` section and a source map.
- **Stack traces at run time.** An exception's `StackTrace` gives methods with file and line, as .NET does with PDBs
  present. Paths that do not throw pay nearly nothing for it.

### 6.4 Diagnostics

Compiler diagnostics are reported like MSBuild's and Roslyn's: severity, a stable id, message, and the source location
(from the PDB) or the assembly and member.

### 6.5 Unsafe code

A pointer is an offset into the module's own linear memory, which the WASM sandbox confines. A pointer into an object
(`fixed`, a `ref` to a field) keeps the object alive and in place for its scope; a span or `ref` into a collection's
storage keeps that storage alive as .NET does.

### 6.6 Reflection

The compiler keeps the types, members and attributes the program's reflection calls can be proven to reach (the
trimming analysis NativeAOT does, honouring the trimming attributes packages already carry) and warns at each spot it
cannot follow. A project-level setting keeps an assembly's or type's metadata whole.

---

## 7. Determinism (hard requirement)

Programs that run on several machines (networked games, server-side rebuilds, replays) rely on **bit-identical
results everywhere**, so the compiler must not introduce variation, and nothing it adds may vary with load or timing:

- **No relaxed or non-deterministic WASM instructions** (no relaxed SIMD). Deterministic SIMD is fine.
- **NaN payloads never become observable.** Float stores canonicalize NaNs (x64 and arm64 produce different default
  NaNs); this is never weakened for speed.
- **Hash codes are deterministic.** `string.GetHashCode` uses a fixed seed; `object.GetHashCode` and
  `RuntimeHelpers.GetHashCode` come from a per-instance counter, never from addresses.
- **Nothing depends on memory addresses** in observable output.
- **Math functions are computed in WASM, never imported from JavaScript**, because browsers' `Math` functions differ
  between engines. Only operations WASM defines exactly use WASM instructions directly.
- **The runtime's own behaviour does not depend on scheduling or load.** When objects are freed, when finalizers run
  and what weak references read are decided by the program's statements, not by which thread got there first or how
  busy the machine is. A difference that shows up in one loaded run in a thousand is a bug.
- **The output is reproducible:** the same project, packages and compiler give a byte-identical module on every
  machine, in the browser and headless, built by the .NET-hosted or the self-hosted compiler.
- **Native libraries** linked into a module (section 8) are built reproducibly too, pinned by the package version, so a
  project's whole output is reproducible.
- Time and randomness come only from host imports; the host decides what they return.
- **Thread timing is visible to plugins.** The analyses mark every value that can depend on scheduling (a race, a float
  sum accumulated by several threads, the order of a concurrent collection), so a plugin (section 11) can treat it as
  non-deterministic. Order-independent parallel work is deterministic and needs nothing.

---

## 8. Packages and native libraries

The design follows NetWasm's (a reference, section 1).

- **Restore is .NET's.** Packages are resolved by NuGet with the project's lock file; versions are pinned so a build is
  reproducible. In the browser the toolchain fetches packages and caches them.
- **Managed package code is IL**, compiled like the project's own. Nothing about a package is special: an engine or a
  framework is a package.
- **P/Invoke into a native library shipped as WASM** (`DllImport`, `LibraryImport`, function pointers obtained from it):
  a package carries the library's WASM build next to its assemblies. The compiler links the module against it: each
  P/Invoke becomes a call to the library's export, marshalling is generated at compile time as the CLR's marshalling
  rules define it, and callbacks into managed code (`UnmanagedCallersOnly`, delegates marshalled as function pointers)
  become the module's exports the library calls.
- **Memory between the module and a native library** is one shared linear memory or separate memories with explicit
  copies, as the library's build allows; the choice is part of the library's package and is decided per library with
  call cost in mind (a boundary call should cost a few nanoseconds).
- **A P/Invoke into a library with no WASM build** compiles to what .NET does when the library is missing: the call
  throws `DllNotFoundException` (or `EntryPointNotFoundException` for a missing entry point) when it runs, and the build
  warns, naming the library and the package that needs it.
- **COM** is not supported (as on .NET outside Windows).

### 8.1 libgodot through 2dog

2dog is a NuGet package that embeds libgodot into a .NET project and ships its bindings. A game is a .NET project that
references it; the compiler compiles the project and 2dog's managed code, and links libgodot's WASM build as a native
library. Engine calls from worker threads follow Godot's own thread rules. The Godot oracle (section 13) is the same
project run under .NET with 2dog's native build.

### 8.2 The web framework

A package gives C# code the page: elements, events, styles, timers, fetch, through host imports. A project that uses
it compiles to the module plus plain HTML and CSS and a small loader; there is no runtime to download and no garbage
collector to pause the page.

---

## 9. The core library

- **It is upstream's library, as NativeAOT builds it** (owner): `System.Private.CoreLib` from `dotnet/runtime`,
  compiled to IL for il2wasm's runtime, as NativeAOT has its own CoreLib build. Only the runtime boundary is the
  compiler's own (memory, counting, groups, exceptions, threads, the host imports); everything else is upstream's code
  and behaves exactly as .NET does.
- **The libraries above CoreLib are the stock ones (owner):** the IL of .NET's browser-wasm runtime pack, unmodified;
  their native calls (`libSystem.Native` and the like) are served by il2wasm's runtime over host imports.
- **Only what the program reaches is emitted**, and only the libraries the program uses are loaded.
- **Invariant globalization.** Formatting, parsing, comparison and casing are culture-invariant on every machine, so
  every machine computes the same strings, and no ICU data is shipped. Localized text is a package's concern.
  `TimeZoneInfo.Local` takes its offset from the host's clock.
- **Clock and randomness** come from host imports; `System.Random`'s default seed from a seeded host import.
  `Console` goes to the host's log import.
- **The platform every module gets** is the one .NET's browser target gets, through host imports, the same for every
  program: files over the host's file system, HTTP through the host's fetch, environment variables, console, time and
  randomness. What .NET's browser target does not support (processes, raw sockets, the registry) fails at run time with
  .NET's exception. The compiler never cuts functionality for a kind of program: a host that wants a module to have less
  does it in what its imports do.

---

## 10. Hosting and self-hosting

- **In the browser.** The toolchain (MSBuild's restore and build of a project, the language compiler, source
  generators and analyzers, il2wasm) runs in a Web Worker as WASM, fetched on the first build and cached. Users of a
  built module never download the toolchain.
- **Self-hosting (owner).** il2wasm is written in C#, so it compiles itself to WASM, and it compiles the rest of the
  toolchain it needs (Roslyn, the parts of MSBuild and NuGet a build uses), so the browser needs no .NET runtime at all.
  Roslyn is the largest and most demanding test the memory model gets.
- **The self-hosted toolchain is as fast as NativeAOT's build of it** and uses less memory. A small project builds in
  a few seconds. Starting a build from a snapshot of an earlier build's memory (section 4.6) is allowed when it is
  byte-identical to a build from the start and a stale snapshot is always refused.
- **Headless too.** The same toolchain runs in a WASM runtime for server-side builds (a platform rebuilding every
  published project and comparing with the creator's build). Output is byte-identical wherever it runs, and build-time
  code (source generators, analyzers, MSBuild tasks) stays inside the WASM sandbox.

---

## 11. Plugins, snapshot and partial runs

### 11.1 Plugin passes

Some platforms need a whole-program analysis of the kind the compiler already does (Bazoodle's networking, the
"Invisible API": for every property its readers and writers, the condition each read runs under, which writes depend
on input or hidden values, which per-tick code has a closed formula of time). Doing that as a separate tool would
repeat the analysis and lose the compiler's precise aliasing, so the compiler has a **plugin pass interface** and stays
general-purpose; such a platform ships its plugin as a package.

A plugin pass, run after the whole-program analyses and before specialization and optimization, can:

- **read the analyses**: the call graph (with devirtualized targets), reachability, ownership and aliasing, thread
  escape and scheduling-dependent values, def-use chains and control-flow graphs per method, and closed forms of
  loop-carried values;
- **map everything back to source**: every IR operation knows the IL instruction, member and source location (from the
  PDB) it came from, so a plugin reports in the developer's terms;
- **instrument** at chosen operations, **add functions** (which may be exported), **clone a method with calls to chosen
  imports suppressed**, and **emit custom sections and diagnostics**.

Plugins are .NET libraries written against the compiler's versioned plugin interface, loaded in-process, and compiled
into the toolchain module when the compiler is self-hosted.

### 11.2 Snapshot, restore and partial runs

- A module's whole state is its memory plus its mutable globals (section 4.6); the compiler exports what a host needs
  to save and restore an instance completely.
- A host may re-run code from a restored state and discard the result.
- **With threads running,** a snapshot of live memory happens only with worker threads parked: the module supports
  parking every worker at a safe point on the host's request, and resuming them.

---

## 12. From cs2wasm

The repository was cs2wasm: the same memory model, runtime and back end with a front end that read C# source through
Roslyn's operation tree. That front end could not compile projects with compiled packages, which most real projects
are. il2wasm keeps:

- the runtime (allocator, reference counting, groups, finalizers, weak references, threads, the host imports);
- the IR's back end: the WASM passes (field loads, the inliner, NaN canonicalization, memory synchronization, size
  work), emission, debug information;
- the host, the snapshot, the self-host tooling;
- the conformance suite, stress, frames and bench tools, which become the oracle for the new front end.

The IL front end replaces the operation-tree front end, which is removed, not kept beside it; the memory analyses
that keyed on source shapes are re-derived on the IR from IL patterns.

---

## 13. Testing and evidence

- **The oracle is .NET.** Test projects run under .NET and through the compiler; their output (console output, or
  the logged sequence of import and native-library calls) must match exactly, checked and as shipped, and under heavy
  machine load.
- **The main suite is NetWasm's (owner)**, its far more complete set of test projects run against il2wasm with .NET as
  the oracle, with the expected outputs taken from .NET, never from NetWasm's own profile. Its tests are used as its
  licence allows (run from a pinned checkout of its repository if they may not be copied), and never shape the
  compiler's code. The tests carried over from cs2wasm cover what NetWasm's do not: cycles, finalizers, weak
  references, threads under load, the memory model's edge cases.
- **Native-library oracles are .NET with the native build.** A 2dog project must make the same sequence of engine calls
  under .NET with 2dog's native libgodot as compiled to WASM with libgodot's WASM build.
- **The self-hosted compiler is a correctness check, not only a speed one.** Every change to the runtime, the IR or
  code generation is checked by building the self-hosted toolchain and comparing its output with the .NET-hosted
  compiler's, byte for byte, on programs larger than hello world, and timing it.
- **Evidence rules:** claim nothing that has not been tested; label anything unmeasured as unmeasured; report numbers
  and failures as they came out, including results that hurt the design. Measure reference-count operations removed,
  build time, module size, run time, frame times and peak memory against NativeAOT, on real projects.
- **Proofs, in order:**
  1. Console projects (math, collections, strings, LINQ, async, exceptions, threads): output equals .NET.
  2. A project with real NuGet packages (a serializer, a collections library, a math library) and a P/Invoke into a
     small native library shipped as WASM: output equals .NET.
  3. The compiler compiles itself and its toolchain; the self-hosted toolchain is faster, smaller and uses less memory
     than NativeAOT's build of it, measured on fixed projects.
  4. A web page: a project using the web framework package, built in the browser, runs with no runtime.
  5. One game: a 2dog project, built in the browser, played.

---

## 14. Not in scope

- A .NET runtime, a JIT, a garbage collector of any kind, WasmGC, the WASM component model.
- Hot reload of individual methods (a host may swap whole modules and carry state across).
- Native (non-WASM) targets.
- Decompiling IL to a source language.

## Sources

- Lobster memory management: <https://aardappel.github.io/lobster/memory_management.html>
- ECMA-335, Common Language Infrastructure
- NativeAOT (dotnet/runtime `src/coreclr/nativeaot`, `src/coreclr/tools/aot`): IL compilation, trimming, CoreLib for an
  AOT runtime
- NetWasm (github.com/zion-sati/NetWasm): the reference for project, package and P/Invoke handling (section 1)
- 2dog: libgodot as a NuGet package with bindings
- WebAssembly exception handling and threads proposals

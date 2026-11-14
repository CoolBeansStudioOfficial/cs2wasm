# cs2wasm: implementation status

What exists today, measured against [SPEC.md](SPEC.md). Claims here are what the test suite checks; anything not
tested is labelled so. Run `dotnet run --project tests/Cs2Wasm.Tests` to reproduce the results.

## Results

- **Conformance (proof 1, partial):** 15 C# programs (arithmetic and math, classes and virtual/interface dispatch,
  structs and enums, strings and formatting, exceptions with stack traces, collections, LINQ and closures,
  generics and pattern matching, nullable value types, tuples, iterators, async/await on one thread, `typeof` and
  seeded `Random`, records) produce output identical to .NET 11's, byte for byte.
- **API checks:** 10 checks of the module shape and embedder API pass (boundary imports, sandbox errors, custom and
  version sections, shared memory import, reproducible output, exception reporting at exports, plugin passes,
  source map).
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
- `goto`; `lock` compiles but takes no lock (there are no threads yet);
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

Not done:
- **cycles (4.3): no arenas yet — reference cycles leak.** A loop building 200 000 two-object cycles grew memory to
  about 41 MB. This is the most important open gap in the memory model. Analysis so far: a type-level cycle graph built
  naively puts every type with an `object`, interface or delegate field into one strongly connected component, so
  arena scoping needs the ownership analysis to narrow field targets first; not designed yet.
- ownership specialization of functions, retroactive owners, the counting-elision statistics (4.1);
- weak references, `ConditionalWeakTable` (4.4);
- threads (4.5): no workers, no atomic counts, no thread statics, no parking (7.2).

### 5. Optimization — minimal
Done: reachability from exports (only reached methods are emitted), devirtualization when a virtual call has one
implementation or the receiver type is sealed, a final pass over the WASM (identical-body merging, `local.set`/`get`
→ `local.tee`, dead code after unconditional branches, call targets ordered by use for short indices).
Not done: inlining, escape analysis (stack allocation of non-escaping objects), constant folding beyond Roslyn's,
local packing, data segment packing.

### 6. The module — done except threads
32-bit core module, imported shared memory (`env.memory`, size from options), passive data segment initialized by the
`init` export, exports from `[UnmanagedCallersOnly(EntryPoint)]` wrapped so no exception crosses them (reported
through `cs2wasm.report_exception`, export returns 0), boundary-table imports (one typed import per allowed method;
unlisted bodiless methods are `CW0002`), custom sections copied as given, `cs2wasm.version` (compiler and ABI
version), `name` section, `cs2wasm.lines` (code offset → file and line), `cs2wasm.frames` (.NET frame names), a v3
source map. The stack pointer global is exported (`__stack_pointer`, section 7.2); the other mutable state is in memory.
Stack traces: `Exception.StackTrace` and `ToString()` give C# frames with file and line in .NET's format
(`   at Program.Depth(Int32 n) in C:\...\x.cs:line 25`), captured through the engine at `throw` only (no cost on
paths that do not throw) and cut at the catching frame as in .NET. Lambda and local-function frame names
approximate Roslyn's generated names. Per-thread start export: not done (no threads).

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
  `BitConverter`, `Type`, spans over arrays. Missing: `DateTime`, `TimeZoneInfo`, threads, `GodotSharp`.
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

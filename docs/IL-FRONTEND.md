# The IL front end: a map of today's compiler

Written 2026-11-23, after the rename, on `claude/il2wasm-rename` (main 4cffb7c plus the rename). It reads today's
code against docs/SPEC.md sections 1, 2, 9 and 12. It describes the code as found and changes none of it. Line
counts are from `wc -l`. Symbol and operation counts are a grep of the type names, so they show how much a file
uses each, not exact figures. Each step's size is an estimate from reading the code. None of it is measured.

## 0. The short version

- **The cut is clean at the WASM instruction list, not higher.** `src/Il2Wasm/Wasm/*` (7,423 lines) has no Roslyn
  type and no `using` of `Il2Wasm.Compiler`. The only places it names the compiler are in comments. It sees a
  `WasmModule` of `WasmFunction`s whose bodies are `List<Instr>`: WASM opcodes with immediates and an `object? Ref`
  (a `WasmFunction`, `WasmGlobal`, `FuncType`, `FieldTag`, `TypeIdRef`, `TryTableInfo`, `IAddress`).
- **There is no mid-level IR today.** `FunctionCompiler`, `Expressions`, `Calls` and the ~40 files around them
  (38,249 lines in `Compiler/`) walk Roslyn's operation tree and emit WASM stack code directly. Retains, releases,
  borrows and moves are decided while emitting. AUDIT step 5, the SSA IR with explicit ownership, was planned and
  its spike done (AUDIT "Step 5.1"), but the IR was never built.
- **So the IL front end is more than an importer.** SPEC 2 step 3 (IL to SSA IR) has no IR to target yet, and the
  ownership decisions now made inline in the operation-tree walk have to become passes over that IR. Today's
  analyses split into three kinds:
  - those whose decision half already runs on WASM bodies, after the whole program is emitted. They port almost
    as they are.
  - those written as dataflow over the operation tree. They port as SSA dataflow.
  - those that key on a C# source shape. They need an IL pattern recogniser.
- **The Roslyn type model is everywhere in the middle.** There are about 1,966 references to `I…Symbol` types
  across `Compiler/` and the API, and about 2,372 to operation types. The back end's helpers (`RuntimeGen`,
  `Types` layouts, `Arenas`' type graph, `Reflection`, `TypeInfo`, `Frames`) take `ITypeSymbol` and
  `IMethodSymbol`. They need a type model over IL metadata (section 5, decision D1).
- **About a quarter of `Compiler/` disappears.** It is the code that lowers C# the way Roslyn would, and IL arrives
  already lowered: state machines, closures, records, anonymous types, tuples, `dynamic`, expression trees,
  interpolated handlers, implicit indexers, nullable lifting, and Roslyn's generated names.

## 1. Where the front end ends and the back end begins

### Front end (knows Roslyn; replaced)

| Files | Lines | What |
|---|---|---|
| `Compiler/FunctionCompiler.cs` | 2,845 | per-method driver: statements, locals, scopes, `try`/`catch`/`finally`, `foreach` (incl. the `List<T>` borrow), `using`, `lock`, the `Own` enum (how a reference on the stack is held), scope-exit releases |
| `Compiler/Expressions.cs` | 3,758 | expressions to WASM, with counting decided inline (retain, release, borrow, move) |
| `Compiler/Calls.cs` | 1,930 | invocation, argument ownership, virtual and interface dispatch stubs, intrinsics, delegates |
| `Compiler/StateMachine.cs`, `Closures.cs`, `MachineDisplays.cs` | 2,044 | the compiler's own lowering of async, iterators, lambdas and local functions (Roslyn's lowering redone) |
| `Records.cs`, `AnonymousTypes.cs`, `Tuples.cs`, `Nullable.cs`, `InterpolatedHandlers.cs`, `ImplicitIndexers.cs`, `DynamicLowering.cs`, `DynamicCalls.cs`, `ExpressionTrees.cs`, `FunctionPointers.cs` | ~3,000 | C# features lowered by the compiler. All arrive lowered in IL |
| `RoslynNames.cs`, `GeneratedReflection.cs`, `RoslynNamesCheck.cs` | 1,186 | rebuild the names Roslyn gives generated methods and types, for traces and reflection. In IL they are in the metadata |
| `Library.cs` (with `UpstreamSources`) | 739 | the library as Roslyn compilations built from source at every compile; program symbols mapped onto library symbols by documentation id |
| `Frontend.cs`, `Api.cs` (`Il2WasmCompiler.Compile(Compilation, …)`) | 298 | the entry point takes a Roslyn `Compilation` |
| `Folding.cs`, `Specialization.cs`, `FormatLowering.cs`, `ConstantData.cs`, `StaticAbstract.cs`, `BorrowedStructArgs.cs`, `StructMoves.cs` | ~1,170 | optimizations and lowerings written on operations. They become IR passes or IL pattern recognisers (section 3) |

### Back end (knows no Roslyn; kept)

| Files | Lines | What |
|---|---|---|
| `Wasm/Ir.cs` | 210 | `ValType`, `Op`, `FuncType`, `Instr`, `WasmFunction`, `WasmGlobal`, `WasmTag`, `DataSegment`, `CustomSection`, `TypeIdRef`, `IAddress`, `SourcePos` |
| `Wasm/Emitter.cs` | 157 | appends instructions, tracks block depth, stamps source position, inline scope, count cause |
| `Wasm/WasmModule.cs` | 1,112 | the pass pipeline in `Encode`, body merging, function and global ordering, sections, `il2wasm.frames`, `il2wasm.lines`, `il2wasm.inline`, `il2wasm.version` |
| `Wasm/Inliner`, `Peephole`, `Locals`, `LocalsRc`, `ConstBranches`, `NullChecks`, `InitChecks`, `Loops`, `LoopLoads`, `SmallBlocks`, `IfConvert`, `ColdExits`, `Fresh`, `RcFastPaths`, `SameBaseLoads`, `FieldLoads`, `StaticLoads`, `ShareConstants`, `FrameSlots`, `Frames`, `MemSync`, `FloatStores`, `NoReturn`, `DataCompression`, `Validator` | ~5,500 | the passes over WASM code (SPEC 5's final pass, SPEC 7.3's NaN canonicalisation) |
| `Upstream/System.Private.CoreLib/Boundary/*` (22 files), `Upstream/*/Boundary` | 12,898 | the runtime in C#: allocator, counts, groups, finalizers, weak references, threads, host imports, `Meta`/`TypeInfo` for reflection. It is compiled by the front end like any library code |
| `host/*` | | the JavaScript engine, the snapshot, the web host |

### Mixed (back-end work driven by Roslyn symbols; ported to the new type model)

| Files | Lines | What reaches into Roslyn |
|---|---|---|
| `Compiler/Driver.cs` | 1,679 | the work queue (`MethodInst.Sym : IMethodSymbol`), roots (`[UnmanagedCallersOnly]` found by attribute), `TypeById : List<ITypeSymbol>`, `VirtualStub { Slot : IMethodSymbol, Cases : type → impl }`, static keys, boundary map by documentation id, `Classify` (intrinsic / import / body / synthesized / missing), `IsUpstreamIntrinsic` (by type and method name), `HasBody` (by syntax), field initialisers by syntax (`FieldInitializer`, `UnreadPureInits`), the thread-mode and all-libraries restarts |
| `Compiler/RuntimeGen.cs`, `RuntimeFns2.cs` | 3,935 | generated WASM runtime: cast and type tests, box tests, per-struct release/retain/copy/move, range releases, finalizer chains (`FindFinalizer` walks `BaseType`), dispatch tables, type info, the arena support, frame objects, `Emit()` (data, tables, sections). Takes `ITypeSymbol` throughout. Calls runtime methods by name (`D.LibMethod("Il2Wasm.Runtime.Rt", "FreeObject", 2)`) |
| `Compiler/Types.cs` | 710 | `TypeMap` (monomorphisation over `ITypeParameterSymbol`), `Layout`/`FieldSlot` (offsets; object header 12 bytes, array data at 16), `Prim`, `Key` (string identity of a type), `InlineArray`, `StorageOf` |
| `Compiler/Arenas.cs`, `RefStores.cs`, `InitOnlyFields.cs` | 1,485 | the type graph (container type to stored type per site), SCCs, cycle-capable types. Store sites are emitted as calls to placeholder functions and resolved on the WASM bodies once the graph is known |
| `Compiler/FieldBorrows.cs`, `StaticLoads.cs`, `FieldLoads.cs` | 1,597 | candidates found on the operation tree. Decided on WASM bodies (`FieldWrites` per `WasmFunction`, `StoreSummary`, call graph by `Op.Call` refs) |
| `Compiler/TypedCounts.cs`, `SharedCounts.cs` | 340 | per-static-type placeholders resolved on WASM bodies once Sharing is known |
| `Compiler/Reflection.cs`, `TypeInfo.cs` | 1,667 | reflection records and type info (kept members, attributes, generic arguments, interfaces) from symbols into data |
| `Compiler/Frames.cs`, `Filters.cs` | 287 | frame names in .NET's format from symbols. Two-pass filters (a per-thread handler chain) around try/catch |

### What the back end needs from a front end

1. **Code**: `WasmFunction`s with `FuncType`, locals and a structured body. That means `block`/`loop`/`if`/
   `try_table` nesting, already structured. IL branches are not structured, so the new front end needs a
   structuring step (section 5, step 3). Each `Instr` carries:
   - `Pos`: an index into the line table, `SourcePos(File, Line)`;
   - `Inl`: the inline scope;
   - `Probes` and `Cause`: only for `IL2WASM_COUNT_RC`;
   - `Imm2` bits that some analyses use as marks (`Sharing.Certified`, `CertifiedRefs`; the exact-type hint on a
     release).
2. **Function flags** that passes read:
   - `FrameName`: null hides the frame from traces;
   - `ProgramCode`, `Family` and `RefFamily`: shared generic bodies, `ShareConstants`;
   - `InlineHint` (AggressiveInlining) and `NoInline`;
   - `IsObjectAlloc`, `TableRoot`, `ForwardTo`, `FrameSlots`, `NoFramePromotion`, `StableStatics`,
     `FieldStoreSet`, `NoFieldHoist`, `LendsLines`.

   All of these come from method attributes and flags, and IL metadata has the same facts (`MethodImplAttributes`,
   custom attributes, `[StackTraceHidden]`).
3. **Types**: a type id per constructed type, numbered only at the end (`Driver.AssignTypeIds`; the encoder writes
   `TypeIdRef`). A `Layout` per type: size, alignment, field offsets, `HasRefs`. The object header is
   {count, type id, hash}; arrays put their data at 16 (other ranks after their dimensions). Explicit layout,
   `FieldOffset`, `Pack`, inline arrays and fixed buffers come from attributes today. In IL some of them are
   metadata rows: ClassLayout, FieldLayout, and the pseudo-custom attributes that `GetCustomAttributes` does not
   return.
4. **Dispatch**: there are no vtables. Each virtual or interface slot reached becomes a `VirtualStub`: a
   type-id switch over the constructed types that implement it (`Driver.Implementation(type, slot)`; whole
   program). From the type model it needs ECMA virtual method resolution: newslot, overrides by MethodImpl rows,
   default interface methods, static virtuals, generic virtual methods.
5. **Statics**: one slot each, keyed by string (`Driver.StaticKey(owner, storage)`), with an address in the data
   segment. Each type with an initialiser has an init flag (`Wasm/InitChecks.cs` removes repeated checks).
   Thread statics are offsets from `TlsBase`. `beforefieldinit` is an explicit flag in IL; today it is inferred
   from the source.
6. **Data**: string literals as immortal objects in the data segment (`_literals`), constant arrays (`ConstantData`),
   blobs. In IL these come from `ldstr` (the user-string heap) and from `RuntimeHelpers.InitializeArray` /
   `CreateSpan` over field RVA data.
7. **Exceptions**: one `WasmTag` (`ExceptionTag`) carrying the exception object. `try_table` with `catch_ref`, a
   type test in code (`IsInst`), `throw`. Filters run two-pass through the handler chain (`Filters.cs`). IL's
   exception clauses (typed catch, filter, finally) map onto these one to one. `fault` is a finally that runs only
   when unwinding: a catch-all that rethrows, which C# never emits but other IL may.
8. **Frames and lines**: frame names (`Frames.cs`: .NET's `StackTrace` text) go to `il2wasm.frames`. Positions go
   to `il2wasm.lines` and the source map. From IL: names from metadata, and file and line from portable PDB
   sequence points by IL offset.
9. **Reflection records** (`Reflection.cs`, `TypeInfo.cs`; read at run time by `Boundary/Meta.cs` and
   `TypeInfo.cs`): kept types and members, their attributes as factory functions in the table, generic
   arguments, interfaces, element kinds.
10. **Runtime entry points by name.** Passes and `RuntimeGen` find runtime functions by display name:
    - `Il2Wasm.Runtime.Rt.AllocObject(`, `Rt.Alloc`, `Rt.FreeFixed`, `ThrowHelpers.*`, `Rt.OnThrow(` in
      `Wasm/*` and `FieldBorrows`;
    - `D.LibMethod(type, name, arity)` in `RuntimeGen`.

    The names come from `Driver.Display(IMethodSymbol)`. An IL front end must give the same display strings, or
    give the runtime functions typed identities instead, as AUDIT step 5.5 planned.

### Every place that reaches into Roslyn symbols and needs an IL-metadata equivalent

- **Type identity and substitution**:
  - `Types.Key`, `Types.Canonical`, `TypeMap.For(IMethodSymbol)`, `IsGenericDefinition`, `Types.FullName`,
    `Types.CommentId`, documentation ids for the boundary map;
  - in IL: a type model with instantiation over TypeSpec/MethodSpec signatures, and doc-id strings built from
    signatures.
- **Layout**: `Types.Compute` and `LayoutOf` (fields in declaration order, base first; `InlineArrayAttribute`,
  `FieldOffsetAttribute`); `FieldLoads.cs` reads `FieldOffsetAttribute` and `IsVolatile` too. In IL, `volatile.`
  is a prefix on the access, not a field flag; `IsVolatile` is a `modreq(IsVolatile)` on the field's type.
- **Method classification**:
  - `Driver.Classify`, `IsIntrinsic` (`Il2Wasm.Runtime.IntrinsicAttribute`, `IsUpstreamIntrinsic`),
    `ImportName` (`Il2Wasm.Runtime.ImportAttribute`), `MissingCapability`;
  - `HasBody`, by syntax: in IL, an RVA of zero, `extern`, or `abstract`;
  - `IsSynthesizable`: record and anonymous-type members that today have no body; in IL they have one.
- **Roots**: `[UnmanagedCallersOnly(EntryPoint=…)]` by attribute. In IL the attribute's blob is read the same way.
  `Main` from the assembly's entry point token.
- **Virtual resolution**: `Driver.Implementation`, `FindFinalizer`, the stubs' `Slot`.
- **Statics and initialisers**: `StaticKey`, `FieldInitializer` and `PropertyInitializer` (syntax), `PureInitKeys`,
  `UnreadPureInits`. In IL there is one `.cctor` per type. Dropping the initialisers of statics nobody reads needs
  the `.cctor`'s `stsfld` sequences recognised per field (section 3).
- **`RuntimeGen`**: `ITypeSymbol` in every per-type helper. It asks for `TypeKind`, `IsSealed`, `BaseType`,
  finalizers, layouts, and `TypeById[id] is INamedTypeSymbol { TypeKind: Class }`.
- **The arena and sharing graphs**: `Sharing.Src.Type : ITypeSymbol`, `Arenas` sites by field symbol, `InitOnlyFields`
  (`readonly`: `initonly` in IL), `RefStores`.
- **Reflection, TypeInfo, Frames, Filters**: names, attributes, members.
- **The plugin interface** (`Plugins.cs`, `PluginCalls.cs`, `PluginSolver.cs`, `PluginUnsure.cs`). It is public and
  typed in Roslyn's terms: `IOperation`, `ISymbol`, `IMethodSymbol`, `Location`, `ControlFlowGraph`,
  `Compilation AddSource(...)`. Every plugin (`samples/invisible-api/Plugin`) is written against it, so a new
  interface version over the IR and the type model is needed (SPEC 11.1: every IR operation knows its IL
  instruction, member and PDB location).

## 2. What the back end needs from the IL front end, restated as an interface

To keep `Wasm/*`, `Boundary/*` and most of `RuntimeGen` as they are:

- **The type model**: types, fields, methods, signatures, generic instantiation, layout, virtual resolution, custom
  attributes, documentation ids, display names in .NET's format.
- **The IR**: SSA values, explicit blocks, exception regions, explicit ownership operations (`retain`, `release`,
  `move`, `borrow`), each operation with its IL offset, member and PDB position. It lowers to `Instr` lists with
  structured control flow.
- **Per-function flags** as listed above.
- **The metadata tables** that `RuntimeGen.Finish` and `Emit` build today from symbols: type ids, stubs, statics,
  literals, reflection records and frames.

## 3. The whole-program analyses: what they are written against, and how each ports

Today every analysis has a "candidate" half that reads the operation tree while a method is emitted. Many also have
a "decision" half that runs once the whole program is emitted, over WASM bodies or string-keyed tables. The
decision halves are independent of the input language.

| Analysis | Today | Ports as |
|---|---|---|
| Ownership: counting inline (`Own`, `Expressions`, `Calls`, `FunctionCompiler`) | decided while emitting, per operation | **an IR pass** (SPEC 4.1). This is the largest new piece. First correct form: every reference value owned and counted. Then the borrow rules come back one by one, each with its switch and test |
| `LocalMoves` (last use hands its count on) | operation tree, syntax order | **SSA dataflow** (liveness); simpler in SSA |
| `LocalBorrows` (a reassigned local whose every value is kept alive elsewhere, `for (n = head; n != null; n = n.Next)`) | operation tree | **SSA dataflow**: a phi whose inputs are stable values or field loads of the phi itself |
| `FieldBorrows` (a never-reassigned local borrows from a field, getter or element) | candidates on the tree. Decision on WASM bodies (`FieldWrites`, `StoreSummary`, call graph) | candidate half **SSA dataflow**. The getter shape is the IL pattern `ldarg.0; ldfld f; ret` (an inlined getter). Decision half **kept as is** |
| `OutBorrows` (`TryGetValue(k, out var v)` borrowed) | operation tree (the out argument's local) | **IL pattern**: `ldloca v; call` where the call is the local's only definition. Then dataflow |
| `OwnedParams` (parameters consumed by a store) | operation tree | **SSA dataflow** per parameter |
| `BorrowedStructArgs`, `StructMoves` | operation tree | **IR passes** on struct values (`ldobj`/`stobj`/`cpobj` and calls) |
| `EscapeAnalysis` (per-parameter summaries; frame allocation) | operation tree, memoized per method | **interprocedural SSA dataflow**; ports directly |
| `FreshStores` (a store into a new object that has not let go cannot close a cycle) | operation tree with syntax order | **SSA dataflow** with dominance in place of syntax order |
| `ChainStores` (stores that go down a structure, `x.F = y.G` with y read out of x) | operation tree (local definitions) | **SSA dataflow** over def chains |
| `OwnedArrays` (an array filled while only its maker knows it, kept by a readonly field) | operation tree plus syntax (71 syntax uses) | **SSA dataflow** for the escape part. The builder shapes it recognises (collection expressions, `ImmutableArray` builders) need **IL patterns** over the calls Roslyn emits |
| `StorageKeeps` (a span or `ref` into a collection's storage keeps it) | operation tree: calls such as `CollectionsMarshal.AsSpan` and `GetValueRefOrNullRef`, ref locals' scopes | callee identity is metadata. The ref's lifetime is **dataflow over byref values** (IL byref locals have no scope, so it uses liveness) |
| `InitOnlyFields`, `RefStores` | symbols plus operation tree | `initonly` is a field flag in IL. "No constructor lets `this` out" is escape on the `.ctor`'s IL. Stores through `ref` are `stind.ref`/`stobj`: **dataflow** |
| `Sharing` (thread escape: value sources at every store, argument and return) and `SharingFlow` | sources recorded while emitting (147 operation uses). `SharingFlow` and `SiteTypeIds` are symbol-free graphs | the recording becomes an **SSA pass** where every value already has one definition. `SharingFlow` **kept**. Certified calls come from the call graph |
| `Arenas` (type graph, SCCs, cycle-capable types) | sites from emission; graph from symbols; resolution on WASM placeholders | the graph needs only the type model. Sites come from IR stores. Resolution **kept** |
| `TypedCounts`, `SharedCounts`, `StaticLoads`, `FieldLoads` (compiler side) | placeholders and tags, resolved on WASM | **kept**; tags from the type model |
| `Specialization` (constant arguments), `Folding` (known conditions: `typeof(T) == …`, `IsSupported`, invariant globalization) | operation tree | constants are **SSA**. `typeof(T) == typeof(X)` is the **IL pattern** `ldtoken; call GetTypeFromHandle; ldtoken; call GetTypeFromHandle; call op_Equality` (NativeAOT and ILLink recognise the same). `IsSupported` getters become **substitutions** (the manifests' `substitute` lines already are) |
| `FormatLowering`, `ConstantData` | operation tree | **IL patterns**: `ldstr` plus `string.Format`, `InitializeArray`/`CreateSpan` over RVA fields |
| `Reflection` (trimming: `typeof(X).GetMethod("M")` keeps X.M) | operation tree | **dataflow on IR** with `ldtoken`/`ldstr` constants, honouring `DynamicallyAccessedMembers` (ILLink's design) |
| `Scheduling` (scheduling-dependent values, SPEC 7) | operation tree | **SSA dataflow** |
| `Solver` (closed forms for plugins), `PluginPaths`, `PluginUnsure`, `Entries`, `Instrumentation` | operation tree and Roslyn's `ControlFlowGraph`; public API in Roslyn types | **re-derived on the IR**: natural loops from the CFG, symbolic execution over SSA. `Entries` (operations on a collection's entries) keys on callee identity: indexer set, `Add`, `foreach`'s enumerator. Needs a new plugin interface version |

### Source shapes that must be recognised from IL patterns (SPEC 2: "recovered from IL patterns, compiler-generated attributes and names, and the PDB")

- **`foreach`**. Over an array it becomes an index loop over `ldlen`; over a string, `get_Length`/`get_Chars`;
  over `List<T>` and other struct enumerators, `GetEnumerator`, then `MoveNext`/`Current` in a `try`/`finally`
  that calls `Dispose`. The element-borrow fact (FunctionCompiler `EmitForEach`, the `List<T>` version check,
  `IL2WASM_LIST_FOREACH_BREAK`) must be recognised from these shapes. AUDIT 5.1(c) rejected Roslyn's CFG because
  the CFG turns array and string `foreach` into `IEnumerable`. IL keeps the index loop, so the array and string
  cases come back. The `List<T>` case is a struct enumerator held in a local, whose loop body never writes the
  list.
- **Async and iterators**. In IL they arrive as a state machine type: `[AsyncStateMachine]` or
  `[IteratorStateMachine]` on the stub, a `MoveNext` with a `switch` on `<>1__state`, and hoisted locals as fields
  named `<name>5__N`. Today's own lowering (`StateMachine.cs`) kept them as locals for the ownership analysis.
  From IL, the hoisted fields of a state machine that never escapes its builder have to be treated as locals.
  The awaiter and builder calls are upstream's `AsyncTaskMethodBuilder` code. That code is larger than today's
  `AsyncSupport` path, so speed and size must be measured. Unverified: whether .NET 11's C# emits runtime-async
  methods (`MethodImplOptions.Async`) for net11 targets. If it does, the front end lowers them itself, as
  NativeAOT does.
- **Lambdas and local functions**. Display classes (`<>c__DisplayClassN_M`, `[CompilerGenerated]`) hold captured
  variables as fields, and capture-free lambdas sit on the `<>c` singleton. Today's `Closures.cs` kept captured
  variables as locals. From IL, a display object that only its method and its lambdas see is recognised, and its
  fields are treated as locals.
- **`using`**: `try`/`finally` with a null check and `Dispose` (constrained for structs).
- **`lock`**: `Monitor.Enter(obj, ref taken)` / `try` / `finally` / `Monitor.Exit`. Sharing and blocking reports key
  on these calls.
- **`??=`**: `ldfld; dup; brtrue; pop; …; stfld`. The fresh-store and field-borrow rules see it as a load, a branch
  and a store.
- **Static initialisers**: one `.cctor`. "An initialiser nobody reads" becomes a `stsfld` group in it.
- **Getters that return a field**: `ldarg.0; ldfld; ret`. A method that returns `this`: `ldarg.0; ret`.

## 4. The library: today against SPEC section 9

**Today.** Every compile builds the library from source. `Library` makes one Roslyn compilation per library (55
manifests). The inputs:

- the dotnet/runtime v11.0.0 files the manifest lists (`file`; CoreLib: 1,676 files);
- `define` symbols;
- about 25 textual `edit`s of upstream files: Dictionary, HashSet, List, Queue, SharedArrayPool, SpinWait,
  ConditionalWeakTable…;
- `substitute` lines: about 125 method or field bodies replaced, mostly the `IsSupported` of hardware
  intrinsics, plus StackTrace, EventSource, GlobalizationMode;
- `runtime` files: 110 Mono-derived files under `Runtime/`, 25,975 lines;
- `Boundary/` files: 12,898 lines, the runtime and the host interop;
- `Generated/`: SR strings, unavailable platform invokes.

The program's symbols from reference assemblies are mapped onto these by documentation id. The self-hosted
compiler spends about 9 s of a build on this ("library ready", FINISH).

**SPEC 9 wants upstream's library, as NativeAOT builds it.** That is CoreLib and the libraries above it compiled
to IL for il2wasm's runtime, with only the runtime boundary il2wasm's own. What that needs:

1. **CoreLib as an IL assembly built ahead of time.** The same manifest (files, defines, edits, the `runtime` and
   `Boundary` halves, `Generated`) becomes a project that Roslyn compiles to `System.Private.CoreLib.dll` and its
   PDB, once per il2wasm version. It is shipped with the compiler or built on first use and cached.
   - Compiling each library is done today, and only emission changes. Unknown until tried: errors that binding
     tolerates today (`Lib.Errors`) but that emit would refuse.
   - Intrinsics stay methods with `[Il2Wasm.Runtime.Intrinsic]`, `[Import]` or upstream `[Intrinsic]`, and are
     recognised by identity in IL.
   - `substitute` lines become IL-level body substitutions, the same idea as ILLink's substitution files.
   - Textual edits stay source edits before the IL build.
2. **The libraries above CoreLib.** These are the same IL on every runtime, so the stock IL can be used. One
   option is the `browser-wasm` runtime pack's implementation assemblies, as NativeAOT-LLVM does. The other is
   building them from the manifests as for CoreLib.
   - Their `Boundary` replacements (System.Console, System.Net.Http, System.IO.Compression) then become P/Invoke
     targets. The browser builds call `libSystem.Native` and friends, so il2wasm provides those exports as its
     runtime's functions over host imports. This is SPEC 8's native-library mechanism applied to its own shims.
   - SR strings come from the assemblies' resources, unless the `UseSystemResourceKeys` switch is used.
   - Decision D2: stock pack or own build per library.
3. **Facades and resolution.** A program's IL references `System.Runtime`, `netstandard` or `mscorlib`. Resolution
   follows their type forwarders to the implementation assemblies, as the runtime does. The doc-id mapping in
   `Library` goes.
4. **Packages are IL too.** `Upstream/GodotSharp` (from source today) becomes the 2dog package's assemblies.
   `Upstream/Microsoft.CodeAnalysis*` (Roslyn from source, with generated resources, for self-hosting) becomes
   Roslyn's NuGet IL. The self-hosting proof (SPEC 13, proof 3) then compiles the toolchain's shipped IL.
5. **Size and speed risk.** Today's library is source that the compiler binds lazily, and `Library.NeedsAll`
   restarts the compile with every library when a skipped one turns out to be needed. IL is read lazily by
   metadata tokens, so only reached methods are imported. The hello-world bar (under 28,383 B; 27,784 B today)
   depends on what upstream's IL reaches and on the borrow and size passes being back (section 5, steps 5 and 6).

## 5. Proposed order of work

Two decisions come first. They are for the owner.

- **D1: the type model.**
  - (a) A compiler-owned type system over `System.Reflection.Metadata`. dotnet/runtime's
    `src/coreclr/tools/Common/TypeSystem` (NativeAOT's, MIT) is the refined design, and per its licence it could
    be reused, not only read. It covers ECMA layout, virtual and interface resolution, generic instantiation,
    casting and signatures.
  - (b) As a bridge: Roslyn's own metadata symbols (a `MetadataReference` gives `IMethodSymbol`s for every PE
    member), with a map from token to symbol built by walking each module (`ISymbol.MetadataToken`). Most of the
    ~2,000 symbol uses in `RuntimeGen`, `Types`, `Arenas`, `Reflection` and `Driver` would then run unchanged
    while the importer is built. Unverified: pseudo-custom attributes (StructLayout, FieldOffset) are not
    returned by `GetAttributes`, so layout would still need the metadata reader. It also keeps Roslyn inside the
    compiler, which SPEC 2 ("the IL front end is the only part that knows IL") does not forbid but does not
    intend.
  - Recommendation: (a), with (b) only if the first slice must come quickly.
- **D2: the libraries above CoreLib**: the stock runtime pack's IL, or own IL builds from the manifests.

Steps, smallest end-to-end slice first. Sizes are judgments from the code, not measurements.

1. **CoreLib (and the libraries hello world reaches) as IL.** `tools/CoreLibManifest` and `UpstreamSources` drive
   a Roslyn emit per library into a cache of DLLs and PDBs, with the substitutions applied at IL level or as
   source as today. Check: the assemblies load in `System.Reflection.Metadata` and every reached method has IL.
   *Small to medium: 2 to 4 days*, most of it the emit errors binding tolerates today.
2. **Type model (D1)** with layout, virtual resolution, generic instantiation, doc ids, display names, and assembly
   resolution through forwarders. *Medium: 1 to 2 weeks* own-built over SRM, *days* if NativeAOT's type system is
   adopted.
3. **IL importer to SSA IR, and IR to `Instr`, with naive counting.** This is the first slice of hello world's IL
   through the existing back end.
   - The IR is SSA: values, blocks, exception regions, ownership operations.
   - The importer covers stack to SSA, all ECMA-335 opcodes and prefixes (`constrained.`, `readonly.`,
     `volatile.`, `unaligned.`, `tail.`), and exception clauses.
   - Structuring for WASM: reducible CFGs by dominance (the "beyond relooper" construction); irreducible ones with
     a dispatch loop, as `StateMachine.cs` builds today.
   - Lowering reuses `Emitter`. Every reference value is counted, with no borrows.
   - Driver, RuntimeGen, Types, Frames and Reflection move to the type model. The thread-mode restart and the
     dispatch stubs carry over.
   - Line table from portable PDB sequence points.
   - The CLI and `Il2WasmCompiler.Compile` take assemblies (or a project: `dotnet build`, then the output). The
     test runner compiles each conformance program with Roslyn to a DLL and PDB first.
   - Check: `Console.WriteLine(42)` prints 42. The size bar is not yet met at this step.

   *Large: 3 to 5 weeks.* It replaces ~8,500 lines of `FunctionCompiler`/`Expressions`/`Calls` with an importer
   (NativeAOT's is of the order of 3,000 lines), plus the porting of the mixed files.
4. **The conformance suite to 264 of 264 with naive counting**, family by family:
   - exceptions and filters, generics and shared bodies, statics and `beforefieldinit`, unsafe and spans;
   - delegates and function pointers, reflection, async on upstream's builders, threads;
   - weak references and finalizers through the existing runtime.

   Cycles work from the start, because the arena sites come from IR stores and need no borrow rules. *Large: 2 to
   4 weeks*, depending on how much upstream's async and reflection IL costs.
5. **Ownership back, as IR passes, each with its switch and test**:
   - moves and local borrows;
   - field borrows (decision half reused);
   - owned parameters, struct moves and struct arguments;
   - out borrows, escape analysis, then fresh, chain and owned-array stores, and storage keeps.

   Each step is measured: count operations removed (`IL2WASM_COUNT_RC`), `tools/frames.mjs`, `tools/bench.mjs`, and
   hello world's size. *Large: 3 to 5 weeks.* The rules are known, but each has to be re-derived on SSA and on
   the IL shapes of section 3.
6. **Threads and Sharing on the IR** (sources per SSA value, typed counts), then the **self-host check**: il2wasm
   compiles its own IL and Roslyn's NuGet IL. *Medium to large: 2 to 3 weeks.*
7. **Plugin interface version 8 over the IR and type model** (positions from the PDB), the solver re-derived, and
   `samples/invisible-api` ported. *Medium: 1 to 2 weeks.*
8. **Packages and P/Invoke (SPEC 8)**:
   - NuGet restore through `dotnet build`'s output;
   - P/Invoke into a native library's WASM, with marshalling generated at compile time;
   - the libraries' own native shims (D2);
   - then NetWasm's test projects as the main suite (SPEC 13), with .NET as the oracle.

   *Large*, and not sized here.
9. **Removal of the operation-tree front end** as each family moves. Per SPEC 12 it is not kept beside the new
   one, and no side-by-side runs of the two are made.

What is reused without change throughout: `Wasm/*` (all passes, the encoder, the sections), the runtime in
`Boundary/*`, the host and snapshot, the decision halves of the field borrows, arenas, typed counts and field and
static loads, `SharingFlow`, the tests, stress, frames and bench tools.

# il2wasm's memory model on the IR: one design

Status: **design, draft of 2026-11-23**. No compiler code is written for it yet. It covers ownership, cycles, threads
and the optimizations that feed them on the IL front end's SSA IR (`src/Il2Wasm/IL`), as one whole-program
analysis. It carries cs2wasm's memory model over first-class: the rules and runtime contracts of the old
operation-tree front end (`src/Il2Wasm/Compiler`) are restated here on the IR. They are not re-derived, and they are
not grafted on later.

Binding inputs:

- docs/SPEC.md sections 4, 5 and 7, with the owner's rulings of 2026-11-23 now in the text:
  - 4.1 "Calls (owner)";
  - 4.3 "The proofs are the core, the fallback is measured (owner)";
  - 4.4 "Finalizers run inline (owner)".
- The owner's standing rules, each recorded in a memory file of the same name:
  - compile-time RC only (`rc-compile-time-only`);
  - no run-time walk (`cycles-no-runtime-walk`);
  - behaviour must not differ (`behavior-must-not-differ`);
  - NaN canonicalisation stays (`nan-canonicalisation-stays`);
  - threads must speed up (`threads-must-speed-up`);
  - compile time matters (`compile-time-matters`).
- docs/research/rc-literature-review.md (cited below as **R**, with its recommendation number: R1 to R11) and its
  notes in docs/research/rc-literature-notes/ (cited by file name).

Evidence labels follow docs/AUDIT.md: **[read]** comes from reading the code, **[run]** was measured, and
**[design]** is proposed here and neither built nor measured. Every number in this document that is a target is
labelled as a target.

---

## 0. The short version

1. **One fact lattice per value and per site, on the existing IR.** The lattice is defined in section 2. The IR
   (`Ir/Ir.cs`) keeps its shape. The facts live in side tables indexed by `Node.Id`, so the importer, SCCP and
   lowering do not change their data structures.
2. **One monotone, saturating, whole-program fixed point.** It runs in a new directory `src/Il2Wasm/IL/Memory/` and
   computes, together:
   - reachability and dispatch;
   - receiver types and constants;
   - per-method summaries (stores, escape, ownership signature);
   - thread escape;
   - the type graph for cycles, with the owner-chain proof obligations.
   It extends `Reach/ProgramFacts.cs` and `Reach/RoundFacts.cs` rather than running beside them. Every fact is a
   **may-set that only grows**, and the must-facts (a borrow holds, a proof holds, a site is thread-local) are read off
   the final solution as emptiness tests. This makes the solution unique and independent of worklist order.
3. **After the fixed point, each method is planned in OSSA form.** Every counted value is either Owned (consumed
   exactly once on every path) or Guaranteed (a borrow whose base outlives it). The plan then goes through inlining,
   **local re-canonicalization after each inlining step**, and an **ownership pairing verifier**, before lowering.
   `Ir/Ownership.cs` (naive counting) becomes the planner's fallback mode and the baseline that removal is measured
   against.
4. **Cycles.**
   - Owner-chain proofs (ancestor back references, exclusive parts, stores down a structure) take types and stores
     out of the group machinery.
   - The run-time group structure that already exists in `Boundary/Runtime.cs` (`ArenaLink`, `ArenaUnlink`,
     `FinPhase` and the rest) is the fallback.
   - Proof coverage and fallback cost are measured before any further rule (SPEC 4.3, ruling 2).
5. **Threads.**
   - A static thread-escape fact per abstract object decides plain or seq_cst atomic counts, plain or atomic
     `Interlocked`/`volatile` accesses, and group locking. The plain `Interlocked` case is how lock elision comes for
     free.
   - No run-time mode bit.
   - A program with no reachable `Rt.SpawnCore` has no atomics at all.
6. **Rulings applied.**
   - Calls lend by default; they hand ownership over when the callee keeps running after its own last use of the
     argument (section 4.3).
   - Finalizers run inline with an emulated finalizer thread identity (section 6.9).

---

## 1. Where it lives

### 1.1 Today [read]

`IlCompiler.Compile` (`Lower/IlCompiler.cs:101-129`) runs rounds. Each round is a fresh `IlCompiler` that reaches the
program from its roots (`Reach`, `IlCompiler.cs:144-175`). Each method is fully compiled when it is dequeued.
`CompileMethodCore` (`IlCompiler.cs:316-355`) does these steps one method at a time, with nothing whole-program
between import and ownership:

1. `Facts.Import` runs the importer, SCCP, and an optional re-import.
2. `Ownership.Run(body)` (`IlCompiler.cs:345`).
3. `FunctionLowering.Run`.

`ProgramFacts.Close` turns what one round saw into a `RoundFacts` object for the next round. That object holds:

- unwritten statics;
- constant arguments;
- return values;
- field value sets;
- slot implementations over constructed types.

Each round's facts only remove code, so the rounds descend from a sound start (`RoundFacts.cs:9-12`). Ownership is
naive:

- every reference is owned;
- every argument is retained on entry (`Ownership.cs:128-130`);
- the only elision is a move at a last use in a consuming position (`Ownership.Consumes`, `Ownership.cs:111`).

### 1.2 Proposed [design]

```
per method (parallel-safe, cached by method):
  Importer.Import -> Sccp.Solve (local) -> Memory/Summary.Build    (transfer functions, no whole-program facts)
whole program (one solver, Memory/Solver.cs):
  reachability + dispatch + receiver types + constants
  + store/mod summaries + escape + ownership signatures + thread escape + type graph and proof obligations
per method again, against the solution:
  Memory/Plan.Build         (OSSA: Owned/Guaranteed, flips, conventions, store kinds, count kinds)
  Ir/Inline + Memory/Canon  (inline a callee, then re-canonicalize the caller locally; repeat to the inline budget)
  Memory/Verify             (pairing verifier; always on in Debug and checked builds)
  Lower/FunctionLowering    (reads Plan instead of Ownership.Info)
whole program, late:
  GenerateLate: destroy functions per type (with group, part and finalizer steps), dispatch stubs, arena support
```

The structural change in `IlCompiler` is that reaching a method no longer lowers it. `CompileMethod` imports the
method and keeps its `Body` in `_bodies`; the solver runs when the queue is empty; then a lowering loop plans and
lowers. The generated functions (`rt.destroy`, `rt.retain_contents`, dispatch stubs, `GenerateLate`,
`IlCompiler.cs:890-990`) get synthesized summaries (section 3.6) so the solver sees what a release can run.

The existing rounds stay as the **outer** loop for one reason: `ImportFacts` (`Ir/ImportFacts.cs`) re-imports bodies
with decided branches removed, and a smaller body gives smaller summaries. The inner solver is what decides memory
facts. Section 3.4 says when the rounds can be dropped.

New files, one owner each (section 11.3 uses this split):

| File | Holds |
|---|---|
| `IL/Memory/Facts.cs` | the lattice types (section 2), dense ids, interned bit sets |
| `IL/Memory/Summary.cs` | per-method local transfer summary built from a `Body` |
| `IL/Memory/Solver.cs` | the whole-program fixed point; owns `RoundFacts`' successor `ProgramSolution` |
| `IL/Memory/Signatures.cs` | ownership signatures, dispatch-group signatures, the call rule, the specialization policy |
| `IL/Memory/Borrows.cs` | speculative borrows, path keys, the two proofs, flip decisions |
| `IL/Memory/Groups.cs` | type graph, SCCs, cycle capability, owner-chain proofs, store kinds |
| `IL/Memory/Threads.cs` | thread roots, escape propagation, count and access kinds, lock elision |
| `IL/Memory/Plan.cs` | per-method OSSA plan; replaces `Ownership.Insert` (keeps `Ownership.Liveness`, `FindBases`) |
| `IL/Memory/Canon.cs` | local re-canonicalization after inlining |
| `IL/Memory/Verify.cs` | the pairing verifier |
| `IL/Ir/Inline.cs` | an IR inliner (none exists in `src/Il2Wasm/IL`; the only one is the WASM back end's `Wasm/Inliner.cs`) |
| `IL/Lower/RcStats.cs` | per-site execution counters (section 9) |

---

## 2. The lattice

All facts are indexed by dense ids:

- `MethodId`: the `IlCompiler._functions` order, made stable by sorting on the method's metadata token and
  instantiation key;
- `NodeId`: `Node.Id` within a body;
- `FieldKey`: the field's typical definition, as `FieldLoads.cs:47` keys by original definition;
- `ElemKey`: the array element type;
- `StaticKey`;
- `TypeId`: the existing `IlCompiler.TypeId`;
- `SiteId`: an abstract object, section 2.4.

Sets are sorted `int[]` that are interned (hash-consed) in `Facts.cs`, so equal sets share storage and compare by
reference.

### 2.1 Per value: ownership (OSSA)

Only values that carry counts take part (`Kinds.Counted`: `VK.Ref`, or a `VK.Struct` with references).

| Fact | Meaning |
|---|---|
| `Trivial` | holds no count: null, a constant, `LdStr` (strings are immortal literals), an object proven Persistent (2.5) |
| `Owned` | holds +1. Consumed exactly once on every path that does not end in a dead end: by a move into a consuming use, or by a `Release` |
| `Guaranteed(base)` | holds no count; valid while `base` is live and the path from `base` is not written (2.2). Its uses must lie inside base's live range |

This is Swift's OSSA (R2; imperative_rc_optimization.md "Swift ARC and Ownership SSA").

- Forwarding operations keep the operand's kind without a count: `CastClass`, `IsInst`, `Unsafe.As` (an intrinsic
  call), and a phi whose inputs are all of one kind.
- Interior-pointer operations need a Guaranteed or Owned base: `LdFldA`, `LdElemA`, `Unbox`, spans made from a
  reference. Their base set is `Ownership.FindBases` (`Ownership.cs:63-95`).

**Dead ends.** A dead-end block needs no releases. The dead ends are:

- `Unreachable`;
- a call to a method whose summary says it never returns or throws to a handler: `Environment.FailFast`, stack
  overflow.

An exception that reaches an export is caught and reported (SPEC 6.2), so a `Throw` is **not** a dead end.

### 2.2 Per value: borrow source path

`Path = (Root, Steps)`. `Root` is one of:

- `Arg(i)`: a parameter, guaranteed by the caller's convention (4.2);
- `Val(n)`: an Owned SSA value of this body;
- `Static(k)`;
- `Var(slot)`: an IL local held in the frame (`Body.Vars`, `InFrame`).

`Steps` is a list of at most **3** entries, each `Field(f)` or `Elem(T)`. That is Lobster's prefix rule (R3;
lobster_and_owner_designs.md "The borrow stack").

- A value that comes out of a longer path, or out of anything that is not a path, is a **generic** value. Lobster's
  generic borrow is unsound (R, "Lobster's correctness argument does not carry over whole"), so here a generic value
  is always `Owned`.
- A path is invalidated by a write to any of its keys: `Field(f)` by a store with key f, `Elem(T)` by a store into
  any `T[]` element, `Static(k)` by a store to k, `Var(slot)` by a `VarSt` to the slot or by a write through an
  escaped `LocA` of it.

### 2.3 Per value and per site: allocation

The lattice comes from Graal's PEA (R8; optimization_interactions.md section 1). Each `NewObj`, `NewArr` and `Box`
site has one of:

| Fact | Meaning | Lowering |
|---|---|---|
| `Virtual` | never escapes and is never observed as an address: scalar-replaced into SSA values | no allocation. A float field keeps the float-store NaN canonicalisation at each virtual store, because bits read back could otherwise differ between x64 and arm64 (`nan-canonicalisation-stays`) |
| `FrameOwned` | escapes no further than its frame (its address may be taken, passed to non-storing callees, or read by a finalizer) | `Rt.InitFrameObject(a, size, tid)` (Runtime.cs:1957) on the shadow stack, immortal header, children released at the frame's end. A finalizable object is allowed (R8 rejects .NET's exclusion): its finalizer runs at the frame's end, inline, by the same rules as 6.9 |
| `Heap` | anything else | `Rt.AllocObject` / `AllocArray`, or `AllocObjectArena` for a cycle-capable type |

The decision comes from the escape summaries in 3.2, as `EscapeAnalysis.ParamEscape` does today
(`Compiler/EscapeAnalysis.cs:31`), but on SSA and after inlining. The old 256-byte limit (`MaxFrameObjectSize`)
stays as a starting cap.

### 2.4 Per abstract object: thread escape

An **abstract object** (`SiteId`) is an allocation site (method, IL offset) cloned by a calling context where the
policy in 6.3 says so. Each has one of:

| Fact | Meaning |
|---|---|
| `Local` | only the thread that made it can reach it |
| `Published` | handed to exactly one other thread through a hand-off that moves the reference: the sender's last use, at a thread start or a synchronized queue. It is never reached by two threads at once (6.4) |
| `Shared` | anything else that another thread can reach |

The order is `Local < Published < Shared`. A **static** has the same fact: `Local` unless code reachable from a
worker reads it (6.2). A **field key** inherits the join of its containers' abstract objects.

### 2.5 Per abstract object and per type: groups and owner chains

- `Persistent`: the object is reachable only through `static readonly` / `initonly` fields after the type
  initializer, and nothing writes its slots after that. It is immortal (count -1; `ArenaPin`, Runtime.cs:4862)
  (R9). It costs no count operations and joins no group.
- Per type: `CycleCapable(scc)`, meaning the type lies on a cycle of the type graph after the proofs have removed
  edges (5.1).
- Per field key, an **edge kind**:
  - `Down`: an owning reference;
  - `Up(k)`: proven to hold the k-th owner up the chain;
  - `Part`: the target is an exclusive part of the container;
  - `Inside`: unproven, but may close a cycle;
  - `Outside`: cannot close a cycle.
- Per value, a **chain position** `Below(v, d)`: the value was read out of v by d owning field reads. This is
  `ChainStores`' fact (`Compiler/ChainStores.cs:143`) on SSA.

### 2.6 Per value: receiver types and constants

- `TypeSet`: the exact types the value can have. It is capped at **16**. Above the cap it saturates to
  `Cone(declared type)`: every constructed subtype. The cap is Native Image's (R8; optimization_interactions.md
  section 3).
- `Val`: SCCP's existing lattice (`Reach/Sccp.cs:12`). Its states are Top, Const, NonNull, Typed and Bottom, and it
  stays as is. `TypeSet` refines `Val.Typed`: a singleton TypeSet with NonNull is `Val.Typed` plus NonNull.

### 2.7 Per site

| Site | Fact |
|---|---|
| a count operation (`Retain`, `Release`, a load's retain, a store's old-value release) | `CountKind`: `Elided`, `Plain`, `Atomic`. Decided from the operand's abstract objects' 2.4 fact |
| `StFld`, `StElem`, `StSFld`, `StInd`, `CpBlk`, struct copies | `StoreKind` (5.3) |
| `Call`, `NewObj`, `CallI` | per argument: `ArgConv` = `Lend`, `HandOver`, `KeepAlive` (4.3); the callee variant chosen (4.4) |
| `LdFld`, `LdElem`, `LdSFld`, `LdInd` of a counted value | `Guaranteed(path)` or `Owned` (a flip, 4.6); `LoadKind`: plain, or `rt.load_retain` for a shared slot (6.5) |
| `Interlocked.*`, `Volatile.*`, a `volatile.`-prefixed access | `AccessKind`: `Plain` or `Atomic` (6.6) |

### 2.8 Per method: the summary

`MethodSummary`, in `Memory/Summary.cs` (local part) and `Memory/Solver.cs` (solved part):

- `Mods`: the field keys, element keys and statics the method may write, transitively. It is a may-set, with a
  `Wild` bit for stores through raw pointers or a `ref` of unknown target. These are Gel's mutation summary (R1) and
  `FieldBorrows.StoreSummary` (`Compiler/FieldBorrows.cs:1023`).
- `ReleaseEffects`: the `Mods` of every destroy and finalizer this method's releases can run (3.6).
- `ParamEscape[i]`: one of `No`, `Returned`, `Stored(keys)`, `Thrown`, `ToThread`. These are
  `EscapeAnalysis.Esc` (`EscapeAnalysis.cs:23`) plus the thread case.
- `Signature`: the ownership signature (section 4).
- `FreshReturn`: the result is an object made in the call and not kept by anything else (`FreshStores.ReturnsFresh`,
  `Compiler/FreshStores.cs:233`).
- `BorrowReturn`: the result is `Guaranteed(Arg(i).Steps)` for the paths it returns. These are getters and
  `return this`, as `Driver.cs:697-720` does today.
- `NeverReturns`, `MayThrow`, `RunsFilters` (section 7).
- `ObservesLifetime`: the method reads a weak handle, a `DependentHandle` or `GCHandle` target, or calls
  `GC.KeepAlive`. These are R's inverted deinit barriers (R, "Swift's lexical-lifetimes episode").

---

## 3. The fixed point

### 3.1 Orientation: everything starts optimistic and only grows

| Component | Bottom (start) | Grows by |
|---|---|---|
| reachable methods | the roots (`Main`, `[UnmanagedCallersOnly]` exports, `Rt.*` methods marked by `ProgramFacts.Close`, `ProgramFacts.cs:134-136`) | call edges from executable blocks |
| executable blocks | the entry block | SCCP's edge worklist (`Sccp.cs:102-141`), with interprocedural `Val` |
| constructed types | none | `NewObj`, `Box`, `NewArr`, `Activator`'s generated switches, `TypeOf` |
| `TypeSet` / `Val` per value, argument, return, field key | Top (empty set) | meets/joins from definitions |
| `Mods`, `ReleaseEffects`, `ParamEscape` | empty | stores, calls, releases |
| path writes per borrow (4.6) | empty | `Mods` of everything in the borrow's interval |
| thread escape per `SiteId` | `Local` | roots (6.2) and stores into escaped objects |
| type-graph edges, violations of each proof | empty | stores |
| ownership demand per parameter (`Signature`) | `Lend` | uses that store the parameter, the call rule, and overrides that are reached |

Every must-fact is the emptiness of a may-set read at the end:

- a borrow holds if its path-write set is empty and its base's abstract objects are `Local` (or the key is
  init-only);
- a proof holds if its violation set is empty;
- a type is acyclic if its SCC is trivial.

Because every component only grows, and each transfer function is monotone in all its inputs, the iteration reaches
the **least** fixed point. That point is unique, so the result does not depend on worklist order (R8; Lerner,
Grove, Chambers, optimization_interactions.md section 3).

Two places look non-monotone and are handled as follows:

- **Dispatch-group signatures and reachable overrides.** An override that becomes reachable can only add demand.
  Nothing ever becomes unreachable inside one solve, because reachability only grows. The relaxation that R asks for
  ("an override that becomes unreachable can relax the group") therefore happens between outer rounds, never inside
  one solve.
- **Saturation.** A `TypeSet` over the cap becomes `Cone(declared)`, a function of the declared type alone. Since
  sets only grow, a set that ever exceeds the cap also exceeds it in the final solution. Saturating on arrival
  therefore gives the same result as saturating on final size, and arrival order does not matter (R8: "decided on
  final set sizes rather than arrival order").

### 3.2 Transfer functions (per `Opc`)

Summaries are built per body once; the solver only re-evaluates the parts that read changed facts.

| IR operation | Effect on facts |
|---|---|
| `Call` Direct | add edge; `Arg` facts of callee meet the actual `Val`/`TypeSet`; caller's `Mods` ⊇ callee's `Mods` ∪ callee's `ReleaseEffects`; per-arg escape from callee's `ParamEscape`; result facts from callee's `Returns`, `FreshReturn`, `BorrowReturn` |
| `Call` Virtual/Interface | targets = `Implementation(t, slot)` for t ∈ receiver `TypeSet` ∩ constructed (the old round's `Previous.Implementations` becomes this, per site); each target as Direct; the **dispatch group** of the site is that target set (4.5) |
| `CallI`, delegate `Invoke` | targets = methods whose address flows to the function pointer / the delegate's abstract object (delegates' abstract objects carry the `LdFtn`/`LdVirtFtn` target set); unknown sources fall back to `ProgramFacts._indirect` methods of a matching signature |
| `NewObj`, `NewArr`, `Box` | a `SiteId` (6.3 context); constructed type; the value's `TypeSet` = {type} |
| `StFld obj.f = v` | `Mods` += key f; field key f's `TypeSet`/`Val` ⊇ v's; type-graph edge (type(obj) -f-> TypeSet(v)); escape: if obj's sites are not `Local`, v's sites become at least obj's state; proof obligations of 5.2 recorded |
| `StElem`, `StInd`, `CpBlk`, `InitBlk`, struct copy to an address | as `StFld` with `Elem(T)` / by-address keys (RefStores' rules, `Compiler/RefStores.cs:40-142`: a `ref T` is any slot of type T, raw pointers are `Wild`) |
| `StSFld` | `Mods` += static key; if the static is worker-read, v's sites become `Shared` |
| `LdFld`, `LdElem`, `LdSFld`, `LdInd` | value facts from the key; chain position `Below(obj, d+1)` when obj is `Below(_, d)` and the key is `Down` |
| `LdFldA`, `LdElemA`, `LocA`, `LdSFldA` | the key becomes address-taken: `FieldLoads.NoteFieldEscape` (`Compiler/FieldLoads.cs:94`) rule, class-wide; `Mods` of any later `StInd` through it |
| `Ret` | method's `Returns` ⊇ value; `ParamEscape[i]` ⊇ `Returned` when value derives from `Arg(i)` |
| `Throw` | value escapes (`Thrown`): its sites are reachable from any catch; treated as stored into a global exception slot |
| `Release` (planned or implied by store) | `ReleaseEffects` ⊇ `DestroyEffects(TypeSet(v))` (3.6) |
| `Phi` | join of inputs |

### 3.3 Order and termination

- **Order.**
  1. The call graph found so far is split into SCCs (Tarjan), processed callees-first.
  2. Within an SCC, a worklist ordered by `MethodId` is iterated to its fixed point.
  3. A changed fact enqueues its dependents. The dependency lists are kept per fact, so only real readers are
     re-run:
     - callee to callers;
     - field key to readers;
     - `SiteId` to its uses;
     - slot to call sites.
  4. When new methods become reachable, the SCC split is recomputed. This is Lean's per-SCC borrow inference (R,
     "Lean's borrow inference is a monotone fixed point per strongly connected component"), widened to all
     components.
- **Termination.** Every component is a finite lattice, so the solve terminates:
  - reachable methods: at most the methods of the loaded assemblies' instantiations reached;
  - `TypeSet`: capped at 16, then one step to Cone;
  - `Val`: height 4;
  - `Mods`: bounded by the number of keys;
  - escape: height 3;
  - signature demand per parameter: height 3;
  - type-graph edges: bounded by the type pairs.
- **Generic instantiation explosion** is bounded as today. Instantiations are reached only from executable code, and
  the type system's recursion guards apply.

### 3.4 One fixed point, and what happens to the rounds

The solver subsumes what today's rounds compute:

| Today's round fact | In the solver |
|---|---|
| `WrittenStatics` | `Mods` over reachable code |
| `Args` | interprocedural `Val` |
| `Returns` | `Returns` |
| `Fields` | per field key |
| `Implementations` | receiver `TypeSet`s |
| `ConstructedClasses` | constructed types |
| `Aliased` | `Unsafe.As` types |

Because the solver starts optimistic and SCCP only marks executable what it proves reachable, a round's re-import
adds nothing semantically. It only shrinks bodies.

- **Phase 1 (slice S1).** The rounds stay. The solver runs inside each round. Its solution feeds the next round's
  `ProgramFacts` (the same `ISccpOracle` interface, `ProgramFacts.cs:16`), and only the last round plans and lowers.
  This keeps every existing switch (`IL2WASM_NO_SCCP`, `NO_CONSTANT_ARGS` and the rest) meaningful.
- **Phase 2.** Once the solver's output shows the same reach as the rounds on the NetWasm suite case by case, the
  rounds are cut to one import plus one solve. A re-import happens only for methods whose decided branches changed.
  This is the compile-time win.

### 3.5 Determinism

- Iteration is over sorted ids. No `Dictionary`/`HashSet` enumeration order reaches output; facts are arrays indexed
  by dense ids.
- `MethodId`, `SiteId` and `FieldKey` are assigned from metadata order (module MVID order, then token), never from
  arrival order.
- The solution is the unique least fixed point (3.1), so a parallel build of summaries (per method, no shared mutable
  state) gives the same input to the solver.
- Emission order stays as `IlCompiler` sorts it today. Variant functions are named by stable keys
  (`|own<mask>`, `|lend`, `|retfield`), as `OwnedParams.OwnedVariant` does (`Compiler/OwnedParams.cs:254`).
- Checked by `tools/selfdet.mjs`: byte-identical modules from the .NET-hosted and self-hosted compiler, on 1 and 4
  cores.

### 3.6 Release effects and synthesized summaries

A release can run code. It can destroy a whole owned subgraph, run finalizers (inline, 6.9), and clear weak handles.
So a borrow is only safe across a release if that release's possible effects do not write the borrowed path. This
applies to every release in the interval, including the release of an unrelated value.

`DestroyEffects(T)` is computed in the fixed point:

```
DestroyEffects(T) = Mods(T's finalizer chain, if T is finalizable and reached)
                  ∪ ⋃ DestroyEffects(U) for U ∈ TypeSet(f), f a counted field or element key of T
                  ∪ {weak-handle tables} if ObjectMarks.SetAt is reached (Lower/Finalizers.cs:34)
```

It is another may-set that grows with the types reached, so it
lives in the same solve.

- A type with no finalizer anywhere below it has `DestroyEffects = ∅` beyond its own fields being freed. Freeing
  never writes a live object's field, so a borrow from a live path cannot be invalidated by it.
- A type whose subgraph can reach a finalizer whose `Mods` is `Wild` makes every release of it a barrier for
  speculative borrows (4.6).

The generated runtime functions get fixed summaries, because their bodies are emitted late:

| Function | Summary |
|---|---|
| `rt.destroy` | `DestroyEffects` of the type |
| dispatch stubs | the join over their implementations |
| `rt.retain_contents` | no writes |
| `Rt.ArenaLink` / `Rt.ArenaUnlink` | writes only runtime-private words, which are not program keys (as `StoreSummary.Internal` excludes them today, `FieldBorrows.cs:1026-1031`), plus `DestroyEffects` of what they can free |

### 3.7 Inlining and devirtualization

- **Devirtualization is inside the fixed point.** A call site whose receiver `TypeSet` has exactly one
  implementation becomes a `Direct` call when the method is planned. With two or three implementations it becomes a
  type test and direct calls (AUDIT A2.3).
  - Dispatch stubs are built only for sites that still need them. Today `GenerateLate` builds a stub per slot over
    all constructed types (`IlCompiler.cs:918-934`), with no single-implementation shortcut.
  - The site's group signature (4.5) is over its own target set, not the slot's.
- **Inlining is after the fixed point**, in `Ir/Inline.cs`, on planned (OSSA) bodies. The policy is
  AggressiveInlining (`f.InlineHint`, `IlCompiler.cs:333`) plus a size budget (starting at 24 IR nodes, the old
  front end's figure: FINISH.md, stream K).
  - The callee's argument conventions become explicit at the splice: a `Lend` argument becomes a Guaranteed alias of
    the caller's value, and a `HandOver` argument becomes a move.
  - The callee's owned return becomes the caller's owned value.
- **Local re-canonicalization after each step** (`Memory/Canon.cs`), the Swift ARC-cleanup pattern (R8;
  optimization_interactions.md section 2):
  - cancel a `Retain` followed by a `Release` of the same value with no observer between them;
  - turn an owned value consumed only by uses in a borrow scope into a Guaranteed one;
  - turn a callee's guaranteed-parameter uses into borrows of the caller's base;
  - re-run the allocation decision of 2.3 on objects the inlined callee made: an object returned fresh and not
    escaping in the caller becomes `FrameOwned` or `Virtual`. That is where .NET 10's 3x to 11x delegate and
    enumerator wins come from (R8).

  Canon uses the global solution (`Mods`, escape) unchanged. This is sound: inlining adds no effects, it only makes
  them local.

  Canon never moves a release later. It only removes pairs or moves a release earlier across operations that cannot
  observe the free. An observer is:
  - a call whose summary has `ObservesLifetime`;
  - a weak or handle read;
  - a pointer use of the object;
  - a call that may run a finalizer that does any of these.

  This is R's inverted Swift barrier list, and the reason Joisha's later decrements are not adopted (R,
  "il2wasm forbids that trade").
- The WASM back end's `Wasm/Inliner.cs` keeps running after lowering. By then counts are already final. It no longer
  needs name tests such as `"rt.release"` (`Inliner.cs:158-161`) to find count calls, because lowering tags them.

### 3.8 The compile-time budget

**Baseline.** These figures are on record and are not to be re-measured (`build-cost-baseline`):

| Build | Time | Memory |
|---|---|---|
| old cs2wasm self-host | 18–20 min | ~14 GB |
| NativeAOT-LLVM | 80 s | 8.7 GB |
| IL front end, hello world on the IL CoreLib, with rounds (AUDIT, il-generics) [run] | 14.4–15.0 s | — |
| IL front end, the same, without rounds [run] | 12.8–13.8 s | — |

**Targets, unmeasured.** Both are measured on hello world and on the self-hosted compiler once the IL front end
compiles it:

- the memory analysis (summaries, solve, plan, canon, verify) takes at most 20% of a build's time and adds at most
  25% to its peak memory;
- the self-hosted toolchain's whole build stays under the old 18–20 minutes. The aim is NativeAOT-LLVM's 80 s order.

**Complexity.**

- A summary is built in one pass over a body.
- The solve costs O(edges × lattice height). The heights are bounded by 3.3. Per-parameter signature demand has
  height 3, which gives Lean's "at most as many rounds as parameters".
- No per-path or shape-graph state. ASAP's exponential analyses are the cautionary bound (R8).

**Memory.**

- All bodies are now held until lowering. Today a body dies after its method is lowered. That is the main memory
  cost and is measured first.
- If it is too much, `Body` can be re-imported at plan time from the IL (import is deterministic) and only the
  `Summary` is kept. Summaries are small: arrays of ids.

**Caching.** A method's local `Summary` depends only on its IL and instantiation. It can be cached per assembly MVID
and compiler flags, as AUDIT step 12.4 plans for the library. The solution cannot be cached, because it depends on
the program.

**Switches** (SPEC 5: every optimization has a switch and a test):

| Switch | Turns off |
|---|---|
| `IL2WASM_MEM_NAIVE=1` | everything: today's `Ownership.Run`, the baseline the removal share is measured against |
| `IL2WASM_NO_SPEC_BORROWS` | speculative borrows |
| `IL2WASM_NO_HANDOVER` | the call rule's hand-over |
| `IL2WASM_NO_OWNED_ENTRY` | owned-entry variants |
| `IL2WASM_NO_BORROW_RETURN` | borrowed returns |
| `IL2WASM_NO_VIRTUAL` | `Virtual` allocation |
| `IL2WASM_NO_FRAME_OBJECTS` | `FrameOwned` allocation |
| `IL2WASM_NO_CHAIN_PROOFS` | owner-chain proofs |
| `IL2WASM_NO_UP_PROOF` | the up-reference proof |
| `IL2WASM_NO_PART_PROOF` | the exclusive-part proof |
| `IL2WASM_NO_PERSISTENT` | Persistent objects |
| `IL2WASM_NO_THREAD_ESCAPE` | thread escape: every count atomic in a threaded build |
| `IL2WASM_NO_LOCK_ELISION` | lock elision |
| `IL2WASM_NO_DEVIRT` | devirtualization |
| `IL2WASM_NO_IR_INLINE` | the IR inliner |

Each has a `*_BREAK` twin that deliberately breaks the rule, so the rule's test is shown to fail. The old front end
did the same, for example `IL2WASM_OWNED_INSERT_BREAK` and `IL2WASM_OUT_BORROW_BREAK`.

---

## 4. Ownership signatures and the call rule

### 4.1 What a signature says

For a method m, the signature holds:

- `Param[i]`: one of the values below;
- `Return`: `Owned`, or `BorrowFrom(i, steps)` in a borrowed-return variant (4.4);
- `This`: the same as `Param[0]`.

| `Param[i]` | Meaning |
|---|---|
| `Lend` | m only borrows i. The caller keeps its count and releases after the call if the call was its last use |
| `HandOver` | m wants i owned. Either m stores it (a consuming parameter, OwnedParams' rule), or m keeps running after its own last use of i (the call rule, 4.3) |

A signature is computed from IL for **every** method reached:

- program code;
- stock libraries;
- CoreLib, including `Il2Wasm.Runtime` code.

No hand lists. The old `IsListAdd`, `IsMapInsert` and `ReturnsAfter` special cases (`Compiler/OwnedParams.cs:110-139`)
become ordinary results of the rule. That is R1: the 53–57% plateau (AUDIT A2.4) is most likely the missing library
signatures.

The only hand-written signatures are the **intrinsics**, whose bodies the compiler emits: the 220 listed in
docs/CORELIB-INTRINSICS.md. They go in one table, `Memory/IntrinsicSignatures.cs`, each entry beside the intrinsic's
lowering case in `IlCompiler.cs`. Two more fixed signatures:

- imports and P/Invokes lend all their arguments, because marshalling copies them;
- `[UnmanagedCallersOnly]` exports take no references.

### 4.2 Defaults

- **+0 by default**, Swift's guaranteed convention (R2). Inside the callee a lent parameter is
  `Guaranteed(Arg(i))`: no retain on entry and no release at exit. Today `Ownership.cs:128-130` inserts a `Retain`
  after every `Arg`. That goes.
- **Returns are owned** (SPEC 4.1), with the borrowed-return specialization of 4.4.

### 4.3 The call rule (SPEC 4.1 "Calls (owner)", ruling 1)

At a call site, for each counted argument value v passed to a parameter i of the chosen target or group:

1. **v is not at its last use** (v is live after the call): `Lend`. The caller keeps v. If the parameter is
   `HandOver`, the caller emits one `Retain` before the call (`KeepAlive`; 4.6).
2. **v is at its last use, and the parameter is `Lend`**: the caller releases v right after the call returns. The
   call is the statement that makes it unreachable.
3. **v is at its last use, and the parameter is `HandOver`**: the count moves into the call (`Node.Moves` bit).
   Nothing is released after the call. The callee releases v at **its** last use, or stores it.

**`KeepsRunningAfter(m, i)`** decides whether a parameter that m does not store is `HandOver`. It is true when, in
m's CFG, some path from a last use of parameter i to an exit passes through either:

- a loop header, meaning a block that is the target of a back edge in m's dominator tree; or
- a call that is not inlined at that point and is not an intrinsic marked cheap in `IntrinsicSignatures`.

Exceptional exits count, because a long-running `try` body is still running. This is exactly "a loop or further
calls follow that use". It covers the motivating case: `Run(world)` stops using `x` early and then loops for the
whole game.

It is computed on m's own body plus the solution (to know which calls are inlined). It is monotone: more reached
code can only add calls. Open question Q8 asks for confirmation of the "not inlined" reading.

**This rule does not need two proofs.** A `HandOver` parameter is owned by the callee, so no write during the call
can free it.

### 4.4 Specialization policy and the size cap

`HandOver` has a cost at non-last-use sites: the caller pays a retain (case 1). To avoid that, a method with a
`HandOver` parameter i whose reason is only `KeepsRunningAfter`, not a store, gets **two entries**:

| Entry | Used at | Behaviour |
|---|---|---|
| the **lend entry** (the method's own function) | non-last-use sites | i is `Guaranteed(Arg(i))`; no count |
| the **owned entry** `|own<mask>` | last-use sites | i is `Owned`; released at the callee's last use |

This is SPEC 4.1's "specialized for the ownership its callers need" and OwnedParams' variant
(`OwnedParams.cs:254`). Variants are created by `Memory/Signatures.cs` after the solve. They do not change any
may-fact: the code is the same, and only count placement differs. So no re-solve is needed.

The cap:

- A method of at most **25 IR nodes** gets the owned entry for every mask its last-use sites need. That is
  Lobster's 25-node cap (R4; lobster_and_owner_designs.md "Function specialization by ownership").
- A larger method gets at most **2** entries: the lend entry and the all-`HandOver` entry.
- A method above **400 IR nodes**, or reached through indirect calls (`ProgramFacts._indirect`), or a member of a
  dispatch group (4.5), gets **one** entry with the owned convention for its `HandOver` parameters. Callers retain at
  non-last-use sites (Lobster's `KeepArgAlive` "cheaper than making the parameter own, which every call would pay
  for" turned round: here it is the price of not cloning).
- The numbers are starting points (targets), tuned by the size goal (`size-goal-below-netwasm`) and the executed
  counts (section 9).

**Constant-argument specialization** (`Compiler/Specialization.cs`) stays separate. SCCP's interprocedural `Val`
covers it. An owned-entry variant and a constant variant are combined only under the 25-node cap.

**Borrowed-return variant.** For methods whose summary is `BorrowReturn` (getters: `ldarg.0; ldfld; ret`; `return
this`; indexers that return an element of a field array), a `|retborrow` variant returns without a retain. A
call-site caller treats the result as `Guaranteed(path of the argument + steps)`, subject to 4.6's proofs, and flips
to owned (one `Retain`) where a proof fails. The method's own function, used by indirect callers, retains. That is
`Driver.cs:697-720` today. Inlining usually makes the variant unnecessary. See Q4 on SPEC 4.1's "return values are
owned".

### 4.5 Dispatch-group signatures

A virtual or interface call site's group is its target set from 3.2 (receiver `TypeSet`s), not every override of the
slot. The group signature is the **join** over the targets, per parameter (`HandOver` > `Lend`).

Every target in a group that is `HandOver` for i, but whose own signature is `Lend` for i, takes the argument owned
and releases it right after its own last use. This is the group's shared entry. It matches SPEC 4.1: virtual-dispatch
groups get "one shared, borrow-first specialization". The group is borrow-first; a `HandOver` comes in only when some
reachable override needs it.

A method in several groups with different joins gets one shared entry per distinct join (at most 2, the cap above);
beyond that, the most-owned. Delegates and `CallI` use the join over their target sets in the same way.

### 4.6 Speculative borrows with flip-on-write (R3)

Every counted load (`LdFld`, `LdElem`, `LdSFld`, `VarLd` of an `InFrame` slot, `LdInd` through a path-known
address) and every `BorrowReturn` call result starts as `Guaranteed(path)`. Its **interval** is from its definition
to its last use, by `Ownership.Liveness` (`Ownership.cs:182`), which includes exceptional successors.

It stays Guaranteed when **both proofs** hold. Otherwise it **flips** to `Owned`, with one `Retain` at the definition,
Lobster's `FlipSpeculative`. SPEC 4.1 "Retroactive owners": behaviour never changes, only cost.

**Proof 1: no write to the path during the interval.** For each key k of the path, and for the root:

- no `StFld`/`StElem`/`StSFld`/`VarSt`/`StInd` with key k in the interval, on a base that may alias. Aliasing is
  type-based by key, as Gel and `FieldBorrows` do. A store whose base is provably the borrow's own base value (the
  same SSA value, or `Below` it) and stores the borrowed value itself is a **self-store** and does not break the
  borrow (`FieldBorrows.SelfStoreMark`, `FieldBorrows.cs:241`);
- no call in the interval whose `Mods ∪ ReleaseEffects` contains k or is `Wild`;
- no release in the interval whose `DestroyEffects` contains k (3.6);
- no throw point in the interval whose handler path can run a filter that writes k (7.2);
- the root stays alive:
  - `Arg(i)`: the caller's convention guarantees it;
  - `Val(n)`: n is Owned and live over the interval, which extends n's liveness and so moves its release later
    within the statement range. This is allowed only if n's release does not move across an observer (3.7);
    otherwise flip;
  - `Static(k)`: the static is not written in the interval;
  - `Var`: the slot is not written and its address has not escaped.

**Proof 2: no other thread can write the path.** One of:

- every abstract object of the base is `Local` or `Published` and on the receiving side of its hand-off (6.4);
- the key is init-only. `InitOnlyFields.InitOnlyKey` (`Compiler/InitOnlyFields.cs:37`) rule, from the IL `initonly`
  flag plus "only the constructors of its own type write it, once, on `this`";
- the static is `Local` (not read by worker-reachable code).

There is never a run-time shared test (`rc-compile-time-only`).

**Where the old front end put its borrow rules:**

| Old rule | On SSA |
|---|---|
| `FieldBorrows` (a never-reassigned local) | every SSA value is never reassigned, so all of it is this rule |
| `LocalBorrows` (`for (n = head; n != null; n = n.Next)`) | a phi whose inputs are Guaranteed values with paths of a common shape: `Val(phi).Next` or `Arg`. The phi is `Guaranteed` with a loop-invariant key set {Next} |
| `OutBorrows` (`TryGetValue(k, out v)`) | a value read back by `VarLd` from the `LocA` slot that the call wrote, after a callee whose summary shows it stores into the out parameter only values read from `this`'s paths: `BorrowReturn` through an out parameter. A Guaranteed path rooted at the receiver, with proof 1 over the interval |
| `BorrowedStructArgs` | a struct argument is already passed as the address of the caller's copy (`Importer.cs:481`). The copy is skipped when the callee never writes the parameter's address (summary) and the caller's source is not written during the call (proof 1 on the source Var or field) |
| `StorageKeeps` | 7.3 and 7.4 |

**KeepAlive at calls.** An argument v that is `Guaranteed(path)` at the call, where the callee's `Mods ∪
ReleaseEffects` hit the path: the caller retains v before the call and releases it after. That is Lobster's
`KeepArgAlive`, and the fix for AUDIT A1.1, which cannot recur on SSA values but can on `Var` slots.

### 4.7 Moves, last uses and structs

- An Owned value's last use in a consuming position moves its count, as today (`Ownership.Consumes`). Phi copies
  move when the value is dead on the successor (`Info.PhiMoves`).
- Structs with references are aggregates. A struct value is Owned or Guaranteed as a whole. Moves of struct
  temporaries are `StructMoves`' rule (`Compiler/StructMoves.cs:41-55`): a producer immediately consumed moves the
  bytes and clears the source (`rt.move_init`, `rt.move_assign`). In SSA the "immediately" becomes "the temporary's
  only use".
- The old front end's syntactic exclusions disappear. Its "no move under `try`" rule (`LocalMoves.cs:50-52`) is
  handled by liveness over exceptional edges: a value live into a handler is not at its last use. Its goto and label
  bail-outs are moot, because the CFG is explicit.

---

## 5. Cycle groups

### 5.1 The type graph and cycle capability

The nodes are types. Each counted slot (field key, element key, display field, delegate target, state-machine field,
struct-in-slot) is a hub node, as `Arenas.Site` is today (`Compiler/Arenas.cs:24`). The edges come from container
type, through the hub, to the **`TypeSet` of the values stored there**. That is the solved per-key type set, not the
declared type. SCCs come from Tarjan (`Arenas.Components`, `Arenas.cs:730`).

A component is **cycle-capable** when both hold (`Arenas.cs:768`, carried over):

- it has at least two nodes, or a self-loop;
- at least one of its hubs is neither `Immutable` nor removed by a proof.

Edges removed before the SCC, each with its old rule:

| Rule | Old source | On the IR |
|---|---|---|
| Construction-only edges: the field is written only in its class's constructors, on `this`, before `this` escapes | `Arenas.ImmutableEdge` / `KeepsThis` (`Arenas.cs:201, 296`); A1.5's fix: an address-escaping field is never immutable | escape of `this` in `.ctor` bodies from 3.2 |
| Owned arrays: an array field whose arrays are filled while only their maker knows them, then never written | `OwnedArrays.Seal` (`Compiler/OwnedArrays.cs:168`), a greatest fixpoint over carriers | here it is the emptiness of a may-set of "element writes after hand-off" per field key. It is monotone, so it joins the one solve, not a fixpoint of its own |
| `Persistent` objects | none | 2.5 |
| `Up(k)` edges | 5.2 | proven |
| Edges whose sources are only `Outside` stores | `FreshStores` / `IsolatedValue` (`FreshStores.cs:279`) | 5.3 |

What made the self-hosted compiler one group of 19,847 types was `object`-typed hub slots (FINISH.md, "Where the
self-hosted compiler's gap is"). The measured bound says value-source narrowing alone cannot split it: 20,171 of
20,340 even when optimistic (`sources-lever-bound`). Two parts of this design address it:

- the per-key `TypeSet` with the abstract-object context of 6.3. This is context-sensitive where SharingFlow was
  context-insensitive, which is the measured reason it failed;
- the owner-chain proofs on Roslyn's precisely typed edges: parent pointers, and green nodes' child arrays (already
  removed by owned arrays).

Whether that splits the group is **unmeasured**. Slice S2's first measurement is the group sizes on the self-hosted
compiler with each rule on and off.

### 5.2 The owner-chain proofs (SPEC 4.3; R5)

Each proof is a set of obligations recorded during the solve. The proof holds if the obligations' violation set is
empty. No proof needs anything beyond type-based keys, SSA def chains and the escape fact.

**(a) Exclusive part.** A field key `D` (Down) of container type C holding type P is an **exclusive part** edge when
every store `c.D = p` satisfies one of:

- p is `Fresh` here: allocated in this method, or a `FreshReturn` result, with no store of it to any `Down` slot
  before this one on any path. Dominance over the SSA def-use chain;
- p was just **detached**: on every path to this store, the store `c'.D' = null` (or the overwrite) that removed p
  from its previous owning slot dominates, and there is no other owning store between them;
- null.

And no `Down` slot of another key holds P (or each such key is itself proven a part edge of the same chain). The
result is that every P object is held by at most one owning slot at a time.

**(b) Ancestor back reference (Up).** A field key `U` of type P holding type C (or an ancestor type k steps up) is
`Up(k)` when all three conditions of the notes hold (cycles_regions_finalizers.md, "proving back references go to an
owner"):

1. **Store site.** Every store `p.U = q` stores, or null:
   - `q` = the container whose part slot receives p in the same method, at a store that is paired with this one.
     The patterns:
     - `c.D = p; p.U = c`;
     - `p.U = c; c.D = p`;
     - `c.list.Add(p)` with `p.U = c`, through an exclusive-part list whose element slot is itself a part;
     - a constructor `new P(c)` storing `this.U = c`, whose every caller passes the object it then stores p into.
       This is an obligation on call sites, checked by the summary of the constructor's `Arg` flow;
   - or a value that is `Below(c, k-1)` for that container.
2. **Sole owning path.** The edge `D` is an exclusive part (a).
3. **No read after the owner's death.** This is the part that needs run time, and it is what the runtime's part
   protocol below gives.

Under .NET, a child that outlives its parent keeps the parent alive through `U`. So an uncounted `U` is valid only
while the child is attached, or while the child has no outside holder. Two cases:

- If every reference to a P is a `Guaranteed` borrow rooted at a path through its owner, nothing can make a P outlive
  its owner, and `U` is uncounted with no run-time work. That is a static fact: no flip, store or `HandOver` of a P
  value anywhere except the owning store.
- Otherwise the compiler knows the sites where a P gets a counted outside reference: the flips, the stores into
  non-part slots, the owned locals. Those sites use the part protocol.

**The part protocol.** These are new runtime calls, next to Runtime.cs's "Owners" block (`OnChainOwned` :2658,
`UpPath` :2674, `UpdateHold` :2696), replacing the run-time chain search where the proof holds:

| Call | Contract |
|---|---|
| `Rt.PartRetain(p)` | a counted outside retain of an attached part. On its outside count going 0→1, retain its owner (the hold: the owner stays alive while an outside holder of a child exists, as under .NET). O(1) |
| `Rt.PartRelease(p)` | the reverse. On 1→0, release the hold (which may destroy the owner, inline) |
| `Rt.PartAttach(c, p)` | the owning store (part slot), lowered as the plain counted store plus setting p's attached bit. If p has outside holders, add the hold on c |
| `Rt.PartDetach(c, p)` | the store that removes p. Clears the attached bit. If p is still alive with outside holders, p's `U` becomes a counted reference: retain c and set p's "U counted" bit, so a later `p.U = x` releases it |
| `Rt.UpStore(p, q)` | the `U` store: uncounted while attached and the bit is clear; otherwise counted |

The two bits live in the hash word's free bits beside `FinalizedBit` (0x20000000) and `MarkedBit` (0x40000000),
or in the arena prefix's flags word for types that also stay cycle-capable. Each call is O(1) with no walk.

Destruction follows Gel's children-first order (R, "Gel shows what an owner chain buys"). Destroying an owner
releases its part slots first. A part whose outside count is 0 then dies, so its `U` is never read after the owner's
free. A part with outside holders would have held the owner, so the owner cannot be dying. Destruction uses an
explicit worklist, as `rt.destroy`'s `DestroyPending` queue does today (`RuntimeGen.cs:1771`, `MaxNesting` 48), and
runs inside the releasing statement, so nothing is deferred.

**(c) Store down a structure.** `x.F = v` with v `Below(x, d)` for d ≥ 1 cannot close a cycle. This is
`ChainStores`' ordering proof (`ChainStores.cs:8-28`), on SSA def chains. The old exclusions for `try`, async and
lambdas disappear, because IL has lowered them to ordinary bodies and fields. `ref`/`out` parameters keep the old
condition on `RefWild` (`Arenas.cs:572`).

**(d) Fresh and isolated stores** (`FreshStores`): a store into a container that has not escaped at that point
(dominance plus the escape fact), or of an isolated value (a structure made only of objects created by that code).
Such a store cannot close a cycle now. The site still exists for later stores. These become store kinds (5.3).

**Coverage is measured per store site** (section 9): proven by (a)/(b)/(c)/(d), Persistent, or fallback. This is
SPEC 4.3's bar.

### 5.3 Store lowering

| `StoreKind` | When | Lowering (runtime calls as in Runtime.cs) |
|---|---|---|
| `Init` | constructor's own store to a field it has never written on this path, old value known null (`InitOnlyFields.ResolveInitStores`, `InitOnlyFields.cs:186`) | store; no old release |
| `Plain` | the target type is not cycle-capable, or the site's edge is `Outside`/removed | store; release old (`rt.release_plain` or atomic per 2.7) |
| `Down` | (c) | as `Plain` |
| `Fresh` | (d), container not escaped | `Rt.ArenaLinkFresh` (Runtime.cs:3752), inline fast path count > 0 and val != container (`Arenas.cs:951-960`) |
| `Iso` | (d), isolated value | `LinkIso`, mapped to the general link at a cyclic site (`Arenas.cs:937`) |
| `PartAttach` / `PartDetach` / `UpStore` | (a)/(b) | 5.2's calls |
| `Link` | fallback: an `Inside` edge in a cycle-capable SCC | `Rt.ArenaLink(val, container, slot)` (:3664), then `Rt.ArenaUnlink(old, container, slot)` (:4751); the precondition is that every link is followed by an unlink for the same slot |
| `LinkAt` | a store through an address into a possibly cyclic slot | `Rt.ArenaLinkAt` / `ArenaUnlinkAt` (:4956, :4966), container from `SlotOwner` (:4908) |
| `Move` | a block move of references (`CpBlk`, `Buffer.Memmove` on references, `Array.Copy`) | `Rt.ArenaMove` (:4980), `ArenaArrayCopy` (:5997), `ArenaArrayClear` (:6053), `ArenaGrowArray` (:3132) at cyclic element types; plain otherwise |
| `LastContainer` | the container's last use is this store | `Rt.ArenaLastBegin`/`ArenaLastEnd` (:4701/:4708) (`LocalMoves.IsLastContainer`) |

The intrinsic stubs at `IlCompiler.cs:704-717` become real lowerings driven by these kinds:

- `Rt.LinkAt` and `Rt.UnlinkAt` (today "a store through an address is a plain one");
- `Rt.ArenaIsSlot`, `Rt.SccOf`, `Rt.ArenaClearObject` and the rest of the `Arena*` dispatchers;
- `Rt.WeakDying`.

`GenerateLate` gains RuntimeGen's per-type arena functions (`EmitArenaSupport`, `RuntimeGen.cs:813`):

- `arena.finalize`, `arena.clear`, `arena.free`, `arena.visit`, `arena.is_slot`;
- the `SccOf`, `ArenaFinType` and `ArenaSlots` tables;
- the count-word fast paths for members (`rt.retain`/`rt.release` sign tests). These are emitted **only** when some
  type is cycle-capable, so a program with no groups pays nothing (A2.6).

### 5.4 The run-time fallback

The fallback is the group machinery in `Boundary/Runtime.cs` as it stands:

- the 32-byte arena prefix;
- arena records with union-find merge (`ArenaMerge`, :2543);
- members' inside and outside counts;
- `ArenaDestroyFrom` (:5595), `ArenaFree` (:5895) and `FinPhase` (:5300).

It is used only for `Link`/`LinkAt`/`Move` sites. Three of its parts touch the ruling and are **not decided here**:

- the run-time owner chain walk of up to 8 steps (`OnChainOwned`, :2658);
- the drop check (`Dropped`, :3458), which visits what a top owns at its drop;
- the 7.D region page return (AUDIT step 7, owner decision of 2026-11-18).

Each is listed as an open question (Q1–Q3), because ruling 2 says "no heap walks, no deferred frees, nothing freed
later". The measurements of S2 give the numbers those questions need.

### 5.5 Finalizers and weak references with groups (SPEC 4.4, ruling 3)

**Order within a dying group.** This is PEP 442 with counting in place of traversal (R6), and it is what `FinPhase`
already does (Runtime.cs:5300):

1. Mark every member `Dying` and set its count to `FinBias`.
2. Clear the whole group's short weak handles: `WeakDyingAll` (:5278), then `ObjectMarks.WeakDying` (ObjectMarks.cs:159).
3. Run every member's finalizer: `ArenaFinalizeObject` per member, each inline (6.9). The order is deterministic:
   ring order, which is program order of joining.
4. If any member's count changed from `FinBias`, a finalizer made an outside reference. **Abort the free** and keep
   the whole group (`FinRevive`). Every finalizer has run once; `FinalizedBit` stops a second run until
   `GC.ReRegisterForFinalize`.
5. Otherwise clear the members' references and free them: `ArenaClearObject`, then `ArenaFreeObject`.

A child's finalizer may read an uncounted `U` (5.2). No memory is freed until step 5, so this is safe (Elsman's
warning, R5).

**Single objects** follow `EmitFinalizeStep` (`Lower/Finalizers.cs:56`):

1. `OnFinalize` clears short handles and reports suppression.
2. Set `FinalizedBit`.
3. Count = 1, call the finalizer.
4. If the count is no longer 1, the object was resurrected: release and return.
5. Otherwise count = 0 and `OnDestroy` (long handles, dependents).

The A1.2 fix is already in that order.

**Parts (5.2).** A finalizable part is still destroyed children-first. Its finalizer runs before its owner's memory
goes, because the owner's destroy releases its part slots before `rt.free` of itself.

**`ConditionalWeakTable` and `DependentHandle` as key-to-value edges.** This is the notes' ephemeron design
(cycles_regions_finalizers.md, inference 4):

- **Type graph.** A dependent entry is a pseudo-hub `dependent` on the key's type, with an edge to the value's
  `TypeSet`. If the value's types can reach the key's type, the pseudo-hub is in a cycle-capable SCC.
- **Run time.** `ObjectMarks` holds the dependent for the key. New calls `Rt.LinkDependent(key, value)` and
  `Rt.UnlinkDependent(key, old)` are used where today the mark's slot is a plain counted reference:
  - at an acyclic pseudo-hub they are a plain counted reference, released by `OnDestroy` (ObjectMarks.cs:204) when
    the key dies;
  - at a cyclic one they are `ArenaLink` with the key as the container. The value's references back to the key are
    then inside references, so "a value never keeps its own key alive" holds by the group's outside count.
- **The CWT's own table** is not a holder of keys. It holds weak handles, so the key's death removes the entry.
- `DependentHandle.Target` set to null is `UnlinkDependent`.
- **Tests.** The CWT example (A→V1, B→P2→A) from the .NET page, plus finalizer and resurrection variants. These
  close FINISH.md's "a table's value that holds its key" difference.

**Weak handle kinds.** Short (`Weak`) handles are cleared at step 2. Long (`WeakTrackResurrection`) handles are
cleared at the free (`OnDestroy`), so they survive resurrection (ObjectMarks.cs:182-204).

`PartMarked` (:4178), which relinks a plain part that gets a weak handle so that member weak refs are cleared before
finalizers, stays for fallback groups.

---

## 6. Threads

### 6.1 What the IL front end must support

Today it compiles one thread [read]:

- `Constants.cs:25-28` folds `ThreadMode`=0 and `SingleThreaded`=1;
- the `volatile.` prefix is ignored (`Importer.cs:649`);
- `Rt.Share`, `RefLock` and `RefUnlock` are no-ops (`IlCompiler.cs:660, 713`);
- static constructors have no threaded protocol (`Lower/Statics.cs`, AUDIT il-runtime).

The threading code itself is upstream's IL: `Thread`, `ThreadPool`, `Task`, `Parallel`, `Monitor` and `Lock`,
`SemaphoreSlim`, the concurrent collections, `ThreadLocal<T>` and `AsyncLocal<T>`. Only the boundary is the compiler's
own:

| Piece | IL front end work |
|---|---|
| thread start | `Rt.SpawnCore(Thread)` (Runtime.cs:6357) → `Engine.ThreadSpawn` import (Runtime.cs:49) → `__thread_entry`; workers' start export (SPEC 6.1) |
| `SingleThreaded` / `ThreadMode` | no longer constants: decided by the solve (`SpawnCore` reachable or not) and folded at plan time; the old `ThreadsSeen` restart (`Driver.cs:208`) has no successor because reach is known before lowering |
| `Monitor` / `Lock` | upstream's managed `Lock` over `LowLevelMonitor` (Interop.Sys.cs:134-182); its fast paths are `Interlocked` on `Lock` fields, so 6.6's access kinds apply |
| `Interlocked` | intrinsics already lowered (`Runtime2.cs:173-250`: byte/ushort CAS, `MemoryBarrier`), the rest CoreLib IL over `Atomic.*` (`IlCompiler.cs:807-817`); access kind per site (6.6) |
| `volatile.`, `Volatile.Read/Write` | lowered to `i32.atomic.load`/`store` (seq_cst; stronger than .NET's acquire/release, allowed) at `Atomic` sites, plain at `Plain` sites; `Thread.MemoryBarrier` → `atomic.fence` only in threaded builds |
| `Atomic.Wait32`/`Notify` | real `memory.atomic.wait32`/`notify` in threaded builds (today "timed out"/0, `IlCompiler.cs:818-819`); main-thread waits as `Engine.MainCanBlock` says (spin) |
| `[ThreadStatic]` | static blocks kinds 2 and 3 (`IlCompiler.cs:499-532`) per thread via the TLS base passed to `__thread_entry`; `Rt.ReleaseThreadStatics` at thread end (today a no-op) |
| static constructors | the old front end's threaded protocol (owner id in the state word, waits, cycle detection) ported to `Lower/Statics.cs` |
| group locks | `Boundary/GroupLocks.cs` stripes, `StoreLockIf`/`StoreLockAtIf`/`StoreUnlockIf` (Runtime.cs:4887-4901) only at shared cyclic sites |
| place locks | `Rt.PlaceLockWord` (`IlCompiler.cs:699`, a 64-entry table) for `rt.store_ref`/`rt.load_retain` at shared slots |
| heap | threaded heap with `HeapLock`; thread-local pages for `Local` sites (AUDIT step 4.3) |

### 6.2 Thread roots and escape

The roots are found from the IL, with no name lists. This replaces `Scheduling.s_spawners` (`Compiler/Scheduling.cs:31`)
and `Sharing`'s thread-root statics:

1. The argument of every executable `Rt.SpawnCore(t)` call is `Shared`. That is the `Thread` object, so its start
   delegate, its delegate's target and captured display, and its parameter follow by 6.3's propagation.
2. A static is **worker-read** when some method reachable from `__thread_entry` reads it. That includes
   `Thread.StartCallback` and everything the started delegates reach, by the solve's own call graph restricted to
   worker roots.
   - The thread pool's queues are statics read by worker code. `Task.Run`, `ThreadPool.QueueUserWorkItem`,
     `Parallel.For` and timers therefore publish what they enqueue by the ordinary rule, with no special case.
3. A value stored into a slot of a `Shared` (or worker-read static) container becomes `Shared` (or `Published`, 6.4).
   This is Koka's static half (R, "Its static half ... is exactly the analysis il2wasm wants").
4. A value read by worker code from a `Shared` slot is `Shared`.
5. `[ThreadStatic]` statics and `ThreadLocal<T>` values are never roots (R7).

If no `SpawnCore` call is executable, every abstract object is `Local`, no thread code is emitted, and the analysis
costs nothing beyond the reach it already did (R7).

### 6.3 Precision: abstract objects with context

Domani et al. found that about 99% accuracy on hot sites is needed (R, threads section). Two refinements address that:

- **1-level call-site context** for allocation sites in methods whose summary is `FreshReturn`, or whose allocation
  flows into a `Part` slot of a parameter. These are factory and helper methods such as `List<T>..ctor` making
  `_items`, `StringBuilder` chunks, and `new T[]` in `Grow`. The abstract object is (site, caller site).
- **Part objects inherit their owner's context.** An array stored in `List<T>._items` (an exclusive-part edge, 5.2a)
  is abstracted by its owner's abstract object, not by the shared field key. One shared `List<object>` does not make
  every list's array `Shared`.

The context depth is capped at 1. It is widened only where section 9's counts show a hot site misclassified.

### 6.4 `Published`: hand-off with plain counts

A value handed to another thread as the sender's **last use** (a `HandOver` or move into the hand-off operation: the
`SpawnCore` argument, an enqueue into a pool queue, a `TaskCompletionSource.SetResult`) is `Published` when:

- the sender holds no other path to its abstract objects after the hand-off. No other `Owned` or `Guaranteed` value
  of those sites is live after the call, and no sender-reachable slot holds them. This is Swift SE-0414's region
  transfer done on the solved facts (threads_and_escape.md, Swift section);
- the receiver side's uses happen after the hand-off's synchronization. The queue's lock or `Interlocked`, or the
  thread start, gives that happens-before edge.

Count operations on `Published` objects are plain on both sides: before the hand-off only the sender counts, after it
only the receiver. Anything not proven is `Shared`. This state is **optional for slice S3**, and its gain is measured
before it is relied on (Q10).

### 6.5 Counts: plain or seq_cst atomic, decided statically

| Count site's operand abstract objects | Emitted |
|---|---|
| all `Local` / `Persistent`, or in a build without threads | `rt.retain_plain` / `rt.release_plain` (TypedCounts' plain functions, `Compiler/TypedCounts.cs:49`; inlined fast path) |
| all `Published` on the current side | plain |
| any `Shared`, not cycle-capable | atomic: `i32.atomic.rmw.add`/`sub` with the destroy on reaching 0 (no CAS loop needed: the word never changes meaning for a non-member) |
| any `Shared`, cycle-capable | the CAS loop of `RuntimeFns2.cs:88-152` with `ArenaRetainSlow`/`ArenaReleaseSlow` (GroupLocks.cs:523/537) |

- There is no run-time mode bit and no shared bit. `SharedCounts`' `SharedBit`/`LimboBit` stay vestigial
  (SharedCounts.cs:122-137).
- **A build without threads emits no atomic instruction at all.** A verifier pass over the module checks this when
  `SingleThreaded` (R7), and `Interlocked` lowers to `PlainRmw` (`IlCompiler.cs:1345`).
- **Shared slots.** Loads use `rt.load_retain` (RuntimeFns2.cs:217) and stores use `rt.store_ref` (`SharedCounts.cs:25`)
  under the place lock, so a load sees the old value counted or the new one. Slots of `Local` containers use plain
  load and store.
- A field key whose containers are all `Local` has plain loads in every method, worker code included.

### 6.6 Lock elision and atomic accesses from the same fact

An `Interlocked.*`, `Volatile.*` or `volatile.` access whose location's container abstract objects are all `Local`
(or which is a `[ThreadStatic]` slot) is lowered to the plain operation. The result is identical, because no other
thread can see the location.

Upstream's `Lock.TryEnter`/`Exit` and `Monitor` are `Interlocked` operations on fields of the lock object, or of the
`Lock` its sync block points to. So `lock (o)` on a `Local` o becomes plain reads and writes of the same state
words. That is lock elision with .NET's exact semantics: reentrancy counts, `SynchronizationLockException` on a bad
`Exit`, and `Monitor.IsEntered`. It needs no rule of its own. R reports Choi et al.'s median of 51% of locks removed;
this design's share is unmeasured.

`Monitor.Wait` on a `Local` object with no timeout is a wait no other thread can end, as under .NET (a hang). It is
reported as a build notice (SPEC 4.5: "a blocking wait ... reported as a build notice"). It is not changed.

### 6.7 Sharing of groups

- A cycle-capable fallback group is shared when any member's abstract object is `Shared`. Only those sites get
  `StoreLockIf` and the stripe protocol (GroupLocks.cs). `Local` groups take no locks, and their record's
  `ArenaShared` flag (Runtime.cs record +12) is never set.
- With threads, a member stored into a plain object stays a counted reference (AUDIT "Threads past four", item 4).
- Parts (5.2) of `Shared` owners use atomic part calls (`Rt.PartRetainAtomic` and so on), with the hold taken by
  atomic increment.

### 6.8 How SPEC 4.5's bars are met

| Bar | How | Measured by |
|---|---|---|
| single-threaded code no slower than the build without threads | `Local` objects get exactly the no-threads code (6.5, 6.6); no mode bit; statics escape only if worker-read; the heap's thread-local pages | `tools/frames-threads.mjs` idle-worker scenes against the same program built without threads, and against NativeAOT-LLVM; target ≤ 1.00x (today 1.65x to 2.8x on the old front end, FINISH.md check 4) |
| N threads faster than one, near N for independent work | private work is `Local` (plain); shared work pays one atomic per count and one lock per group operation, never per member (stripes) | `tools/bench-threads` (`plinq_trees.cs`, `shared_group.cs`), private churn/tree scaling in frames-threads; target ≥ 0.8·N at 4 threads for private work |
| racy code memory-safe, no hangs, each finalizer exactly once | atomic counts on every `Shared` path; `FinalizedBit`; `load_retain` protocol | stress runs (`tools/stress.mjs`) of the thread tests, 200 loaded runs with no difference |
| determinism (SPEC 7) | free order within a thread is program order; no address-derived hashes | `tools/selfdet.mjs`; Q7 on cross-thread free order |

### 6.9 Inline finalizers' thread identity (ruling 3, SPEC 4.4)

An inline finalizer runs on the mutator's physical thread, inside a **finalizer context**. `Rt.FinalizerEnter` and
`Rt.FinalizerExit` are new runtime calls that wrap every finalizer call: `CallFinalizers` in `rt.destroy` and
`ArenaFinalizeObject` in `FinPhase`. While a finalizer context is open:

- `Thread.CurrentThread` returns one process-wide `Thread` object for "the finalizer thread". It is made once, and
  its `ManagedThreadId` is fixed (Q6). Its `ExecutionContext` and `SynchronizationContext` are its own, so
  `AsyncLocal` reads in a finalizer see the finalizer thread's values.
- `[ThreadStatic]` reads see the finalizer thread's own static block. The TLS base global is swapped at enter and
  exit, which is O(1).
- **Lock ownership.** `Lock`'s owner check compares against the current managed thread id. Inside the context that
  is the finalizer's id, so locks held by the interrupted code are not the finalizer's own. A finalizer that tries to
  take such a lock would wait forever under inline execution, because the holder is suspended under it, while
  CoreCLR would block the finalizer thread until the holder releases. `Lock`'s contention path checks "the owner is
  a thread this physical thread interrupted" (a per-physical-thread stack of interrupted managed ids) and **throws
  or reports** (Q5). The compiler also gives a build notice when a finalizer's summary reaches `Monitor.Enter` on a
  lock type that code holding locks can release into (the release runs inside a lock region).
- An exception escaping a finalizer ends the instance, reported through the host's exception import, as an
  unhandled exception on .NET's finalizer thread ends the process (Q12).
- Nested finalizers (a finalizer's release frees another finalizable object) nest the context. The id stays the
  finalizer thread's.

---

## 7. Exceptions and unwinding under ownership

### 7.1 Paths

- Liveness has handlers as successors of their try's blocks (`Ownership.ExceptionSuccs`, `Ownership.cs:156`) and
  finally ends followed by their leave targets (`FinallyEnds`, :169), as today. An `Owned` value live at a throwing
  instruction is consumed on the exceptional path by the handler entry's release, or by the function's outer cleanup
  `try_table` (`FunctionLowering.cs:175-213`). Those locals are zero when they hold no count, so cleanup is
  path-independent; the plan keeps that invariant.
- A `Guaranteed` value used in a handler has its interval extended into the handler, which proof 1 covers.
- A dead end (2.1) needs no releases. An exception that escapes to an export is not a dead end.

### 7.2 Filters' two passes (SPEC 3)

A filter runs during the first pass, before the finally and fault blocks of the frames it would unwind
(`Lower/Filters.cs`, `rt.first_pass`). So while it runs, every frame between the throw and the filter's frame is
still live with its borrows, and a filter's writes happen inside other frames' borrow intervals. The rule:

- `FilterMods` is the union of the `Mods ∪ ReleaseEffects` of every reachable filter function. It is solved in the
  fixed point, because filters are bodies like any other.
- For a `Guaranteed` interval containing a throwing instruction whose exceptional path reaches a finally, fault or
  catch that uses the borrow, proof 1 includes `FilterMods`. The filter may belong to this method or to any frame up
  the stack.
- A program with no filters has `FilterMods = ∅` and pays nothing.
- A filter's own reads of the method's frame variables are borrows from `InFrame` Vars (`Ir/FilterSlots.cs:13-43`).
  The frame is intact during the first pass, so those are ordinary `Var`-rooted borrows.
- `Body.FiltersOnePass` (a filter that reads a struct argument, FilterSlots.cs:40) keeps one pass, as now.

### 7.3 Pinned and derived addresses

The rules already found stay, restated as OSSA interior-pointer rules:

- `Derives`/`RootOf`/`FindBases` (`Ownership.cs:36-95`) give each `Ptr` or by-ref-like value its base set. A base
  stays Owned or Guaranteed to the derived value's last use: "a use of it is a use of them".
- Pinned locals (`Ir/Pinning.cs:16-36`) store their root into a hidden `Var` at each store to the pinned local. The
  object stays counted to the next store or to the method's exit or unwind.
- Integer addresses (`Rt.AddressOf`, `ArrayData`, `StringData`, `Ownership.IsAddressOf`, :48) keep their object as
  bases.
- **One fix: phis are followed.** Today an address carried round a loop does not keep its object (`Ownership.cs:63-95`
  comment, AUDIT il-codegen). The base set of a `Ptr` phi becomes the join of its inputs' sets. It is monotone, and
  at worst an object is kept to the address's last use, never longer.

### 7.4 Returned byrefs and spans (the known leak)

A method that returns a byref or a span whose base only it holds currently leaves the base's count under the result:
a leak (`Ownership.cs:280-287`, AUDIT il-codegen "Unknown"). The fix carries `StorageKeeps`' runtime contract over:

- The callee returns the base as a hidden owned keeper: `Rt.ReturnKeep` (`StorageKeeps.EmitReturnKeep`,
  `Compiler/StorageKeeps.cs:335`).
- The caller takes it (`Rt.ReturnDone`) and treats it as an Owned base of the returned value. It is released at the
  returned value's last use, which is exact: .NET keeps the storage alive while the span is used (SPEC 6.5).
- Calls whose summaries say they return only what they were given (`StorageKeeps.GivesOnlyWhatItGot`, :244: spans,
  `MemoryMarshal`, `Unsafe`) need no keeper. On the IR that is a summary fact: the returned address's bases are
  parameter bases.
- `CollectionsMarshal.AsSpan` and `GetValueRefOrNullRef` get no special case. The returned address's bases are the
  list's `_items`, read through the `Part` edge. A later `Grow` that replaces `_items` releases the old array unless
  the span's base keeps it. That is the 2026-11-23 bug of `StorageKeeps.cs`, closed by the base rule rather than by
  name.

---

## 8. The verifier and the checked build

### 8.1 `Memory/Verify.cs`: the ownership pairing verifier on the IR

It runs after `Plan`, and after each inline plus canon step. It is always on in Debug builds and checked builds, and
behind `IL2WASM_VERIFY=1` in shipped builds. Its checks:

1. Every `Owned` value is consumed exactly once on every path from its definition to an exit: a move, a `Release`,
   or a dead end. Exceptional paths count.
2. No use of a value after its consuming use.
3. Every use of a `Guaranteed(path)` value lies in its root's live range.
4. Every flip decision has its `Retain` at the definition.
5. Every interior pointer's bases are live to its last use.
6. `Node.Moves` bits only on consuming operand positions (`Ownership.Consumes`).
7. Phi inputs agree in kind, or are made Owned by the phi copy.
8. Per call, the argument conventions match the target's or group's signature.
9. In a build without threads, no `Atomic` count, load or access kinds.

A failure is an internal compiler error that names the method, the value and the path. The verifier is the guard the
Brownbridge history asks for (R5): it is what makes the uncounted edges and borrows fail loudly.

### 8.2 The checked build

`Lower/Checks.cs` today has `Rt.Checked` = 1:

- freed memory is poisoned and never reused;
- `rt.release` traps on a release too many;
- `rt.retain` traps on a freed object.

Additions:

- **Shadow counts.** In checked builds every object gets a fourth header word. Every value is counted naively on it
  (`Ownership.Run`'s plan, emitted beside the real plan). When the real count frees an object whose shadow count is
  above zero, the build traps with "a borrow or proof outlived its object", naming the site. This validates every
  elided count and every proof at run time, on the whole suite.
- **Guaranteed-use check.** Each use of a Guaranteed value checks that the object is not poisoned.
- **Part-protocol check.** `UpStore`, `PartDetach` and destroy check the attached and counted bits against the
  shadow.
- **Group check.** The fallback's existing checked reports stay ("stored ... with no outside count", Runtime.cs:4573;
  "a member with no outside reference, released", :3639).
- The suite runs checked and as shipped (SPEC 13), and the NetWasm suite in both modes.

---

## 9. Instrumentation

### 9.1 Executed counts per site (R11, `--rcstats`-style)

`IL2WASM_COUNT_RC=1` is ported to the IL front end in `Lower/RcStats.cs`:

- Each planned count operation gets a site id and a category. Lowering emits a counter increment at it, into a
  table in linear memory, dumped at exit.
- `IL2WASM_COUNT_RC_SITES=sites.txt` writes the site table.
- A new `tools/il-rcstats.mjs` sums the dump by site and category, as `tools/rccauses.mjs` does for the old front
  end.

| Category | Sites |
|---|---|
| `arg-keepalive` | `KeepAlive` retains/releases at calls |
| `arg-after-call` | releases after a `Lend` last use |
| `handover-release` | callee releases at its last use |
| `owned-convention` | retains at non-last-use sites of single-entry `HandOver` methods and groups |
| `flip-field`, `flip-elem`, `flip-static`, `flip-var` | speculative borrows that flipped, by root |
| `generic` | values with no path |
| `store-old` | old-value releases at stores |
| `store-retain` | a non-last-use stored value |
| `phi` | phi copies |
| `unwind` | handler-entry and cleanup releases |
| `result` | releases of owned call results not otherwise consumed |
| `destroy` | releases of children inside destroy functions, counted separately |
| `group` | `Arena*` calls, by entry point |
| `part` | part-protocol calls |
| `runtime` | count operations inside `Il2Wasm.Runtime` code and the generated `rt.*` functions, separate from program and library code (R, "operations done inside the runtime's own code counted separately") |

- **Removal share**: 1 − (executed count operations) / (executed count operations of the same run built with
  `IL2WASM_MEM_NAIVE=1`), reported per program, with and without `runtime`. The target is as close to 100% as
  possible, with 95% only Lobster's figure (`lobster-rc-is-the-point`). Per slice targets are in section 11.
- **Store coverage** (SPEC 4.3): executed stores by `StoreKind`, and the fallback's share. Fallback cost is
  `group`-category calls and the time in `Arena*` in a profile (`tools/profcompare.mjs`).

### 9.2 Live objects at exit against .NET

- `IL2WASM_IL_COUNT_OBJECTS=1` (exists: AUDIT il-frontend-2) reports blocks live at exit.
- Extend it to per type, as `IL2WASM_MEMSTATS=1` did for the old front end (FINISH.md stream L).
- The oracle is the same program under .NET, after `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect()` at
  exit, counted per type from a heap dump (`dotnet-gcdump` of the oracle run, on the homelab).
- Only objects that statics can reach should remain on both sides. The check is that the per-type counts are
  **equal**. A difference is a leak, or a premature free, which checked mode would trap first.
- Run on the IL tests, the NetWasm suite's cases that have no GC-dependent output (the suite's classification, AUDIT
  netwasm-suite), the old suite's cycle and finalizer tests (016, 017, 082–085, 090, 119, 121, 127, 128, 139), and
  the fixed-memory API checks (a long-lived tree in 16 MB).

---

## 10. The old Compiler/ files: what each carried and where it lands

| Old file (src/Il2Wasm/Compiler) | Rule it carried | Lands in | Dropped, and why |
|---|---|---|---|
| `LocalBorrows.cs` | a reassigned local borrows when every value it gets is held for its scope (`WrittenBorrow` :55, `HeldThroughScope` :134) | `Borrows.cs`: Guaranteed phis with loop-invariant path keys (4.6) | scope by syntax (`:142`), the `"borrow? k1|k2"` placeholders: SSA intervals replace them |
| `LocalMoves.cs` | last-use moves, self-assignment moves, hand-off-then-overwrite, last container (`ComputeLocalMoves` :25, `IsLastContainer` :101) | `Plan.cs` (SSA last use, `Node.Moves`); `LastContainer` store kind (5.3) | the try/goto/lambda bail-outs: exceptional liveness covers try; IL has no lambdas |
| `FieldBorrows.cs` | a local or temporary borrows from a field, static, element or getter when no store to its keys can run in scope; `StoreSummary` over call-graph SCCs (:1023); thread confinement (:651-692) | `Borrows.cs` (proofs 1 and 2), `Summary.cs`/`Solver.cs` (`Mods`) | the marker/`WriteMark` nops and the WASM-body resolution (:625): the decision is made before lowering |
| `FieldLoads.cs` | field tags, loop store sets, `CtorOnly`; field address escape class-wide (:94, test 207) | `Summary.cs` keys; the loop store sets feed `Wasm/FieldLoads` unchanged through `Module.FieldHoist` | nothing; with threads, `Local` field keys may now hoist (was readonly-only) |
| `OwnedParams.cs` | consumed parameters, the owned variant, list and map insert exceptions (:110-139), struct parameter moves | `Signatures.cs`: `HandOver` by store; owned entries (4.4) | the hand-written `IsListAdd`/`IsMapInsert`/`ReturnsAfter`; the 1–4 uses and exclusive-branch limits: SSA last use decides |
| `OutBorrows.cs` | borrowed `out` values (`OutBorrowable` :37, `OutBorrowCall` :144) | `Borrows.cs`: `BorrowReturn` through an out parameter (4.6) | the "quiet region" syntax check: proof 1 over the interval is the general form; the threads exclusion becomes proof 2 |
| `BorrowedStructArgs.cs` | a struct argument passed by the caller's address when the callee never writes it (:21, :110) | `Signatures.cs` (read-only struct parameter as a summary fact) + `Borrows.cs` proof 1 on the source | the `LeafCallee` restriction for `this`: callee `Mods` replaces it |
| `Specialization.cs` | constant-argument copies of library methods (:37, :163) | SCCP's interprocedural `Val` (3.4); variants only under the 25-node cap | library-only restriction: constants apply to all code |
| `StructMoves.cs` | struct temporaries moved, not copied (`TakeFresh` :41); `InitStoreKey` (:26) | `Plan.cs` (4.7); `Init` store kind | "nothing emitted in between": SSA single use replaces it |
| `FreshStores.cs` | stores into unescaped objects, isolated values (`CtorsKeepThis` :24, `IsolatedValue` :279, `IsoMethod` :374) | `Groups.cs` (5.2d): `Fresh`/`Iso` store kinds; escape from `Summary` | syntactic "every use before keeps it in" (:119): dominance plus escape |
| `StorageKeeps.cs` | spans and refs keep collections' storage; `ReturnKeep`/`ReturnDone` (:335) | 7.3/7.4: bases, keeper returns | the name lists (`IsStorageCall` :85, `GivesOnlyWhatItGot` :244) and the function-level keep local (it kept arrays longer than the span's use); bases by summary |
| `OwnedArrays.cs` | sealed array fields as their own type-graph nodes (`Seal` :168) | `Groups.cs` edge removal (5.1), as a may-set in the one solve | the separate greatest fixpoint and the 71 syntax uses |
| `InitOnlyFields.cs` | init-only keys need no store check; a constructor's first store releases nothing (`InitOnlyKey` :37, `ResolveInitStores` :186) | `Borrows.cs` proof 2; `Init` store kind | `: this(...)` chain depth limit 16: constructor chains are calls with summaries |
| `StaticLoads.cs` | loop-invariant static loads when nothing stores the static (`ResolveStaticLoads` :60) | `Summary.cs` `Mods` over statics → `WasmFunction.StableStatics` for `Wasm/StaticLoads` | the threads exclusion: `Local` statics now qualify |
| `Arenas.cs` | sites, type graph, SCCs, cycle capability (:768), store placeholder resolution (:870), frame and immortal pins | `Groups.cs` (5.1, 5.3); lowering emits final calls directly | the placeholder rewrite on WASM: store kinds are known before lowering; `IL2WASM_IMMUTABLE_EDGES` (the unsound old rule) |
| `ChainStores.cs` | stores down a structure (`ChainFlow` :143, `CtorHolds` :92) | `Groups.cs` (5.2c) on SSA def chains | the try/async/lambda exclusions |
| `RefStores.cs` | stores through `ref`/pointers name their slots by type; `Unsafe.As` merges (:40-142) | `Summary.cs` by-address keys; `LinkAt` store kind | nothing |
| `EscapeAnalysis.cs` | per-parameter escape (:31), frame objects (:304), cycle types stay on the heap (:372) | `Summary.cs` `ParamEscape`; 2.3 allocation lattice | the no-finalizer restriction (R8); the 256-byte cap stays as a start |
| `Sharing.cs` | thread escape over value sources; `MayBeShared` (:254), `MaySlotsBeShared` (:237); certified calls (:37) | `Threads.cs` on SSA abstract objects (6.2–6.3) | string-keyed source nodes; `PriorThreadLocal` and the second compilation (:393, `Api.cs`); `OptimisticFlow` (unsound); `IL2WASM_SHARE_CHECK` run-time trap (kept only in checked builds) |
| `SharingFlow.cs` | site type sets by SCC over source nodes (:287) | the solver's per-key `TypeSet` (2.6) with context (6.3) | the flow cap of 2000 and string keys |
| `SharedCounts.cs` | `rt.store_ref` under the place lock; publish placeholders (stripped) | 6.5 (`store_ref`/`load_retain` at `Shared` slots) | `SharedBit`/`LimboBit` and `rt.publish_*`: vestigial, no run-time shared bit |
| `TypedCounts.cs` | per-type plain/atomic counts (`ResolveCounts` :122); plain count encoding (:49) | 6.5 per site by abstract object | per-static-type granularity (too coarse: Domani) |
| `Instrumentation.cs` | plugin hooks retain their arguments; disables proofs in instrumented code | plugin passes run on the IR before planning (SPEC 11.1); a hook's argument is a `HandOver`-unknown call: Owned | — |
| `Solver.cs` | plugin closed forms; no memory role | — | — |
| `Driver.cs` | pipeline order (1402-1462); borrowed-return getters (697-720); `ResolveSharing` (197-206); `ThreadsSeen` restart | `IlCompiler` phases (1.2); `BorrowReturn` (4.4) | the restart; recompile rounds for static initializers |
| `FunctionCompiler.cs` | per-function prescan order (195-209), frame objects, storage keeps, struct parameter move check (208-209) | `Plan.cs`, `Verify.cs` (the move check becomes verifier check 1) | syntax prescans |
| `RuntimeGen.cs` | `EmitDestroy` (:655) with finalizer step and sealed-tail loop; `ExpandExactReleases` (:1726); `EmitDestroyDispatch` with depth queue (:1771); `EmitOnDestroy` (:1640); `EmitArenaSupport` (:813); `FusedDrop` | `GenerateLate` in the IL front end (`IlCompiler.cs:946-990` today has destroy and finalize) gains arena support, exact releases, the depth queue and the part steps | nothing; `FusedDrop` stays as it is |

---

## 11. Vertical slices

Each slice covers all four areas (ownership, cycles, threads, optimization) for one kind of program. It ends with the
suites green and a merge to main. The bars apply to each slice in addition to the ones before it:

- **IL tests**: `tests/Il2Wasm.IL.Tests`, 109 of 109 today, plus the slice's own.
- **NetWasm suite**: **283 or more of 450 case-modes**, compared **case by case**. No case that passed before may
  fail, checked and as shipped.
- **Leaks**: live objects at exit equal .NET's, per type (9.2).
- **Speed**: `tools/bench.mjs` and `tools/frames.mjs` against NativeAOT-LLVM's WebAssembly build in the same engine
  (`nativeaot-means-wasm-target`), once the IL front end compiles those programs. Making it compile them is part of
  S1.

### S1. Single-threaded allocation and trees

- **Ownership**:
  - `Facts.cs`, `Summary.cs`, the solver with reach, dispatch, `TypeSet` and `Mods`;
  - signatures with the call rule and owned entries;
  - speculative borrows;
  - `Plan.cs` replacing `Ownership.Insert`;
  - `Verify.cs`;
  - `KeepAlive`;
  - `BorrowReturn`;
  - returned-span keepers (7.4).
- **Cycles**: the type graph and cycle capability computed and reported (a build notice per cycle-capable program
  type, as `Diags.CyclePossible` today). Acyclic programs get plain stores. Groups are not wired yet: a
  cycle-capable store stays the current stub, and its count is reported.
- **Threads**: `SingleThreaded` from the solve; zero atomics verified.
- **Optimization**: devirtualization, `Virtual`/`FrameOwned` allocation, the IR inliner with Canon, `Persistent`.
- **Bars**:
  - removal share **≥ 90% executed** (target) on the IL tests, bench kernels and NetWasm cases with allocation;
  - bench.mjs's `tree` and `binarytrees`-shaped kernels at or below NativeAOT-LLVM;
  - hello world size not above today's IL front end (970,075 B, AUDIT il-generics);
  - analysis time within 3.8's budget.

### S2. Long-lived cyclic structures

- **Ownership**: parts' borrow paths.
- **Cycles**:
  - the owner-chain proofs (5.2a–d);
  - the part protocol in Runtime.cs;
  - the fallback wired: the stubs at `IlCompiler.cs:704-717` become real lowerings, plus `EmitArenaSupport` in
    `GenerateLate`;
  - group finalization and weak references (5.5);
  - CWT and `DependentHandle` edges.
- **Threads**: none new.
- **Optimization**: `ChainStores`/`FreshStores` store kinds; owned arrays.
- **Bars**:
  - old suite cycle tests 016, 017, 082–085, 090, 119, 121, 127, 128 ported as IL tests;
  - the fixed-memory API check (a long-lived tree in 16 MB);
  - the CWT and dependent-handle tests;
  - frames.mjs `tree` and `churn` scenes;
  - **coverage and fallback cost measured on the self-hosted compiler and the game** (SPEC 4.3), reported with
    group sizes with each rule on and off, then brought to the owner (Q1–Q3).

### S3. Shared queues and tasks

- **Ownership**: proof 2 everywhere.
- **Threads**:
  - the IL front end's thread support (6.1);
  - roots and escape (6.2–6.3);
  - plain and atomic counts;
  - `load_retain`/`store_ref`;
  - access kinds and lock elision;
  - the threaded static-constructor protocol;
  - `[ThreadStatic]` per thread;
  - inline finalizer identity (6.9);
  - optionally `Published` (6.4).
- **Cycles**: shared groups with stripes only at shared sites.
- **Optimization**: worker-read statics.
- **Bars**:
  - the old suite's thread tests (021, 127, 131, 139, 187) ported, 200 loaded runs with no difference;
  - frames-threads.mjs idle-worker scenes **≤ 1.00x the no-threads build**;
  - the NetWasm suite's async and task cases.

### S4. Parallel work

- **Threads**: context-sensitive sites widened where 9.1 shows misclassified hot sites; `Parallel.For`/PLINQ shapes;
  part protocol atomic variants.
- **Cycles**: shared groups under load.
- **Optimization**: abstract-object context for parts.
- **Bars**:
  - `tools/bench-threads` `plinq_trees` scaling **≥ 3.2x at 4 threads** (target);
  - `shared_group` no collapse;
  - the threaded self-hosted compiler faster on 4 threads than 1 (FINISH.md check 5);
  - output byte-identical on 1 and 4 cores (`tools/selfdet.mjs`).

### 11.3 What parallel agents can build without touching each other's files

The order is fixed:

1. **Serial first.** One agent writes `Memory/Facts.cs`, the `Summary.cs` skeleton, the `Solver.cs` skeleton with
   reach and dispatch moved out of `IlCompiler.Reach`, and the `IlCompiler` phase split (1.2). Everything else needs
   these. The interfaces are frozen at the end of this step.
2. **S1 in parallel, one agent per row:**

   | Agent | Files |
   |---|---|
   | a | `Signatures.cs` |
   | b | `Borrows.cs` |
   | c | `Plan.cs` + `Verify.cs` (one agent: the verifier checks the planner) |
   | d | `Ir/Inline.cs` + `Canon.cs` |
   | e | `Lower/RcStats.cs` + `tools/il-rcstats.mjs` + the per-type live-object check |
   | f | `Lower/FunctionLowering.cs` reading `Plan` (after c's interface) |

3. **S2, in parallel with S3:**
   - `Groups.cs` (one agent);
   - Runtime.cs's part protocol plus `Lower/Finalizers.cs` and `ObjectMarks.cs` dependents (one agent; no compiler
     files);
   - `GenerateLate`'s arena support in a new `Lower/Groups.cs` (one agent).
4. **S3, in parallel with S2:**
   - `Threads.cs` (one agent);
   - `Lower/Statics.cs` threaded protocol, `[ThreadStatic]` and `Atomic.Wait` lowering (one agent);
   - finalizer context `Rt.FinalizerEnter/Exit` and `Lock`'s interrupted-owner check in CoreLib's Boundary (one
     agent).
5. **S4** after S3: one agent on the context policy, with measurement.

The shared points are `Facts.cs`, `IlCompiler.cs` and Runtime.cs. Changes to `Facts.cs` after step 1 go through one
owner. Runtime.cs edits are confined to new regions per agent.

---

## 12. Literature recommendations not adopted (they conflict with the constraints)

| Recommendation or technique | Source | Conflict | Instead |
|---|---|---|---|
| Lobster's `EmitKeep` slots (values kept to function end or loop round) | R, Lobster section | frees later than unreachability | release at the call or the last use (4.3) |
| Joisha's later decrements for DCE; Swift's lexical lifetimes to scope end | R, "Joisha ... at the expense of holding onto garbage longer" | late frees | Canon only moves releases earlier (3.7) |
| Nim scope-end destruction | R4 | late frees | SSA last use |
| Biased RC's merge queue | R7 | deferred free; free timing depends on which thread drops last | static plain/atomic split (6.5) |
| CPython deferred counts, QSBR; concurrent deferred RC; coalescing; born-dead | R, threads; imperative_rc_optimization.md | deferral | none |
| Koka's negative-count shared bit | R7 | run-time "is shared" test decides counts | static escape fact |
| Koka/Lean `is-unique` reuse; FL-reuse tokens across calls | R10 | run-time uniqueness check; memory held across calls | not built; only the static forms in R10, after measurement |
| Parkinson per-SCC counts for mutable groups | R5 | a cut sub-cycle freed later than its cutting statement | frozen data only (Persistent, 2.5) |
| PEP 442's re-traversal after finalizers | R6 | a walk | outside-count check (5.5 step 4) |
| a single finalizer thread, finalizers queued in drain order | R6 (notes) | moves work later | inline (6.9), per ruling 3 |
| Deterministic multithreading (Kendo, DThreads) | R, threads | costs the N-times bar | add no nondeterminism of our own |
| Gel's "non-zero count at owner death is fatal" | R, Gel | rejects programs | the hold makes it "not dead yet" (5.2) |
| Lobster's generic borrow for non-path expressions | R3 | unsound | generic values are Owned (2.2) |
| Region page return for groups (MLKit-style regions, AUDIT 7.D) | R5; AUDIT step 7 | a member dropped early stays until its region goes | not used; Q1 |

---

## 13. Open questions for the owner

**Ruled 2026-11-23: the owner accepted every proposed answer below.** SPEC 4.1 (borrowed-return specializations) and
4.4 (the finalizer thread's identity and the blocked-lock case) were amended to match.

| # | Ruling |
|---|---|
| Q1 | The 2026-11-18 region page-return acceptance lapses: no regions; SPEC 4.3 governs. |
| Q2 | The drop check stays in the fallback only; S2 measures how often it runs and its cost, and the numbers go to the owner (ruling 2). |
| Q3 | The run-time owner-chain search (at most 8 steps, constant bound) stays in the fallback. |
| Q4 | Borrow-returning specializations are allowed where every caller borrows; the method's own entry returns owned (SPEC 4.1). |
| Q5 | A finalizer that would block on a lock the interrupted code holds is reported to the host and ends the instance (SPEC 4.4). |
| Q6 | The finalizer thread: `ManagedThreadId` 2, `Name` null, `IsBackground` true (SPEC 4.4). |
| Q7 | Scheduling-dependent free order for racy releases of shared objects is allowed; single-threaded and correctly synchronized programs stay deterministic. |
| Q8 | As 4.3 says: an inlined call and a cheap intrinsic are not further calls; exceptional paths are; the rule covers `this` and structs with references. |
| Q9 | Size decides specialization (deterministic); executed counts only tune the caps. |
| Q10 | `Published` plain counts on the static hand-off proof are accepted, verified by the checked build. |
| Q11 | Confirmed: persistent static graphs are never freed and their finalizers never run. |
| Q12 | Confirmed: an exception that escapes an inline finalizer ends the instance. |
| Q13 | Yes: the finalizer context has its own `[ThreadStatic]` and `AsyncLocal` state (SPEC 4.4). |
| Q14 | 3.8's budget (at most 20% of build time, at most 25% more peak memory) is accepted as targets; the whole build still has to shrink toward NativeAOT-LLVM's. |

The questions as they were put:

Each needs a ruling. None is decided here.

- **Q1. Region and group page return (AUDIT step 7.D, accepted 2026-11-18) and SPEC 4.3.** The 2026-11-18 decision
  accepted that a member dropped early may stay until its region or group goes. SPEC 4.3 and ruling 2 (2026-11-23)
  say "nothing freed later than the statement that makes it unreachable". This design does not use regions. Does the
  2026-11-18 acceptance still stand for anything?
- **Q2. The drop check** (`Dropped`, Runtime.cs:3458) is approved as "option 1" (2026-11-18). It visits what a member
  owns at its drop to see whether every reference comes from inside. Is it a "heap walk" under ruling 2, to be
  removed once the proofs cover the cases it serves, or does it stay in the fallback?
- **Q3. The run-time owner-chain search** (`OnChainOwned`, up to 8 steps, Runtime.cs:2658) walks up a chain at a
  store. Can the fallback keep it while the static part protocol (5.2) replaces it where proven?
- **Q4. Borrowed-return variants against SPEC 4.1's "Return values are owned".** The old front end had them
  (`Driver.cs:697-720`), and this design keeps them as a specialization (4.4). Does SPEC 4.1's sentence allow a
  borrow-returning specialization when the method's own entry stays owned?
- **Q5. A finalizer that would block on a lock the interrupted code holds** (ruling 3: "throws or is reported").
  Which one? If it throws, which exception, and from where (the `Enter` inside the finalizer, which then ends the
  instance by Q12)? If it is reported, how (host exception report and the finalizer skipped, or a trap)?
- **Q6. The finalizer thread's identity.** Which `ManagedThreadId` does it report? CoreCLR's is not fixed (often 2).
  Is its `Name` null, as CoreCLR's? Is `IsBackground` true?
- **Q7. Free order across threads and SPEC 7.** When two threads release a `Shared` object concurrently, which one
  frees it, and runs its finalizer, depends on scheduling. .NET allows any timing. SPEC 7 says free timing is
  "decided by the program's statements". The design marks such frees as scheduling-dependent for plugins (SPEC 7,
  last bullet). Is that enough, or must something else hold?
- **Q8. The call rule's "further calls".** Does a call that the IR inliner inlines at that point count as a further
  call (4.3 says no)? Does a cheap intrinsic count (4.3 says no)? Do exceptional paths count (4.3 says yes)? Does the
  rule also cover `this` and structs carrying references (4.3 says yes)?
- **Q9. The owned convention for large methods and dispatch groups.** It trades a retain at each non-last-use call
  site for not cloning. The caps (25 nodes for full specialization, 400 for two entries) are starting targets. Should
  size or executed counts decide, when they disagree?
- **Q10. `Published` objects with plain counts (6.4).** They rely on the hand-off's happens-before edge and on a
  static proof that the sender keeps no path. Is a proof of this shape acceptable, or must every object another
  thread can reach use atomic counts?
- **Q11. Persistent (immortal) static graphs.** Their objects are never freed and their finalizers never run. That is
  also .NET's behaviour for objects statics hold for the program's life, since .NET Core does not finalize at exit.
  Confirm.
- **Q12. An exception that escapes an inline finalizer ends the instance** (reported to the host), as it ends the
  process under .NET. Confirm, since on the mutator's thread it would otherwise unwind into unrelated code.
- **Q13. The finalizer thread's `[ThreadStatic]` and `AsyncLocal` state** (6.9 swaps them). Ruling 3 names only
  `CurrentThread`, `ManagedThreadId` and locks. Is the wider emulation wanted?
- **Q14. Analysis budget.** Section 3.8's targets (≤ 20% of build time, ≤ 25% more peak memory) are mine. Do they
  match the owner's "reasonably fast to compile with"?

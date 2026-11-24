# Whole-program optimization and ownership / reference counting, designed as one system

Scope: how escape analysis, inlining, specialization, devirtualization and points-to analysis interact with compile-time RC and ownership, with phase ordering, fixed points and scalability. Prepared 2026-11-23. Primary sources were read directly (PDFs converted to text) unless noted.

## 1. Escape analysis and stack allocation in managed compilers (.NET JIT, ILC, HotSpot C2, Graal PEA), and how stack allocation combines with counting

### Takeaway
The .NET JIT's escape analysis started out intraprocedural and limited, and it gains almost entirely from inlining. .NET 10 extended it to delegates, small arrays, struct fields and spans, and added "conditional escape" guarded by type checks, for 3x to 11x speedups on microbenchmarks. Graal's flow-sensitive Partial Escape Analysis (PEA) runs on the same SSA IR as inlining, GVN and constant folding, interleaved with them. It removed up to 58.5% of allocated bytes and gave up to 33% speedup. For il2wasm, an object that never escapes is the strongest ownership fact there is: its single owner is the frame, so it needs no count operations, no header count field and no free call.

### Cited Findings
**.NET JIT design doc (dotnet/runtime, `object-stack-allocation.md`; the 2016 prototype is described there, revived later and undated)**
- The 2016 prototype (by `echesakovMSFT`) used a "simple intra-procedural escape analysis" based on a connection graph, "without field edges in the connection graph". "All call arguments are assumed to be escaping." [object-stack-allocation.md](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
- "Objects with finalizers can't be stack-allocated since they always escape to the finalizer queue." Allocations inside a loop qualify only if they do not escape the iteration. Weak GC fields "need investigation". [same](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
- Phase ordering: the JIT works top-down (callers before callees), which "doesn't fit well" with stack allocation. The doc proposes running escape analysis alongside inlining and caching callee parameter-escape summaries, weighting inlining toward callees that take possibly stack-allocatable arguments, and using bottom-up order at higher tiers. If an analyzed callee changes later, the JIT must either inline it or revoke the dependent stack allocations. [same](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
- Write barriers: a reference known to be on the heap gets a normal barrier, one known to be on the stack gets none, and one that may be either gets a checked barrier. In the prototype, once any object in a method was stack-allocated, every object in it was reported as TYPE_GC_BYREF with checked barriers. [same](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
- 2016 measurements on System.Private.CoreLib via crossgen:
  - The conservative analysis moved 21 allocation sites to the stack.
  - The optimistic one (no call argument assumed to escape) moved sites in 424 of 6150 methods, 586 of 7977 allocation sites. 5345 of the sites were exception allocations; excluding them, 586 of 2632 sites qualify, an upper bound of 22.2%.
  - In the Roslyn csc self-build, GreenNode.WriteTo accounts for 8.17% of object allocations but only 0.67% of allocated bytes (6.24 MB of 920.1 MB). [same](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)

**.NET 10 (Stephen Toub, "Performance Improvements in .NET 10", 2025-09-10)**
- .NET 9 "had already introduced some limited escape analysis and stack allocation support". .NET 10 added the following. [devblogs](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10)
  - Delegates (PR #115172): the JIT knows a delegate's `Invoke` doesn't retain `this`.
  - Arrays (#104906, #112250).
  - Struct fields and `Span<T>` (#113977, #116124).
  - Span operations such as `Memmove` treated as non-escaping (#113093).
  - Conditional escape analysis with dynamic PGO (#111473, #116978, #116992, #117222, #117295): an object escapes only if a generated type-check guard fails, so `foreach` enumerators over `IEnumerable<T>` are stack-allocated on the fast path.
  - Retyping an `IEnumerable<int>` return as `int[]` (#111948).
  - Inliner boosts for callees that return small fixed-length new arrays (#114806) and for callees that box (#110596).
- Measured, .NET 9 to .NET 10 (i9-11950H, Ubuntu 24.04.1): [devblogs](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10)

  | Benchmark | .NET 9 | .NET 10 | Allocated |
  |---|---|---|---|
  | Delegate `Sum` | 19.530 ns | 6.685 ns (0.34) | 88 B to 24 B |
  | Array `Test` | 11.580 ns | 3.960 ns (0.34) | 48 B to 0 |
  | `BitConverter` span | 9.7717 ns | 0.8718 ns (0.09) | 32 B to 0 |
  | PGO enumerator `Sum` | 109.86 ns | 35.45 ns (0.32) | 32 B to 0 |
  | Retyped enumerator | 16.341 ns | 2.059 ns (0.13) | 32 B to 0 |

- The Microsoft runtime docs note that `foreach` enumerator calls that stay virtual block inlining and stack allocation. Stack allocation of reference-type arrays is constrained because the GC must track the references stored in them. [What's new in .NET 10 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)

**Graal Partial Escape Analysis (Stadler, Würthinger, Mössenböck, CGO 2014)**
- PEA works on the high-level Graal IR (SSA, sea of nodes), "so that it can be applied, possibly multiple times, at any point during compilation". "Partial Escape Analysis is particularly effective if it can interact with other parts of the compiler, such as inlining, global value numbering, and constant folding. In order to do so, it needs to work on the same internal program representation". [PEA paper PDF](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)
- Algorithm:
  - It iterates forward along control flow, keeping a per-allocation state: virtual (field values and lock count) or escaped (materialized value).
  - At merges it unifies states. A merge can force materialization, and the merge is iterated "until no additional materializations happen".
  - Loops are processed with a speculative entry state and re-run until the back-edge state matches.
  - A reference equality where exactly one side is virtual folds to false, and type checks on virtual objects fold statically.
  - Materialization is pushed to the branches where the object actually escapes. [same](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)
- Results (DaCapo 14 benchmarks, ScalaDaCapo 12, SPECjbb2005):
  - Allocated memory fell by up to 58.5% (ScalaDaCapo factorie, 24.5 GB per iteration) and performance rose by up to 33% (factorie).
  - Average speedup: DaCapo 2.2%, ScalaDaCapo 10.4%, SPECjbb2005 8.7%. DaCapo jython slowed by 2.1% because PEA "can in rare cases increase the size of compiled methods".
  - Lock operations fell only slightly: tomcat by 4%, SPECjbb2005 by 3.8%. [same](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)
- Against HotSpot C2's flow-insensitive EA (C2 vs Graal PEA gains): DaCapo 0.9% vs 2.2%, ScalaDaCapo 7.4% vs 10.4%, SPECjbb2005 5.4% vs 8.7%. The authors caution that "it is hard to tell the difference between better Escape Analysis and the rest of the compiler performing better". [same](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)

**Swift as an RC compiler with stack promotion**
- Swift's performance pipeline places `AllocBoxToStack` at the start of every function-pass round. The pipeline computes escape and side effects (`ComputeEscapeEffects`, `ComputeSideEffects`) after the high-level function passes, then runs `StackPromotion` as a module pass, "the first stack promotion on high-level SIL before serialization". [PassPipeline.cpp](https://raw.githubusercontent.com/swiftlang/swift/main/lib/SILOptimizer/PassManager/PassPipeline.cpp)

### Inferences
- Under compile-time RC, a non-escaping counted object needs no count operations at all, and possibly no heap header. The rest of the program sees only borrows of it (from the owning frame), and its release is a static scope end; with finalizers, the frame runs the finalization at that scope end.
- PEA's per-branch materialization corresponds to "own lazily". In branches where the object stays virtual, no RC exists. At the materialization point the object becomes a normal heap object whose owner is whatever it escaped into. Such an analysis is a natural sub-lattice of a combined ownership lattice: Virtual < FrameOwned < SingleOwner < Shared.
- .NET cannot stack-allocate objects with finalizers, because .NET's finalization timing is nondeterministic. il2wasm can make a different choice, because .NET allows finalizers to run at any time after unreachability. Whether that is allowed under the "behaviour must not differ" rule needs a decision from the user.
- Every .NET 10 gain comes from inlining or devirtualizing first (delegate `Invoke`, enumerator via guarded devirtualization, retyping). Escape and ownership facts are only as good as the call-graph precision feeding them, which argues for interleaving ownership with inlining and devirtualization rather than running it after.

### Gaps
- I found no published aggregate (suite-level) numbers for .NET 9/10 stack allocation, only microbenchmarks.
- I found no document on whether ILC/NativeAOT runs any whole-program escape analysis beyond the JIT's per-method one. The ILC architecture doc doesn't mention it.

## 2. Inlining and specialization interacting with RC (Swift, Koka/Perceus, Lean, Lobster, Nim)

### Takeaway
None of the mature RC compilers settles ownership once. Swift keeps ownership explicit (OSSA) through inlining and specialization, running copy propagation and semantic-ARC cleanups both before and after each inliner, and lowers ownership late. Lean and Koka insert RC operations after the functional optimizations (inlining and specialization) and fuse or specialize them afterwards. Lobster interleaves ownership with type checking and specializes functions by argument ownership, but caps that specialization for large functions and dynamic-dispatch groups. Lobster reports removing about 95% of runtime RC operations.

### Cited Findings
**Swift**
- OSSA is SSA plus ownership on value-operand edges: each value is None, Owned, Guaranteed or Unowned, and every use is lifetime-ending or not. The checks apply at "all points of the pipeline until OSSA is lowered". [SIL Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- When a function is inlined, a lexical borrow scope is added for each guaranteed argument and a lexical move for each owned argument, which preserves source-level lifetimes across inlining. [same](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- Pipeline (current `main`):
  - Each function-pass round runs specializer, then devirtualizer ("Run devirtualizer after the specializer, because many class_method/witness_method instructions may use concrete types now"), then `ARCSequenceOpts`, then `CopyPropagation` + `SemanticARCOpts` + `CopyToBorrowOptimization`, then the inliner (`EarlyPerfInliner` at high level, `PerfInliner` "Inlines everything" at mid and low levels), then copy propagation and semantic-ARC again ("Clean up Semantic ARC before we perform additional post-inliner opts").
  - High-level function passes are followed by a module round (dead function elimination, StackPromote). After "one last copy propagation/semantic arc opts run before serialization/lowering ownership", SIL is serialized and the mid-level pipeline runs.
  - The low-level pipeline is "the non-OSSA pipeline". Its TODOs, such as "remove this pass once we have OSSA throughout the pipeline", show that the move to OSSA everywhere is still in progress. [PassPipeline.cpp](https://raw.githubusercontent.com/swiftlang/swift/main/lib/SILOptimizer/PassManager/PassPipeline.cpp)

**Lean 4**
- In "Counting Immutable Beans" (Ullrich and de Moura, IFL 2019; arXiv v3 2020-03-05), the compiler from λpure to λRC runs in three steps: "(1) Inserting reset/reuse pairs (2) Inferring borrowed parameters (3) Inserting inc/dec instructions". The first two are optional for correctness. [arXiv 1908.05647](https://arxiv.org/pdf/1908.05647)
- Borrow inference is a whole-program heuristic over a signature map β: Const → {O, B}, solved over mutually recursive functions simultaneously. Two rules force a parameter to be owned:
  - It is owned if it is used by `reset`, because a borrowed value may be shared even when its count is 1.
  - It is owned if it appears in a tail call that needs ownership, so that tail calls are preserved. [same](https://arxiv.org/pdf/1908.05647)
- Inlining interacts with reuse: inlining `balance1` lets the input cell be reused in red-black tree insertion, and an alternative rewrite without inlining achieves the same. [same](https://arxiv.org/pdf/1908.05647)
- Values are tagged single-threaded or multi-threaded. Single-threaded RC uses no fences, and the tag is changed only before a value is shared. [same](https://arxiv.org/pdf/1908.05647)
- New compiler, Lean v4.30.0 (release notes dated 2026-05-26):
  - The C emission pass was ported from IR to LCNF (#12781), "the final step" of the IR/LCNF conversion.
  - Expand reset/reuse was ported to LCNF "with improved exponential-code prevention", giving "~15% decrease in binary size and slight speedups" (#12665).
  - User borrow annotations `(x : @&Ty)` are preserved by inference (#12830), and tail calls take priority over borrow annotations.
  - A `trace.Compiler.inferBorrow` option was added (#12810).
  - `ReaderT`'s context was marked borrowed "reducing reference-counting pressure across the metaprogramming stack" (#12942). [Lean v4.30.0 release notes](https://lean-lang.org/doc/reference/4.30.0/releases/v4.30.0/)

**Koka / Perceus**
- Reinking, Xie, de Moura and Leijen, MSR-TR-2020-42 (2020-11-22, v4 2021-06-07; PLDI 2021). Perceus emits precise dup/drop so that cycle-free programs are "garbage free". The implementation is "further extended with the optimizations described in Section 2" (reuse, drop specialization, reuse specialization). "the component transformations, including inlining and dup/drop fusion, are standard". [Perceus TR](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Perceus is "closely based on" Lean's algorithm, extending it with drop and reuse specialization. The authors "would like to integrate selective 'borrowing'". [same](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Results: on the red-black tree benchmark, "the purely functional Koka implementation is within 10% of the C++ performance". Fig. 9 reports relative time and peak working set against OCaml, Haskell, Swift, Java and C++ (AMD 3600XT, Ubuntu 20.04). [same](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)

**Lobster** (memory management doc, undated living doc)
- There is one owner per allocation, usually the first variable, field or vector element it is assigned to. Other uses are borrows. Where a parent wants ownership but a child only lends, the compiler inserts an increment instead of rejecting the program. [Lobster memory management](https://aardappel.github.io/lobster/memory_management.html)
- Ownership analysis is interleaved with type checking: functions are checked in call-graph order and specialized by type, and ownership is specialized the same way. Arguments that are assigned to are always owned, and return values are always owned. "Big functions, and all functions in a dynamic dispatch, get one specialization in which borrowable arguments borrow." [same](https://aardappel.github.io/lobster/memory_management.html)
- The analysis "remove[s] around 95% of runtime reference count operations". Cycles are not collected; a "cycle report" is printed at exit. [same](https://aardappel.github.io/lobster/memory_management.html)

### Inferences
- Consensus ordering: (a) inline, specialize and devirtualize first on a representation where ownership is either implicit (Lean, Koka) or explicit but freely re-canonicalizable (Swift OSSA). (b) Infer borrow signatures whole-program. (c) Insert or canonicalize RC. (d) Fuse and specialize RC operations. Swift repeats (c) after every inliner round because inlining exposes caller/callee dup-drop pairs that cancel.
- Lobster's specialization cap is the size-control mechanism. Each function gets at most one variant per distinct ownership vector, a large function gets one variant only, and a dispatch group gets one shared signature. Worst-case code growth is therefore bounded by the number of small functions times the number of ownership patterns actually used.
- For il2wasm, the dispatch-group rule matters most. After whole-program devirtualization, many virtual sites become monomorphic or small-polymorphic, so a dispatch group's single ownership signature can be computed only over the implementations that are actually reachable. That gives devirtualization a second payoff, in ownership precision.

### Gaps
- Nim (ARC/ORC): I did not retrieve sources on whether its move/sink analysis runs before or after inlining. Nim's `--mm:arc` injects destructors in the front end before C-compiler inlining. This is from memory and unverified.
- Lobster has no published measurement of the code-size cost of ownership specialization.
- I did not extract precise per-column numbers from the Lean beans benchmark table (base / -reuse / -borrow / -ST), because the PDF table parse was ambiguous.

## 3. Whole-program combined analyses: composing dataflow analyses, products, points-to scalability, and how Native Image and NativeAOT stay fast

### Takeaway
Combining analyses into one fixed point beats iterating separate passes (Click and Cooper; Lerner, Grove and Chambers). At whole-program scale, though, the production systems win by giving up precision where it does not pay. Graal Native Image's saturation (PLDI 2024) caps points-to sets at a threshold and turns quadratic growth into linear: a 300k-method app went from more than 9 minutes to 100 s. SkipFlow (CGO 2025) adds primitive values and branch predicates and cuts reachable methods by about 9% at no time cost. ASAP (2017) shows the opposite failure: fully precise whole-program static deallocation was exponential even on toy programs.

### Cited Findings
- Lerner, Grove and Chambers, "Composing dataflow analyses and transformations", POPL 2002, pp. 270–282. Analyses are written independently and combined automatically, with the components communicating implicitly through graph transformations. This avoids both the precision loss of iterating separate analyses and the burden of hand-written "super-analyses". [BibTeX record](https://bibtex.github.io/POPL-2002-LernerGC.html); [IBM Research page](https://research.ibm.com/publications/composing-dataflow-analyses-and-transformations). The abstract was reconstructed from excerpts; the full text was not read.
- Wimmer, Stancu, Kozak and Würthinger, "Scaling Type-Based Points-to Analysis with Saturation", PACMPL 8 (PLDI), Article 187, June 2024: [paper PDF](https://www.fit.vut.cz/research/publication-file/c189291/303462/3656417.pdf)
  - The analysis is context-insensitive and type-based (types, not allocation sites, are the heap abstraction).
  - Saturation removes pointer-assignment-graph nodes whose points-to set exceeds a threshold. Those fall back to declared-type (RTA-like) information, and a saturated virtual call becomes a single canonical saturated-invocation node.
  - Without saturation, memory is O(variables × types).
  - Threshold 0 is RTA. Threshold 1 already devirtualizes every single-exact-type site, and thresholds up to about 16 give "substantial increases in precision".
  - About 100k reachable methods take "about 30 seconds without saturation, and saturation reduces the analysis time only by about 25%". At 300k methods, "the analysis without saturation takes more than 9 minutes, while with saturation it takes only 100 seconds--a more than 5x difference and a linear growth".
  - Termination: the analysis is monotone, and saturation only shrinks the set of tracked variables.
- Kozak, Stancu, Vojnar and Wimmer, SkipFlow, CGO 2025 (arXiv 2025-01-31): [arXiv 2501.19150](https://arxiv.org/pdf/2501.19150)
  - SkipFlow tracks primitive values and adds predicate edges, so branches whose conditions are statically decided are not analyzed.
  - Reachable methods fall by an average of 6.3% (microservices), 8.4% (Renaissance) and 13.3% (DaCapo; Sunflow 52.3%, by removing AWT and Swing), about 9% overall.
  - "the average analysis time is even reduced by 1.6%". It ships in GraalVM for JDK 24 behind flags, with on-by-default planned for JDK 25 (per the search summary of the paper).
- Kozak et al., "Comparing Rapid Type Analysis with Points-To Analysis in GraalVM Native Image", MPLR 2023 (arXiv 2023-08-31). RTA cut analysis time by 40% and overall build time by 15%, at a cost of about 15% larger images on average. [arXiv 2308.16566](https://export.arxiv.org/pdf/2308.16566v1.pdf)
- NativeAOT / ILC architecture (dotnet/runtime BOTR `ilc-architecture.md`): [ILC architecture](https://raw.githubusercontent.com/dotnet/runtime/main/docs/design/coreclr/botr/ilc-architecture.md)
  - "The core concept driving the compilation in ILC is dependency analysis." The graph mixes object nodes (output artifacts) and abstract dependency nodes, with three kinds of edge:
    - static edges;
    - conditional edges ("depends on node B, but only if node C is part of the graph", for example vtable slots that exist only when the virtual method is used);
    - dynamic edges (rare, for generic virtual methods).
  - The optional IL scanner is "another compilation with a null/dummy code generation backend". Its graph is "a strict superset of the graph built by a real compilation" because it does not model inlining or devirtualization. Its results (for example, the set of implementers of an interface) let the real compilation devirtualize, and vtable slots "aren't stable until the compilation is done".
- ASAP (Proust, Cambridge UCAM-CL-TR-908, July 2017): [ASAP TR](https://www.cl.cam.ac.uk/techreports/UCAM-CL-TR-908.pdf)
  - ASAP is whole-program static deallocation with no RC or GC, built from Shape, Share and Access analyses and inserting CLEAN code at compile time.
  - Section 7.4: on generated "flat" programs, "The execution time of both the Shape and Access analyses appear exponential in the size of flat programs", while Share is not. "Deep" programs scale better.
  - Section 9.1.1 is titled "Asap's analyses: too precise for their own good". Even under linear types, ASAP detects aliasing that blocks deallocation.

### Inferences
- Design for il2wasm: one monotone worklist over the SSA IR whose lattice is a product of reachability (ILC-style conditional dependencies) × receiver types (saturating, threshold around 16) × ownership/escape state × thread-escape × constants. That is the Click-Cooper / Lerner-style combination, with saturation as the widening that keeps it linear. Each component's improvement feeds the others, for example a constant that kills a branch (SkipFlow) shrinks the receiver sets, which devirtualizes, which sharpens the borrow signatures.
- Use the ILC "scanner as superset" trick for ownership: compute ownership facts on the pre-inlining graph as a sound over-approximation, then re-canonicalize locally after inlining (the Swift pattern) instead of re-running the whole-program fixed point.
- ASAP is the cautionary bound: per-path heap shape precision is exponential. Lobster-style "pick one owner, insert an increment when unsure" plus type-based saturation is the scalable point in the design space.

### Gaps
- I did not retrieve Click and Cooper's "Combining Analyses, Combining Optimizations" (TOPLAS 1995) text or any numbers from it.
- I found no source here on MLton's whole-program scaling (defunctionalization, monomorphisation, compile times).
- No source was found for the title "Skipping the Analysis" (2024). The closest Native Image works are Saturation (PLDI 2024) and SkipFlow (CGO 2025).
- I found no measured ILC whole-program analysis time.

## 4. Interprocedural ownership/borrow inference with call graphs and virtual dispatch

### Takeaway
Every system surveyed gives a virtual or dynamic dispatch site one conservative ownership signature shared by all implementations: Lobster explicitly, Lean via partial-application wrappers. Precision therefore depends on resolving the dispatch first, which is exactly what closed-world type analysis (ILC scanner, Native Image saturation) provides.

### Cited Findings
- Lobster gives "all functions in a dynamic dispatch" one specialization with borrowable arguments borrowed. [Lobster](https://aardappel.github.io/lobster/memory_management.html)
- Lean: a constant with borrowed parameters that is partially applied is replaced by a fresh wrapper `c_O` whose parameters are all owned, because a closure may escape the borrow scope. [Counting Immutable Beans](https://arxiv.org/pdf/1908.05647)
- Native Image: with saturation threshold 1, every variable with one exact type gets static type checks and devirtualized calls. Saturated call sites keep one canonical invocation node. [Saturation PLDI 2024](https://www.fit.vut.cz/research/publication-file/c189291/303462/3656417.pdf)
- ILC: "if the compiler sees that there's only one type implementing an interface", interface calls become direct calls. Devirtualization is a policy, and types reachable from JIT code or reflection can opt out. [ILC architecture](https://raw.githubusercontent.com/dotnet/runtime/main/docs/design/coreclr/botr/ilc-architecture.md)
- Swift runs the devirtualizer right after the specializer, before the ARC and copy optimizations and the inliner, in every round. [PassPipeline.cpp](https://raw.githubusercontent.com/swiftlang/swift/main/lib/SILOptimizer/PassManager/PassPipeline.cpp)

### Inferences
- An ownership signature per dispatch group, computed as the meet (most-owned) of the reachable overrides' needs, makes the signature shrink as reachability shrinks. Running it inside the same fixed point as type propagation means an override becoming unreachable can relax a group from Owned to Borrowed. This is monotone if ownership demand only increases as implementations are added.
- Guarded devirtualization (.NET conditional escape) suggests a further option: a fast path specialized to borrowed arguments for the dominant receiver, plus a slow path using the group signature.

### Gaps
- I found no paper that formally combines borrow inference with class-hierarchy or points-to dispatch resolution in one fixed point. This appears to be a genuine gap in the literature.

## 5. Deterministic compiler output under parallel compilation

### Takeaway
The sources read here say little directly. ILC lists "Multi-threaded compilation" as a goal without describing a determinism mechanism.

### Cited Findings
- The ILC architecture doc gives multi-threaded compilation as a goal and does not describe how output order is fixed. [ILC architecture](https://raw.githubusercontent.com/dotnet/runtime/main/docs/design/coreclr/botr/ilc-architecture.md)
- The PEA paper suggests parallelizing its state iteration at control splits. [PEA](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)

### Inferences
- A monotone fixed point has a unique least solution independent of worklist order, so a parallel whole-program analysis is deterministic in its result if the lattice is monotone and saturation thresholds are applied per node rather than per arrival order. Saturation should be checked on the final set size, or the analysis should run to a deterministic schedule.
- Emission order (function indices, data layout) must be a sort over stable keys, not discovery order.

### Gaps
- I did not retrieve sources on reproducible-builds practice for parallel compilers (for example Roslyn `/deterministic`, LLVM parallel codegen, Bazel). This needs a separate search.

## 6. Recent work, 2022–2026 (Perceus line, Lean new compiler, Swift OSSA, MLIR bufferization, ASAP)

### Takeaway
The trend is to keep ownership explicit in SSA for as long as possible (Swift OSSA, Lean LCNF backend completed 2026-05) and to make the in-place/ownership decision a single monolithic analysis-plus-rewrite pass over SSA use-def chains (MLIR One-Shot Bufferize), with deallocation as a separate ownership-based pass.

### Cited Findings
- MLIR One-Shot Bufferize: [MLIR Bufferization docs](https://mlir.llvm.org/docs/Bufferization/)
  - It is "A whole-function at a time analysis" and "Monolithic": "A single MLIR pass does the entire work" (analysis and rewrite internally).
  - In-place versus copy is decided greedily on SSA use-def chains via read-after-write conflict detection (definition, conflicting write, read). When it detects a conflict, it "will insert a buffer copy for the conflicting write". Copies are "likely inserted if the SSA use-def chain splits".
  - Function boundaries are off by default. With `bufferize-function-boundaries`, "recursive calls are not supported".
  - Deallocation is done by separate ownership-based buffer deallocation passes. Bufferization runs "as one of the last steps in a pass pipeline".
- Lean new compiler: the LCNF backend was completed in v4.30.0 (2026-05-26), with reset/reuse on LCNF (~15% smaller binaries) and user borrow annotations. See Section 2. [Lean v4.30.0](https://lean-lang.org/doc/reference/4.30.0/releases/v4.30.0/)
- Swift: OSSA spans the high-level pipeline through serialization, and the low-level pipeline is still non-OSSA, with TODOs to extend OSSA there. [PassPipeline.cpp](https://raw.githubusercontent.com/swiftlang/swift/main/lib/SILOptimizer/PassManager/PassPipeline.cpp)
- ASAP (2017): fully static whole-program deallocation, with exponential Shape and Access analyses. See Section 3. [ASAP TR](https://www.cl.cam.ac.uk/techreports/UCAM-CL-TR-908.pdf)

### Inferences
- One-Shot Bufferize is the closest existing analogue to "one ownership analysis, not stacked passes". It analyzes everything first with alias and equivalence sets built incrementally, then rewrites once. Its known limits (greedy order, no recursion across function boundaries) are the places where il2wasm's SCC-based fixed point must go further.

### Gaps
- I did not retrieve Koka papers from 2022–2026 (for example "FP²: Fully in-Place Functional Programming", ICFP 2023, or frame-limited reuse work). They need a follow-up search.
- I did not retrieve Lean Zulip summaries about new-compiler compile-time or performance numbers beyond the release notes.

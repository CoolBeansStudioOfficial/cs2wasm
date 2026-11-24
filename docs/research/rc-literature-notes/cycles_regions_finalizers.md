# Cycles, regions, ownership proofs, finalizers and weak references without a tracing GC

Scope: approaches that could improve or validate il2wasm's design: compile-time RC, cycle-capable types found from the type graph, run-time "groups" with an owner chain and uncounted back references, and a cut subtree destroyed at the cutting store. Also the .NET semantics the design must keep: finalizers run once, short weak references clear before finalizers, ephemerons, resurrection.

Note on verification: this session fetched only some of the sources (fetched or search-verified this session: PEP 442, GCHandleType, DependentHandle, ConditionalWeakTable, Gay and Aiken abstracts, Hallenberg/Elsman/Tofte metadata, Parkinson/Clebsch/Wrigstad ISMM 2024 abstract, Sotoudeh 2025, Nim acyclic docs, the Vale/verdagon page on Gel, the Brownbridge/Salkild/Pepels history, the Cherem/Rugina abstracts). Items marked **[not fetched this session]** come from well-known publications. Their citation is the canonical DOI or URL, but their numbers and wording were not checked again here. The report writer should treat them as lower confidence.

---

## Region-based memory (Tofte-Talpin/MLKit, Cyclone, Gay-Aiken RC regions, OO region inference, Vale, Rust arenas, Verona/Reggio): how do regions handle a structure that lives across frames and is partly cut?

### Takeaway
Regions free memory in bulk at a region's end and cannot reclaim a cut-off part of a region that is still alive. Every production region system adds a second mechanism for that case: MLKit adds a copying GC, Gay and Aiken add per-region reference counts, Cherem and Rugina add individual frees, and Verona/Reggio adds nested subregions owned through one entry object. The closest match to il2wasm's "group" is a Reggio-style region whose single bridge reference is owned. The closest match to "a group nothing outside refers to goes whole" is a per-SCC or per-region count (Gay-Aiken, Parkinson et al. 2024).

### Cited Findings
- **Gay and Aiken, "Memory management with explicit regions" (PLDI 1998).** Regions are safe because each region keeps a reference count of pointers into it, and deleting a region with a nonzero count is checked. The abstract reports a safety overhead "from negligible to 17%", with regions still faster than malloc/free and GC on their benchmarks. — [Memory management with explicit regions (academia.edu copy)](https://www.academia.edu/175363944/Memory_management_with_explicit_regions)
- **Gay and Aiken, "Language support for regions" (PLDI 2001; ACM SIGPLAN Notices 36(5), 2001, pp. 70–80).** Type annotations for region structure (`sameregion`, `parentptr`, `traditional`) let the compiler skip the RC update when a pointer stays within a region or points to a parent region. Measured result: reference-counting overhead fell from a worst case of 27% to 11% on a suite of realistic benchmarks. — [Language Support and Compilation Techniques for Regions, UCB/CSD-00-1115](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2000/5340.html); [gay-regions.pdf (Cornell course copy)](https://www.cs.cornell.edu/courses/cs612/2000SP/papers/gay-regions.pdf). The 27%→11% figure comes from the abstract via search. Per-benchmark numbers were not verified.
  - The `parentptr` annotation is a statically checked "back reference up the region tree is uncounted". It is the direct ancestor of il2wasm's uncounted back references up an owner chain.
- **Gay and Aiken, "Memory Management with Use-Counted Regions" (UCB tech report, 2004)** continues the RC-regions line. — [EECS tech report 5216](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2004/5216.html)
- **Hallenberg, Elsman and Tofte, "Combining Region Inference and Garbage Collection" (PLDI '02, Berlin, 17–19 June 2002, pp. 141–152).** The paper extends Cheney copying collection to work inside regions. Region inference "very significantly reduces the number of garbage collections" (exact per-benchmark numbers not retrieved). The GC exists because pure region inference leaks space when a long-lived region accumulates dead data. — [ResearchGate entry](https://www.researchgate.net/publication/2525968_Combining_Region_Inference_and_Garbage_Collection); [Elsman papers list](https://elsman.com/papers)
- **Elsman, "Garbage-Collection Safety for Region-Based Type-Polymorphic Programs" (PLDI 2023).** A soundness problem in combining region inference with tracing GC was identified and fixed: dangling pointers that regions allow become unsafe once a tracer follows them. — [ACM DL 10.1145/3591229](https://dl.acm.org/doi/10.1145/3591229); [PDF](https://elsman.com/pdf/gcsafe-pldi23.pdf); see also [Elsman TLDI 2003, GC safety for region-based MM](https://elsman.com/pdf/tldi03.pdf)
- **Elsman and Hallenberg, "On the Effects of Integrating Region-Based Memory Management and Generational Garbage Collection in ML" (PADL 2020).** This is the later measured study of MLKit with generational GC (numbers not retrieved). — [Springer 10.1007/978-3-030-39197-3_7](https://dl.acm.org/doi/abs/10.1007/978-3-030-39197-3_7)
- **Tofte and Talpin, region inference (POPL 1994; Information and Computation 132(2), 1997)** and **Tofte, Birkedal, Elsman, Hallenberg, "A Retrospective on Region-Based Memory Management" (HOSC 17, 2004).** The retrospective discusses the space leaks of pure region inference (for example tail-recursive loops that accumulate in one region) and why the GC was added. — [Springer, A Retrospective on Region-Based Memory Management](https://link.springer.com/article/10.1023/B:LISP.0000029446.78563.a4)
- **Cyclone (Grossman, Morrisett, Jim, Hicks, Wang, Cheney, "Region-based memory management in Cyclone", PLDI 2002).** Cyclone has lexical regions, dynamic regions, a heap region and region subtyping ("outlives"), all checked statically. It has no cycle reclamation within a live region; a region is freed whole. — [Cyclone PLDI'02 slides (Grossman)](https://www.cs.washington.edu/homes/djg/slides/cyclone_pldi02.pdf). Later Cyclone work added unique and reference-counted pointers (Hicks et al., ISMM 2004, **[not fetched this session]**).
- **Cherem and Rugina, "Region analysis and transformation for Java programs" (ISMM 2004, pp. 85–96).** A flow-sensitive, interprocedural region liveness analysis rewrites Java programs to use regions. The authors report that for several benchmarks most data could be placed on the stack or in short-lived regions, giving substantial memory savings (numbers not retrieved). The follow-on system Jreg pairs a region compiler with a VM; regions are not lexically scoped, and dangling pointers are allowed if never followed. — [dblp: Sigmund Cherem](https://dblp.org/pers/c/Cherem:Sigmund.html); [Semantic Scholar author page](https://www.semanticscholar.org/author/Sigmund-Cherem/2637946)
- **Cherem and Rugina, "Compile-time deallocation of individual objects" (ISMM 2006).** This is the non-region follow-up: the compiler inserts frees for individual objects using a shape/uniqueness analysis of fields. — [ACM DL 10.1145/1133956.1133975](https://dl.acm.org/doi/abs/10.1145/1133956.1133975)
- **Chin, Craciun, Qin, Rinard, "Region inference for an object-oriented language" (PLDI 2004)** infers regions for a core Java with region subtyping; it is the OO counterpart of Tofte-Talpin. **[not fetched this session]** DOI 10.1145/996841.996871.
- **Verona / Reggio, OOPSLA 2023 (ACM DOI 10.1145/3622846, CC BY 4.0).** The PDF was downloaded, but its text could not be extracted in this session. From the literature, this is Arvidsson et al., "Reference Capabilities for Flexible Memory Management" **[not fetched this session, contents unverified]**. Its model is a forest of isolated regions, each entered through one owning "bridge" object, each with its own memory manager (arena, RC or tracing). A subregion is freed as a unit when its owning reference goes. — [MSR PDF](https://microsoft.com/en-us/research/uploads/prod/2023/10/oopslab23main-p377.pdf)
- **Vale regions (2020 design post).** Region isolation allows non-atomic RC; read-only regions and bump regions bring RC overhead to zero. At the time of the post these were still being designed, with no measurements. — [verdagon.dev, "RAII next steps", 15 July 2020](https://verdagon.dev/blog/raii-next-steps)
- **Rust arenas** (for example typed-arena or index-based arenas): cyclic graphs are stored as indices or `&'arena` references and freed only when the whole arena drops, the same limitation as other regions. **[not fetched this session]**
- **Wikipedia overview** of region-based memory management lists MLKit, Cyclone, RC regions and arenas. — [Region-based memory management](https://www.wikipedia.org/wiki/Memory_arena)

### Inferences
- No region system frees a cut-off cycle inside a live region at the cutting statement. They either wait for the region to die (Tofte-Talpin, Cyclone, arenas) or call a tracer (MLKit). il2wasm rules out a run-time walk and deferred frees, so pure regions cannot meet its rules for structures that are partly cut across frames.
- Two region ideas fit il2wasm's rules directly:
  - **Gay-Aiken's `parentptr`/`sameregion`** is a static proof that a pointer needs no count. It matches il2wasm's "back reference to the owner" proof and "store that goes down a structure" proof.
  - **Reggio's single owned entry per subregion** is a static form of "group with an owner chain". If a group can only be entered through its root, cutting the root's owning reference frees the whole group in work proportional to the group's own objects. Freeing those objects is unavoidable anyway, so the cost is bounded by the change, not by the living structure.
- MLKit's 2023 soundness fix is a warning. If il2wasm keeps uncounted back references, any code that follows them (finalizers, weak-reference reads, debuggers, reflection) must never see a freed owner. This is the same dangling-pointer-meets-traversal problem Elsman fixed.

### Gaps
- Per-benchmark numbers for MLKit regions plus GC (PLDI 2002 and PADL 2020) were not retrieved. Only the qualitative claim "very significantly reduces the number of GCs" was confirmed.
- The Reggio paper text could not be extracted (no PDF tool available), so its mechanisms and numbers are unverified.
- Chin et al. 2004 and Cyclone's unique/RC pointers were not fetched.

---

## Ownership types as compile-time proofs (Clarke/Potter/Noble, ownership domains, Boyapati, owners-as-dominators)

### Takeaway
Ownership types prove statically that every path from outside a structure to an internal object goes through its owner (owners-as-dominators). That is the proof il2wasm needs to treat a part as exclusive and an owner back reference as uncounted. Gel (2007) built a C# subset on such ownership and alias counting, without a GC, and is the closest predecessor of il2wasm.

### Cited Findings
- **Gel / GEL2** was "a subset of C#, extended with an ownership-based type system which allows GEL2 to free memory safely and deterministically without using a garbage collector". The description comes from discussion of the paper "Ownership You Can Count On" (Dingle et al.; originally a Google experimental language). — [digitalmars.D thread quoting the paper, Nov 2021](https://lists.puremagic.com/pipermail/digitalmars-d/2021-November/321376.html); [digitalmars archive](https://digitalmars.com/d/archives/digitalmars/D/Ownership_You_Can_Count_On_355291.html)
- Gel was "the first language to incorporate constraint references" (2007). The paper calls the technique "alias counting": an owner may be destroyed only when no non-owning references remain. This solves the RC cycle problem by "enforcing that there are no other references to an object when we let go of its owning reference". — [verdagon.dev, 15 July 2020](https://verdagon.dev/blog/raii-next-steps)
- In Vale's version of constraint references:
  - Assist mode halts the program when an object is freed while constraint references still point to it; a footnote describes an alternative that keeps the object alive until the last reference goes.
  - Release ("Fast") mode compiles them to raw pointers with no checks.
  - The "clasp" pattern makes two linked objects null out each other's pointer when destroyed.
  
  — [verdagon.dev](https://verdagon.dev/blog/raii-next-steps)
- A critic in the D thread calls GEL2 "a borrow counter for single ownership, limited to single threaded programs". — [digitalmars.D, Nov 2021](https://lists.puremagic.com/pipermail/digitalmars-d/2021-November/321378.html)
- **Clarke, Potter, Noble, "Ownership Types for Flexible Alias Protection" (OOPSLA 1998)** introduced owner parameters and the owners-as-dominators invariant. **[not fetched this session]** DOI 10.1145/286936.286947.
- **Aldrich and Chambers, "Ownership Domains: Separating Aliasing Policy from Mechanism" (ECOOP 2004)** replaces strict dominance with declared domain link permissions. **[not fetched this session]**
- **Boyapati, Salcianu, Beebee, Rinard, "Ownership Types for Safe Region-Based Memory Management in Real-Time Java" (PLDI 2003)** combines ownership with regions and threads. An object's region outlives the objects it owns, so freeing a region is safe without RC checks. **[not fetched this session]** DOI 10.1145/781131.781168.

### Inferences
- Owners-as-dominators is exactly the invariant behind "a subtree cut from a living structure is destroyed at the store that cuts it". If the owner dominates the subtree, cutting the owner's reference makes the whole subtree unreachable, and freeing it costs work proportional to the subtree (the change).
- Gel shows what happens when the proof fails at run time: another reference survives the owner. The options are to halt (Gel/Vale Assist mode) or to keep the object alive. il2wasm's "behaviour must not differ" rule rules out halting. So where ownership cannot be proved statically, il2wasm must fall back to counting. That fallback is exactly what the group machinery is for.
- Ownership domains (permitted cross-domain links) model il2wasm's accepted "outside references into a group" better than strict dominance does.

### Gaps
- Gel's measured results (from "Ownership You Can Count On") were not found; the Vale page gives none.
- No source found reports ownership-type inference (rather than annotation) on unannotated C#/Java at scale.

---

## Static detection of acyclic types; type-graph cycle-capability (CPython, Swift, Nim, Pony); proving back references go to an owner

### Takeaway
Production systems use a simple per-type "can this type take part in a cycle" test from the type graph, plus manual annotations (Nim `{.acyclic.}`, Swift `weak`/`unowned`) when the type test is too coarse. None of them proves automatically that a back reference points to its owner. That proof comes from shape analysis or ownership types, not from production compilers.

### Cited Findings
- **Nim:**
  - The `acyclic` pragma marks recursive object types as acyclic so the cycle collector ignores them. If the programmer marks a type acyclic that is in fact cyclic, memory may leak, but memory safety is preserved. — [Nim pragma acyclic docs](https://nim-docs.readthedocs.io/en/latest/manual/pragmas/pragmas/pragma_acyclic/)
  - The ORC announcement (8 Dec 2020) shows `Node {.acyclic.} = ref object` and says this annotation "can be crucial" for getting ORC's performance close to ARC's. — [ORC - Vorsprung durch Algorithmen](https://nim-lang.org/blog/2020/12/08/introducing-orc.html)
  - If static analysis of a ref's type shows that no cycle is possible through it, the collector never treats it as an edge. — [nimskull GC docs](https://nim-works.github.io/nimskull/gc.html)
  - `--mm:orc` produces more machine code than `--mm:arc`. — [Nim mm docs](https://nim-lang.org/docs/mm.html)
- **CPython:** only types with `Py_TPFLAGS_HAVE_GC` (types that can hold references) are tracked by the cycle collector; atomic types such as int and str are not. PEP 442 refers to the "GC header" present only on such objects. — [PEP 442](https://peps.python.org/pep-0442/). The flag details come from CPython's devguide **[not fetched this session]**.
- **Swift:** a cycle-free strong graph is the programmer's job. `weak` references are optional and become nil when the referent is deinitialized; `unowned` references assume the referent outlives them and trap on access after it is gone. **[not fetched this session]** The Swift Programming Language, "Automatic Reference Counting" chapter, https://docs.swift.org/swift-book/documentation/the-swift-programming-language/automaticreferencecounting/
- **Pony ORCA:** actor-local tracing plus deferred reference counting between actors. It is contrast only, because it traces. **[not fetched this session]**
- **"Cyclic reference counting by typed reference fields"** (Computer Languages, Systems & Structures, 2011) uses field types to decide which references can close cycles, a type-based variant of the cycle-capability test. — [ScienceDirect](https://www.sciencedirect.com/science/article/abs/pii/S1477842411000285)
- **Gay-Aiken `parentptr`** is a checked annotation that a pointer goes to a parent region. — [UCB/CSD-00-1115](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2000/5340.html)

### Inferences
- The il2wasm type-graph test is the same as Nim's and CPython's, but Nim shows its weakness: a tree type with no back edge is acyclic, yet the test reports it cyclic as soon as any field's static type can point back up (for example a `Node` with a `Node Parent`). Nim leaves this to annotations. il2wasm cannot (C# source has no annotation), so its "back reference to the owner" proof plays the role of Nim's `acyclic` pragma, made automatic. This is a novel requirement with no production precedent found.
- A sound parent-pointer proof must show three things:
  1. every store to `x.Parent` stores the object whose child field holds `x`;
  2. that child field is the only owning path to `x`;
  3. reads of `Parent` never happen after the parent is freed.
  
  This is a shape-analysis property (doubly-linked list and tree-with-parent invariants), not a type property.

### Gaps
- No automatic analysis found in production that proves "back reference is to the owner" for an unannotated OO language. The academic shape-analysis literature (for example TVLA, separation-logic list/tree predicates) was not surveyed in this session.

---

## Cycle handling without tracing (Gel destroy, weak/unowned, Brownbridge/Salkild/Pepels/Lins, SCC/group RC, Lobster): anything that frees a cut-off cycle at the cutting store in bounded work?

### Takeaway
General mutable graphs: proven impossible. Sotoudeh (2025) shows that any correct reachability-based collector that frees promptly must have pathological pauses, and the strong/weak (Brownbridge) line of algorithms has a long history of incorrect claims. Prompt, bounded freeing of a cut-off cycle is achievable only when static structure gives a spanning tree (ownership, owner chain), or when the cyclic structure is immutable (per-SCC counts, Parkinson et al. 2024). il2wasm's design sits in this feasible space, and its proofs are what keep it there.

### Cited Findings
- **Brownbridge (1985)** split references into strong and weak and counted only the strong ones, so the strong references form an acyclic graph. — [Loidl/Negeli seminar report, JKU Linz 2005 (German)](https://ssw.jku.at/Teaching/Lectures/Sem/2005/Reports/NegeliLoidl.pdf)
- **Salkild** showed Brownbridge's method "might free reachable memory". The date is given as 1985 in one source and 1987 in another. — [Sotoudeh, ACM 10.1145/3720430](https://dl.acm.org/doi/10.1145/3720430); [Loidl report](https://ssw.jku.at/Teaching/Lectures/Sem/2005/Reports/NegeliLoidl.pdf)
- **Thompson and Lins, "Cyclic reference counting: a correction to Brownbridge's algorithm"** (unpublished note, 1988) is cited in reference lists; its contents were not found. — [Cyclic Weighted Reference Counting without Delay (academia.edu)](https://www.academia.edu/9818102/Cyclic_Weighted_Reference_Counting_without_Delay)
- **Pepels, van Eekelen, Plasmeijer, "A Cyclic Reference Counting Algorithm and Its Proof"** (tech report, 1988) proved its scheme correct and terminating, without a tight running-time analysis. — [ACM 10.1145/3720430](https://dl.acm.org/doi/10.1145/3720430)
- **Sotoudeh, "Pathological Cases for a Class of Reachability-Based Garbage Collectors"** (arXiv 2504.11654v2, dated 17 Dec 2025; Stanford). — [arXiv HTML](https://arxiv.org/html/2504.11654v2); [ACM DL 10.1145/3720430](https://dl.acm.org/doi/10.1145/3720430)
  - Main result: "every GC algorithm that can implement a realistic mutator-observer interface has some pathological program". Formally, any correct collector has t_AR(n)·d(n) = Ω~(log^{3/2} n), so O(1) delay forces superlogarithmic pauses.
  - Sparse acyclic heaps: Ω(log n) pauses at O(1) delay.
  - Under the OMV conjecture the bound becomes Ω(n).
  - Brownbridge's claim of immediate cycle collection with O(1) pauses on acyclic heaps is inconsistent with the bound.
  - The paper gives a counterexample (Appendix B) where Pepels et al. take asymptotically more time than plain RC on acyclic heaps.
  - Its own upper-bound collector keeps a spanning forest in Euler-tour trees (from dynamic-connectivity work by Holm et al. and Kapron et al.), with delay 1 and O(log n) acyclic pauses. The author says it is not competitive for general use.
- **Parkinson, Clebsch, Wrigstad, "Reference Counting Deeply Immutable Data Structures with Cycles: an Intellectual Abstract"** (ISMM '24, Copenhagen, 25 June 2024, DOI 10.1145/3652024.3665507). — [MSR publication page](https://www.microsoft.com/en-us/research/publication/reference-counting-deeply-immutable-data-structures-with-cycles-an-intellectual-abstract/); [ISMM 2024 program](https://conf.researchr.org/details/ismm-2024/ismm-2024-papers/3/Reference-Counting-Deeply-Immutable-Data-Structures-with-Cycles); [preprint PDF](https://microsoft.com/en-us/research/uploads/prodnew/2024/05/preprint.pdf)
  - When a graph is frozen (made deeply immutable), its SCCs are computed once, in nearly linear time, using union-find. One reference count is kept per SCC rather than per object.
  - This gives precise reachability without a backup cycle detector. The alternatives, a tracer or a backup cycle detector, "give up prompt reclamation and deterministic memory usage".
  - It is designed for Verona and for immutable objects in Python.
- **Gel constraint references / destroy (2007):** the owner releases and the object is destroyed; any remaining non-owning reference is an error. — [verdagon.dev](https://verdagon.dev/blog/raii-next-steps)
- **Lobster:** the Vale post cites Lobster's compile-time ownership analysis as removing about 95% of RC operations. — [verdagon.dev](https://verdagon.dev/blog/raii-next-steps). Lobster's own memory-management doc says it does not collect cycles; leaked cycles are reported at program exit (**[not fetched this session]**, https://aardappel.github.io/lobster/memory_management.html).
- **Swift and Rust:** `weak`/`unowned` (Swift) and `Weak<T>` (Rust `Rc`/`Arc`) break cycles by hand. A cycle formed entirely of strong references leaks. **[not fetched this session]**
- **Lins's cyclic RC (lazy mark-scan, 1992)** and **Bacon-Rajan (ECOOP 2001)** do a local trial-deletion trace from a candidate whose count dropped above zero. That trace is a heap walk bounded by the subgraph reachable from the candidate, which can be the whole living structure. **[not fetched this session]** Related: [A reference-counting GC algorithm for cyclical functional programming (ISMM 2008)](https://dl.acm.org/doi/10.1145/1375634.1375645)

### Inferences
- Sotoudeh's bound is the key validation result for il2wasm. The requirement "free at the cutting statement, work bounded by what changed" cannot hold for arbitrary mutable graphs, whatever run-time scheme is used. So the design must rest on compile-time structure. The owner chain is a statically maintained spanning tree: Sotoudeh's upper bound maintains a spanning forest at run time with O(log n) cost, and il2wasm's group design does it with O(1) per store because the tree edges are fixed by type and store-site proofs.
- Where the proofs fail (a group admits a back reference that is not to its owner), any bounded-work rule must be conservative. It must either keep the group alive while any outside count is nonzero (Parkinson et al.'s per-SCC count, which leaks a cut sub-cycle until the whole group dies, conflicting with "nothing freed later than the statement") or do unbounded work. The literature offers no third option.
- Parkinson et al. is the nearest published "group RC" design. It differs from il2wasm in two ways that matter:
  - it requires immutability, so SCCs never split;
  - it computes the SCCs once at freeze time, with a walk.
  
  il2wasm's groups are mutable and split by stores. Splitting cheaply requires the owner-chain spanning tree, which Parkinson et al. do not need.
- The Brownbridge history shows that strong/weak schemes are easy to get wrong (Salkild's counterexample; the Pepels complexity counterexample). il2wasm's uncounted back references are a Brownbridge-style split, so they need a machine-checked or carefully tested proof that the uncounted edge never becomes the only path to a live object.

### Gaps
- No published design found that frees a cut-off mutable cycle at the cutting store with work bounded by the change and without static restrictions. Sotoudeh's bound suggests none can exist.
- The Lins and Bacon-Rajan measured numbers and Lobster's own doc were not fetched.
- The contents of the Thompson–Lins correction are unknown.

---

## Finalizers, weak references and ephemerons under RC (PEP 442, Swift, .NET, Java, Hayes) and pitfalls of emulating .NET with prompt freeing

### Takeaway
.NET fixes four ordering rules that a prompt-free runtime must reproduce:
- a short weak reference (`Weak`) is zeroed before the finalizer runs, even if the finalizer resurrects the object;
- `WeakTrackResurrection` survives resurrection;
- a `DependentHandle` acts like a weak reference to the target that holds a strong field to the dependent;
- a `ConditionalWeakTable` value never keeps its own key alive.

CPython's PEP 442 shows how to get these with RC plus a cycle detector: clear weak references first, run all finalizers, check again whether anything was resurrected, then break the cycle. The re-check step is a walk, which il2wasm forbids, so resurrection inside a group is the hardest case.

### Cited Findings
- **PEP 442, "Safe object finalization"** (Antoine Pitrou; created 18 May 2013; Python 3.4; Final). — [PEP 442](https://peps.python.org/pep-0442/)
  - Adds a `tp_finalize` slot and a "finalized" bit in the GC header that prevents double finalization.
  - Disposal by reference counting: the finalizer runs just before deallocation; if it resurrects the object, deallocation is aborted; an already-finalized object is not finalized again.
  - Disposal of a cyclic isolate, in order:
    1. weakrefs to the isolate's objects are cleared and their callbacks run, while the objects are still safe to use;
    2. the finalizers of all objects in the isolate are called (their order is undefined);
    3. the isolate is traversed again, and if any object is now reachable from outside, the collection is aborted and the whole isolate is resurrected;
    4. `tp_clear` breaks the internal references, which frees the objects.
  - The PEP notes that the GC "doesn't recalculate the CI after step 2", which is why step 3 exists.
  - Guarantee: "an object's finalizer is always called exactly once, even if it was resurrected afterwards."
- **.NET GCHandleType** (Microsoft Learn, ms.date 2025-07-01, updated 2026-05-27). — [GCHandleType Enum](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.gchandletype)
  - `Weak`: "When an object is collected, the contents of the GCHandle are zeroed. `Weak` references are zeroed before the finalizer runs, so even if the finalizer resurrects the object, the `Weak` reference is still zeroed."
  - `WeakTrackResurrection`: "the handle is not zeroed if the object is resurrected during finalization."
- **.NET DependentHandle** (.NET 6+). — [DependentHandle Struct](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.dependenthandle)
  - It "will conditionally keep a dependent object instance alive as long as a target object instance is alive as well, without representing a strong reference to the target instance."
  - "Conceptually equivalent to having a weak reference to a given target object instance A, with that object having a field or property (or some other strong reference) to a dependent object instance B."
  - Setting `Target` to null makes `Dependent` return null and become collectable.
  - `TargetAndDependent` reads both atomically.
  - The type is not thread-safe with respect to `Dispose()`.
- **.NET ConditionalWeakTable.** — [ConditionalWeakTable<TKey,TValue>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.conditionalweaktable-2)
  - "It does not persist keys."
  - Adding a pair "does not ensure that the key will persist, even if it can be reached directly from a value stored in the table". The page's example: key A with value V1, and key B whose value P2 references A.
  - The entry is removed "as soon as no other references to a key exist outside the table".
  - Keys are compared with `ReferenceEquals`; `GetHashCode` overrides are not used.
  - The page says nothing on thread safety or resurrection.
- **Java `java.lang.ref`:** objects pass through strongly → softly → weakly → (finalizable) → phantom reachable. Weak references are cleared atomically when the referent becomes weakly reachable, before it is finalized; phantom references are enqueued only after finalization; finalization was deprecated for removal (JEP 421, JDK 18). **[not fetched this session]** https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/ref/package-summary.html
- **Hayes, "Ephemerons: A New Finalization Mechanism" (OOPSLA 1997).** An ephemeron's value is traced only if its key is reachable by other means. This needs a fixpoint during tracing and is the semantics behind .NET's DependentHandle and CWT. **[not fetched this session]** DOI 10.1145/263698.263733.
- **Swift deinit:** runs synchronously when the strong count reaches zero; weak references read nil from that point; Swift has no resurrection. **[not fetched this session]**

### Inferences (pitfalls of emulating .NET with prompt freeing)
1. **Order across a group.** When a group dies whole, .NET requires every short weak handle to any object in it to read null before any finalizer in the group runs, including finalizers of other objects that can reach the first through fields. il2wasm must clear the weak handles for the whole group (PEP 442 step 1) before running any finalizer. The cost is proportional to the group's handles, which is within "work bounded by what changed".
2. **Resurrection needs a re-check, and PEP 442's re-check is a walk.** A finalizer may store `this` or a group member into a static, so the group becomes reachable again. Options that stay within il2wasm's rules:
   - (a) the resurrecting store itself is a counted store into the group; if the group's outside count is nonzero after all finalizers have run, abort the free (PEP 442 step 3 done by counting, not traversal);
   - (b) treat finalizable objects as group roots whose death is decided only by counts.
   
   Either way, a resurrected object must not be finalized again unless `GC.ReRegisterForFinalize` is called (CPython's finalized bit; .NET's finalization-queue rules).
3. **WeakTrackResurrection** must keep pointing at the object after finalization and resurrection. So a handle cannot be cleared just because the object's finalizer was scheduled. This needs two handle kinds and two clearing points.
4. **Ephemerons without tracing.** A CWT entry (key K, value V) means: V's count is held by K's lifetime, not by the table. If V references K (allowed: "even if it can be reached directly from a value"), a naive implementation stores a strong K reference inside V, which keeps K alive forever. The fix is to count the dependency as K → V (like DependentHandle's "A having a field to B"): free V when K dies, and do not count V's references back to K as outside references. This is exactly an owner back-reference problem, so il2wasm's group machinery must treat a CWT entry as a group edge from K to V.
5. **Finalizer order is unspecified in .NET**, and PEP 442 leaves it undefined too. Prompt freeing will produce a deterministic order (for example post-order down the owner chain). That is allowed, but programs that work by accident under CoreCLR's order may behave differently. Tests should accept any order.
6. **Finalizers running at the store.** Prompt freeing runs finalizers synchronously inside the cutting statement, possibly while locks are held or in the middle of an expression. .NET runs them on a separate finalizer thread. Reentrancy hazards (a finalizer touching the structure being mutated) come with prompt destruction; CPython and Swift accept them for deinit/`__del__` run by RC.
7. **Uncounted back references seen by finalizers.** A finalizer of a child may read `Parent`. If the group frees the parent first, this is a use-after-free. Freeing must run every finalizer in the group before any memory is released (PEP 442 steps 2 then 4).

### Gaps
- Microsoft's conceptual page on finalization order and the "long vs short weak reference" guidance page were not fetched; only the API reference pages were.
- The Java reference-type ordering and Hayes's paper were not fetched again here.
- No source found that implements .NET's full finalizer, short/long weak and ephemeron semantics on top of prompt RC without a tracer. CPython is the nearest, and it relies on a trace for cyclic isolates.

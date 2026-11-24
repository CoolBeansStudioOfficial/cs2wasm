# Precise reference counting, borrow inference and memory reuse in functional and functional-imperative compilers (Perceus/Koka, Lean 4, Roc/Morphic, Nim ARC/ORC), judged for il2wasm (mutable, cyclic, multithreaded CLR semantics, no GC)

Source dates are given in brackets. Unless marked "inference", every claim comes from the linked primary source. PDFs were read as full text with pdftotext. Researched 2026-11-23.

## 1. Perceus / Koka: precise "garbage-free" RC, drop specialization, reuse, borrowing, FBIP, frame-limited reuse, FP²

### Takeaway
Perceus's "garbage free" is a theorem about a cycle-free calculus with no mutable references. It holds only between dup/drop instructions. Reuse and borrowing break it on purpose: reuse is only "frame-limited", and borrowing is not even that. So "garbage free" lines up with il2wasm's rule (nothing freed later than the statement that makes it unreachable) only when no reuse token or borrow is outstanding. The mechanisms that carry over to a mutable cyclic heap are ownership passing, drop specialization, dup push-down and dup/drop fusion. Reuse carries over only in a weakened form, because CLR objects have identity and their memory cannot be swapped for another object of the same size.

### Cited Findings
**Sources:** Reinking, Xie, de Moura, Leijen, "Perceus: Garbage Free Reference Counting with Reuse", MSR-TR-2020-42 v4 [22 Nov 2020], extended version of the PLDI 2021 paper (DOI 10.1145/3453483.3454032) — [Perceus TR](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf). Lorenzen and Leijen, "Reference Counting with Frame Limited Reuse", MSR-TR-2021-30 v2 [15 Mar 2022], ICFP 2022 (PACMPL 6, ICFP, Article 103) — [FL-reuse TR](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf). Lorenzen, Leijen, Swierstra, "FP²: Fully in-Place Functional Programming", MSR-TR-2023-19 [8 May 2023] — [FP² TR](https://www.microsoft.com/en-us/research/uploads/prod/2023/05/fbip.pdf).

**Precision and ownership passing**
- Perceus is "precise: an object is freed as soon as no more references remain." It contrasts this with scope-based RC in C++ `shared_ptr`, Rust `Rc`, Nim (a `finally` block calls destroy) and, typically, Swift. Ownership is passed into callees: in `foo() { xs = list(..); ys = map(xs, inc); print(ys) }`, no drops are emitted in `foo`, `map` frees `xs` as it goes, and "For map, the memory usage is halved" — [Perceus TR §2.2](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Theorem 4 ("Perceus is precise and garbage free"): every heap object is reachable from the erased expression "for every intermediate state ... that is not at a dup/drop operation." Theorem 2 says the calculus (λ1) "does not model mutable references" and that "mutable references are indeed the only source of cycles." With mutable references the theorem would weaken to: each object is reachable, "or it is part of a cycle" — [Perceus TR §3](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- The calculus is linear-logic based. The app/let rule splits the owned environment Γ into Γ1 and Γ2. Γ2 is consumed by the later expression, so it can be *borrowed* while deriving the earlier one. This is like Wadler's `let!`, and it places a dup right before the use, not before the whole expression — [Perceus TR §3.2](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)

**Drop specialization, push-down and fusion**
- `drop(x) = if is-unique(x) then drop children; free(x) else decref(x)`. Drop specialization inlines this at a known constructor, but only when the children are used. Dups are then pushed into the branches, and dup/drop fusion removes matching pairs. "After this transformation, almost all reference count operations in the fast path are gone." In `map` over a unique list, the node is freed with no further RC operations — [Perceus TR §2.3](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)

**Reuse analysis and reuse specialization**
- Reuse analysis "analyses each match branch, and tries to pair each matched pattern to allocated constructors of the same size in the branch". When the matched object is dead, it emits `drop-reuse(xs)`, which returns a reuse token used by `Cons@ru(...)`. Reuse specialization then skips writes to fields that are unchanged — [Perceus TR §2.4–2.5](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Reuse is a run-time decision: the token is the object's own address if it is unique, and otherwise the children are dup'ed, the object is decref'd and fresh memory is allocated (the `reverse-acc` code in FP² §1.4.2) — [FP² TR](https://www.microsoft.com/en-us/research/uploads/prod/2023/05/fbip.pdf)

**Threads, mutation and cycles in Koka**
- Thread sharing is tracked precisely. Only two things can make an object thread-shared: passing it to a new thread, and storing it into a thread-shared mutable reference. Marking is recursive over the object and its children. The count of a thread-shared object is stored as a negative number, so a single inlined test (`rc <= 1`, a relaxed read) catches both the "shared" and the "free" slow paths. With atomic RC operations everywhere, the benchmarks slowed down by "from 5% (rbtree) up to 59% (nqueens)" — [Perceus TR §2.7.2, §4](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Mutable reference cells involve two counts (the cell's and the value's). A thread-shared ref has a race: one thread reads x and is suspended before its dup, another thread swaps the ref and drops y, and the dup then touches freed memory. The fix is a double-CAS or a lock on thread-shared refs only, with a check that avoids the atomic path "almost all of the time" — [Perceus TR §2.7.3](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Cycles: inductive and coinductive immutable types cannot be cyclic. The programmer breaks cycles through mutable refs by hand, "this strategy is also used by Swift". Future work is to track the types that can form cycles and run an incremental cycle collector — [Perceus TR §2.7.4](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Explicit control flow is a precondition. Effect handlers and exceptions are compiled to an internal core with explicit control flow (evidence translation) before Perceus runs. The conclusion notes this "is not zero-cost (like C++ exception handling)" — [Perceus TR §2.7.1, §6](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)

**Perceus measured results** (Koka 2.0.3 → C via gcc 9.3.0, custom mimalloc; 6-core AMD 3600XT 3.8 GHz, 64 GiB, Ubuntu 20.04; median times; compared with OCaml 4.08.1, GHC 8.6.5, Swift 5.3, Java SE 15.0.1 G1, C++ gcc 9.3.0) — [Perceus TR §4, Fig. 9](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- rbtree: purely functional Koka is "within 10% of the C++ performance" (C++ uses in-place std::map). Java was close in speed but used 1.7 GiB against Koka's 170 MiB. Koka "no-opt" (drop/reuse specialization and reuse analysis disabled) was "more than 2× slower".
- On benchmarks with lots of sharing (deriv, nqueens) the optimizations "are less effective".
- cfold: no-opt used 11% *less* memory, "because the reuse analysis essentially holds on to memory for later reuse".
- The paper does not report a share of count operations removed. Results are given only as times and peak working set.

**Frame-limited reuse (drop-guided reuse)**
- Both earlier reuse algorithms are flawed. Koka's "algorithm K" is "fragile with respect to small program tranformations". Lean's "algorithm D" "can lead to an arbitrary increase in peak memory usage". Both ran *before* RC insertion — [FL-reuse TR §3](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Drop-guided reuse runs *after* Perceus. It tracks the known size of each variable from match patterns, and when it meets a `drop(x)` whose size matches a later allocation it rewrites it to `dropru(x)`. This is simpler and robust to how the program is rearranged — [FL-reuse TR §3.2](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Definition: an evaluation is frame-limited if at every step the heap splits into H1 (all reachable) and H2 with |H2| ≤ c·|E|, where |E| is the depth of the evaluation context (the recursion depth). Drop-guided reuse is proven frame-limited (Theorem 4). A token can stay live across a recursive call, so the extra memory is bounded per stack frame, not by a constant — [FL-reuse TR §4.1, §5](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Borrowing "can increase the memory usage of a program by an arbitrary amount and it is generally not frame-limited". So "Koka currently has no automatic borrow inference and generally only uses borrowing for built-in primitives (like (big) integer operations)". Borrowing *is* frame-limited when the borrowed value's size is bounded by a constant (the side condition sizeof(Δ) ≤ c in the fl rules) — [FL-reuse TR §4.2, §5](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Results (32-core AMD 5950X 3.4 GHz, 32 GiB, Ubuntu 20.04): red-black tree insertion with drop-guided reuse and tail recursion modulo cons (TRMC) is "about 19% faster" than C++ std::map. A Schorr-Waite style FBIP `check` makes binarytrees 17% faster and "within 25%" of C++. An FBIP rbtree variant is "about 10% faster than the regular version" — [FL-reuse TR §1, §6–7](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)

**FP² / fully in-place (FIP)**
- A `fip` function is statically checked to live in a linear fragment: owned parameters and variables are used once, there are no general lambdas, and borrowed parameters (`^x`) can be shared but not consumed. Such functions run with no (de)allocation and constant stack space. `fbip` relaxes this to allow non-tail recursion and deallocation, and `fip(n)` / `fbip(n)` allow n allocations. Examples include splay trees, finger trees, merge sort, quicksort, and zippers that compile to Schorr-Waite pointer reversal — [FP² TR §1–2](https://www.microsoft.com/en-us/research/uploads/prod/2023/05/fbip.pdf)
- There are two ways to embed it. One is static uniqueness typing (Clean style), with the drawback of code duplication into unique and shared versions. The other is dynamic, using precise RC with an `is-unique` check at run time. Koka implements the dynamic one — [FP² TR §1.4](https://www.microsoft.com/en-us/research/uploads/prod/2023/05/fbip.pdf)

**Later Koka-family work**
- Leijen and Lorenzen, "Tail recursion modulo context: an equational approach (extended version)", J. Functional Programming [24 Oct 2025]. Seen only as a search-result listing and not read. It is about TRMC, not RC — (search result; no primary URL retrieved)
- Lam and Parreaux, "Being Lazy When it Counts: Practical Constant-Time Memory Management for Functional Programming" (HKUST) [preprint dated 2024-05-09]. It uses lazy RC (deferred freeing of children) with one uniform cell size to get constant-time alloc and free. It reports throughput "on par with Koka" with a 32-byte cell, slower on nqueens and rbtree-ck, faster on lam and life. Its proof requires an acyclic heap — [CTRC preprint](https://lptk.github.io/files/ctrc-2024-05-09.pdf)
- Parkinson, Clebsch, Wrigstad, "Reference Counting Deeply Immutable Data Structures with Cycles: an Intellectual Abstract", ISMM 2024 [25 Jun 2024], DOI 10.1145/3652024.3665507. When an object graph is frozen, its strongly connected components are computed once with an SCC algorithm plus union-find, and each component gets a single count. This gives precise reachability for immutable cyclic data with no backup cycle collector — [MSR page](https://www.microsoft.com/en-us/research/publication/reference-counting-deeply-immutable-data-structures-with-cycles-an-intellectual-abstract/)

### Inferences
- Relation to il2wasm's rule: Perceus with no reuse and no borrowing, on acyclic data, meets "nothing freed later than the statement that makes it unreachable" at statement granularity. Theorem 4 even allows dup/drop states to be transient. Reuse tokens break the rule, because an object freed by `drop-reuse` stays allocated until the paired constructor runs, possibly after a recursive call. Borrowing breaks it too: a borrowed argument stays alive in the caller until its own last use, possibly after the callee would have freed it. il2wasm can keep its rule only by restricting reuse to a token-to-allocation span with no intervening call, or by treating "the memory is reused, not freed" as acceptable. The second needs a user decision, because observable behaviour (finalizers, weak references) must not change.
- Holds under mutation, cycles and threads: ownership passing into callees, last-use moves, drop specialization (inlining the "unique, then free children" path at a statically known class), dup push-down into branches, and dup/drop fusion. All of these are local rewrites on the SSA def-use graph and need no immutability. Fusion is sound whenever no other thread can drop the object between the dup and the drop. Under compile-time thread-escape analysis (SPEC 4.5) this holds for non-escaping objects.
- Does not hold: Perceus's guarantee that only mutable refs form cycles, and the acyclicity proof behind garbage-freedom. Under CLR semantics any reference field is a mutable ref, so il2wasm's cycle groups are where Perceus says "or it is part of a cycle". Koka's run-time `is-unique` check before reuse is also a run-time shared check of the kind il2wasm forbids, so reuse must rest on *static* uniqueness (FP²'s static variant), not on Koka's dynamic one.
- Reuse under CLR semantics: Koka reuses a dead `Cons` cell's memory for a *different* `Cons` value. In .NET a new object with the same size and type is safe to place in a just-freed block only if the free is truly final: no weak reference, no finalizer, no `GetHashCode` / `RuntimeHelpers` identity or lock state that can be observed. The stronger form, in-place field update of the old object, is safe only when the compiler proves the old object is unique and dead, and when skipping the constructor's side effects leaves behaviour unchanged. In practice it is an allocator fast path: a free immediately followed by an allocation of the same size class can hand back the same block without going through the free list. This keeps behaviour identical and avoids the peak-memory problem.
- Koka's thread-shared sign-bit scheme is a *run-time* shared bit, which il2wasm's memory rules rule out. Koka's *static* half (only thread start and stores into thread-shared refs make an object shared) is the compile-time escape analysis il2wasm already uses.
- The Parkinson SCC idea is close to il2wasm's cycle groups: a group's count stands for an immutable SCC. It applies to CLR only for frozen graphs (for example `readonly`-only reachable sets or `static readonly` data); mutable cycles change their SCC structure.

### Gaps
- No Perceus or Koka paper reports the *share of count operations removed*. Only times and peak memory are given. I found no such statistic.
- The exact per-benchmark numbers in Perceus Fig. 9 did not come out cleanly from the PDF's two-column layout, so only the numbers stated in the prose are quoted.
- I did not find or read any 2024–2026 Koka RC paper beyond the TRMC JFP 2025 listing and the CTRC and ISMM 2024 work. Anton Lorenzen's later work on Koka (for example a thesis) was not found.
- The FP² TR was read. Its ICFP 2023 publication details were not confirmed here.

## 2. Lean 4 "Counting Immutable Beans": borrow inference, reset/reuse, destructive updates

### Takeaway
Lean's borrow inference is a monotone dataflow fixed point. It starts with every parameter borrowed and marks a parameter owned if it, or a projection of it, reaches reset/reuse, is passed to an owned parameter or a partial application, or would break a tail call. Mutually recursive functions are solved together as one SCC. In the 2026 compiler it has moved to the LCNF IR, records explicit "own reasons", and lets user annotations override the performance heuristics. Borrowing cut run time by a geometric mean of about 1.27/1.24 (2–3%) across the IFL'19 suite, and by up to 14–16% on binarytrees and deriv. Reuse mattered more, with a geometric mean of about 1.74/1.24.

### Cited Findings
**Source:** Ullrich and de Moura, "Counting Immutable Beans: Reference Counting Optimized for Purely Functional Programming", IFL 2019; arXiv v3 [5 Mar 2020] — [arXiv 1908.05647](https://arxiv.org/abs/1908.05647)

**Reset/reuse and borrowed references**
- Reset/reuse: `let y = reset x` behaves as follows. If x is unique, it eagerly decrements x's fields, overwrites them with a placeholder, and returns the cell. If x is shared, it decrements x and returns a special "box" value. `reuse y in ctor_i w` then either writes into the cell or allocates. In every control path, y is used exactly once, by a `reuse` or by `dec y` — [arXiv 1908.05647, §3, §5.1, Fig. 2](https://arxiv.org/abs/1908.05647)
- Insertion (function R, with helpers D and S): for each `case x`, look in each arm for the point after which x is dead, insert `w = reset x` there, and substitute a constructor of the same arity with `reuse w`. The result is that `map` and `swap` on unshared lists allocate nothing — [arXiv 1908.05647, §5.1, Fig. 3](https://arxiv.org/abs/1908.05647)
- Borrowed references "do not actually" hold a token. A borrowed reference "asserts that the value is kept alive by another, owned reference". The idea of explicit RC instructions and of borrowed references is taken from the Swift compiler — [arXiv 1908.05647, §2](https://arxiv.org/abs/1908.05647)

**Borrow inference and its fixed point**
- Correctness constraint: "we must never mark x as borrowed if it is used by a let y = reset x instruction", because a borrowed reference can be shared at run time even when its count is 1 — [arXiv 1908.05647, §5.2](https://arxiv.org/abs/1908.05647)
- Heuristic (`collectO`): a parameter should be owned if it or one of its projections is used in a reset, or is passed to a function that takes it owned. The second condition "is a heuristic and is not required for correctness", and is there because the callee may reuse the cell. Variable applications `x y` and constructor and partial applications default to owned. A partially applied constant with borrowed parameters gets an owned wrapper `c_O` — [arXiv 1908.05647, §5.2, Fig. 4](https://arxiv.org/abs/1908.05647)
- Fixed point: start with β(c) = Bⁿ (all borrowed), compute S = collectO(body), set β(c)_i := O for y_i ∈ S, and repeat until nothing changes. Mutual recursion: "a simple extension where we process a block of mutually recursive functions simultaneously" — [arXiv 1908.05647, §5.2](https://arxiv.org/abs/1908.05647)
- Tail-call refinement: if a borrowed parameter receives an owned argument at a tail call, a `dec` would have to follow the call and the tail call would be lost. The implementation therefore marks β(c)_i = O whenever c is tail-called with an owned x_i — [arXiv 1908.05647, §5.3](https://arxiv.org/abs/1908.05647)
- RC insertion: owned variables get an inc before each owned use except the last, and a dec after the last use if they are not consumed. Borrowed variables get an inc only when passed to an owned context — [arXiv 1908.05647, §5.3, Fig. 5](https://arxiv.org/abs/1908.05647)
- Destructive updates on arrays and strings: `Array.write a i v` updates in place if `a` is unshared, which is the classic RC optimization. FBIP-style data-structure coding (for example red-black tree insert) allocates only one node when the input is unshared — [arXiv 1908.05647, §6](https://arxiv.org/abs/1908.05647)

**Measured results** (Lean 4 → C, Clang 9; i7-3770, 16 GB, Ubuntu 18.04; mean of 50 runs via temci). Fig. 6 gives run time normalized to the base run (rbmap_* normalized to rbmap); columns are base / −reuse / −borrow / −ST (−ST = atomic RC for all values) — [arXiv 1908.05647, §7, Fig. 6](https://arxiv.org/abs/1908.05647)
- Geometric mean: 1.24 / 1.74 / 1.27 / 1.89.
- −reuse per benchmark: binarytrees 0.98, const_fold 1.64, rbmap 3.23, rbmap_10 3.62 (base 1.49), rbmap_1 5.42 (base 4.72), unionfind 1.41; deriv, parser and qsort 1.00.
- −borrow / −ST as I read them from the linearized text (see Gaps): binarytrees 1.14/1.22, deriv 1.16/1.42, const_fold 0.90/1.23, parser 1.00/1.68, qsort 1.00/1.13, rbmap 1.07/1.71, rbmap_10 1.52/2.43, rbmap_1 4.47/8.02, unionfind 1.00/2.31. The text confirms "The borrowed inference heuristic provides significant speedups in benchmarks binarytrees and deriv", and that reuse "significantly improve[s] performance" on const_fold, rbmap and unionfind.
- Compared with other compilers: Lean is "5x as fast as OCaml on const_fold". Lean spends 17% of run time deallocating there, while OCaml spends 90% in GC. The authors' conjecture that reuse helps mainly through fewer cache misses was *refuted*: only rbmap shows significantly fewer cache misses — [arXiv 1908.05647, §7, Fig. 7](https://arxiv.org/abs/1908.05647)

**Lean 4 compiler state in 2026 (LCNF)**
- Borrow inference now runs in `src/Lean/Compiler/LCNF/InferBorrow.lean`, per SCC ("Infer the borrowing annotations in a SCC through dataflow analysis"). Its `loop` repeats `step` (collect over all decls in the SCC) while `modified`, starting from every parameter borrowed. The rules for marking a parameter owned are split by purpose:
  - Correctness: preserve tail calls to recursive functions *and join points*, and keep values subject to reset-reuse owned.
  - Performance: propagate owned parameters of callees, own all arguments of a partial application, and own parameters packed into a constructor.
  - User borrow annotations block the performance reasons, but not the forced ones (reset-reuse, tail-call preservation).
  - The `OwnReason` enum also covers forward and backward propagation through `oproj`, call results, runtime-allocated literals, calls to a free variable, and `@[export]` annotations.
  - Latest commits: [2026-03-27] "perf: tagged values can be interpreted as borrowed (#13152)"; [2026-11-05] "fix: do not borrow join point parameters that receive allocated literals (#16276)"
  - [InferBorrow.lean](https://github.com/leanprover/lean4/blob/master/src/Lean/Compiler/LCNF/InferBorrow.lean)
- `PropagateBorrow.lean` (Henrik Böving, ©2026) pushes user-provided borrows forward over a bot/borrow/own/top lattice. Its purpose is that reset-reuse is not inserted on values the user asked to borrow — [PropagateBorrow.lean](https://github.com/leanprover/lean4/blob/master/src/Lean/Compiler/LCNF/PropagateBorrow.lean)

### Inferences
- The fixed point is monotone (B to O only, over a finite set), so it ends in at most (number of parameters in the SCC) rounds. It is essentially a greatest-borrowed-set solution. Nothing in it needs immutability: "owned if reaching a consuming use" is a plain dataflow fact on SSA. What *does* need immutability is that a borrowed parameter is safe at all. In Lean nobody can overwrite the caller's reference while the callee runs. In C#, a borrowed parameter `p` that was read from a field `o.f` can be freed during the call if the callee, or another thread, does `o.f = null`. Borrow inference for il2wasm therefore needs an extra condition, the one Lobster and Swift's OSSA also face: the caller's owner must be a local SSA value, or a field proven not to be written during the call (an interprocedural mod analysis plus thread-escape analysis).
- Lean's rule "never borrow a value that reaches reset" is the same as il2wasm's need for a static uniqueness proof before in-place reuse.
- Run-time vs compile-time: Lean's reset branches on the count at run time (`isShared`). Under il2wasm's ban on run-time shared checks, reset/reuse becomes useful only where uniqueness is static. Where it is, the analysis is a dead-after-match point paired with a same-size allocation, the same as algorithm D, and it inherits algorithm D's peak-memory problem unless it is restricted the way drop-guided reuse is.
- Borrowing gave little in Lean (a few percent geometric mean) compared with reuse (about 40% geometric mean slowdown without it). For a language where most count operations come from parameter passing of objects with long-lived owners, the ratio may be different. That is not tested here.

### Gaps
- The −borrow and −ST per-benchmark pairs were read from text in which the table cells were linearized. The pairing is consistent with the prose, but should be checked against the PDF table.
- Lean does not report the share of count operations removed.
- I did not find a published evaluation of the 2025–2026 LCNF borrow inference.

## 3. Roc and Morphic (alias analysis for in-place mutation; lambda set specialization); Roc's borrow inference

### Takeaway
Morphic is a whole-program alias and uniqueness analysis that lets Roc update in place without checking the count. In Roc's own measurements it gave no measurable gains, except about 3× on a synthetic hot `List.set` loop. It has been run in a "trivial" mode since a loop and recursion bug, and in January 2025 the core developers favoured removing it. Roc, like Lean, relies on cheap run-time `rc == 1` checks.

### Cited Findings
- Roc Zulip, "remove morphic?" thread [24–25 Jan 2025; continued Nov 2026] — [Roc Zulip](https://roc-lang.github.io/zulip-export/stream/395097-compiler-development/topic/remove.20morphic.3F.html):
  - Purpose: Morphic analyses uniqueness to mutate in place without a refcount check, and may remove some refcount operations (Brendan Hansknecht). Ayaz Hafiz: "morphic is about specializing borrows". Lambda sets are a separate technique from the same research, and Roc relies on them.
  - Measured: a code comment says that disabling the analysis made NQueens about 5% slower, while cFold and deriv were about 1.5% faster and rBTreeCk about 4% faster. Best case, a hot loop of `List.set`: 68.6 ms with refcount checks against 21.9 ms without (about 3.13×). Brendan: checking rc == 1 is "generally pretty cheap", so the benefit is unclear outside hot loops.
  - Bugs: Morphic was switched to `solve_trivial` to avoid a bug with loops (TODO #7367). Ayaz: "definitely bugs in the ir generation".
  - Decision: in Jan 2025 Richard Feldman favoured disabling or removing it before 0.1.0, and Ayaz and Brendan agreed. Removal is manual work because Morphic is tied into the LLVM backend and generates extra specializations. Sam Mohr: lambda sets will not exist in the new pipeline.
  - In Nov 2026 Feldman said the new compiler uses a replacement designed by the Morphic authors ("a more powerful alternative"). The same thread notes that `List.update` lacked the in-place eligibility check that `List.set` has (fixed in PR #13090 [16 Nov 2026]).

### Inferences
- Morphic's result is a cautionary data point for il2wasm. When the run-time uniqueness check is cheap and well predicted, a static uniqueness proof gains little. It helps where the check sits inside a hot loop or blocks vectorization. Under il2wasm's rules no run-time check exists at all, so static uniqueness is the *only* way to get in-place reuse. The trade-off Roc measured (proof vs check) does not apply. The relevant number is what fraction of potential reuse sites the static analysis can prove.
- Morphic's borrow specialization (cloning a function per caller borrow or own pattern) is an alternative to Lean's single-ABI inference. It costs code size, which matters for il2wasm's binary-size goal.

### Gaps
- I did not retrieve the Morphic paper itself (Brandon, Sullivan et al., UC Berkeley, also "Better Defunctionalization through Lambda Set Specialization", PLDI 2023 per general knowledge, not verified here) or Roc's borrow inference source. Precise algorithms and published numbers for Morphic are therefore missing.
- The talks by Folkert de Vries and Richard Feldman were not reviewed.

## 4. Nim ARC/ORC: move semantics, destructors, sink/lent, cursor inference, ORC cycle collector

### Takeaway
Nim ARC is scope-based, not precise: destructors run at scope exit (`try/finally`). The Perceus paper uses this as its counterexample. Its count reductions come from last-use moves (`=sink` + `=wasMoved`), sink parameters, `lent` borrowed returns, and "cursor inference", a compile-time copy-on-write check that turns `dest = src` into a non-owning alias when neither side is mutated afterward. That check works with mutation because it is conditioned on proving no mutation. ORC adds a run-time cycle collector, which is the contrast case for il2wasm.

### Cited Findings
- Nim manual, "Nim Destructors and Move Semantics" [current docs, undated] — [Nim destructors](https://nim-lang.org/docs/destructors.html):
  - Hooks are `=destroy`, `=wasMoved`, `=sink`, `=copy`, `=dup` and `=trace`. A move happens on `lastReadOf(x)` found by control-flow analysis. `ensureMove` makes a failure to prove the move a compile-time error.
  - Rewrite rules: destroy-var (`try stmts finally: =destroy(x)`), move-optimization (`=sink(x,z); =wasMoved(z)`), copy-to-sink (`=dup` temporary), move-to-sink, and self-assignment removal. "The current implementation destroys resources at scope exit."
  - `sink` parameters are affine (consumed once or not at all). Fields of objects and tuples are tracked separately. Sink *inference* is opt-in (`--sinkInference:on`), and "The details of the inference algorithm are currently undocumented."
  - `lent T` is "like var T a hidden pointer" whose lifetime is proven not to outlive its origin. No destructor is injected for `lent` or `var` expressions.
  - Cursor inference: in `dest = src`, a real copy is needed only if dest or src is mutated afterward. For example, `let v = tab["key"]` becomes a cursor when `tab` is not mutated.
  - The `{.cursor.}` pragma makes a field or variable a raw non-owning pointer with no run-time checks ("not C++'s weak_ptr"). It is used to break cycles (for example `right {.cursor.}: Node`) and to iterate linked structures without count operations.
  - `--mm:orc` "ships with a cycle collector". `=trace` is needed for custom containers.
- Yarantsev, "Introduction to ARC/ORC in Nim", Nim blog [15 Oct 2020]: ORC is ARC plus a cycle collector with a local tracing phase and an adaptive collection threshold. The post contains **no benchmark numbers** — [Nim blog](https://nim-lang.org/blog/2020/10/15/introduction-to-arc-orc-in-nim.html)
- Perceus cites Nim as scope-based RC (destroy in `finally`) that retains memory longer than precise RC — [Perceus TR §2.2](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)

### Inferences
- Cursor inference is the one technique in this family stated directly for a *mutable* heap. Its precondition is "no mutation of src's container or of dest during dest's lifetime". In C# that is an interprocedural mod/ref question, plus "no other thread writes", which il2wasm's thread-escape analysis can supply. It is a borrow on a local whose owner is a heap path, exactly the case Lean's borrow inference does not need to consider. Nim's `.cursor` is unchecked, and il2wasm cannot have it since behaviour must not differ. Inferred cursors with a proof are fine.
- Scope-based destruction conflicts with il2wasm's "nothing freed later than the statement that makes it unreachable". Nim's move and sink rules carry over; its destroy placement does not.
- ORC's trial deletion is a run-time heap walk, which il2wasm's design rules out.

### Gaps
- I found no published measurement of ARC cursor inference: no share of copies or count operations removed, and no speedup. Searches returned nothing relevant. Araq's forum and RFC posts (nim-lang/RFCs on destructors and cursor inference) were not retrieved.
- The ORC algorithm details (Bacon-Rajan style trial deletion, the `acyclic` pragma) are not covered by the sources read here.

## 5. Swift Hylo/Val (only as far as reuse); and which techniques survive a mutable, aliased, cyclic, multithreaded heap (CLR)

### Takeaway
No Hylo source was retrieved. Across the sources read, every count *elision* technique (ownership passing, last-use moves, drop specialization, dup/drop fusion, borrow inference, cursor inference) is a dataflow rewrite that stays sound on a mutable heap given two extra proofs: the owner is not overwritten during a borrow, and the object does not escape to another thread. What depends on immutability is the guarantee side: Perceus garbage-freedom (acyclic, no mutable refs), Koka and Lean reuse built on fresh values with no identity, and FIP's linearity checks. Memory reuse under CLR semantics is safe only as an allocator-level pairing of a final free with a same-size allocation, or as in-place update of a statically unique dead object whose identity cannot be observed.

### Cited Findings
- Perceus states its static guarantees depend on language properties: explicit control flow, immutable inductive types (so only mutable refs form cycles), and precise thread-sharing points. It contrasts this with Swift, where "almost all reference count operations need to assume that references are potentially thread-shared". It attributes Swift's weaker RC results partly to static guarantees that "are not strong enough" — [Perceus TR §2.7, §4](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf)
- Reuse "holds on to memory for later reuse" (11% higher memory on cfold). Lean's reuse algorithm can raise peak memory arbitrarily, and drop-guided reuse is bounded only per frame — [Perceus TR §4](https://www.microsoft.com/en-us/research/uploads/prod/2020/11/perceus-tr-v4.pdf); [FL-reuse TR §3–4](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Borrowing on a unique argument keeps the whole input alive while the output is built, "generally not frame-limited" — [FL-reuse TR §4.2](https://www.microsoft.com/en-us/research/uploads/prod/2021/11/flreuse-tr.pdf)
- Static uniqueness (Clean-style typing) is a recognised alternative to the run-time `is-unique` check, at the cost of duplicating code into unique and shared versions — [FP² TR §1.4.1](https://www.microsoft.com/en-us/research/uploads/prod/2023/05/fbip.pdf)
- Deeply immutable cyclic graphs can be counted per SCC with no cycle collector — [Parkinson et al., ISMM 2024](https://www.microsoft.com/en-us/research/publication/reference-counting-deeply-immutable-data-structures-with-cycles-an-intellectual-abstract/)

### Inferences (applicability matrix for il2wasm)
- **Holds on a mutable, cyclic, threaded heap (with compile-time proofs il2wasm already has or can add):**
  - Ownership transfer into calls and last-use moves (Perceus, Nim sink).
  - Drop specialization at a known class, which inlines the unique-free path. Under il2wasm's static ownership the "unique" branch is known at compile time, so it becomes a direct free with a recursive release of the children.
  - dup push-down and dup/drop fusion within a thread-local region.
  - Borrow inference as Lean's monotone SCC fixed point, *plus* a no-overwrite-of-owner condition for heap-path owners.
  - Cursor inference (Nim), with the mutation-freedom proof made rather than assumed.
  - Tail-call-preserving ownership (Lean's rule) is independent of mutability.
- **Depends on immutability or acyclicity:**
  - Garbage-freedom proofs (Perceus Theorems 2 and 4), which exclude mutable refs and cycles.
  - Reuse as "same-size constructor in a match arm": CLR objects have identity, may have finalizers and weak references, and their fields may be aliased through interior pointers (`ref` locals, spans).
  - FIP, which needs linear use of owned values.
  - Lean's borrow safety argument, which assumes the caller's reference cannot be overwritten.
  - Parkinson's per-SCC count, which needs a frozen graph.
- **Depends on run-time checks il2wasm forbids:** Koka and Lean `is-unique` / `isShared` reuse and in-place array updates, Koka's negative-count thread-shared bit, Nim ORC trial deletion, and lazy RC (CTRC) deferred frees (which also break "no deferred frees").
- **A CLR-safe form of reuse:** (a) *Free-then-allocate fusion*: when SSA shows a final release of an object of size class S followed, with no intervening call or observable event, by an allocation of size class S, pass the block directly. Behaviour is unchanged, because the old object was unreachable, had no finalizer, and had no live weak reference. Peak memory does not grow, because the block is never held across a call. (b) *In-place update of a statically unique, dead, non-escaping object* of the same exact type, when the constructor being replaced is the compiler-known field-initialising kind (record `with` expressions, immutable-collection builders, struct-like classes). This needs a proof that no `GetHashCode` / identity, lock, weak reference or finalizer state can be observed. (c) *Destructive update for collections* (`ImmutableArray.SetItem`, `string` concatenation chains, `List<T>` copies) when the source is statically unique and dead. This is FBIP applied to BCL types, and it changes no observable behaviour because the old value is unreachable.

### Gaps
- Hylo/Val (mutable value semantics, `inout`/`sink`/`let` parameter conventions) was not researched for lack of tool budget. No claims about its reuse are made here.
- No source in this family measures count operations removed as a share of the total. The closest figures are run-time ratios (Lean −borrow geometric mean ≈1.27/1.24; Koka no-opt "more than 2×" slower on rbtree).
- Swift OSSA and Lobster are outside this assignment's sources and are not covered here.

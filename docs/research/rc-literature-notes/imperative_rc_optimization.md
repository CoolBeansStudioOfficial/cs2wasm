# Reference-count optimization in imperative and object-oriented compilers and runtimes

Scope: how compilers and runtimes for mutable OO languages remove or cheapen count operations, and what share of operations each technique removes. The reader's setting is il2wasm: compile-time RC over SSA, with no deferral, no run-time "is shared" checks, and .NET-identical behaviour, including finalizers and weak references. Notes collected 2026-11-23. Each source carries its own date. "Not fetched" marks claims whose primary text was not read in this pass.

## Swift ARC and Ownership SSA (OSSA): representation, passes, conventions, lexical lifetimes

### Takeaway
OSSA makes every non-trivial SSA value either Owned or Guaranteed. An Owned value is consumed exactly once on every path. A Guaranteed value is a borrow scope nested inside a live base. Under this representation, `copy_value` and `destroy_value` are the only count operations, and the verifier checks their pairing. Two things removed the most traffic. One was switching the default parameter convention to +0 (guaranteed): up to 5% average code-size gain, preliminary, 2018. The other was ordinary copy propagation over OSSA. Swift then had to add "lexical lifetimes" (Swift 5.7, 2022), because shortening lifetimes to the last use broke weak references, unsafe pointers and deinit side effects. That lesson maps directly onto .NET finalizers and WeakReference.

### Cited Findings
**OSSA representation** (swiftlang/swift `docs/SIL/Ownership.md`, current main, read 2026-11):
- Each value has one constant ownership kind. Each operand is either non-lifetime-ending or lifetime-ending. Along reachable paths, a value needs exactly one lifetime-ending use, ignoring dead-end blocks. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- The ownership lattice is None (trivial types and addresses), Owned, Guaranteed, Unowned and Any. Owned joined with Guaranteed gives None. A value never has kind Any; operands may accept Any. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Owned** values are move-only. Each is consumed exactly once per path, by `destroy_value` or by a consuming use (apply, `store [init]`, casts, forwarding terminators). Leaks and double consumes are verifier errors. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Guaranteed** values are scoped and depend on an enclosing owned or guaranteed base.
  - `begin_borrow` or `load_borrow` opens the scope; `end_borrow` closes it.
  - The base must stay live across the whole scope.
  - `end_borrow` prevents optimizations from shortening the base's lifetime.
  - Source: [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Unowned** values are valid only for an instant and must be copied before use in an owned or guaranteed context. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Count operations:** `copy_value` produces a new owned value and `destroy_value` consumes one. "ARC effects apply only to the specific value", so RC optimization becomes local def-use reasoning instead of alias reasoning. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Forwarding instructions** pass ownership through without a count:
  - Over an owned operand (for example `unchecked_ref_cast`), the instruction ends the operand's lifetime and yields an owned result.
  - Over a guaranteed operand (for example `struct_extract`), it implicitly opens a borrow, and uses of the result are validated as uses of the operand.
  - `struct_extract` cannot forward owned ownership, because that would partially consume the struct. Consuming a field needs `destructure_struct`, which consumes the whole aggregate.
  - Source: [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Interior pointers** (`ref_element_addr`, `project_box`, `ref_tail_addr` and others) require a guaranteed base. The verifier treats transitive uses of the interior address as uses of the base object. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Dead-end blocks** (post-dominated by `unreachable`) need no lifetime end. The obligation applies only on paths that `return` or `throw`. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Function arguments:**
  - An `@guaranteed` argument is borrowed for the duration of the call; consuming it is a diagnostic error.
  - An `@owned` argument is consumed by the callee.
  - Inlining adds a lexical borrow scope for each guaranteed argument and a lexical move for each owned argument, unless the passed value is already lexical.
  - Source: [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- **Move-only types:** `$@moveOnly T` wrappers may be copied in Raw SIL, but the copies must be gone before Canonical SIL. `MoveOnlyObjectChecker` and `MoveOnlyAddressChecker` diagnose consuming uses of borrowed values. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)

**Three lifetime classes** (same doc):
- **Strict** (`~Copyable`): deinit runs at the consume point or at scope end, in reverse declaration order.
- **Lexical** (non-trivial Copyable types that are not eager-move): the lifetime is anchored to the scope and cannot be moved across deinit barriers.
- **Optimized / eager-move**: the lifetime may end right after the last use. This is inferred when all fields are eager-move, for example the standard `String`, `Array`, `Set` and `Dictionary`.
- Lexical markers are `begin_borrow [lexical]`, `move_value [lexical]`, lexical function arguments and `alloc_stack [lexical]`. Optimizers must check `isLexical` before changing a lifetime.
- Source: [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)
- Deinit barriers are synchronization points, weak or unowned loads, pointer accesses, and exclusive accesses to globals or class members. Destroys of lexical values cannot be reordered across them. — [Swift Ownership.md](https://raw.githubusercontent.com/swiftlang/swift/main/docs/SIL/Ownership.md)

**Lexical lifetimes: motivation and rules** (Andrew Trick, "Swift variable lifetimes", gist filename `2022-variable-lifetime-proposal.md`, last active 2024-05-02):
- **What broke:** Swift 5.6 optimizations shortened lifetimes to the last use. As a result:
  - code unwrapping a weak reference crashed, because the object had been released early;
  - unsafe pointers into objects dangled;
  - deinitializers with side effects (for example closing a file handle) ran early.
  - Sprinkling `withExtendedLifetime` everywhere made code unreadable.
  - Source: [Trick gist](https://gist.github.com/atrick/cc03c4d07fb0a7bee92c223ae5e5695b)
- **Rule:** a variable's deinit is anchored to the end of its lexical scope but may run earlier. It is not ordered with other code unless a barrier forces ordering. Optimization must keep the deinit after the anchor when any of the following occurs after the variable's last use:
  1. access to a weak or unowned reference to an object kept alive by the variable;
  2. access to an unsafe pointer into such an object;
  3. a synchronization point: async calls, calls to externally defined or opaque functions, or access to a global variable.
  - Source: [Trick gist](https://gist.github.com/atrick/cc03c4d07fb0a7bee92c223ae5e5695b)
- **Still allowed:**
  - Moving a deinit earlier when no barrier follows the last use. Deinit side effects and traps are unordered relative to ordinary code.
  - Eager-move lifetimes for value types and the standard copy-on-write (CoW) collections.
  - Generic code is treated conservatively as holding references, but specialization can recover value semantics.
  - Noncopyable types with a `deinit` get strict lifetimes, and their deinits run in source order.
  - Source: [Trick gist](https://gist.github.com/atrick/cc03c4d07fb0a7bee92c223ae5e5695b)
- **Rejected alternative:** making every release an arbitrary side effect would "defeat many classic optimizations". C++ is cited as showing how costly a fully synchronous destructor model is. No cost numbers are given, and the "Class optimization in the presence of ARC" section is TBD. — [Trick gist](https://gist.github.com/atrick/cc03c4d07fb0a7bee92c223ae5e5695b)
- **Shipped behaviour:** lexical lifetimes shipped in Swift 5.7, and a 2024 forum reply says the docs are still accurate. — [Swift Forums, "What is the current status of lexical lifetimes?"](https://forums.swift.org/t/what-is-the-current-status-of-lexical-lifetimes/76587/2)
- A Swift forums thread says the compiler never extends a reference's lifetime across arbitrary unknown code, and never hoists a weak load above the destroy of a strong reference. Search summary only; the thread was not fetched. — [Swift Forums, weak reference guarantees](https://forums.swift.org/t/what-guarantees-does-swift-provide-regarding-weak-references/82804)

**Guaranteed (+0) calling convention:**
- Erik Eckstein, 2018-02-23: "Our preliminary measurements show that on average benchmark performance improves with the guaranteed convention". He also says "there are also some regressions" and "code size improves on average (up to 5%)". The data was preliminary, because the implementation was incomplete.
  - Rationale: callers often retain an argument only for the callee to release it. Setters and initializers, which store their arguments, keep the owned convention.
  - This is not a language change, since Swift does not define destruction points.
  - Source: [Swift Forums proposal](https://forums.swift.org/t/proposal-to-change-the-default-ownership-for-passing-parameters/10261)
- The change shipped in Swift 4.2 (2018) and was ABI-affecting. With the caller owning the traffic, the optimizer can remove retain/release pairs around repeated calls. — [Jesse Squires, 2018-07-05](https://www.jessesquires.com/blog/2018/07/05/swifts-new-calling-convention/); [Gottesman gist](https://gist.github.com/gottesmm/524fca6a4e9fb3d5736a1b9d6686c5e8)
- `self` was already passed at +0 guaranteed from Swift 2, while other parameters were +1 until 4.2. — [swift-dev 2016-01, Joe Groff](https://lists.swift.org/pipermail/swift-dev/Week-of-Mon-20160118/000887.html); [swift-dev 2017-12, Gottesman](https://lists.swift.org/pipermail/swift-dev/Week-of-Mon-20171225/006310.html)

**Source-level ownership controls** (not fetched; titles and numbers only): [SE-0366 `consume` operator](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0366-move-function.md), [SE-0377 `borrowing`/`consuming` parameter modifiers](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0377-parameter-ownership-modifiers.md), [SE-0390 noncopyable structs and enums](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0390-noncopyable-structs-and-enums.md), [SE-0427 noncopyable generics](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0427-noncopyable-generics.md). The forum roadmap thread "A roadmap for improving Swift performance predictability: ARC improvements and ownership control" (2021-12) frames these as the user-visible half of the OSSA work. — [Swift Forums roadmap](https://forums.swift.org/t/a-roadmap-for-improving-swift-performance-predictability-arc-improvements-and-ownership-control/54206)

### Inferences
- **Why OSSA matters for il2wasm:**
  - OSSA's Owned/Guaranteed split is the SSA form of "single owner plus borrow" that il2wasm wants.
  - Pairing count operations becomes a verifier invariant rather than a dataflow guess.
  - Most removable traffic in the old Swift optimizer came from not encoding borrows. Once borrows are first-class (guaranteed arguments, `load_borrow`, forwarding projections), a copy is needed only where a value really outlives its base.
- **What OSSA does not cover for il2wasm:** Swift's semantics deliberately leave destruction points undefined, except at lexical barriers. il2wasm's rule is stricter: nothing may be freed later than the statement that makes it unreachable, and .NET semantics must hold. That makes il2wasm's constraint a hybrid:
  - frees may not be delayed (Swift allows delay up to scope end);
  - lifetimes may not be shortened past observers (Swift's barrier rule).
  - .NET's own rule, where the JIT may collect an object after its last use and `GC.KeepAlive` exists, is closer to Swift's eager-move rule. A finalizer or WeakReference observation is then a deinit barrier.
- **Barriers to adopt in il2wasm:** Swift's barrier list (weak/unowned loads, pointer accesses, opaque calls, globals, synchronization) is a ready-made list of points where an eager free must not cross an observer. Adding finalizer-bearing types, `WeakReference.Target` and `GCHandle` would make it complete for .NET.
- **Payoff of +0:** "+0 by default, +1 only for storing parameters" is the cheapest single convention change. Without interprocedural analysis it removes the caller-retain/callee-release pair on every non-storing call.

### Gaps
- No public, precise "share of retain/release removed by OSSA" number was found. Eckstein's 2018 figure (up to 5% average code size, preliminary) is the only measurement found. The ARCOptimization doc (`docs/ARCOptimization.md`/`.rst`, covering RC identity and ARCSequenceOpts) returned 404 at the tried path and was not read.
- The contents of SE-0366/0377/0390/0427 were not fetched. No performance numbers from those proposals are reported here.

## Objective-C ARC optimizer (LLVM ObjCARCOpts) and the Clang ARC rules

### Takeaway
The ObjC ARC optimizer is a per-pointer, bidirectional (top-down and bottom-up) dataflow over retain/release "sequences". It pairs a retain with releases and deletes the pair when nothing in between can decrement the object to zero, or when the object is "known safe". It may move releases only when they carry `clang.imprecise_release`. That permission exists because Clang's ARC language rules state that locals lack precise lifetime unless they are annotated `objc_precise_lifetime`.

### Cited Findings
- **Purpose:** the pass's header describes it as eliminating redundant, partially redundant and inconsequential reference-count operations, plus redundant weak operations. — [LLVM ObjCARCOpts.cpp](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html)
- **Per-block state** holds two per-pointer maps, `PerPtrTopDown` and `PerPtrBottomUp`. Predecessor and successor states are merged. Path counts are kept, and if they overflow the analysis clears its maps and becomes conservative. — [LLVM ObjCARCOpts.cpp](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html)
- **Sequence states** are `S_None`, `S_Retain`, `S_CanRelease`, `S_Use`, `S_Stop` and `S_MovableRelease`.
  - Per-pointer flags are KnownSafe, ImpreciseRelease, CFGHazardAfflicted and KnownPositive.
  - `CheckForCFGHazards` compares successor states. A successor in `S_CanRelease` clears sequence progress unless the pointer is KnownSafe.
  - Source: [LLVM ObjCARCOpts.cpp](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html)
- **Movement requires imprecise release:** releases are moved only when they carry `clang.imprecise_release` metadata. A release that is partially redundant through a PHI with null inputs is cloned into the non-null predecessors, which critical edges block. — [LLVM ObjCARCOpts.cpp](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html)
- **Peepholes:**
  - ARC calls on null or undef are deleted.
  - An `autoreleaseRV`/`retainRV` pair in the same block cancels; this is the return-value handshake that avoids the autorelease pool.
  - An unused `autorelease(x)` on a single-use identifiable object becomes `release`.
  - An autorelease followed by a pool pop becomes a release before the pop. The source notes this changes drain order from LIFO to FIFO.
  - Source: [LLVM ObjCARCOpts.cpp](https://llvm.org/doxygen/ObjCARCOpts_8cpp_source.html)
- **Clang ARC language rules:**
  - "under certain circumstances, ARC is permitted to re-order and eliminate operations in a manner which may alter the overall computation history".
  - ARC "is sometimes permitted to optimize `release` operations in ways which might cause an object to be deallocated before it would otherwise be".
  - "local variables of automatic storage duration do not have precise lifetime semantics" unless annotated `objc_precise_lifetime`.
  - Source: [Clang ARC spec](https://clang.llvm.org/docs/AutomaticReferenceCounting.html)
- **Liveness limits:** an object may not be deallocated early when:
  - it is held in a precise-lifetime `__strong` variable;
  - the program later loads from an imprecise variable and depends on the value;
  - it is released at the end of a full-expression and used later in that expression.
  - Source: [Clang ARC spec](https://clang.llvm.org/docs/AutomaticReferenceCounting.html)
- **Conventions:**
  - `ns_consumed`: the caller retains and the callee releases at function end.
  - `ns_returns_retained`: the callee retains at return and the caller releases at the end of the full-expression.
  - Unretained returns: no mandatory caller work.
  - A weak read retains the pointee until the end of the full-expression.
  - Source: [Clang ARC spec](https://clang.llvm.org/docs/AutomaticReferenceCounting.html)

### Inferences
- **What carries over to il2wasm:**
  - KnownSafe: some other reference provably holds the object live across the region. This is exactly the "borrow under a live owner" proof.
  - The RV handshake: hand the +1 directly to the caller instead of through a pool. It is equivalent to an owned return convention.
- **What does not carry over:** the imprecise-release permission. ObjC relies on language-level licence to free earlier or later. il2wasm cannot free later, so release motion must go only earlier, and only across non-observers.
- Pairing a retain with a release across a path where a store or an unknown call could decrement is unsafe in ObjC too. CFG-hazard checking is the general version of il2wasm's "no shared bit" problem.

### Gaps
- No measured "fraction of ObjC retains/releases removed" was found. The contract and expand passes, `objc_precise_lifetime` handling and the KnownSafe computation were outside the fetched excerpt.

## Rust: ownership model and what carries over to inference

### Takeaway
Rust's moves, borrows and NLL/Polonius are a checking discipline: programs that cannot be proven are rejected, and Rc/Arc counts are explicit `clone()` calls. Nim's ARC/ORC is the closest imperative analogue in an inference setting where nothing is rejected. Nim infers moves at the last read and treats unmutated aliases as uncounted "cursors". When a move cannot be proven, it falls back to a copy instead of an error.

### Cited Findings
- **Nim sink parameters:**
  - A location passed to `sink` should not be used afterward.
  - If the compiler cannot prove the last use, "it passes a copy instead".
  - Sink is affine (consumed once or not at all).
  - The analysis covers locals, with object and tuple fields tracked separately.
  - Source: [Nim destructors doc](https://nim-lang.org/docs/destructors.html)
- **Nim move rewrite:** `x = lastReadOf z` becomes `=sink(x, z); =wasMoved(z)`, and a `=wasMoved(x)` followed by `=destroy(x)` cancel out. — [Nim destructors doc](https://nim-lang.org/docs/destructors.html)
- **Nim cursor inference** is a form of copy elision. If neither the destination nor the source is mutated afterward, no copy is materialized. Example: `let v = tab["key"]` with `tab` an unmutated parameter makes `v` a cursor, with no inc and no dec. `{.cursor.}` is uncounted and unchecked, and misuse can dangle. — [Nim destructors doc](https://nim-lang.org/docs/destructors.html)
- **Nim destruction timing:** destruction happens at scope exit; the current implementation does not destroy at the last use. `lent T` is a borrowed view that the compiler proves does not outlive its origin. ORC adds a cycle collector, which ARC lacks. — [Nim destructors doc](https://nim-lang.org/docs/destructors.html)
- **CPython 3.14 `LOAD_FAST_BORROW`:**
  - It pushes an uncounted reference to a local when "the reference in the frame outlives the borrowed reference".
  - Borrowed references are converted back to owned ones where the frame escapes (generators, frame materialization).
  - The PR author reported that about 97% of LOAD_FAST{_LOAD_FAST} instructions are optimized, according to pystats.
  - The observable side effect is that refcount values read lower.
  - Source: [CPython PR #130708 (2025)](https://github.com/python/cpython/pull/130708); [rushter.com overview](https://rushter.com/blog/python-refcount/)

### Inferences
- Rust contributes the vocabulary: move, shared and mutable borrow, and a borrow region that ends at the last use (NLL). The il2wasm equivalent of a borrow region is a "guaranteed" SSA value whose base is provably live.
- Rejection becomes fallback: where Rust rejects, il2wasm must insert a count operation (Nim's "passes a copy instead"). The quality metric is therefore how rarely the fallback fires, not soundness.
- Nim's cursor rule ("neither side mutated afterward") and CPython's ~97% borrowed-load coverage both show something specific. Most count operations on locals and parameters can be removed by local, intraprocedural liveness of the owner, without alias analysis. The hard residue is heap fields and containers.

### Gaps
- The Rust NLL RFC 2094 and Polonius material were not fetched, so no citations or numbers are given. Rust's model rejects programs and therefore has no "share removed" metric.
- No CPython speedup figure was retrieved for LOAD_FAST_BORROW.

## Academic RC optimization: Blackburn and Shahriyar line, coalescing, deferral, ulterior, trial deletion, static elimination

### Takeaway
The Java RC literature removes most count operations dynamically, by deferral (ignoring stacks), coalescing and ignoring new objects. Those are exactly the mechanisms il2wasm forbids. Its intrinsic measurements are still the best data on where count operations come from:
- about 71% hit objects young enough to die before a 2–16 MB window;
- about 49% hit objects whose maximum count is 1 or 2;
- under 1% of objects overflow a 3-bit count, yet those attract about 23% of operations.

On the static side, Joisha's Bartok work on nondeferred RC for C# (2006–2008) is the closest precedent to il2wasm. Overlooking-roots optimizations brought nondeferred RC within 4% of deferred RC on nearly all programs, and within 23% in the worst case.

### Cited Findings
**Shahriyar, Blackburn and Frampton, "Down for the Count? Getting Reference Counting Back in the Ring"** (ISMM 2012, pp. 73–84, [DOI 10.1145/2258996.2259008](https://doi.org/10.1145/2258996.2259008)). Numbers below are from Shahriyar's PhD thesis (ANU, 2015), chapter 4, which reproduces the paper. Setup: Jikes RVM/MMTk, Java benchmarks (SPECjvm98, DaCapo, pjbb2005), RC with deferral and coalescing.
- **Baseline gap:** the state-of-the-art RC "is over 30% slower than its tracing counterpart" (well-tuned mark-sweep). It is "over 40% slower than a generational tracing" collector. — [Shahriyar thesis 2015](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Count width:** with 3 bits, "only 0.65% of objects experience overflow". However, "these overflowed objects attract nearly 23% of all increment and decrement operations, on average". — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Where operations go:**
  - "On average, 71% of increments and 71% of decrements are performed upon newly allocated objects (over 90% for some benchmarks)". Operations on non-new objects are around 9–10%. Around 10% are due to root reachability, and cycle collection performs 4% of decrements. "New" means allocated within the last 2 MB or 16 MB window.
  - Elsewhere the thesis says young objects are responsible for "more than 70% of increments and decrements" and that on average only 10% of them survive.
  - Source: [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **By maximum count:** "on average 31% of increments are performed for objects having maximum reference count of one and 18% increments ... maximum reference count of two" (Table 4.4: 49% of increments and decrements fall on objects with a maximum count of 1 or 2). "17% of increments are due to objects with very high maximum reference counts (>63)". — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Coalescing (Levanoni–Petrank style):**
  - Overall it "elides around 64% of pointer field changes on average, and around 90% for old objects" (Table 4.5).
  - With an 8 MB window it observes 50.5% (compress) and 92.2% (jess) of mutations.
  - For avrora, luindex and sunflow it elides 90% or more.
  - It is "significantly more effective with old objects".
  - Source: [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Lazy mod-buf and born-dead:**
  - Lazy mod-buf insertion means new objects are not logged; they are discovered at collection time. Born dead means new objects are allocated with RC 0 and no compensating decrement.
  - Together they eliminate "over 80% of increments and 90% of decrements" on average.
  - Lazy mod-buf alone: 14% total, 3% mutator and 59% collection time improvement over Standard RC.
  - Combined optimizations: 23% total-time improvement over Standard RC, reaching parity with mark-sweep.
  - Source: [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Remaining gap:** "Sticky Immix outperforms our optimized reference counting collector by 10%". — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- **Header word cost:** adding a 32-bit header word for the count costs 2.5% in total time. — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)

**Shahriyar, Blackburn, Yang, McKinley, "Taking Off the Gloves with Reference Counting Immix"** (OOPSLA 2013):
- RC Immix combines RC with Immix line/block heap organization and opportunistic copying. "RC Immix performs 3% better than Gen Immix", the production generational collector, at a moderate heap, and reduces cache miss rates by around 20%.
- Its conservative variant RC Immix^cons is "2% slower than RC Immix and still slightly faster than production exact Gen Immix".
- Source: [thesis ch. 5–6](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf); [MSR-hosted paper PDF](https://microsoft.com/en-us/research/wp-content/uploads/2016/02/rcix-oopsla-2013.pdf)

**Levanoni and Petrank, "An On-the-Fly Reference Counting Garbage Collector for Java"** (OOPSLA 2001; TOPLAS 2006):
- Mechanism: log each modified slot's old value once per collection cycle (a dirty bit per object or slot). At collection, decrement the old value and increment the current value. All intermediate writes coalesce away.
- Stacks are handled via a local ZCT and "marked local" roots, so this is also deferred.
- Two-bit counts were used. Fewer than 1% of objects got stuck for most benchmarks, but 3.7–4.7% did for compress, javac and mpegaudio.
- Speed: up to 10% faster than the original JVM on mtrt, and "only 3.6% percent slower" on SPECjvm98 single-processor.
- Setup: Sun JVM 1.2.2, 4-way 550 MHz Xeon.
- Source: [Levanoni & Petrank PDF](https://user.it.uu.se/~kostis/Seminars/PLI/Papers/levanoni.pdf); [Technion copy](https://csaws.cs.technion.ac.il/%7Eerez/Papers/refcount.pdf)
- Wikipedia states that coalescing plus new-object handling eliminates "more than 99%" of counter updates on typical Java benchmarks. This is secondary and unverified against the paper. — [Wikipedia: Reference counting](https://en.wikipedia.org/wiki/Reference_counting)

**Deutsch–Bobrow deferral, ulterior RC and trial deletion** (contrast only):
- Deutsch and Bobrow (1976) "eliminated the need for a write barrier on local references (in stack and registers)". Modula-2+ adopted this. — [Levanoni & Petrank related work](https://user.it.uu.se/~kostis/Seminars/PLI/Papers/levanoni.pdf)
- Blackburn and McKinley's ulterior RC (2003) traces the young generation and reference-counts the old one. It is cited for the finding that operations on non-new objects are only about 9–10%. — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)
- Bacon and Rajan (2001) trial deletion is cyclic collection by tentative decrement of candidate roots. In the thesis's measurements, cycle collection accounts for 4% of decrements. — [thesis](https://openresearch-repository.anu.edu.au/bitstream/1885/99879/1/Thesis%20Shahriyar%202015.pdf)

**Static elimination of count operations:**
- Park and Goldberg, "Reference Escape Analysis: Optimizing Reference Counting based on the Lifetime of References" (PEPM 1991, pp. 178–189). If a reference provably does not outlive another live reference to the same object, its count operations can be skipped. A follow-up, "Static analysis for optimizing reference counting", appeared in IPL 1995 ([DOI 10.1016/0020-0190(95)00096-u](https://doi.org/10.1016/0020-0190(95)00096-u)) and targets higher-order functional languages. No measured removal numbers were retrieved. — [bibtex PEPM-1991](https://bibtex.github.io/PEPM-1991-ParkG.html); [typeset PDF](https://typeset.io/pdf/reference-escape-analysis-optimizing-reference-counting-knwytd2zwk.pdf)
- **Joisha (Microsoft Research, Bartok C#/CLR compiler):**
  - "Compiler optimizations for nondeferred reference-counting garbage collection" (ISMM 2006) covers RC update coalescing over local ranges and immortal-object RC elision.
  - "Overlooking Roots: A Framework for Making Nondeferred Reference-Counting Garbage Collection Fast" (ISMM 2007, pp. 141–158) unifies prior optimizations through the notion of an overlooking root: a reference whose lifetime subsumes another's, making the other's count operations redundant. Per its abstract (via an index summary), the margins versus deferred RC fall "to within 4%" on nearly all programs and "to within 23%" in the worst case.
  - "A Principled Approach to Nondeferred Reference-Counting Garbage Collection" (MSR-TR-2007-104; PPoPP 2008) shows how to convert a full OO program (interior pointers, exceptions) into nondeferred RC.
  - Sources: [ISMM 2006 bibtex](https://bibtex.github.io/ISMM-2006-Joisha.html); [ISMM 2007 bibtex](https://bibtex.github.io/ISMM-2007-Joisha.html); [sciweavers abstract](https://sciweavers.org/node/96777); [MSR TR-2007-104](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2007-104.pdf); [US patent 8103706](https://patents.google.com/patent/US8103706)
- From TR-2007-104 (Bartok, C#):
  - The optimizations "statically detect and eliminate redundant RC updates on stack references".
  - RC insertion increases high-level IR statements by about 48%, and over 80% when Bartok compiles itself. Basic blocks increase by about 11%, up to 24%.
  - The more efficient of two RC-insertion templates is used over 90% of the time.
  - The baseline nondeferred binaries are larger than the deferred ones. Size comparisons are given after the coalescing, immortal-elision and three overlooking-root optimizations.
  - Source: [MSR TR-2007-104](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2007-104.pdf)
- **Joisha's two templates:**
  - A caller-retains template (CR, used) and a callee-retains template (RA). The RA template straddles the function boundary, which makes subsumption harder to detect.
  - With the CR template, "there is a small chance that garbage will be held longer than even a standard tracing collector", though it was not observed.
  - Relaxing the placement of an RC decrement to later in a block enables dead-code elimination "at the expense of holding onto garbage longer". il2wasm forbids that trade.
  - Source: [MSR TR-2007-104](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2007-104.pdf)

### Inferences
- **What dominates the residue.** Across the Java data, most dynamic count operations involve young objects and objects whose maximum count is 1 or 2. Those are exactly the objects a compile-time single-owner analysis should own outright, with zero count operations. The 17–23% concentrated on very popular objects, whether stuck or with counts above 63, behaves like shared containers and globals. That share will likely remain in any static scheme and is the real floor.
- **Why dynamic results do not transfer.** Coalescing's 64%/90% and lazy/born-dead's 80%/90% are dynamic removals. They depend on deferral, or on freeing later than unreachability, which il2wasm forbids.
  - The statically recoverable analogue of coalescing is intra-statement or intra-block cancellation of an inc against a dec on the same field, as in Joisha's local coalescing.
  - The analogue of "ignore new objects" is the static fact that a freshly allocated object has a single owner until it escapes into the heap.
- **Joisha is the most relevant prior art.** It is nondeferred, built for C#/CLR, uses subsumption ("overlooking roots") over stack references, and reaches within 4% of deferred RC. It removed stack and parameter traffic. The remaining cost was heap-field updates, which no static scheme in the literature eliminates wholesale. This is consistent with il2wasm's earlier plateau of 53–57%, if the remainder is field and container traffic.
- **Calling conventions converge.** Swift moved from callee-releases to caller-guaranteed. Joisha found that a callee-side template hides subsumption across the call boundary. Both point to +0/borrowed parameters as the default and +1 only for storing parameters.

### Gaps
- No exact removal percentages were retrieved for Joisha 2006/2007 (the ISMM PDFs were not accessible). Only the abstract-level "within 4% / 23%" time margins are cited.
- The 1991 and 1995 Park–Goldberg papers were not read in full, so no removal numbers are given.
- No paper titled "Reference counting deeply revisited" was located in this pass. Its existence and authors are unverified.
- 2020–2026 imperative/OO-specific static RC work was not covered here, beyond CPython 3.14's borrowed loads (2025) and Swift lexical lifetimes (2022). The following belong to the functional-language thread and were not fetched: LXR (Zhao, Blackburn, McKinley, PLDI 2022, a deferred RC collector); Perceus (PLDI 2021); Ullrich and de Moura's "Counting Immutable Beans" (2019, borrow inference for Lean); and Lorenzen and Leijen's frame-limited reuse (ICFP 2022).
- No study found reports a breakdown of statically remaining count operations by category (fields vs containers vs parameters vs returns) for an OO language. The Java breakdown above is by object age and maximum count, not by syntactic site.

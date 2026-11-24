# Reference counting under real multithreading, and static thread-escape analysis

Notes for il2wasm (C#/IL to WebAssembly, compile-time RC with cycle groups, Web Workers on one shared memory). Research date: 2026-11-23. Primary-source PDFs were converted with pdftotext and read directly where noted; numbers are quoted with their conditions.

## Biased reference counting (Choi, Shull, Torrellas, PACT 2018) and CPython free-threading (PEP 703, PEP 683, 3.13/3.14)

### Takeaway
Biased RC splits the count into an owner-only plain counter and an atomic shared counter. Most objects are private in real programs, so most RC operations become plain. The owner path costs a TID check, measured at 8.8% over plain non-atomic increments. CPython's free-threaded build combines BRC with immortal objects and deferred counting (stack references not counted, reclaimed only at GC). Its single-thread cost is now about 1% (macOS aarch64) to 8% (x86-64 Linux) on pyperformance in 3.14. That cost includes more than RC: per-object locks and mimalloc.

### Cited Findings
- **BRC paper:** Choi, Shull and Torrellas, "Biased Reference Counting: Minimizing Atomic Operations in Garbage Collection," PACT '18, Nov 1–4 2018, Limassol, DOI 10.1145/3243176.3243195. The idea is to bias each object to its allocating thread. The owner updates a "biased" counter non-atomically and other threads update a "shared" counter atomically. Invariant I1: biased + shared = total references. — [ACM DL](https://dl.acm.org/doi/abs/10.1145/3243176.3243195); full text read from [docs.rs copy](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **BRC header layout:** BRC fits in Swift's existing 64-bit RCWord, with no size increase: TID 18 bits, biased counter 14 bits, shared counter 14 bits, 2 flag bits (Merged, Queued) and 16 reserved bits. The original Swift word used 30-bit counters. The authors justify 14 bits by citing that most objects' counts fit in 7 bits. — [BRC paper §4.2](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **BRC merge and queue protocol:** A non-owner decrement can drive the shared counter negative. The object is then marked Queued and pushed on the owner's QueuedObjects list. When the owner's biased count reaches 0, the owner "unbiases" the object, merging the counters; the object is freed when the merged total reaches 0. Without the queue such an object would leak, because the biased counter never reaches zero while the shared counter is negative. — [BRC paper §4](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **Swift RC cost (BRC paper's motivation):** RC took an average of 42% of execution time in the client programs and 15% in the server programs, 32% across all programs. Removing atomics entirely (an unsafe "Ideal" configuration) reduced average execution time by 25%. Swift's compiler optimizations remove "up to 97% of RC operations" in these programs. — [BRC paper §2–3, §6](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **How much is private (BRC Table 1 and Table 6):** On average, over 99% of objects in client programs and over 93% in server programs are private, meaning their count is touched by only one thread. About 93% of RC operations in client programs and about 87% in server programs are to private objects. Only 1.72% (client) and 10.15% (server) of RC operations actually hit the shared counter. Queued objects average 0.00% (client) and 2.35% (server). The outlier is Regex-Redux, with nearly 50% of RC operations on shared objects and 12% on the shared counter. Benchmarks: Swift Benchmark Suite, CryptoSwift, SwiftyJSON, Raytrace, GCBench-Single/Multi, Regex-Redux; servers Perfect-JSON/Blog and Kitura-JSON/Blog. — [BRC paper §7.1](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **BRC per-operation latency (Table 7):** Single-threaded increment loop, best case with no contention, on an Intel Core i7 with 4 cores at 3.50 GHz, 16 GB RAM, Ubuntu 16.04:

  | Configuration | Increment time |
  |---|---|
  | Original (atomic) | 13.84 ns |
  | Ideal (non-atomic) | 5.77 ns |
  | Biased, owner | 6.28 ns |
  | Biased, non-owner | 15.57 ns |

  The atomic increment is therefore 2.40x slower than a plain one. The BRC owner path is 8.8% slower than Ideal, and the non-owner path is 12.5% slower than Original. — [BRC paper §7.2](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **BRC end-to-end results:** Average client execution time fell by 22.5%, landing within 3.7% of the unsafe Ideal configuration. Average server throughput rose by 7.3%. Ideal could not be run on the servers because they crash without atomics. Peak memory rose by 1.5% on average. In a sensitivity study, BRC becomes slower than Original only when 75% or more of objects are queued. Each experiment was run 10 times and averaged. — [BRC paper §7.3–7.5](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **Comparison with Ungar et al.:** The BRC paper says Ungar et al.'s Swift technique "uses more atomic operations than BRC due to its conservative escape detection." It also adds store-barrier overhead to detect and recursively mark escaping objects. — [BRC paper §8](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **PEP 703 object header:** The header holds `ob_tid` (owner thread, 0 means none), `ob_ref_local` (32-bit owner count, whose top two bits flag immortal or deferred objects), and `ob_ref_shared` (shared count shifted left by 2, low two bits hold state). It also holds a 1-byte `ob_mutex`, 1-byte `ob_gc_bits` and 2 bytes of padding. The PEP deliberately uses separate fields instead of BRC's packed word "to avoid overflow issues" and to allow a fast deallocation path that skips an atomic. — [PEP 703](https://peps.python.org/pep-0703/) (2023, accepted)
- **PEP 703 state machine on `ob_ref_shared`:** Transitions use compare-and-swap. The four states are:
  - `00` default: the only state that allows fast deallocation.
  - `01` weakrefs: entered on the first weakref, or when a non-owner first fetches the object from a list or dict.
  - `10` queued: entered when the shared count goes negative. The object goes on the owner's merge queue and the owner is signalled via `eval_breaker`. If the owner has exited, the acting thread merges immediately.
  - `11` merged: `ob_tid` is 0, and the object is freed when the shared count reaches 0.

  — [PEP 703](https://peps.python.org/pep-0703/)
- **PEP 703 deferred RC:**
  - Covered objects: top-level functions, code objects, modules and methods.
  - Push and pop of these on the interpreter stack skip RC.
  - The true count is only computable during GC with all threads paused, so these objects are freed only at GC.
  - Heap types use per-thread arrays of local counts that the GC sums.

  — [PEP 703](https://peps.python.org/pep-0703/)
- **PEP 703 immortalization:**
  - Immortal: interned strings, small ints, static type objects, True, False and None.
  - Mechanism: `ob_ref_local = UINT32_MAX`, which makes Py_INCREF and Py_DECREF no-ops.
  - Relation to PEP 683: similar to PEP 683 (adopted in 3.12), with a different bit encoding.

  — [PEP 703](https://peps.python.org/pep-0703/)
- **PEP 703 measured overhead:** On pyperformance 1.0.6, `--disable-gil` against a 3.12 immortal-objects baseline:
  - one thread: 6% on Intel Skylake, 5% on AMD Zen 3;
  - multithreaded: 8% on Skylake, 7% on Zen 3.

  The largest contributor is BRC, followed by per-object locking. Multithreaded overhead is higher partly because each bytecode is specialized only once, for thread safety. — [PEP 703](https://peps.python.org/pep-0703/)
- **CPython 3.14 docs:** Average pyperformance overhead ranges "from about 1% on macOS aarch64 to 8% on x86-64 Linux."
  - Header size: `None` is 32 bytes free-threaded vs 16 bytes default on AMD64. GC objects are the same size.
  - Immortalization in 3.14 is limited to code constants and `sys.intern()` strings.
  - Deferred RC applies to modules, top-level functions, class-scope methods, descriptors and `threading.local`.
  - Freeing memory relies on QSBR.

  — [Python 3.14 free-threading HOWTO](https://docs.python.org/3.14/howto/free-threading-python.html)
- **PEP 779:** The free-threaded build was "currently around 10%" slower on pyperformance (about 3% on macOS), and used about 15–20% more memory (geometric mean). Proposed Phase II targets: 15% slowdown at most and 20% memory. — [PEP 779](https://peps.python.org/pep-0779/) (2025)
- **Secondary reports:** What's New drafts describe a single-threaded penalty of roughly 5–10% in 3.14, down from about 40% in 3.13. A FOSDEM 2026 slide reports about 3% versus regular 3.14. — [python-checkins mail archive](https://mid.mail-archive.com/python-checkins@python.org/msg16461.html); [FOSDEM 2026 slides](https://fosdem.org/2026/events/attachments/ABJMWD-the_gil_and_api_performance_past_present_and_free-threaded_future/slides/266979/the_gil_a_fw7etsx.pdf). Both are secondary and not verified against primary benchmark data. The 40% figure is mostly because the specializing interpreter was disabled in free-threaded 3.13, which is an inference.
- **Concurrent Deferred RC (Anderson, Blelloch, Wei, PLDI '21):**
  - Venue: pp. 526–541, DOI 10.1145/3453483.3454060.
  - Mechanism: a generalization of hazard pointers defers decrements "until no other process can be incrementing them," and defers or elides increments for short-lived references (`snapshot_ptr`). This yields wait-free, constant-time overhead.
  - Setup: 4-socket, 72-core Xeon E7-8867 v4, 1–200 threads.
  - Against other RC libraries at 128 threads, read-heavy workload: snapshots improve throughput 1.7x over OrcGC, 5x over their own non-snapshot version, 7x over an optimized Herlihy et al., and 16x over Folly. Update-mostly: at least 2x.
  - Against manual SMR: "In most cases, our throughput is 1.2–2.5x slower than EBR, but we experience 3–61x less memory overhead." The linked list is up to 5.1x slower than EBR.

  — [PLDI '21 paper (NSF PAR)](https://par.nsf.gov/servlets/purl/10252248); [program page](https://pldi21.sigplan.org/details/pldi-2021-papers/35/Concurrent-Deferred-Reference-Counting-with-Constant-Time-Overhead)
- **Related CDRC work:** Blelloch and Wei's acquire-retire preprint, arXiv:2002.07053. A PLDI '22 follow-up turns manual reclamation into automatic RC. — [arXiv](https://www.arxiv.org/abs/2002.07053v2); [HOPC 2023 abstract](https://par.nsf.gov/servlets/purl/10467347)
- **Ownership-tag schemes without a TID:**
  - Lean ("Counting Immutable Beans," Ullrich and de Moura, 2019): every header is tagged single-threaded, multi-threaded or persistent. `markMT(o)` recursively tags everything reachable as MT before it is shared (for example by `Task.mk`). Invariant: MT reaches only MT or persistent values, and persistent reaches only persistent. Single-threaded values use plain RC. The test needs no synchronization "because the tag is only modified before a value is shared." — [arXiv 1908.05647](https://arxiv.org/pdf/1908.05647)
  - Koka (Perceus, MSR-TR-2020-42, v4 2021): uses the same idea and encodes thread-shared objects as a negative count. One inlined test `rc <= 1`, a relaxed read, then catches both "shared" and "free". Making all RC atomic gave "a slowdown from 5% (rbtree) up to 59% (nqueens)." The paper cites Ungar et al. reporting up to 50% in Swift. — Perceus tech report (read locally from [MSR-TR-2020-42](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/))
- **Ungar, Grove, Franke, "Dynamic Atomicity: Optimizing Swift Memory Management," DLS 2017, DOI 10.1145/3133841.3133843:** Each count carries an atomicity bit. A store barrier plus compiler-generated recursive tracers mark objects that become reachable from shared ones. The authors report ARC consuming up to 80% of execution time in some programs. — [IBM Research page](https://researcher.watson.ibm.com/publications/dynamic-atomicity-optimizing-swift-memory-management); [bib](https://lclem.github.io/bibliographer/articles/10.1145/3133841.3133843)

### Inferences
- BRC's 2.40x figure is the best clean number for an uncontended atomic increment versus a plain one, on x86 in 2018. Under contention the gap grows far larger.
- For il2wasm, a compile-time escape proof can remove even BRC's 8.8% owner-path check on provably local data. BRC or a Koka-style sign bit is then the fallback only for sites the analysis cannot prove. That fallback covers data reachable from statics, or passed to `Task.Run`, `Thread` or `ThreadPool`.
- The Lean and Koka tag schemes need a "mark recursively on publish" step. That is cheap in pure languages, where publication points are few. In C#, any store into a shared object's field publishes, which is Ungar's store barrier. A static analysis can place that barrier only at fields that may hold shared objects.
- Deferred and immortal counting in CPython rely on a tracing GC pass, which il2wasm does not have. Immortality without GC maps cleanly onto static readonly data and literals ("persistent" in Lean's terms). Deferred counting does not map without some form of epoch or quiescence reclamation, like CPython's QSBR or CDRC's hazard announcements.
- On determinism: BRC's free timing depends on which thread drops last, and on when the owner drains its queue. Finalization order can therefore vary across runs. Deterministic output requires that finalizer side effects not depend on that order, or that frees be serialized to deterministic points.

### Gaps
- Could not retrieve the full Dynamic Atomicity results; the ACM DL is paywalled.
- No primary per-version (3.13 vs 3.14) pyperformance table was found beyond the official docs and PEP 779 ranges.
- No 2023–2026 paper extending BRC itself was found. Searches surfaced only CDRC/OrcGC lineage and CPython's adoption.

## Swift atomic counting, -assume-single-threaded, Sendable and region-based isolation (SE-0414); Rust Rc/Arc and Send/Sync

### Takeaway
Swift's RC is always atomic unless a hidden flag is used. `-assume-single-threaded` was described by maintainers as an experimental hack, and covered only reference types. SE-0414 regions are a static proof that a non-Sendable value is unreachable from other isolation domains, used today for data-race checking, not for choosing non-atomic RC. Rust achieves "pay only if shared" through the type system: Rc is not Send, Arc is atomic.

### Cited Findings
- **`-assume-single-threaded`:** Converts retain and release to non-atomic forms in SIL; the test `assume_single_threaded.sil` still ships in Swift 6.0.3 and 6.1.3. A maintainer called it "a quick hack to allow some experiments." It covers only reference types, so atomic counts remained for value types (value witness functions) and the measured gains were limited. No benchmark numbers were published in the 2017 thread. — [swift-dev Jan 2017](https://lists.swift.org/pipermail/swift-dev/Week-of-Mon-20170130/003929.html); [Swift forums](https://forums.swift.org/t/performance-issues-in-automatic-reference-counting-arc/4732); [Debian source test file](https://sources.debian.org/src/swiftlang/6.1.3-1/swift/test/SILOptimizer/assume_single_threaded.sil)
- **Cheap atomics on Apple CPUs:** A later forum post states that Apple A14-class CPUs make uncontended atomics "almost as cheap as nonatomic accesses." This is unmeasured in the source. — [Swift forums "Swift Performance"](https://forums.swift.org/t/swift-performance/28776/105)
- **SE-0414 region-based isolation (accepted with modifications, 2024):**
  - A region is a set of non-Sendable values that are only aliased or reachable from within the region. Regions merge when values come to alias.
  - A value computed only from Sendable inputs starts its own disconnected region, and a disconnected region may be transferred across isolation domains.
  - Reviews found soundness holes and patched them by merging into the actor's region:
    - key-path literals with isolated components;
    - captures of isolated closures;
    - `assumeIsolated`, closed by requiring its result type to be Sendable.

  — [SE-0414 proposal](https://github.com/apple/swift-evolution/blob/main/proposals/0414-region-based-isolation.md); [acceptance](https://forums.swift.org/t/accepted-with-modifications-se-0414-region-based-isolation/70051); [returned for revision](https://forums.swift.org/t/returned-for-revision-se-0414-region-based-isolation/69123)
- **Rust:** `Rc<T>` uses non-atomic counts and is `!Send`/`!Sync`. `Arc<T>` uses atomic counts and is Send/Sync when T is. The compiler enforces this at `thread::spawn`, which requires `F: Send + 'static`. — [std::rc::Rc docs](https://doc.rust-lang.org/std/rc/struct.Rc.html); [std::sync::Arc docs](https://doc.rust-lang.org/std/sync/struct.Arc.html); [std::marker::Send](https://doc.rust-lang.org/std/marker/trait.Send.html)

### Inferences
- C# has no Send/Sync, so the il2wasm analysis must infer the Rc/Arc split per allocation site. The nearest analogue is SE-0414's region merging, a flow-sensitive alias-region analysis.
- SE-0414's soundness holes are a checklist of places where a thread-escape inference for C# could also be unsound:
  - closures captured by isolated or async code (C#: lambdas passed to `Task.Run`, `ThreadPool`, `Parallel.For`);
  - "assume" escape hatches (C#: `Unsafe`, `GCHandle`, delegates stored in statics).
- The Apple-hardware claim does not transfer to WebAssembly. There, `i32.atomic.rmw.add` is lowered to `lock xadd` on x86-64 (see the WebAssembly section), the same cost as the native atomic.

### Gaps
- Found no measured Swift numbers for `-assume-single-threaded`, nor any Swift work using SE-0414 regions to emit non-atomic RC. Absence of evidence, not proof.
- Rust facts are cited from the std documentation; the pages were not re-fetched in this session.

## Static thread-escape analysis: Choi et al. 1999, Whaley–Rinard 1999, Steensgaard 2000, Domani et al. 2002, Salcianu–Rinard 2001, later work

### Takeaway
Classic Java escape analyses removed a median of about 51% of lock operations, in a range of 11–92%, and stack-allocated a median of 19% of objects. Measured fractions of objects that are truly thread-local are 45–69%, or 87–99% for Swift RC operations in BRC. Static analyses lose much of this to allocation-site granularity and to treating statics as escaping. Analysing the interactions between threads (Salcianu–Rinard) gave almost no additional synchronization removal over intrathread analysis.

### Cited Findings
- **Choi, Gupta, Serrano, Sreedhar, Midkiff, "Escape Analysis for Java," OOPSLA '99:**
  - Venue: SIGPLAN Notices 34(10):1–19, DOI 10.1145/320385.320386; extended in TOPLAS 25(6), 2003.
  - Method: connection graphs, classifying objects as NoEscape, ArgEscape or GlobalEscape. Thread-local objects get lock elision; method-local objects get stack allocation.
  - Setup: IBM HPCJ prototype on a 333 MHz PowerPC with 128 MB, 10 benchmarks.
  - Stack-allocatable objects exceeded 70% of dynamically created objects in 3 of 10 benchmarks, median 19%.
  - 11% to 92% of lock operations were eliminated, median 51%.
  - Execution time fell 2% to 23%, median 7%.

  — [ACM DL](https://dl.acm.org/doi/abs/10.1145/320385.320386); numbers from [UNICAMP seminar slides summarising the paper](https://www.ic.unicamp.br/~edson/disciplinas/mo615/2011-2s/seminario/mo615-2s11-seminario-escape_analysis-slides.pdf) (secondary, in Portuguese); [TOPLAS 2003](https://dl.acm.org/doi/10.1145/945885.945892)
- **Salcianu and Rinard, "Pointer and Escape Analysis for Multithreaded Programs," PPoPP 2001:**
  - Method: parallel interaction graphs model fork and join between threads, to recapture objects that escape a thread but not the whole multithreaded computation. Implemented in MIT Flex (Java to C).
  - Synchronization operations executed (original / intraprocedural / interthread):

    | Benchmark | Original | Intra | Inter |
    |---|---|---|---|
    | Water | 2,367,193 | 919,575 | 919,575 |
    | Barnes | 2,838,720 | 678,355 | 678,355 |
    | Http | 67,268 | 8,460 | 7,406 |
    | Quote | 268,913 | 200,650 | 198,610 |

    The interthread analysis gave "little additional benefit," but it was required to verify region safety for Barnes and Water.
  - Analysis time was 0.5–94.2 s, for programs of about 11–18K bytecode instructions.
  - Hardware: 400 and 866 MHz Pentium II/III.

  — [paper PDF](https://people.csail.mit.edu/rinard/paper/ppopp01.pdf)
- **Base-analysis limitation:** Rinard's page says the base analysis treats an object accessible to multiple threads as permanently escaped. — [Rinard research page](https://people.csail.mit.edu/rinard/research/PointerAndEscapeAnalysis)
- **Whaley and Rinard, "Compositional Pointer and Escape Analysis for Java Programs," OOPSLA 1999:** Confirmed bibliographically. Measured results were not retrieved. — [Rinard research page](https://people.csail.mit.edu/rinard/research/PointerAndEscapeAnalysis)
- **Steensgaard, "Thread-Specific Heaps for Multi-Threaded Programs," ISMM 2000, pp. 18–24:**
  - A shared old generation plus per-thread young heaps. Escape analysis segregates allocation sites whose objects may become reachable both from a global variable and by more than one thread.
  - No dynamic class loading. Static fields are roots for every local region, so a global rendezvous is still needed before thread-local collection.

  — as described by [Jones and King 2005](https://www.cs.kent.ac.uk/pubs/2005/2228/content.pdf); the original's numbers were not retrieved.
- **Domani et al., "Thread-Local Heaps for Java," ISMM '02, pp. 76–87, DOI 10.1145/512429.512439:**
  - Method: dynamic, not static. A write barrier marks objects global when they become reachable by a second thread. Allocation sites that are at least 99% global allocate directly in the global heap.
  - The authors criticise static analysis because a site making a million objects of which 90% are local is still classed global, and an object that becomes global late is global from creation.
  - pBOB: about 30% of allocations are global. More than 99% of local objects die per collection, versus 63–80% of global objects.
  - Collection time fell 2–60%, by about 50% on average. Long pauses were cut 38–76%; local pauses are about 1 ms against about 100 ms for full collections.
  - The write barrier costs 2.1–2.8% of execution time, so overall throughput was basically unchanged.
  - Setup: IBM JVM 1.3.0 on a 550 MHz PIII Xeon.

  — [paper PDF](https://csaws.cs.technion.ac.il/~erez/Papers/TLH-ISMM-02.pdf)
- **Jones and King, "A Fast Analysis for Thread-Local Garbage Collection with Dynamic Class Loading," 2005:**
  - Static per-allocation-site classification into Local, OptimisticallyLocal and Global "heaplets." Safe under dynamic class loading; no write barrier and no locks for local GC. Thread objects live in the shared heap.
  - Stricter rule than Steensgaard: everything reachable from statics escapes.
  - VolanoMark client, measured dynamically: 48%, 47% and 45% of objects stay local throughout life (55%, 54% and 52% by bytes) at 1024, 2048 and 4096 threads.
  - Over 70% of methods are already loaded when the snapshot analysis runs.

  — [Kent tech report PDF](https://www.cs.kent.ac.uk/pubs/2005/2228/content.pdf)
- **Loci (Wrigstad, Pizlo, Meawad, Zhao, Vitek, ECOOP 2009):** A type-annotation system for thread-locality in Java. Its dynamic study reports that "in the entire DaCapo suite, 69% of all objects are thread-local." — [Loci paper](http://janvitek.org/pubs/ecoop09.pdf)
- **Swift (BRC paper):** Over 99% (client) and over 93% (server) of objects, and about 93% and 87% of RC operations, are thread-private by dynamic measurement. — [BRC paper](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **Other cited works not read:** Aldrich, Sirer, Chambers, Eggers, "Comprehensive Synchronization Elimination for Java" (Sci. Comp. Prog. 2003), and Marlow and Peyton Jones's local-heap GHC GC. — [Jones and King references](https://www.cs.kent.ac.uk/pubs/2005/2228/content.pdf); [Marlow local GC](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/07/local-gc.pdf)
- **Later work, 2026:** An arXiv preprint "Sound Automatic Lock Placement for Concurrent Programs with Pointers" (arXiv 2611.16509) surfaced in search. Not read. — [arXiv](https://arxiv.org/pdf/2611.16509)

### Inferences
- **Precision ceiling:** dynamic studies put truly thread-local objects at roughly 45% (VolanoMark, which is thread-per-connection with heavy sharing), 69% (DaCapo) and 93–99% (Swift clients). Static analyses recover less, because of:
  - allocation-site granularity, Domani's 90%-local site;
  - flow-insensitivity, the late-escaping object;
  - statics counted as escaping.
- **Handling of thread pools, tasks, closures and statics in this literature:** these analyses treat `Thread.start` or `run` arguments, and anything stored into statics, as escaping. The Java papers predate task-based APIs. In C#:
  - `Task.Run(lambda)` captures a closure object (display class). Everything reachable from it escapes to the pool at the call. Salcianu–Rinard's fork/join modelling could recapture objects when `await` or `Wait()` joins and the task is no longer referenced.
  - `ThreadPool` and `Parallel.For` behave like fork-join with unknown worker identity.
  - `[ThreadStatic]` and `ThreadLocal<T>` storage is thread-local by construction and should not count as a global escape.
- Salcianu–Rinard's evidence suggests that, for lock and RC elision, intrathread escape (does the object ever become reachable by another thread?) captures most of the benefit. Interthread reasoning mainly matters for proving regions safe, which corresponds to cycle groups freed at a join.
- Domani's result says partial identification is not useful: 50% direct-global placement made throughput worse, while about 99% gave the full benefit. By analogy, the site classification must be near-exact on hot sites, or the mixed paths cost more than they save.

### Gaps
- Whaley–Rinard 1999 and Steensgaard 2000 measured fractions were not retrieved; ACM full text was not accessible.
- Choi et al. numbers come from secondary seminar slides, not the paper itself.
- No study was found measuring thread-escape precision for C#/.NET or for async/Task-heavy programs. This looks like an open area.

## Cost of atomics in WebAssembly (V8, SpiderMonkey, JavaScriptCore), atomics on non-shared memory, wait on the main thread, shared-everything threads

### Takeaway
All wasm atomics today are sequentially consistent. They are allowed on non-shared memory, but `wait` traps there. Misaligned atomics trap. V8 lowers `i32.atomic.rmw.add` to `lock xadd` on x86-64, so an atomic RC operation costs a native locked RMW, about 2.4x a plain increment uncontended in BRC's native measurement. The shared-everything-threads proposal adds thread-local globals, acquire/release orderings and waitqueues. It is still under active development and unimplemented in Wasmtime.

### Cited Findings
- **Threads proposal (phase 4):**
  - Non-shared memory: "All atomic memory accesses can be performed on both shared and unshared linear memories." If the memory is unshared, "the wait operation traps." Notify on unshared memory returns 0 waiters unless the host made one.
  - Ordering: all atomic accesses are sequentially consistent, and other orderings "may be provided in the future."
  - Alignment: misaligned atomic accesses trap, and the alignment immediate must equal the natural alignment. Non-atomic misaligned accesses on shared memory do not trap.
  - Memory size: shared memory must declare a maximum. Growing it is allowed and visible to all agents.
  - The explainer's example states that blocking on the main thread is not allowed.

  — [WebAssembly/threads Overview](https://github.com/WebAssembly/threads/blob/main/proposals/threads/Overview.md)
- **Main-thread wait:** Browsers throw a TypeError for `Atomics.wait` on the main thread, and Chromium's issue on lifting this for separate-process pages was closed Won't Fix. `Atomics.waitAsync` is the main-thread alternative. Emscripten busy-waits on the main thread instead. A WHATWG issue (#12570) and a shared-everything WaitOnMainThread explainer propose allowing it. — [Chromium 41476333](https://issues.chromium.org/issues/41476333); [whatwg/html #12570](https://github.com/whatwg/html/issues/12570); [WaitOnMainThread.md](https://github.com/WebAssembly/shared-everything-threads/blob/main/proposals/shared-everything-threads/WaitOnMainThread.md); [Emscripten pthreads docs](https://emscripten.org/docs/porting/pthreads.html)
- **V8 lowering:** Liftoff compiles `i32.atomic.rmw.add` to `LOCK XADDL` on x86-64. The poster's attempt to drop the lock prefix (Sept 25, 2024) had no effect, and no V8 reply was visible. — [v8-dev thread](https://www.mail-archive.com/v8-dev@googlegroups.com/msg162287.html)
- **Expected x86 lowering (LLVM, as a reference for what seq_cst wasm must map to):** seq_cst stores become XCHG (or MOV plus MFENCE), plain loads remain MOV, and RMW add becomes LOCK XADD. — [LLVM Atomics guide](https://llvm.org/docs/Atomics.html)
- **Shared-everything-threads proposal:**
  - Status: "under active development as we merge in various ideas discussed during phase 1"; "Expect frequent and significant changes!"
  - `shared` extends to tables, globals, functions, GC types, segments and tags. Shared may refer only to shared.
  - Thread-local globals: `thread_local` globals are per-thread values. The listed implementation options include a TLS base pinned in a register.
  - New atomics: `global.atomic.*`, `table.atomic.*`, `struct.atomic.*` and `array.atomic.*`. A new ordering immediate allows `acqrel` alongside `seqcst`.
  - Waitqueues: `waitqueue` objects with `struct.wait` and `memory.wait32`. Waits on unshared locations or on the main browser thread validate but trap.
  - Thread builtins: `thread.spawn_ref`, `thread.spawn_indirect` and `thread.available_parallelism`, as component-model builtins intended to be polyfillable.
  - The page gives no performance data.

  — [shared-everything-threads Overview](https://github.com/WebAssembly/shared-everything-threads/blob/main/proposals/shared-everything-threads/Overview.md)
- **Implementation status:** Wasmtime lists shared-everything-threads as unimplemented (issue #9466). Chromium has a prototype tracking bug. wasi-threads (legacy) says future work moves to shared-everything-threads. — [Wasmtime proposals table](https://docs.wasmtime.dev/stability-wasm-proposals.html); [wasmtime #9466](https://github.com/bytecodealliance/wasmtime/issues/9466); [Chromium 42204563](https://issues.chromium.org/issues/42204563); [wasi-threads](https://github.com/WebAssembly/wasi-threads)

### Inferences
- **No cheaper atomics today:** because only seq_cst exists, il2wasm cannot emit a relaxed atomic increment, which is the standard `Arc` increment ordering. Every atomic RC increment pays a locked RMW. Atomic seq_cst stores, for publishing pointers, likely pay XCHG or MFENCE. This makes "plain counts for unshared data" far more valuable on wasm than on native code, where relaxed increments exist.
- **Non-shared memory gives no relief:** a module built "without threads" uses unshared memory. Its atomics are still valid, and whether engines lower them to plain operations is unverified. The safe design is to emit no atomics at all in the threads-off build, rather than rely on engine lowering, which meets the owner's "no slower without threads" bar by construction.
- **TLS today:** until `thread_local` globals ship, per-thread data such as a BRC-style owner ID or per-thread free lists must live at a per-thread linear-memory offset held in a plain, unshared global per instance. That works because each Worker instantiates its own instance with its own globals; this is an inference consistent with how Emscripten pthreads works.
- **Main thread:** the main thread cannot block, so any lock acquired on the main thread needs a spin path or a `waitAsync` design. Lock elision for main-thread-only data avoids this entirely.

### Gaps
- No published microbenchmark was found of wasm atomic RMW against plain operations in V8, SpiderMonkey or JSC. No V8 or SpiderMonkey blog post on atomics cost was found.
- No source was found stating whether any engine lowers atomics on non-shared memory to plain instructions.
- The 2026 phase number of shared-everything-threads is not confirmed. The repo states it is merging phase-1 ideas.

## Lock elision, lock coarsening and biased locking, and their interaction with RC

### Takeaway
Biased locking directly inspired BRC. HotSpot disabled biased locking in JDK 15 because cheaper modern atomics reduced its benefit relative to its complexity. Escape-based lock elision and RC elision share one analysis, and an object proved thread-local gets both.

### Cited Findings
- **BRC and biased locking:** BRC "is inspired by biased locking" but differs: RC needs no strong exclusivity, so multiple threads can update concurrently via the split counters, and revocation is cheap and voluntary. Biased lock revocation, by contrast, needs a safepoint. — [BRC paper §8](https://docs.rs/crate/biasedrc/latest/source/paper.pdf)
- **Lock elision from escape analysis:** Choi et al. 1999 removed 11–92% of locks, median 51% ([slides](https://www.ic.unicamp.br/~edson/disciplinas/mo615/2011-2s/seminario/mo615-2s11-seminario-escape_analysis-slides.pdf)). Salcianu–Rinard removed up to 89% of executed synchronizations in Http (67,268 to 7,406) ([PPoPP 2001](https://people.csail.mit.edu/rinard/paper/ppopp01.pdf)).
- **HotSpot:** Escape analysis is used for lock elision and scalar replacement. — [OpenJDK HotSpot EscapeAnalysis wiki](https://wiki.openjdk.org/spaces/HotSpot/pages/11829250/EscapeAnalysis)
- **JEP 374:** "Deprecate and Disable Biased Locking," JDK 15 (2020). Per my recollection, the rationale was that gains seen in the past are far less evident today, since modern processors have cheaper atomics, and that the code was complex to maintain. — [JEP 374](https://openjdk.org/jeps/374). The page returned 403 to the fetcher, so the rationale wording is not verified in this session.

### Inferences
- In il2wasm, C# `lock(obj)` on a provably thread-local object can be elided, and that object's RC operations made plain, from the same escape fact. Lock coarsening similarly coalesces RC operations across a locked region. Inside a monitor held for an object graph, counts on objects reachable only under that lock can be updated plainly with the lock's acquire and release providing ordering. This gives "at most one lock or atomic per operation." It is an inference, not found in the literature.
- Biased locking's revocation cost is exactly what BRC avoids, so il2wasm should prefer BRC-style split counters over owner-biased locks for dynamically uncertain sites.
- Owner exit needs handling: CPython merges immediately if the owner thread has terminated, and Worker termination in browsers must hand queued objects over in the same way.

### Gaps
- No paper was found that measures combined lock and RC elision from a single escape analysis.
- The JEP 374 text was not retrieved.

## Determinism under threads: Kendo, CoreDet, DThreads, Grace and others

### Takeaway
Deterministic multithreading costs roughly 7–20% for lock-based deterministic ordering (Kendo, PARROT, DetLock), and up to 5–8x for store-buffer approaches (CoreDet). DThreads sometimes matches pthreads but degrades with heavy locking. For il2wasm, the cheaper path is to keep RC and finalization effects out of observable output, and deterministic for data-race-free programs, rather than make all scheduling deterministic.

### Cited Findings
- **Kendo (Olszewski, Ansel, Amarasinghe, ASPLOS 2009):** Deterministic lock acquisition ordered by logical clocks, about 20% slowdown with 4 threads. A later Kendo-T reports a 16% geometric mean on SPLASH-2 at 4 processors and 15% at 48 cores. — as summarised in [Devietti dissertation](https://homes.cs.washington.edu/~djg/theses/devietti-dissertation.pdf) and [DetLock](https://ce-publications.et.tudelft.nl/publications/1311_detlock_portable_and_efficient_deterministic_execution_for.pdf) (secondary)
- **CoreDet (Bergan et al., ASPLOS 2010):** "Up to 8x slower than pthreads," with about a 5x average in one summary. — [DThreads SOSP 2011](https://people.cs.umass.edu/~emery/pubs/dthreads-sosp11.pdf); [Devietti dissertation](https://homes.cs.washington.edu/~djg/theses/devietti-dissertation.pdf)
- **DThreads (Liu, Curtsinger, Berger, SOSP 2011):** Runs threads as processes with page-protection diffs and commits them at deterministic fences. About 3x faster than CoreDet; it "often matches and sometimes exceeds" pthreads, but costs more with heavy lock use. — [DThreads SOSP 2011](https://people.cs.umass.edu/~emery/pubs/dthreads-sosp11.pdf)
- **Grace (Berger et al., OOPSLA 2009):** Limited to fork-join programs; needs code changes to avoid large overhead. No figure was found. — [Liu dissertation](https://scholarworks.umass.edu/server/api/core/bitstreams/25fcab15-8568-47b2-8c98-ce338684b608/content)
- **PARROT (SOSP 2013):** 6.9% overhead across 55 real programs in its default mode. **DetLock:** 14% on average, at most 38%. — [PARROT](https://www.cs.columbia.edu/~junfeng/papers/parrot-sosp13.pdf); [DetLock](https://ce-publications.et.tudelft.nl/publications/1311_detlock_portable_and_efficient_deterministic_execution_for.pdf)

### Inferences
- .NET does not guarantee deterministic thread interleaving. So "identical where .NET's own output is" requires only that il2wasm add no nondeterminism of its own:
  - RC free order;
  - finalizer timing and thread;
  - hash codes;
  - allocation addresses showing through `GetHashCode` or `RuntimeHelpers`.

  Full deterministic multithreading (7–20% or more) would break the owner's speed bars.
- **RC-specific nondeterminism:** under BRC, or any shared-count scheme, the thread that runs a destructor or finalizer and its timing depend on interleaving. Queuing finalizers to a deterministic point, such as a single finalizer thread in drain order, makes this observably deterministic for race-free programs.

### Gaps
- No primary numbers were obtained for Grace or for Kendo's original paper; the figures above are from secondary summaries.
- No study of deterministic RC or deterministic finalization specifically was found.

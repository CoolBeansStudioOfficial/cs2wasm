# Lobster's compile-time reference counting and owner-based memory designs (Gel/alias counting, Inko, Vale, Mojo, Austral, Hylo/MVS)

Source dating: the Lobster documentation page is undated, but GitHub issue #169 (2022) already cites it and its "95%" figure, so the figure dates from 2022 or earlier. The Lobster source quoted here is current `master` as fetched on 2026-11-23; the latest commit was dated 2026-11-24 UTC. The Gel paper dates from about 2007 (its IBM file name is `Dingle07Ownership.pdf`), and its benchmarks ran on 2006–07 hardware. The Vale HGM post is from January 2021, the generational-references post was updated July 2023, and the Hylo/MVS paper was published in JOT in 2022. The Inko, Mojo and Austral docs are current versions, fetched 2026-11-23.

Source code citations point to raw files under `https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/`, abbreviated LOBSRC below.

## Lobster: how owned and borrowed are decided, specialization, retroactive owners, and the 95% figure

### Takeaway
Lobster gives an ownership kind to every AST node, not just to every variable. A mismatch between the kind a child produces and the kind its parent wants is fixed by inserting one inc, one dec, or an anonymous temporary. Almost all of the removed count operations come from a few defaults:
- single-assignment locals initialized from a location borrow "speculatively", and flip to owning only if something writes to what they borrow;
- parameters borrow unless the function assigns them;
- builtins borrow their arguments;
- small functions are specialized per argument ownership;
- a write that conflicts with a borrowed parameter makes the caller keep the value alive with one inc, rather than making the parameter own.

The "95%" figure is an unqualified claim: the docs give no description of the method and no per-program data. Cycles are not freed at all. They leak, and a report at program exit lists them.

### Cited Findings
**Core model**
- Ownership is computed per AST node. Each new allocation gets one owner, usually the first variable, field or vector element it is assigned to; other uses are borrows. "The initial ownership and all borrows do not require any runtime reference counting." — [Lobster memory management](https://aardappel.github.io/lobster/memory_management.html)
- Parent/child matching rules ([docs](https://aardappel.github.io/lobster/memory_management.html)):
  - both agree: no count operations;
  - parent owns, child borrows: one inc;
  - parent borrows, child owns: an anonymous variable holds the value and deletes it at the end of the scope, like a C++ temporary.
- The code implements these rules in `AdjustLifetime(Node *&n, Lifetime recip, ...)`. For each returned value it compares the given and the wanted `LifetimeType`:
  - given `LT_BORROW`, wanted `LT_KEEP`: sets an `incref` bit and calls `DecBorrowers`;
  - given `LT_KEEP`, wanted `LT_BORROW`: sets a `decref` bit;
  - `LT_ANY` on either side is compatible with anything.

  Non-reference values that depend on a borrow are changed to `LT_ANY` "to avoid it travelling any further". Values that might become references (`V_VAR`) are treated conservatively. Multiple return values carry one lifetime each (`LT_MULTIPLE`), with up to 64 per bitmask (`"too many return values"`). — [LOBSRC/typecheck_base.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_base.h)

**Per-construct rules** ([docs](https://aardappel.github.io/lobster/memory_management.html))
- Variables that are assigned always own. `let`/`for` element variables initialized from a variable, field or vector element borrow for their scope.
- `if` takes the union of its branches, and owns if they differ. `switch` always owns. `while` borrows its condition. `for` borrows the value it iterates; the element borrows unless the body writes elements, in which case each element is owned in turn.
- Assignment wants an owned value and produces a borrow. Constructors produce owned values. L-values produce borrows.
- `return` owns, except that a single returned variable is treated as a borrow.
- Builtins borrow all their arguments and return owned values by default, with per-builtin exceptions: for example, `push` owns its 2nd argument and `top` returns a borrow. In the source, natreg signature flag `'k'` maps to `LT_KEEP` ([LOBSRC/natreg.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/natreg.h)).
- String constants are borrowed and static. They hold a permanent reference and are marked in the type table, so builtins that write in place copy first.
- `s += x` appends in place when `s` is not shared.

**Retroactive ("speculative") owners**
- The docs: if the source of a borrowing variable is later written, "the variable retroactively becomes an owner". Its initializer gets the inc, and the variable gets a dec at the end of its scope. This is what makes the `old := cur` pattern work. — [docs](https://aardappel.github.io/lobster/memory_management.html)
- Code: `SpecIdent::speculative`, documented as "A variable that borrows what it was initialized with rather than owning a reference of its own (a single-assignment variable initialized from a variable, field or element, or a for loop element), as long as nothing writes to what it borrows from while it is alive". A write that hits the borrow triggers `FlipSpeculative`:
  - `sid->lt = LT_KEEP`;
  - `MakeLifetime(sid->spec_define->child, LT_KEEP, 1, 0)`, i.e. an inc on the initializer.

  If the scope ends without such a write, `ReleaseSpeculative` keeps it a borrow. `SpecBorrowable` limits this to references, and excludes structs of references and `V_VAR`. — [LOBSRC/idents.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/idents.h), [LOBSRC/typecheck_locations.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_locations.h)
- A variable defined inside a loop treats a write made earlier in the same loop body as a conflicting write. The reason given is that the variable "lives across its iterations" (`LoopWroteBefore`). — [LOBSRC/typecheck_locations.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_locations.h)

**The borrow stack: a compile-time count of borrowers per location path**
- Borrowed L-values sit on a compile-time `borrowstack`. Each `Borrow` is a path: a variable plus field/index derefs, with its own `refc`. `PushBorrowPath` reuses an existing entry with the `SamePath` and bumps its count.
- An expression that is not a path (for example `f()[i]`) "gets a generic borrow, which disables lock checks, so is unsafe" (comment in `PushBorrow`).
- After type checking the code asserts `borrowstack.empty()`. A negative count reports `"... used in ... without being borrowed"`, and the source comment on that check reads "FIXME: this should really just not be possible, but hard to guarantee." — [LOBSRC/typecheck_base.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_base.h), [LOBSRC/typecheck.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck.h)
- When a write hits a borrowed path, `CheckLvalBorrowed` tries these resolutions in order ([LOBSRC/typecheck_locations.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_locations.h)):
  1. flip the speculative holders to owners;
  2. flip speculative variables along the alias chain;
  3. `KeepArgAlive`: the caller's argument is given its own reference, an inc plus a temporary released at the end of the caller's scope. The source comment says this is "Cheaper than making the parameter own, which every call would pay for";
  4. for `f(v[0], v.pop())`, the earlier argument-list element takes a reference of its own (`MakeLifetime(c, LT_KEEP, 1, 0)`);
  5. only after all of these does it report `cannot modify X while Y borrows it`.
- Aliasing is handled path-wise. `ExpandAliases`/`owning_aliases` cover writes made through an owning variable that was assigned the same object. The older docs listed `var c = a` followed by a write through `c` as a known gap; the current source has `AliasPath owning_aliases` aimed at it. — [docs](https://aardappel.github.io/lobster/memory_management.html), [LOBSRC/idents.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/idents.h)

**Function specialization by ownership**
- Functions are type checked in call-graph order and specialized by argument types and ownership. In the docs' version there are three exceptions:
  - arguments that are assigned always own;
  - big functions, and all functions in a dynamic dispatch, get a single specialization that borrows wherever it may;
  - return values are always owned.

  — [docs](https://aardappel.github.io/lobster/memory_management.html)
- In the code, `ParamOwns(arg, type)` is `!arg.sid->id->single_assignment || argtype->t == V_STRUCT_R`. A parameter owns only if it is reassigned in the body, or if it is a by-value struct of references. — [LOBSRC/typecheck_base.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_base.h)
- The size threshold is `auto single_spec = ov.gbody_node_count > 25;`. Functions over 25 nodes get one specialization that borrows wherever allowed. The comment: "Having a lifetime per arg is mostly useful on smaller functions to not get unnecessary refc overhead on the border, especially if they later get inlined."
- Otherwise `ArgLifetime` returns the caller's own lifetime for the argument (`return c->lt;`), so a borrowed argument produces a borrowing specialization.
- Special case: a non-single-assignment variable passed to a function that also receives lambdas gets `LT_KEEP`. This guards against a lambda assigning the same variable. — [LOBSRC/typecheck_calls.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_calls.h)
- Function values (closures) called through a declared function type are always called with borrowed arguments. A parameter that needs to own copies on entry (`copy_on_entry`, one inc). Every anonymous function is specialized this way from the start. — [LOBSRC/idents.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/idents.h), [LOBSRC/typecheck_base.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_base.h)
- Borrowed parameters are aliases of the caller's path (`BindParamAliases`). Writes through them, including writes in callees, are checked against the caller's borrows. — [LOBSRC/typecheck_locations.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_locations.h)

**Globals and closures**
- Top-level variables are mostly not tracked as free variables: "This check typically culls >90% of free variables". The VM counts the references to globals it gives up at scope exit. — [LOBSRC/typecheck_locations.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/typecheck_locations.h), [LOBSRC/vmdata.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/vmdata.h)

**Fields, vectors and value structs**
- Vector elements are locations. Writing an element, or calling `pop`/`remove`/`truncate`, conflicts with borrows of any element or of the same constant index. — [docs](https://aardappel.github.io/lobster/memory_management.html)
- `struct` is inline and copied on assignment ("usually up to 4 fields or so"), is immutable, and "reduce[s] pressure" on the analysis. — [docs](https://aardappel.github.io/lobster/memory_management.html)

**Code generation**
- Values that are only borrowed by their consumer are kept in a per-function "keep" slot until the function returns, or until the loop comes around again (`EmitKeep`). — [LOBSRC/codegen_refcount.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/codegen_refcount.h)

**The 95% figure**
- The docs: "Using this analysis was able to remove around 95% of runtime reference count operations." The only evidence offered is that the author tested "several dozens of Lobster programs (many of which medium sized game prototypes)" and "needed to make minor changes in only 2 locations". No method, per-program data, or static/dynamic distinction is given. — [docs](https://aardappel.github.io/lobster/memory_management.html)
- The author refers to "that remaining 5% of runtime refcounts" and calls optimizing it "quite a bit of work ... for diminishing return gains". — [Lobster issue #169](https://github.com/aardappel/lobster/issues/169)
- Current master has a dynamic instrument, `--rcstats`: "the generated code counts every inc and dec it executes per site the code generator handed out ... and the VM counts the ones its own C++ does (builtins, deleting the contents of objects and vectors, globals given up at scope exit)". — [LOBSRC/vmdata.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/vmdata.h), [LOBSRC/compiler.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/compiler.h)

**Cycles**
- Cycles are not freed. The docs: a "'cycle report' at program exit that helps the programmer know where to break those cycles" is "largely sufficient". — [docs](https://aardappel.github.io/lobster/memory_management.html)
- Detection happens only at exit. "if you want to be using doubly linked lists, you must manually set to `nil` at least one of the links ... Not doing so is considered a programmer bug." Of about 100 programs, "maybe 3" had to clear a cycle field such as `e.enemy = nil`.
- The author rejects weak references because of their overhead (double indirection or a liveness check) and because they need `nil` to be a valid value. — [Lobster issue #151](https://github.com/aardappel/lobster/issues/151)
- The VM uses `dump_leaks = true` by default. `RefObj::CycleCheck` marks objects by temporarily negating `refc`; this is for printing and serialization, not collection. — [LOBSRC/vmdata.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/vmdata.h)

**Threads**
- Worker threads are separate VMs (`is_worker`, `vector<std::thread> workers`). They communicate through a `TupleSpace` ("all data shared between threads") with `WorkerWrite`/`WorkerRead`, and the main VM shares only string constants with them. There is no shared mutable heap, so counts need not be atomic. — [LOBSRC/vmdata.h](https://raw.githubusercontent.com/aardappel/lobster/master/dev/src/lobster/vmdata.h)

**Known failure cases (docs)**
- `cannot assign to borrowed argument` "typically never triggers", except in some dynamic-dispatch cases.
- `variable x still has 1 borrowers` and `used in <feature> without being borrowed`: "I've never even seen happen".
- The opt-in Rust-like single-owner mode, which would drop the count field, "not been implemented".

— [docs](https://aardappel.github.io/lobster/memory_management.html)

### Inferences
- The levers that give Lobster its 95%, in rough order of how many operations each removes on typical code:
  1. Function-local single-assignment variables borrow by default. Most C#/IL locals are SSA-like after the front end, so this transfers directly.
  2. Parameters borrow unless they are reassigned (`ParamOwns`).
  3. Builtins and library calls borrow their arguments, and only marked sinks such as `push` own. In .NET terms, every BCL method needs an ownership signature: which parameters it stores, and whether it returns a borrow (getters, indexers, `List<T>[i]`).
  4. Fixing a conflicting write at the caller (`KeepArgAlive`) instead of making the callee own.
  5. Per-ownership specialization of functions of 25 nodes or fewer.
  6. Static string constants.
  7. Inline value structs.

  A compiler at 53–57% is most likely missing items 3 (borrowing signatures for library calls/getters) and 4/1 (speculative borrow with flip-on-write, as against conservatively owning any local that might be invalidated).
- Lobster's correctness argument is path-based: a borrow is invalidated only by a write to a path that is a prefix of it, with aliases expanded. This depends on Lobster having no unrestricted aliasing analysis problem it cannot see. Its escape hatch, the generic borrow for non-path expressions, "is unsafe". A CLR compiler must instead treat such writes conservatively, for example by giving the borrow an owned reference, because behaviour must match .NET.
- Several things do not transfer to CLR semantics:
  - cycles are leaked (the requirement here is freeing at the cutting statement);
  - there are no finalizers or weak references (a deliberate omission);
  - threads have no shared heap (.NET threads share objects, so a cross-thread borrow is outside Lobster's model);
  - the 25-node specialization threshold trades code size for count operations, which matters for this project's size goal.
- Lobster does no interprocedural "this callee cannot free type T" reasoning of the kind Gel uses. Its interprocedural power comes only from specialization and alias binding. Gel's type-based mutation analysis (next section) is complementary, and could remove counts that Lobster's rules would keep.

### Gaps
- No primary source gives the programs, the method (static sites or executed operations), or per-program percentages behind "95%". It is unclear whether `--rcstats` existed when the figure was measured. I found no talk or blog post with more numbers. Wouter van Oortmerssen's talks were not retrieved.
- I did not retrieve the exact implementation of the exit-time cycle/leak report (`DumpLeaks` lives in VM source not fetched).
- I did not determine the date of the speculative-borrow and `KeepArgAlive` machinery, which may be newer than the docs page.

## Gel / alias counting (Dingle and Bacon, "Ownership You Can Count On")

### Takeaway
Gel is a C# subset in which every object has exactly one owning pointer (type `T^`). Non-owning pointers (`T`) are counted at run time. When an owner is destroyed, the object's fields are destroyed first, recursively, and then the object's non-owning count is checked: zero frees it, non-zero is a fatal run-time error. Because subobjects are destroyed before the check, any structure whose non-owning pointers point only at ancestors in the ownership tree (back pointers to the owner, parent pointers) is freed at once, with no extra walk. An interprocedural, type-based analysis removes 89–100% of count increments.

### Cited Findings
**Mechanism**
- "alias counting ... combines type-based object ownership and run-time reference counting of references by non-owning (aliasing) pointers. When an owning pointer is destroyed, if the target object's reference count is zero, the object is destroyed; otherwise a run-time error occurs." Gel is "essentially a subset of C# ... extended with ownership-based memory management". — [Dingle & Bacon, Ownership You Can Count On (archived IBM PDF)](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)
- "In Gel there is always exactly one owning pointer to every object." `new` returns an owning pointer. Assigning to an owning variable transfers ownership. Ownership is taken out of a field or array element with `take` (for example `Node ^p = take n.next;`), which nulls the source; this is equivalent to `T Take(ref T p)`. Boxed values have type `object ^`. Strings are reference counted separately, since "they don't point to other objects so they can never be involved in a pointer cycle." — [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

**Destruction order**
- "First Gel destroys all fields in o (in reverse lexical order). If any of these fields are owning pointers to other objects then those objects are destroyed recursively; if any fields are non-owning pointers the reference count of their referent is decremented. Finally Gel checks o's non-owning reference count."
- On a doubly linked list: "Before checking the first node's reference count, Gel destroys the second list node, which destroys the non-owning pointer to the first node and hence decrements the first node's reference count to 0."
- "In general, then, Gel can automatically destroy any data structure in which every non-owning pointer from an object o points to an object which is an ancestor of o in the ownership tree."

— [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

**Limits of destruction**
- Gel "cannot destroy arbitrary graphs of non-owning pointers". Non-ancestor non-owning pointers must be nulled by hand first.
- Destruction is recursive, so it "may use a large amount of stack space".
- Cycles of owning pointers (`MakeACycle`) are never destroyed.
- Gel has no user destructors.

— [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

**Proposed (not implemented) multi-phase destruction**
- Phase 1: run destructors down the ownership tree. Phase 2: decrement the target of every non-owning pointer. Phase 3: check that each count is zero and free.
- This would allow arbitrary internal non-owning pointers.
- Destructors that mutate the graph can make a destructor run zero times or twice. The authors call this safe but not exactly-once.

— [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

**Count elimination**
- "Suppose that, for a type T, a given block of code never mutates any pointer of type U ^, where U is T, any superclass of T, or any subclass of T. Then the code cannot possibly destroy any object which can be held in a variable of type T, and therefore any non-owning variable or temporary of type T whose lifetime is contained in the block of code need not hold a reference count."
- It is interprocedural: the analysis builds a call graph, computes for each method the set of owning types that may be mutated during a call to it, and checks each non-owning local or temporary over the control-flow nodes where it is live.

— [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

**Measured results (dynamic count of executed increments)**

Measured on a 1.83 GHz Core 2 Duo with 2 GB RAM, Ubuntu 6.10. Gel compiles to C++ and was built with gcc 4.1.2 -O2.

| Benchmark | CPU with optimization (s) | Increments with optimization | CPU without (s) | Increments without |
|---|---|---|---|---|
| sort | 4.8 | 93.4M | 5.9 | 868.2M |
| sortstring | 5.1 | 34.7M | 5.7 | 326.6M |
| binarytrees | 17.8 | 0 | 20.2 | 471.1M |
| self-compile (73,000 lines) | 5.6 | 13.0M | 6.9 | 225.7M |

- The paper's summary: "the optimizer eliminates 89% to 100% of reference count increments, speeding execution by 10% to 35%." The abstract says "typically eliminates 90% ... speeding Gel programs by 20-40%". The paper's two speed-up ranges differ. — [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)
- Comparisons in CPU seconds and resident MB. Java was the Sun JDK 1.5 server VM; C# was Mono 1.1.17 with the Boehm GC.

| Benchmark | C++ | Java | C# | Gel |
|---|---|---|---|---|
| sort 5 | 7.1 s / 16.5 MB | 5.2 s / 74.7 MB | 3.3 s / 27.4 MB | 4.8 s / 16.6 MB |
| sortstring 5 | 4.4 s / 23.6 MB | 6.5 s / 127.8 MB | 6.8 s / 33.5 MB | 5.1 s / 33.0 MB |
| binarytrees 18 | 11.4 s / 25.4 MB | 3.6 s / 92.6 MB | 17.6 s / 65.9 MB | 17.8 s / 25.5 MB (about 70% slower than C++) |
| self-compile | – | – | 5.3 s / 67.4 MB | 5.6 s / 66.8 MB |

  — [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)
- Threads: "its reference counting optimizations can not easily be applied in the presence of aliasing arising under multi-threading. As a result, Gel currently only supports single-threaded programs." — [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)
- The authors' experience: in the one large program (the compiler itself), "most objects had natural owners". — [same](https://web.archive.org/web/2019id_/http://researcher.watson.ibm.com/researcher/files/us-bacon/Dingle07Ownership.pdf)

### Inferences
- Gel's destruction order is exactly the "owner chain" case. If a compiler can prove that every non-owning edge in a group points to an ownership ancestor, then cutting the owning edge to the root frees the whole group at that statement. The cost is one recursive descent of the owned subtree, which a free must do anyway; there is no extra heap walk. Gel checks this at run time with a count. A static proof of ancestor-ness would remove the check and the count.
- Gel's elimination rule ("no code in this live range mutates an owning pointer of a related type") is a cheap, whole-program, type-based effect analysis, and it suits IL well: it needs a call graph plus the set of owning field types each method may store. It gets binarytrees to zero increments.
- Gel differs from CLR semantics in three ways: it has explicit `^` annotations where IL has none, it restricts programs to one thread, and it treats a dangling non-owning pointer at destruction as a fatal error. For il2wasm, the annotations would have to be inferred, and a non-zero count could not become an error because behaviour must equal .NET.

### Gaps
- The archived PDF is the tech-report version. I did not confirm the IBM report number or whether a peer-reviewed version exists. Nim's "owned refs" runtime proposal (https://nim-lang.org/araq/ownedrefs.html) is directly derived from this paper, but I did not retrieve it here.

## Inko, Vale, Mojo, Austral, Hylo/MVS: ownership, freeing, cycles, performance

### Takeaway
- Inko is the Gel model without the elimination optimization: single ownership, a borrow counter in every heap object, and a panic at drop if the counter is not zero.
- Vale checks a generation number at dereference instead of counting. Its static elimination of checks (region borrowing, HGM) is partly a design only.
- Mojo frees each value after its last use (ASAP), within a Rust-like ownership and origin system.
- Austral and Hylo forbid shared mutable references. Austral uses linear types with explicit deallocation; Hylo uses second-class references, so graphs become indices.

None of these frees a general cyclic graph at the statement that cuts it without a walk. Only Gel's ancestor rule, and tree-shaped ownership in MVS and Austral, give "freed at the cut".

### Cited Findings
**Inko**
- Single ownership: when the owner is done, the value is dropped, which runs the destructor and frees it. Borrows are `ref T` and `mut T`, and are applied automatically at calls. — [Inko manual: memory management](https://docs.inko-lang.org/manual/main/getting-started/memory-management)
- "each heap allocated value stores a borrow counter". "When dropping an owned value, the borrow counter is checked and a panic is produced if the count is not zero". Example message: "can't drop a value of type 'Array' as it still has 1 reference(s)". Moving while borrowed is allowed; the error comes only at drop. — [same](https://docs.inko-lang.org/manual/main/getting-started/memory-management)
- The cost is described as "a small runtime cost", and "most of the borrow count mutations can be optimized away", but this optimization is planned, not implemented. No numbers are given. — [same](https://docs.inko-lang.org/manual/main/getting-started/memory-management)
- Data is sent between processes only as `uni` (unique) values created with `recover`. Value types (`Int`, `Float`, `String`, ...) are copied on move. — [same](https://docs.inko-lang.org/manual/main/getting-started/memory-management)
- The manual does not say how back references are handled, beyond saying that linked lists and graphs are supported. — [same](https://docs.inko-lang.org/manual/main/getting-started/memory-management)

**Vale**
- Each allocation carries a generation number. A non-owning reference holds a pointer and a remembered generation, and is checked before each dereference. Owning references need no check.
- Benchmark (BenchmarkRL terrain generator):

| Mode | Time | Overhead |
|---|---|---|
| Unsafe | 43.82 s | baseline |
| Naive RC | 54.90 s | +25.29% |
| Generational references | 48.57 s | +10.84% |

  The post states the regions prototype showed "no observable overhead when using linear style and regions" but gives no number. — [Vale: Generational References (updated July 9, 2023)](https://verdagon.dev/blog/generational-references)
- Hybrid-Generational Memory adds a static check-skip "when a nearby local holds the owning reference" (tracked through fields and calls "like an automatic borrow checker"). It also adds "scope tethering": a 1-bit tether on the object, and a tethered object whose owner frees it is queued until the tether is released.
- The post says HGM "is **not** implemented yet" and "later evolved into the cell mechanism". It gives no measurements. — [Vale: Hybrid-Generational Memory (Jan 2021)](https://verdagon.dev/blog/hybrid-generational-memory)
- Vale's author has since let generations overflow and memory return to the OS. A use-after-free is caught as a segfault and the containing region is discarded, which requires reworking weak references. — [Lobster issue #169, Verdagon comment](https://github.com/aardappel/lobster/issues/169)

**Mojo**
- "Mojo destroys values as soon as they're no longer used." Destruction happens after the last use, not at the end of scope, and this includes temporaries inside an expression.
- `__deinit__` is generated for every struct with fields that need deinitializing. Fields inside `__deinit__` follow ASAP destruction too.
- A deinitializer is the only place fields can be moved out (`self.name^`) without reinitializing them.
- Writing `_ = x` extends a lifetime explicitly, for example for RAII guards.
- The page does not cover cycles or reference counting.

— [Mojo manual: Death of a value](https://mojolang.org/docs/manual/lifecycle/death)

**Austral**
- Austral splits types into a Free and a Linear universe. "a value of a linear type must be used once and only once. Not _can_: _must_."
- `Pointer<T>` is linear, so the compiler forces a `deallocate`: "We can't leak memory, we can't deallocate twice."
- Borrowing is "stolen lock, stock, and barrel from Rust".
- Linear values have "only one owner", so they can be mutated without locks across threads.

— [Austral specification](https://austral-lang.org/spec/spec.html)

**Hylo / mutable value semantics (Racordon et al., JOT 2022)**
- "references become second-class citizens: they are only created implicitly, at function boundaries, and cannot be stored in variables or object fields." Whole/part relationships "always form a tree". Arbitrary graphs are adjacency lists of indices.
- `inout` exclusivity is checked by verifying that no path appears twice among a call's `inout` arguments.
- Arrays carry a reference counter used only for copy-on-write. The paper says "copy-on-write prevents purely static garbage collection."

— [Racordon et al., Implementation Strategies for Mutable Value Semantics, JOT 21(2) 2022](https://www.jot.fm/issues/issue_2022_02/article2.pdf)
- Setup: 1,344 fuzzer-generated programs, 20 runs each, on a Xeon Gold 6154. Compilers were Swift 5.5.2 -Ounchecked, Clang 11 -O3, and Scala Native 0.4.2.
  - Results: "Swift proved slower than C++ only for programs with extremely large number of mutating operations (> 90%)".
  - Swiftlet, the authors' research compiler, also beat C++ overall, but lagged Swift on Bounce, NBody and Permute. The authors attribute the gap to missing optimizations for unique reference counts: "unnecessary reference counter checks account for most of the performance loss".

  — [same](https://www.jot.fm/issues/issue_2022_02/article2.pdf)

### Inferences
- Static proof that a part is exclusive comes from Hylo/MVS (parts form a tree by construction, and `inout` paths are disjoint) and from Austral (linearity). Mojo and Rust-like origins prove exclusivity of a borrow, not that an object graph is a tree. None of them gives a static proof that a back reference points to its owner. In every system here, a back reference is either forbidden (MVS, Austral), checked at run time (Gel's ancestor rule via counts, Inko's panic, Vale's generation check), or leaked (Lobster).
- Freeing a cyclic structure at the cutting statement with no walk works only when the cycle consists of owner-ancestor back edges:
  - Gel's destroy-children-then-check order gives this at run time, at the cost of a count per back edge;
  - a compile-time proof that a field always holds an ancestor (for example, it is assigned only `this` or the constructor's owner argument, and is never re-pointed) would let a CLR compiler skip both the count and the check for such fields.
- Inko and Gel both make "count is non-zero at drop" an error. For .NET semantics that state must instead mean "not actually dead yet". So single ownership plus checked borrows works for il2wasm only where the analysis proves the count is zero at the owner's death; everywhere else it must fall back to real shared counting.
- Vale's measured +10.84% for generational references against +25.29% for naive RC (one benchmark) is the only head-to-head number found. Generational references cannot express .NET's "object stays alive while referenced" semantics. They detect liveness violations; they do not prevent them. So they do not apply to il2wasm except perhaps for weak references.

### Gaps
- No measured numbers exist for Inko's borrow-count cost, Vale region borrowing or HGM, Mojo ASAP destruction, or Austral. Austral's borrow and region syntax was in the unread part of the spec.
- The Mojo pages fetched do not describe how Mojo handles cycles; ArcPointer pages were not retrieved.
- Hylo's current (2024–2026) compiler status and any newer measurements were not checked; the MVS numbers come from Swift/Swiftlet in 2022.

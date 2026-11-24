# il2wasm (formerly cs2wasm): audit findings and plan

Renamed il2wasm on 2026-11-23 (docs/SPEC.md section 12). The entries below are history and keep the names they were
written with: read `cs2wasm` as il2wasm, `cs2wasmc` as the command line `il2wasm`, a `CS2WASM_*` switch as
`IL2WASM_*`, `src/Cs2Wasm*` and `tests/Cs2Wasm.Tests` as `src/Il2Wasm*` and `tests/Il2Wasm.Tests`, the namespace
`Cs2Wasm` as `Il2Wasm` (the import module `cs2wasm` is `il2wasm`).

Audit of 2026-11-18 against `docs/SPEC.md`, the Invisible API design (`bazoodle/docs/invisible-api.md`) and the three
production goals:

1. The self-hosted compiler and Roslyn are each faster than their original counterparts.
2. Any output is spikeless and more memory-efficient than NativeAOT's, with speed at least equal.
3. The plugin system supports every stated and potential need of the Invisible API.

Each problem below gives its **source** (where in the code it comes from, and why) and the **fix**. Evidence is
labelled: **[run]** reproduced by running, **[read]** found by reading the code, **[inferred]** reasoned from the code
and not yet reproduced. Part B is the ordered plan.

State at the audit: suite 139 passed, 0 failed; `tools/bench.mjs` 0.95x time (5 of 9 kernels slower), 32.0 MB against
71.3 MB, 340 KB against 1,328 KB.

---

## Part A. Problems and their sources

### A1. Memory safety (blocks everything else)

**A1.1 Use-after-free when a local is rewritten during its own call. [run]**
`Use(x, x = y)` prints 50606 (expected 50303); `UseRef(p, ref p)` with the callee clearing the ref prints 606
(expected 404); `UseOut(q, out q)` prints 606 (expected 707).
- Source: `LoadVar` returns `Own.Stable` for a wasm local and for an address-taken frame local
  (`Compiler/Expressions.cs:310-315`, `Compiler/FunctionCompiler.cs:417`), and `ToCallSafe` adds no count for a
  `Stable` value (`Compiler/Calls.cs:384-395`). "Stable" assumes nothing writes the local while the borrow is live,
  but a later argument of the same call, or the callee through `ref`/`out`, can.
- Fix: a local is `Stable` for a call only if no later argument assigns it and it is not passed by `ref`/`out` in the
  same call and its address has not escaped. Otherwise retain it for the call (SPEC 4.1 "nothing is rejected": a
  run-time count where Lobster would report an error).

**A1.2 A finalizer that stores `this` leaves a dangling reference. [run]**
- Source: after the finalizer the count is forced to 0 and the block freed (`Compiler/RuntimeGen.cs:637-651`).
- Fix: after the finalizer returns, read the count again; if something now refers to the object, keep it alive and
  mark it finalized (as .NET does: no second finalization unless re-registered).

**A1.3 Weak slots can keep a freed address under threads. [read]**
- Source: `ObjectMarks.OnDestroy` returns early when `s_busy != 0`, read without the lock
  (`Upstream/System.Private.CoreLib/Boundary/ObjectMarks.cs:58`). A dying target then skips clearing its weak slots.
- Fix: take the lock in `OnDestroy` (or make the busy test and the clearing one atomic step).

**A1.4 Arena unlink guesses inside/outside from counts. [read]**
- Source: slot bits cover only the first 16 words; array elements and later fields are guessed
  (`Boundary/Runtime.cs:1821-1845`, `SlotIndex` 1368-1373). `ArenaLinkCore` returns early for busy arenas and pinned
  objects (1750-1757), leaving a counted reference between two members.
- Fix: record inside/outside per slot for every slot (a side bitmap for arrays and long objects); make link and
  unlink symmetric by construction, not by the `t_prevSlot` hand-off.

**A1.5 "Written only by constructors" edges. [read]**
- Source: the ref-return case is dead code (`&& false`, `Compiler/Arenas.cs:192`), so a private field that escapes by
  ref-return counts as immutable; a cycle through it is never put in an arena and leaks.
- Fix: treat any field whose address escapes as written after construction.

**A1.6 No stack limit check. [run + read]**
2 MB of recursive `stackalloc` on a 1 MB stack traps only after running down through the statics.
- Source: the frame prologue subtracts from the stack pointer with no comparison (`Compiler/FunctionCompiler.cs:205-206`);
  `stackalloc` likewise (`Compiler/Expressions.cs:3196-3224`). The stack sits directly above the statics.
- Fix: compare against the stack's low limit in the prologue of functions with a frame and at `stackalloc`; on
  overflow, trap (SPEC 3: "stack overflow ends the instance"). Keep a guard gap. Honour `Options.StackSize` in
  `__thread_entry` (`RuntimeGen.cs:875`).

**A1.7 The suite cannot see any of the above. [run]**
- Source: the runner compares console output only (`tests/Cs2Wasm.Tests/Program.cs:85-88`); nothing poisons freed
  memory or counts live blocks; the WASM validator runs only under `CS2WASM_VALIDATE=1` and never fails a build
  (`Wasm/WasmModule.cs:86-96`); there is no internal assertion anywhere.
- Fix: plan step 1.

### A2. Speed (goals 1 and 2)

**A2.1 The premise.** Removing the GC removes pauses and memory overhead, not mutator cost. A generational GC
allocates by moving a pointer and pays nothing for a dead object; counting pays at each retained copy and touches
every object it frees. Speed has to come from doing less counting and from better code, not from the absence of a
collector. **[reasoning]**

**A2.2 No IR between the operation tree and stack code. [read]**
- Source: `FunctionCompiler` emits `List<Instr>` directly (`Wasm/Ir.cs:99,129`); the optimizer is about 15 passes
  that pattern-match stack code (`Wasm/WasmModule.cs:159-250`), some on fixed instruction shapes
  (`Wasm/InitChecks.cs:22`) and on function names (`"rt.release"`, `Inliner.cs:158-161`).
- Consequence: no bounds-check removal, no hoisting of loads out of loops beyond `LoopLoads`, no counting removal by
  dataflow, inlining only of callees that cannot throw (`Wasm/Inliner.cs:12-20`).
- NativeAOT's module has been through LLVM before the same engine runs it, so the engine cannot close this gap for us.

**A2.3 Devirtualization is local. [read]**
- Source: only sealed receivers (`Compiler/Calls.cs:210`) and the exact type of a `new` (`EscapeAnalysis.cs:14`);
  everything else calls a dispatch stub (`Calls.cs:564`). `shapes` is 1.25x.
- Fix: class-hierarchy analysis over the constructed types (the reachability pass already knows them): a slot with
  one implementation is a direct call; with two or three, a type test and direct calls.

**A2.4 Counting removal is at 53-57%, against Lobster's 95%. [read; last recorded figure, commit 56e4ff3]**
- Source: ownership is decided per operation while lowering, with whole-program store summaries by field key (type
  based, not per object: `Compiler/FieldBorrows.cs:344-448`). Ownership specialization covers only "consumed"
  parameters on direct calls (`Compiler/OwnedParams.cs:7-36`). Getters return borrowed, callers re-retain
  (`Driver.cs:570`).
- No current figure is in the repo; `docs/STATUS.md` (cited at `Arenas.cs:17`) was deleted.

**A2.5 Thread mode is one switch for the whole program. [read + measured in commit 75e0f72]**
- Source: `SharedCounts.cs:99` ("no object is marked shared"), `RuntimeFns2.cs:60-127`, `Driver.cs:143-149`: if a
  thread start is reachable, every count is a compare-and-swap loop and every heap reference read is
  `rt.load_retain`; the heap stops using pages and never joins freed blocks (`Runtime.cs:412-414, 697`).
- Cost: the self-hosted compiler takes 19.7 s threaded on one thread, 12.6 s built without threads, NativeAOT 6.8 s.
- SPEC 4.5 asks for atomic counts only on objects escape analysis finds shared.

**A2.6 Every free touches the object. [read]** `linked_list` 1.16x, `dictionary` 1.13x.
- Source: inherent to counting; made worse by no reuse of a just-freed block for the next allocation of the same
  size, and by the count-word sign test on every retain/release once any cyclic site exists (`RuntimeGen.cs:661-674`).

**A2.7 The compile is done twice for a threaded program. [read]**
- Source: `Driver.ThreadsSeen` restarts with a new `Driver` (`Api.cs:82-90`). Also: the static-initializer loop
  recompiles to a fixpoint (`Driver.cs:1145-1168`), 49 `Descendants()` re-walks, string keys built on hot paths,
  linear `Functions.FirstOrDefault(f => f.Name == …)` scans, about 8 whole-module sweeps of Peephole and Locals.
  These are the compiler's own speed, which is goal 1.

**A2.8 The measurement is too weak to steer by. [read + run]**
- Source: nine kernels in 213 lines (`tools/bench/Programs.cs`); NativeAOT side is net10.0 with a floating
  `10.0.0-*` package (`tools/bench-naot-wasm/bench-naot-wasm.csproj:6,21`) against .NET 11; "peak memory" is the
  final linear-memory size (`tools/bench.mjs:75-79`); the self-host comparison is one program
  (`Console.WriteLine(42)`), with size and memory unrecorded; numbers live only in commit messages.

### A3. Spikes and memory (goal 2)

**A3.1 A store can walk a graph. [read]**
- Source: when a plain object joins an arena, everything it transitively holds is visited and linked
  (`ArenaLinkJoined`, `Runtime.cs:1305`; `ArenaLinkContents` 1953).

**A3.2 Arenas only merge. [read]**
- Source: `ArenaMerge` (`Runtime.cs:1322`); the only split is a single member with no inside references leaving
  (`TryLeave` 1510). So the last release visits every member, in several passes with finalizers
  (`ArenaFree`/`ArenaDestroyFrom` 1991-2092), and a detached sub-cycle stays until the arena dies
  (`Runtime.cs:1073-1076`), for the program's life if the arena hangs off a static.
- SPEC 4.3 asks for arenas "scoped to things that end", with inside references as uncounted indices; the built
  design is a run-time union structure with counted pointers (`Runtime.cs:1055-1063, 1772-1808`).

**A3.3 Freeing a large graph is one synchronous cost. [read]** Inherent to immediate release; predictable, unlike a
GC pause. Whether it counts as a spike is the owner's decision (deferring is ruled out by the owner).

**A3.4 Memory under threads only grows. [read]** No pages, no joining of blocks, 256 KB cached per thread
(`Runtime.cs:412-414, 697`). The self-hosted compiler reaches the 1 GiB limit on a real input
(`tests/Godot/proof5_hello2d/README.md:95-97`).

**A3.5 Frame-time spikes against NativeAOT-LLVM: the check, and what it found (2026-11-20). [run]**
- The check: `node tools/frames.mjs` (programs `tools/frames/Frames.cs`, NativeAOT side `tools/frames-naot-wasm`).
  Five game-like frame loops, each its own Node process for each side, 1,000 warm-up frames then 6,000 timed
  frames (Stopwatch inside the module); median, p99, p99.9, worst frame and peak memory, each the lowest over the
  runs (default 3); checksums compared; exit 1 when a scene's worst frame or p99.9 is above NativeAOT's.
  `--worst N` prints the slowest frames by number (what a frame number does is fixed: the tree's rebuild is every
  500th, bigdrop's drop every 400th).
- First result (3 runs, machine shared with other sessions), worst frame cs2wasm against NativeAOT:
  churn 8.6 ms vs 23.1 ms, collections 0.31 vs 2.56, strings 0.15 vs 0.36, **tree 31.9 vs 4.3**, bigdrop 2.8 vs 17.5
  (the drop of 150,000 acyclic objects with their arrays in one frame: the cascade free costs less than
  NativeAOT's collections). Peak memory 16 MB against 59 to 71 MB everywhere but tree (32 MB).
- **tree is the one that spikes, and not because of a cascade.** Its worst frames are the full rebuilds (every
  500th): ~15 ms to build 19,531 entities and ~4 to 20 ms to free the old tree (bigger after 500 frames of moves).
  NativeAOT builds the same tree in ~1 ms. `Entity` is cycle-capable even without its `Parent` field
  (`Entity -> List<Entity> -> Entity[] -> Entity`), so every store of the build is an arena link (ArenaLink,
  LinkMembers, ArenaJoin, ArenaMerge, ArenaArrayCopy on each list growth) and every member gets its 40-byte words:
  without `Parent` the build is still 6 to 9 ms. Its median frame is 23x NativeAOT's for the same reason: the walk's
  `foreach` over children counts each child from outside, and a child with an up-reference then holds its owner
  (UpdateHold, TopHeld along the chain) and lets it go at the release.
- Not a leak: memory grows between rebuilds because the moves enlarge the tree (reachable entities 76k, 136k, 226k
  at 1,000, 3,000, 6,000 frames without the rebuild, memory 32, 48, 64 MB in step); a re-parent that sets `Parent`
  before or after the `Add` frees the same.
- The structural fix is the compiler's, not the runtime's: a tree whose nodes own their children lists must stop
  being a run-time group while it is built and walked (7.E: a structure built new and attached whole; the walk's
  loop variable a borrow of the list it iterates). That is stream A's work in Compiler/Arenas.cs; this check is
  its measure.
- churn's median frame is 3.4x NativeAOT's (0.28 vs 0.08 ms; p99 and beyond are better): speed, not spikes;
  not looked into yet.
- Done on the walk (same day): the `foreach` over `e.Children` retained the list and each child, and a member
  counted from outside walks its owner chain (UpdateHold, TopHeld). The loop's element borrow was refused because the
  recursive call's summary said it stores array elements: through the changed-list MoveNext call (only throws; now
  marked as a throw path, its stores the throw helper's, checked against MoveNext's source), through Rt.OnThrow's trace
  buffer and string (fresh memory: its element key left out, its fields kept), and through Rt.FreeObjectArena
  (the allocator's, as Rt.FreeObject already was). The list is now held through the field's borrow placeholders, as
  a temporary is. Median 0.76 to 0.14 ms; the rebuild spike stays (29.9 ms vs 3.8 ms on a quiet machine).
- List.Add of an isolated value does not help here: array sites always count as general in Arenas.Analyze, and the
  scene's children refer to their parent. Streams I and J take more types out of the cycle graph; tree is measured
  again after they merge.
- **The rebuild in parts (2026-11-21, branch tree-spike).** `tools/tree-parts.cs` times the rebuild's two halves apart
  (homelab, quiet): build of 19,531 entities 6.0 ms, drop of the old tree 4.3 ms (NativeAOT-LLVM: the whole rebuild
  frame 2.7 to 3.8 ms). What each half was:
  - **A third of the group was garbage.** The group freed at the rebuild held 52,000 entities for 31,000 reachable
    (`last old tree`): subtrees replaced earlier stayed with the group. Cause: a member destroyed at an unlink had
    its owner let go first (ArenaUnlinkOld: OwnerLetGo, then ArenaDestroyFrom), so the destruction's UpPath could not
    walk past it, and the up-references from it (a child's parent field) stayed on the u of its old container and the
    container's owners for good. A list's array with a stale u refused its Handover when the list grew (the sum of its
    items' u no longer matched its own), kept every item it held, and those items held it. Fix: a member destroyed
    there, and one that dies with it (ArenaVisit mode 0), keeps its owner while it is destroyed. Cut tops kept: 370 of
    1,000 to 61; the group at the rebuild 34,000 entities. The 61 left: a moved node held by a local keeps holding
    the array it was removed from (it is not Owned there, only Held), and its new container waits behind it; not done.
  - **The drop's passes.** ArenaFree went round the ring four times and its visit read every member each member refers
    to (count and record: a miss each, the ring of an aged group in no order in memory). Without finalizers (and
    without threads) it is now three passes over a list taken in one walk of the ring: each member's count word zero
    first (a release of it, from another member's clear, then does nothing: rt.release at zero), then the clears, then
    the frees. Drop 4.3 to 1.9 ms (with the garbage above gone).
  - **The build's ownership.** A node built from the leaves up is plain until its first child is linked; the child's
    parent field was linked first, as the node's first counted reference, so every node was owned by its first child
    and turned round when it was put in its parent's list (TakeOwnership, Reevaluate, holds up the chain). Now a
    joining object's slot to an ownerless member that code still holds from outside decides ownership after the
    links (ArenaLinkJoined): if the member is then above the slot's object, the slot is an up-reference (Reevaluate),
    else the object owns it as before. Stores into a constructor's own object are fresh when no constructor lets
    `this` out (FreshStores.cs, ThisIsFresh): no arena made and merged per entity (19,530 merges to 3,124 per build).
    Array.Copy into a plain array only the copying code holds (a list growing) is plain counted stores, without threads.
  - The fresh constructor stores showed a hole: a plain container's field past the sixteenth word held a plain counted
    reference, but its unlink went by the counts, and a container that joined an arena at that very store read the old
    value as an inside one (its outside count never given back: test 132, 2,269 objects left). The slot's old value is
    now known plain when the container was (ArenaLink's t_prevBits).
  - The waiting owner's bookkeeping written into ArenaLinkJoined and LinkMembers gave the strings scene (no tree in
    it: StringBuilder's chunks are its cycle type) one 0.6 ms frame, at the same frame of every run on the homelab and
    gone with `--no-wasm-dynamic-tiering`: an engine tier-up, not work of the program. Out of line (LinkWaiting,
    Waiting), it is gone.
  - Tried and reverted: the ownership decision deferred to the member's last outside release (Adopt): test 127's
    async nodes and a pending task's waiter were left, held in a group by counted references no owner took.
  - Left: the build is 5.3 to 5.6 ms against NativeAOT's ~1 ms; per leaf entity the store into its parent's array still
    joins the entity and its list, links its parent field as an up-reference (UpPath, three hops), and its hold flips
    on and off around the store (the caller's temporary is an outside reference). The rebuild frame is now 7.1 to 7.2
    ms p99.9 against NativeAOT's 2.7 (homelab, frames.mjs; main: 10.2 to 10.3).
- **The build again (2026-11-22, branch claude/tree-rebuild; the desk machine, where the build was 4.3 ms).** Measured
  first, the same program with its parts taken out one by one: the 39,000 objects alone, made and freed, 0.6 ms; as
  many leaves in one flat list 1.15 ms, with their parent fields 1.6 ms. So most of the tree's cost was not its leaves
  but its 3,906 nodes with children: their lists growing from four to eight (1.6 ms, 410 ns a growth) and a node's
  own links. By the counts (`CS2WASM_DROP_STATS=1`) a growth ran four retains in the copy (each a child's first
  outside reference, which holds its owners up the chain), the new array's join with a link per element (a waiting
  owner in side words of its own, the count given back after the store by a release), Handover's two visits and the
  old array's destruction with a visit; a node with children ran four Reevaluate visits, three TakeOwnerships and
  two releases after the store, in a group of its own that was merged at once.
  - Done: a list's new array stored over the one it was copied from takes the items in one pass when the old array
    then dies at the unlink (`Rt.ReplaceArray`; 0.5 ms). `List<T>.Add` takes a new object owned (OwnedParams.cs: no
    second count on the item that comes and goes around the store; 0.3 ms). A plain object that joins for the store
    that links it is asked nothing about the owners' chain, and joins in one piece when its only count is that
    reference (`Rt.JoinOwned`; 0.2 ms). UpdateHold is called only where HoldSettled says there is something to change
    (0.3 ms). Reevaluate reads the counted slots by the bits, with no visit by type; a member's count word is changed
    without an atomic instruction in a program without threads (0.1 ms together). Build 4.3 to 3.0 ms, drop 1.5 to
    1.4; frames.mjs tree p99.9 5.9 to 4.5 ms, worst 6.3 to 4.8, median 0.071 to 0.062 (NativeAOT-LLVM 2.4, 3.1, 0.028).
    Each has its switch (`CS2WASM_NO_ARRAY_REPLACE`, `_NO_OWNED_ADD`, `_NO_FRESH_JOIN`, `_NO_SLOT_WALK`, `CS2WASM_ATOMIC_COUNTS`).
  - The new paths are out of line (JoinOwned, ReplaceArray, LinkJoinedSlot): written into ArenaLink and ArenaVisit
    they brought the strings scene's one frame of 0.5 ms back (the engine's change of tier, as LinkWaiting's did).
  - Tried and not kept: the store's count on a member given back before the store is written, where the release after
    the store would take the count off and do nothing else (0.2 ms of the build: a node put into its parent's list
    holds its owners for the length of the store). In the slots of a joining object it changed what a check at the
    drop meanwhile sees (a member no code refers to, where it had the store's count): 432 checks for 531 in the scene,
    but longer ones, the scene's p99 0.12 to 0.16 ms, and not understood further. Outside the join it gained nothing
    that could be measured.
  - Left, by the same variants: the leaves 1.5 ms (75 ns each: 31 for the two objects, the rest their two joins and
    the parent field's up-reference with its three hops), a node's own links 0.5 ms (121 ns: its group made and
    merged, the waiting owner, the visits), the growth 0.9 ms (223 ns: the copy's four retains still hold the owners
    up the chain and let them go, 13 hold changes a growth, besides the two arrays made and the one freed), the drop
    1.4 ms (three passes over 66,000 members that have lived a while). The rebuild frame is 4.5 ms against 2.4: 1.8
    times NativeAOT's, from 2.45; the parity the scene is held to is not reached. The growth's holds go only if the
    copy knows that the new array replaces the old one (List<T>'s code says so; the runtime sees two calls); the rest
    is the price of a member (its words, its place in the ring, its owner), which fewer members (7.E) or members
    kept together (7.D) would lower, not a faster link.
  - Checked: the suite 201/201 checked and as shipped (homelab); `tools/stress-cycles.sh` (tests 127 and 139 built by
    the runner, each run 800 times, 8 at once, on the homelab): 139 the same output every run, in both modes. **Test
    127 still prints `async 5` / `pending 1` in about one run in a hundred under that load, on main as on this
    branch** (main: 9 of 800 as shipped, 13 of 1,600 checked; the branch: 4 of 800 as shipped, 34 of 2,800 checked):
    the structure with two tops (A1, "Closed (2026-11-21)") is rarer than its one run in eight, not gone.
- **The rebuild, round 2 (2026-11-22, branch claude/tree-rebuild-2; the desk machine).** Measured first, by the
  counts of a program that only builds and drops the tree (`CS2WASM_DROP_STATS=1`): of 76,000 hold changes a build,
  46,000 were the growths' (12 each: the copy's four counts from outside, each item holding its owners up the chain,
  and the store's undoing of them), and ArenaRetain's quick path was never taken (15,600 calls a build, all the copy's).
  (Commit 3f6dcdd's message says 13 a growth and 51,000 of 53,000: round 1's figure, and one of the two counters.)
  - Done, the growth: `List<T>.Capacity`'s setter asks `Rt.GrowInto(this, _items, newItems, _size)` before its
    `Array.Copy` (an edit of List.cs in the manifest, the same line; folded away for a list of values, a call that
    answers no at its first test for a list whose array is no member). Where the old array would die at the store
    that follows (what ReplaceArray asks: the container's slot and its own items' holds all that refers to it, each
    of the first n elements a member it owns once, no linked element past them, the up-references that pass it all its
    items', no note of a check at the drop), `Rt.ArenaGrowArray` does the copy, the store and the unlink in one step:
    the new array joins as the container's, in the slot; each item's reference moves out of the old array into the
    new, its owner word the new array, its counts and its hold as they were; the old array, with nothing in it, is
    destroyed. No item is counted twice in between, so nothing holds and lets go: hold changes 76,000 to 30,000 a
    build, none of them a growth's. The setter's local still counts the new array until the setter returns, without
    the hold a member with up-references below it makes for a reference from elsewhere: the reference is a local of
    the container's own method, which its caller keeps, and nothing runs before it is let go. Anything else (threads,
    a container or an arena being destroyed, an item twice in the list or of another owner, a list of anything with a
    string in it) is the copy and the store as before. `CS2WASM_NO_ARRAY_GROW=1`. Build 3.05 to 2.3 ms.
  - Done, the merge: ArenaMerge points the members of an arena of eight or fewer straight at the survivor and frees
    its record, where each member's record word followed a forwarding record at its next use (and missed the
    same-arena path of ArenaLink until then): 3,124 merges a build, each of the five members a node with children
    starts with. `CS2WASM_NO_MERGE_REPOINT=1`. A joining object's slot that holds a pinned object (the empty array of
    every new list) returns before ArenaLinkCore's questions. Build 2.3 to 2.15 ms, drop 1.42 to 1.36.
  - Numbers (the same session, main bc06ea9 against the branch): `tools/tree-parts.cs` build 3.05 to 2.13 ms, drop
    1.4 to 1.35; `node tools/frames.mjs --runs 3` tree median 0.061 to 0.054 ms, p99.9 4.55 to 3.53, worst 5.14 to
    3.94 (3.72 and 3.79 in two more invocations) against NativeAOT-LLVM's 0.029, 2.40, 3.12: **1.47 and 1.2 to 1.26
    times NativeAOT's, not at or below it.** The other scenes as on main (medians 2.17x, 1.29x, 1.21x, 0.87x; worst frames
    0.14x to 0.27x). bench.mjs 0.95x. Hello world 27,031 to 27,089 bytes.
  - **The one slow frame of the strings scene is the harness's, not the module's.** Run directly (`node host/run.mjs
    frames.wasm strings`, five runs a side), the branch's module had one frame of 0.44 to 0.9 ms at frame 2995 of every
    run and one in churn at frame 3270, main's module neither, and both had one in collections at frame 3175 (0.46 to
    0.9 ms; its other frames end at 0.2). It is not a change of tier (still there with `--liftoff-only`,
    `--no-wasm-dynamic-tiering`, `--no-wasm-lazy-compilation`). `node --trace-gc` shows it: one scavenge of Node's own
    heap in a run (`Scavenge 5.5 -> 4.9 MB, 0.45 ms, allocation failure`), in main's module as in the branch's. The host
    returns the clock as a BigInt (`monotonic_ns` is an i64; two a frame for the scene's Stopwatch), Node's young
    generation fills once in a run, and the frame it fills in is fixed by how many allocations came before: inside the
    timed part of that frame or between two frames by the module's size, the directory and the environment (under
    frames.mjs in this checkout main's module had it in strings, 0.449 ms and "SPIKES ABOVE NATIVEAOT", and the
    branch's did not; run directly it was the other way round). With `--min-semi-space-size=32
    --max-semi-space-size=32` there is no scavenge in a run and no such frame in any scene of either module: worst
    frames, five runs a side, main against the branch: churn 0.30 and 0.43 ms, collections 0.18 and 0.19, strings 0.056
    and 0.057, bigdrop 1.20 and 1.19, tree 4.93 and 3.78. `tools/frames.mjs --js-young 32` runs both sides so (not
    the default). So the frame laid to Reevaluate by slot bits in round 1 (`CS2WASM_SLOT_WALK`, "cause not found"), and
    to LinkWaiting and JoinOwned written in line before it, was this; the slot walk measured again here gains nothing
    in the tree scene either way (build 2.19 against 2.13 ms) and stays off. What would end it is a clock the host
    returns without an allocation (an f64, or written into the module's memory).
  - What a build costs now, by the same program with parts taken out (`objects alone`, a flat list, a flat list with
    parent fields, a tree of fan 4 that never grows a list): the 39,000 objects made, 0.5 ms; a leaf's links, 40 ns
    (27 its two joins and the store, 13 its parent field's up-reference), 0.63 ms for 15,625; **a node with children,
    250 ns more than a leaf, 1.0 ms for 3,906**: its list and first array a group of their own (a record made, merged
    and freed), the node joined through its first child's parent field with a waiting owner in side words, its own
    parent field owned and turned round when it is put in its parent's list, three Reevaluates, and eight hold changes
    with two releases the long way for the store's count kept until the store is written (the list's, then the
    node's own with three owners above it). The drop, 1.35 ms for 75,000 members (30,660 entities reachable: some
    7,000 members are let-go parts kept with the group), by its passes (`CS2WASM_DROP_STATS=1` prints them): the walk
    of the ring 0.40 ms, the clears 0.46, the frees 0.53: 5 to 7 ns a member each, 2.6 to 5.5 in a tree just built, so
    0.4 ms of it is the order of an aged ring in memory. Nothing in a pass was found that another pass makes
    unnecessary: the frees cannot follow each member's clear (a finalizer of a plain object released by a later clear
    may allocate, and a freed member's block made anew would be released through a reference a later member still
    holds).
  - Tried and not kept: a plain array without elements stored into a plain object left plain (a list's first array:
    it leads nowhere, so the store closes no cycle; no group of the list and the array per node): build 2.19 against
    2.18 ms, the same work done when the node joins. Not tried again: the store's count given back before the links
    (round 1's; it is what the eight hold changes a node are).
  - Seen on main while writing test 191, not followed: a class's finalizer does not run its base class's (`class B :
    A { ~B() { } }`: .NET runs ~B then ~A, cs2wasm ~B alone; test 191 has one finalizer and a virtual method for
    that reason). A finalizer of a member that grows a list of its own group that has items (`Parent.Children.Add(new
    Node())` as the group goes) leaves the new objects alive for good: the list's new array, no member, counts the
    dying items, and the group is taken to have been stored where the program can reach it.
  - Checked: tests 016, 017, 127, 131, 132, 133, 139, 145, 147, 149, 150, 171, 175, 178, 179, 180, 185, 187, 190 and
    191 here, checked and as shipped. On the homelab the suite, twice: 202/202 checked both times; as shipped 201/202
    (test 187: `rounds: result 3, rounds 1`), then 202/202. **Test 187 differs under load on main too**: run by
    itself (`TESTS=187_two_tops_one_cycle bash tools/stress-cycles.sh`, 200 runs a mode, 8 at once, twice), main with
    that option alone 19 of 800 (6 and 5 as shipped, 5 and 3 checked), the branch 20 of 800 (3 and 5, 3 and 9): the
    two-tops structure of test 127, at one run in forty. `tools/stress-cycles.sh` on the branch, twice: 139 the same in
    all 800 runs; 127 differed (`async 5` / `pending 1`) in 1 and 0 of 200 as shipped, 4 and 2 of 200 checked; main in
    the same hour 3 of 200 and 3 of 200. A program with threads runs none of this round's code (ArenaGrowArray answers
    no at once, the merge and the pinned slot are behind `!ThreadMode()`), and tests 127, 139 and 187 start threads.
- **The rebuild, round 3 (2026-11-22, branch claude/tree-rebuild-3, from main d061dda; the desk machine).** Measured
  first: the paths a build takes, by the runtime's counts (`CS2WASM_DROP_STATS=1`, which now prints them at each large
  drop: the difference of two lines is one build) of a program that only builds and drops the tree
  (`tools/tree-build.cs`, variant 0; its other variants take parts of the build out), and a profile of it by function
  (`node --cpu-prof` on a module built with `CS2WASM_NAMES=1`).
  - **The counts of one build on main** (19,531 entities, 3,906 with children; 42,968 members: an entity, its list,
    and an array for each list with items). Rt.ArenaLink 42,967 times: 19,531 the empty array of each new list
    (List<T>'s constructor stores a pinned object into a plain one: through the lock and ArenaLinkCore's questions to
    "a counted reference"), 3,906 a list's first array (3,125 of them, a leaf's parent's, make a group of the list
    and the array, merged into the tree's as the node joins: 3,124 merges), 19,530 an item into its parent's array
    (15,625 leaves by JoinOwned, 3,905 nodes as members of the same group). Members joined 46,874 (the 3,906 arrays
    of the growths among them), each visited once as it joins (42,968). For the 3,906 nodes: Reevaluate 14,060 (3.6 a
    node), TakeOwnership 10,935 (2.8), UpdateHold 21,090 with 14,060 hold changes and 15,620 steps up a chain,
    Rt.ArenaRelease 10,937 of which 7,031 the long way; 12,500 up-reference links (the parent fields of a node's
    second to fifth leaf), 3,906 growths.
  - **What the hold changes were.** A node put into its parent's list is a member whose one reference from outside
    is the store's own count, kept until the store is written (GiveBackLater). Its parent field becomes an
    up-reference in that link (Reevaluate, the ownership turned round: each node owns its parent from the link of its
    first child's parent field until then), and a member with an up-reference below it that is referred to from
    elsewhere than its owner holds its owners up the chain: three holds made, and three undone by the release after
    the store, which takes the long way for it. The same for a node's list as the node joins (its count the
    node's own slot's). Round 1 gave the count back early in the slots of a joining object, saw the scene's checks
    at the drop change, and dropped it; "outside the join it gained nothing".
  - Done, the store's count: `Rt.EarlyGiveBack`. Where the release after the store would take the count off and do
    nothing else (ArenaRelease's first case, asked at the link: a member without an owner that holds nothing, which
    the container owns from this link on, TakeOwnership's turn of a node that owned its parent included and asked as
    there; other members of the arena referred to from outside; no note of a failed check on either), LinkMembers
    gives the count back before it takes the ownership, in a joining object's slots as elsewhere. The end is the
    same; no hold is made in between. Hold changes 14,060 to none a build, UpdateHold 21,090 to 7,030, releases the
    long way 7,031 to 1 (the root's). The scene's checks at the drop are what they were (557 in a run of
    tools/tree-parts.cs, 373,385 members gone through, on main and on the branch). `CS2WASM_NO_EARLY_GIVE_BACK=1`.
    Build 2.10 to 1.91 ms in tools/tree-parts.cs by the switch alone, 2.03 to 1.81 in tools/tree-build.cs.
  - Done, the empty array: a pinned object stored into a plain object's slot returns before the lock, with what
    ArenaLink notes for the unlink that follows (`CS2WASM_NO_PINNED_SKIP=1`). 0.03 to 0.05 ms a build (1.90 to 1.87 and
    1.96 to 1.91 ms, the lowest 1.85 to 1.81 and 1.91 to 1.86: at the edge of what the timings tell apart, a build
    varies by 0.05 ms from run to run).
  - **The drop.** Its three passes over the old tree's 75,000 members on main: the walk 0.33 to 0.40 ms, the clears
    0.46, the frees 0.53. The clears and the frees fused in one loop gained nothing (1.32 against 1.32 ms: it is
    not the second pass over the memory that costs), and the walk without the clears takes what it took (0.33 to
    0.40): a chain of loads. So the walk's wait is where work can go, and the frees' cost is their own.
  - Done, two passes: the clears in the walk (`Rt.ArenaFree`, a group without finalizers, without threads, not in a
    checked build). A member's count is made zero as its turn comes, then its references are released; a reference
    to a member the walk has not come to finds a count of "no reference from outside", and rt.release does nothing
    for that count (it went to Rt.ArenaRelease for it: a release too many, which a checked build still reports
    there). The members are listed as they are cleared and freed from the list after the last clear, as before: a
    freed block may be used again by whatever a clear lets go (a finalizer's allocations, another group's drop),
    and a later member may still refer to it. **Before a finalizer runs** (the first code of the program since the
    walk began: `rt.before_fin`, called in the destroy functions inside the hold, `Rt.ArenaBeforeFinalizer`) the
    members the walk has not come to are made as the walk before the clears left them (count zero, off the list of
    members to destroy, the note of a failed check gone), in every such pass under way, so a finalizer sees what
    it saw: the members cleared so far the same ones (the ring's order), the rest dying. `CS2WASM_NO_FUSED_DROP=1`.
    (`Rt.ArenaBeforeFinalizer` by that name: FieldBorrows' store summaries take `Rt.Arena*` as the runtime's own;
    under another name every release in the program "stored anything", and the scene's ordinary frames went from
    0.051 to 0.16 ms.) Drop 1.04 to 0.87 ms by the switch alone.
  - Done, the frees: `Rt.ArenaFreeList`. A member's block that lies in a page of small blocks goes straight back
    onto its page's list (FreeSmallOld's case; PageFree for a page that was full or is empty now): the page knows
    its blocks' size, so no dispatch on the type and no call of its free function, and not by way of the block
    kept aside for the next allocation of the size, which every free here put on its page one free later. The
    allocator's map is asked once for each page met, not for each member, and side words are freed where a member
    has them. Any other block (a large array) as before. `CS2WASM_NO_FREE_LIST=1`. The frees 0.53 to 0.25 ms; drop
    1.13 to 0.87 by the switch alone.
  - Numbers (the same session, main d061dda against the branch): tools/tree-parts.cs build 2.14 to 1.91 ms, drop
    1.33 to 0.88 (the walk with the clears 0.46 to 0.64, the frees 0.25); tools/tree-build.cs (a tree just built,
    42,968 members) build 2.05 to 1.84, drop 0.53 to 0.36. `node tools/frames.mjs --runs 3 --js-young 32`: tree
    p99.9 3.61 to 2.97 ms, worst 3.88 to 3.12, median 0.056 to 0.052, against NativeAOT-LLVM's 2.40, 3.13, 0.029;
    the two modules run in turn five times, the lowest: p99.9 3.65 to 2.84, worst 4.01 to 3.09. **The worst frame
    is at NativeAOT's (0.99x); p99.9 is 1.18 to 1.24 times NativeAOT's, not at or below it.** The twelve rebuild
    frames of a run, three runs: 2.70 to 3.13 ms (main 3.31 to 4.01). NativeAOT's own rebuild frames take 0.26 ms
    (the objects of a new tree are a pointer moved each), and four of the twelve have a collection in them, 2.33 to
    2.49 ms and one of 3.10 to 3.15: its p99.9 and its worst frame are its collections, this side's the rebuilds
    themselves, every one of them, so p99.9 here is the middle rebuild. For it to be at 2.40 a rebuild has to take
    0.45 ms less. The other scenes as on main (five runs each in turn: churn median
    0.104 and 0.104, p99.9 0.176 and 0.173; collections 0.008, 0.170 and 0.171; strings 0.009, 0.019; bigdrop 0.003,
    1.160 and 1.159; worst frames within each other's spread). bench.mjs 0.95x to 0.96x with the switches and
    without. Hello world 27,089 bytes, unchanged (it has no group).
  - Tried and not kept (each measured by itself, none moved the number): a null slot not visited (a test in the
    generated visit functions; 15,624 visits of empty elements a build); a slot that holds a pinned object not
    visited; a member's clear leaving out the slots whose bits say "linked" (0.85 against 0.85 ms); the waiting
    container kept in the join's list, not in side words (no block of side words for each node, 3,906 a build:
    build +0.03 ms, drop -0.02); the members sorted by type before the clears and the frees (no dispatch missed,
    but the order in memory lost: drop 1.32 to 1.43); the ring walked from both ends at once, the back's half
    cleared from the list after (0.88 to 0.98).
  - **What a build and a drop cost now** (tools/tree-build.cs's variants, medians): the objects alone, made and let
    go, 0.56 ms; the tree without parent fields 1.39; with them 1.84; without the growths (capacity 8 set first)
    1.67; so the growths 0.17, the parent fields 0.45, the joins of 39,000 objects with no parent in it 0.8. By
    function (a profile; the engine puts small functions into their callers): Rt.ArenaVisit with what it calls
    for a joining object's slots 0.38 ms, Rt.ArenaLink 0.35, the program's own Build 0.27, the allocation 0.26,
    LinkMembers 0.26, ArenaGrowArray 0.09. A node with children still costs about 170 ns more than a leaf (by round
    2's 40 ns for a leaf's links, not measured again): its list's group made and merged, the ownership of its parent
    taken and turned round, three Reevaluates. The drop:
    11.6 ns a member (8 the walk with the clear, 3.3 the free).
  - **What reaching NativeAOT's p99.9 would take** (0.45 ms more of 2.85): not a faster path, fewer
    members. 15,625 of a build's 42,968 members are the lists of leaves, each holding nothing but the pinned empty
    array: a join, a visit, a place in the ring and a member's free each, by the costs above 0.2 to 0.3 ms of a
    rebuild (an estimate, not measured; a plain list still has to be made, released and freed), so not the whole
    0.45 by itself. Such a list
    could stay a plain object until its first array is stored, but the runtime rests on "a member refers to no plain
    object of a cycle-capable class": the plain list would have to name its holder (in its owner word, the
    holder's slot marked by the fourth value of its bits), join with that slot linked when it first takes a
    linkable reference or is stored anywhere else, and have the note taken off when the holder's slot is
    overwritten or the holder dies (its clear, the destroy visit, the drop); every reader of the slot bits (some
    fifteen places) has to know the fourth value. A change of the runtime's invariants with its own tests, not
    started here. The other way is step 7.D (a group's members in pages of its own, released by returning the
    pages: the drop without a pass over the members).
  - Seen and not followed: the same tree built with the parent set after the child is in its list
    (`e.Children.Add(c); c.Parent = e;`, tools/tree-build.cs variant 2) takes 5.5 ms a build, three times the
    scene's order (5.9 on main).
  - Checked: test 192 (groups let go whole: plain objects with finalizers held by the members, whose finalizers
    allocate, make and drop a group, let another group go and ask a weak reference to a member not yet cleared;
    a member holding the last reference to a member of another kind of group; a dictionary, arrays with holes, an
    array too large for a page, a pinned object in the members' lists, groups made apart and joined, the stores'
    states: bottom-up, top-down, a child in two lists, a root stored into its own descendant). On the homelab at
    aeab011: the suite 203/203 checked and 203/203 as shipped; `tools/stress-cycles.sh` (200 runs a mode, 8 at
    once): 139 the same in every run, 127 differed in 1 of 200 as shipped and 2 of 200 checked (main's rate), 192
    the same in every run of both modes. The checked build takes none of the two-pass drop nor the free list
    (`Rt.Checked()`), the early give-back and the pinned store both; a program with threads none of the four.
- **The rebuild, round 4 (2026-11-22, branch claude/tree-rebuild-4, from main d62ff76; the desk machine; not merged
  when this was written).** The frames that are not a rebuild first (tools/tree-frame.cs: the new subtree's build 15.9
  us, its store over the old one 9.8), by the counts and by a program that only builds a subtree of 156 entities and
  stores it over the one before (`tools/tree-store.cs`, new; its variants: without parent fields, the parent set
  after, the subtree a group of its own let go whole, a node of the old subtree held over the store).
  - **Why one replaced subtree in twenty-five stayed** (`drop checks 557 ... fail-inside 557` in 11,461 frames of
    tools/tree-parts.cs). Not a missed case of the unlink: an up-reference made through an owner that had let go. The
    scene's move takes the last child of a node `c` (a local holds it: with its parent field an up-reference below
    it, it holds its old array, the array its list, the list `c`, and keeps the array for its owner though the array
    let it go), sets `moved.Parent = to`, then `to.Children.Add(moved)`. When `to` is `c`'s own parent (one time in
    twenty-five), `to` is on the moved child's chain through that hold, and the store was an up-reference: its u on
    the old array, the old list and `c`, the child still holding the old array when the old parent reference went.
    The Add then found the child with an owner and left the new array waiting. From then on the child, in its new
    list, held `c`'s array from inside; when `c` was replaced, no count of it reached zero, and its check at the drop
    found one reference from outside what `c` owns (the child's hold): `fail-inside`, and `c` with all it owned stayed
    until the group went (in .NET it is unreachable). (The figure "30,660 entities against 19,531" of the note before
    this is the tree as the moves have grown it, all reachable; what stayed was 7,000 to 9,000 members of the 75,000
    at a rebuild.) Done: a new up-reference asks for a chain of owned links (`Rt.OnChainOwned`,
    `CS2WASM_NO_OWNED_CHAIN=1`): through an owner that let go the reference is a counted one; the old up-reference
    goes at the unlink after the store, the hold with it, the child has no owner, and the list that takes it next owns
    it and makes the reference an up-reference then (TakeOwnership, Reevaluate), as for a move to any other node. No
    walk: one flag read at each step of the walk the link made already. tools/tree-parts.cs: checks at the drop 557
    to 0, the group at a rebuild about 75,000 members to 67,380, 499 frames without a rebuild 72,752 members for
    68,260 reachable to 68,264; drop 0.73 to 0.89 ms to 0.67 to 0.69, the other frames' p99 0.112 to 0.072 ms (the
    failed checks were those frames: with this switch alone off, 0.105).
  - **The destroy of a subtree at a store**, member by member through its owners (`Rt.ArenaDestroyFrom`): 31.5 ns a
    member (10.8 us a store of tools/tree-store.cs) where the same subtree as a group of its own, let go whole, takes
    5.8 (2.0 us). Without parent fields 26.8: the up-references were 5 ns of it. By function (a profile): the visit of
    each dying member by its type (`rt.arena_visit`, the type's visit function, Rt.ArenaVisit for each slot behind
    eight tests of other modes, UpPath's two walks for each parent field, Kill), three more passes over the dying
    (counts, clears, frees through each type's free function). Done, each with its switch:
    - `Rt.DyingPath` (`CS2WASM_NO_DYING_UP=1`): a dying member's up-reference to a member that is dying too, every
      member between them dying, is not taken off their u (nothing reads the u of a member being destroyed) nor are
      their holds settled. 10.1 to 8.8 us a store alone.
    - `Rt.ArenaDestroyPlain` (`CS2WASM_NO_FAST_DESTROY=1`), in a group without finalizers, without threads: the same
      steps without what only finalizers need: no references noted for a finalizer, the visit's state set once and
      not around each member, a member's count zero as its turn comes (a reference a later member holds to it is left
      in its slot for the clear's release, which does nothing at zero, as one to a member dead before always was), and
      the blocks back onto their pages without the dispatch on the type (ArenaFreeList's way). Rt.ArenaVisit asks for
      mode 0 first (`Rt.VisitDying`). With the next one off: 6.3 us.
    - The dying member's linked references read by its slot bits, or as the elements of an array of references, with
      no visit by its type (`CS2WASM_NO_DESTROY_BY_BITS=1`). The bits tell of the first sixteen words; a new flag,
      Far, says a slot past them was linked (the u count starts one bit higher for it), and such a member, and an
      array of structs or of rank above one, is visited by its type as before. 6.3 to 4.2 us.
    tools/tree-store.cs: 10.1 to 10.9 us a store on main, 4.1 to 4.2 on the branch, 31 to 12.2 ns a member; by the
    passes (`CS2WASM_DROP_STATS=1` now times them): the visits 18.3 to 6.8 ns, the counts 2.0 to none, the clears
    2.5, the frees 3.4. tools/tree-frame.cs: the store 9.8 to 4.0 us (p99 15 to 7.6), the move's mean 1.5 to 0.66.
    What is left against the group's 5.8: each dying member is taken off the ring by itself (its neighbours written)
    and its count decided from its owner's reference; a group let go whole does neither.
    Not changed: a subtree replaced while code holds a node of it (variant 4) costs 11.3 us at the store, 33 ns a
    member: the check at the drop that fails goes through the subtree three times and notes each member under its top
    (side words for each).
  - **The parent set after the add** (`e.Children.Add(c); c.Parent = e;`, tools/tree-build.cs variant 2: 5.5 ms a
    build for 1.9). By the counts of a build: 6,875 checks at the drop, all failing, 68,500 members gone through by
    them; none of the 3,906 growths taken by ArenaGrowArray (3,125 Handovers the long way); 38,280 releases the long
    way; where the scene's order has no check and 3,907 such releases. Cause: the node, its list and
    the list's array's owner are plain objects until the first child's parent field is stored; that store joined the
    node as the property of the child at once (a plain object that joins for a store is its container's), and as the
    node's list then took the array, the ownership of the child was turned round: every node the property of its
    first child, that child a top, its parent field a counted reference for good. And worse than slow: a subtree of
    such a tree stored over another was never destroyed before its group went (tools/tree-store.cs variant 2: 1,700
    members destroyed in 20,000 stores of 343; test 193's `after` counts, 2,012 for .NET's 832). Done
    (`CS2WASM_NO_STORE_WAITS=1`): a plain object that code still holds, stored into a member's own slot, joins with
    its owner left open until its slots are linked, as a joining object's slot already did (Waiting, LinkWaiting):
    then the node is above the child, and the child's field an up-reference. The store's slot does not hold the value
    yet when LinkWaiting runs (Reevaluate read null there, the first version of this): `Rt.ReevaluateWaiting` is
    told the slot. Build 5.5 to 2.35 to 2.46 ms, drop 0.67 to 0.50; the subtree is destroyed at its store (5.2 us).
    What is left above the scene's order (1.9): the local `c` is a reference from outside while its parent field
    becomes an up-reference, so the child holds its array, the array its list, the list the node, and lets them go
    when the local goes: six hold changes a child, 117,000 a build, and each child joins by the general link (its
    count is two). The scene's order hands the child to Add as its last use; this one cannot.
  - **A leaf's list left a plain object** (step 4 of round 3's list), worked out and measured, not done. What it
    takes: (1) the join of an object's slots leaves a plain object only that slot refers to, whose own slots hold
    nothing that can be linked (null or pinned), plain, the holder's slot a counted reference with bits 0; (2) the
    plain object names its holder and the slot (two of its words, free while it is plain), because whatever makes it
    a member later (a store into it: the list's first array; a store of it anywhere) must link the holder's slot
    then, or the group has a counted reference from a member to a member that no link knows of and never goes
    (seen at once in the prototype: in the scene's order a node joins before its first child is in its list, so
    every node's list was left plain and empty, took its array later, and the tree stayed for good); (3) every way a
    plain object becomes a member goes through ArenaJoinOnly, which can ask; the name can be stale (the holder's slot
    stored over, the holder destroyed or let go whole, with no pass that could clear it), so it is a hint, checked by
    what is there: a live member at that address whose slot, a slot of its type with bits 0, holds the object;
    ReplaceArray and ArenaGrowArray, which join without ArenaLinkJoined, refuse an object with a hint. The paths
    that rest on "a member refers to no plain object of a cycle-capable class" and would need it: ArenaLinkCore's
    join of a plain container (a new group of the list and its array, merged into the holder's by the hint),
    JoinOwned and LinkMembers' fresh case ("its words are zero", "above nothing": still true, the object owns
    nothing), TryLeave (a member that holds only such an object leaves, and the hint then names a plain object:
    refused by the check), the clears and the destroy (the object is released as any plain reference: right as they
    are), the check at the drop (visits by type, passes a plain child by: right). Finalizers: only a class without
    one, or its finalizer would run at the holder's clear and not with the group's. Measured with the prototype
    (sound only where a list has its array before its node joins): tools/tree-build.cs variant 3 build 1.67 to 1.61
    ms and drop 0.30 to 0.26, variant 2 build 2.38 to 2.27 and drop 0.49 to 0.38: 0.1 to 0.2 ms a rebuild, less than
    round 3's estimate, and in the scene's own order each of the 3,906 nodes with children would pay a group of its
    own, a merge and the hint's link for its list, about what the leaves save. Not worth a second kind of reference
    between members.
  - **The allocation** (tools/tree-build.cs variant 4, the objects alone: 0.57 ms for 39,062, 15 ns each with their
    constructors, two random numbers and their release). By the profile 60% is the program's own function with the
    allocation written into it (a block off the size's run, three stores), 17% Rt.ArenaLink: the one avoidable call,
    `_items = s_emptyArray` in List<T>'s constructor, 19,531 a build, about 0.1 ms. It is not a fresh store because
    CtorsKeepThis asks every constructor of the class and `List(IEnumerable<T>)` uses `this`; asked of the
    constructor that runs and those it chains to, it would be. Not done (a change of the compiler's analysis for 3%
    of a build).
  - Numbers (main d62ff76 built beside the branch, run in turn three times): `node tools/frames.mjs --runs 3
    --js-young 32`, tree: median 0.052 to 0.047 to 0.048 ms (NativeAOT-LLVM 0.028 to 0.029: 1.8x to 1.65x), p99
    0.109 to 0.115 to 0.074 to 0.075 (0.048 to 0.051), p99.9 2.94 to 2.99 to 2.67 to 2.71 (2.38 to 2.44: 1.22x to
    1.11x), worst 3.14 to 3.27 to 2.74 to 2.87 (3.09 to 3.21: 0.89x). **p99.9 is not at NativeAOT's: a rebuild
    frame has 0.28 ms too much**, its build unchanged at 1.9 ms. The other scenes as on main (churn median 0.105 and
    0.105, collections 0.008, strings 0.009, bigdrop 0.003; their p99.9 and worst within each other's spread).
    bench.mjs 0.95x to 0.96x on both. Hello world 27,089 bytes (it has no group). tools/tree-build.cs: variant 0
    1.81 to 1.92 and 1.88 ms, 1: 1.40 and 1.36, 2: 5.5 to 2.4, 3: 1.70 and 1.68, 4: 0.57, 5: 1.92 and 1.88.
  - Seen while writing test 193, on main too, not followed: an array of two dimensions of a class that can be on a
    cycle, held in a member's field (`Node[,]`), keeps its whole group for good (Arenas.ArraySite answers nothing
    for a rank above one, so the array is no member and its elements are counted references to members that no
    link knows of). A member that refers back to a member above it through an array's element or a field past the
    sixteenth word (`n.Raw[6] = n`, `n.W.Back = n`), let go, stays until its group goes: the array is the container
    waiting to own it, and Dropped makes no check for a member with one.
  - Checked: test 193 (a tree that lives on while subtrees are replaced and moved, counted against .NET while it
    lives: both orders of the parent's store, the move to the old node's parent, a node of a replaced subtree held
    by a static and by another list, wide members, arrays with holes and of structs, nodes with finalizers, plain
    objects whose finalizers allocate and replace a subtree of the same tree); it fails with
    `CS2WASM_NO_OWNED_CHAIN=1` and with `CS2WASM_NO_STORE_WAITS=1` and passes with each of the destruction's three
    switches. On the homelab at b3c5175: the suite twice, 203/204 checked and 204/204 as shipped (test 143: the host's `Atomics.waitAsync(...).value.then is not a function`, engine.mjs line 228, a wait that had ended already; by itself 100 loaded runs a mode the same every time), then 204/204 both ways; `tools/stress-cycles.sh` (200 loaded runs a mode, 8 at once): 139 the same in every run, 127 the same in 200 as shipped and differing in 1 of 200 checked (`async 5` / `pending 1`, main's rate), 193 the same in every run of both modes. Not run: the self-hosted compiler.
- **The churn loop (2026-11-22 to 23, branch claude/churn-1, from main 77b2aad; the desk machine; not merged when this
  was written).** The scene with the worst median: a static `List<Particle>`, 300 to 500 born a frame, every particle
  updated and the survivors moved down with `s_particles[w++] = p`, the tail removed; 19,400 live. Median 0.105 ms
  against NativeAOT-LLVM's 0.049 (2.14x) on main; its worst frame is a quarter of NativeAOT's.
  - Measured first, both sides (`tools/churn-parts.cs`; `tools/churn-move.cs`, new: the scene's move apart from the
    order in memory and from the frees; `tools/churn-order.cs`, new: what the order in memory costs the read loop).
    us a frame, cs2wasm against NativeAOT-LLVM: the scene's loop 102.7 against 45.0; the same loop over 19,392
    particles allocated in order, none dead (every store into the element's own slot) 75.1 against 40.4, with the
    first 400 dead and held elsewhere (every survivor moves 400 slots down, no free) 76.6 against 41.2, without the
    store 46.0 against 29.6. So of the 57.7 between the two loops: **the order in memory 26** (102.7 less 76.6;
    NativeAOT 3.8: its collector keeps the list's order in memory), the frees in it (400 particles a frame at 6.6 ns:
    2.6); **the store with its counts 30.6** against 11.6; the read loop 16.4 (46.0 against 29.6), of which **the
    static read again 9 to 13** (the list in a local by hand: the scene's loop 89.7, the read loop 1.95 ns a particle
    against 2.39) and the NaN test on float stores 2 to 5 (SPEC 7.3).
  - The move (lead 1), read and measured, nothing built. The engine's optimizing tier puts the indexer's getter
    and setter and the three count functions the setter calls (`rt.retain`, `rt.store_ref`, `rt.release`) into the
    loop (`node --trace-wasm-inlining`: all of them, 789 bytes to 2,031; `--no-wasm-inlining`: the loop 206 us):
    no call is paid for a count that only adds or takes one. Inlined by the compiler instead
    (`CS2WASM_INLINE_THROWING=40`): the setter grows past what the engine then inlines, the store through the list
    2.4 to 5.4 ns: not that way. What a moved survivor costs is what the store does: the list's size check, the
    array's null and index checks, the array's type check (kept for a type parameter's array so that the family's
    bodies are one; NativeAOT's shared code checks through its helper too), the count of the new value (load,
    test, store), the exchange, the count of the old one (null test, load, test, store), the version: four stores
    where NativeAOT has two and a card test. An array store alone: 1.28 ns against 0.66. The old value is the new
    one only until the frame's first death, which is among the oldest particles, at the list's start: a test for it
    would pay in no frame of this scene. A dead particle: 6.6 ns (the dispatch on its type, its string's count, the
    block onto its page), 2.6 us a frame of 87: left. The two counts of a move are **what counting costs** here
    (12 us a frame in the cache): nothing short of knowing the moved value's old slot is overwritten or cut off
    later takes them out, and that is not known at the store.
  - **The static read once before the loop** (`CS2WASM_NO_STATIC_HOIST=1`; Compiler/StaticLoads.cs decides,
    Wasm/StaticLoads.cs moves the read). The loop read `s_particles` three times a particle, each with its null
    check: the loop stores and calls, so LoopLoads leaves it and the engine cannot know the slot is not stored. A
    function is given a static's slot when: the field is a reference, not thread-static, read with a plain load,
    and the program has no threads (another thread may store it, and release what it held); no code takes its
    address and reflection cannot set it; the function stores it nowhere (FieldWrites: with the stores that only
    replace a null, which the borrows leave out, and those without a place); nothing it calls does (StoreSummary
    over FieldWrites under AllStoresPrefix; an indirect call by what it reaches; a call of the host by the exports
    and the table). The one store the summary leaves out is the type's initializer's own: a loop that refers to the
    type's initialization flag at all (a check left in it, an initializer inlined) is left alone, so the check that
    came before a read in the loop was made before the loop. Each outermost loop of such a function then reads the
    slot once, before the loop, into a local (the local holds no count: the static holds its object while the
    function runs). Test 195: each condition's loop (a callee, the loop itself, `??=` here and in a callee,
    initializers that run inside the loop, ref, out, Interlocked, Volatile, a ref parameter, reflection, a delegate,
    a function pointer, virtual and interface calls, a handler, recursion, nested loops, an iterator, generic
    statics, a null static); with a condition taken out of the compiler its loop's output changes (seven of them,
    each tried). Not covered: a static auto-property read through its getter (the function has no read of the slot
    when it is decided); a program with threads (none is hoisted).
  - **Which page is used again** (`CS2WASM_NO_RIPE_PAGES=1`; Runtime.cs, PageAlloc, programs without threads). The
    read loop over the same particles in other orders (`tools/churn-order.cs`, us): in order 45.4, at random 58.9,
    at random within 16 KB 48.0; a page's blocks used in 48 visits (what the scene has: every frame's new particles
    go nine to a page into all 44 pages, where the 400 dead of the frame before lay) 57.7, in 8 visits 54.8, in 4
    52.9 (49.7 when each visit is in address order), in 2 49.7 (46.1). No order of reuse inside a page helps while
    every page is nearly full: a page has to be left until much of it is free, and that is memory. Prototypes on
    the scene's loop (87.7 to 89.1 as it was): a page used again when an eighth is free 91.4 against 93.7 beside
    it, a quarter 89.0, half 88.4; only when empty 76.3 to 78.9 (the particles' pages 44 to about 70); half free
    and first in line 83.5; an eighth in use, or half free and passed over four times, 78.5 to 79.1, **and the tree
    scene's heap 9.6 to 13.3 MB** (its pages wait half empty and never empty), its p99.9 2.66 to 2.71-2.78 ms: not
    kept. What is in: the size's pages with freed blocks are a ring, a page that gets its first freed block goes
    to the back; an allocation that needs a page takes, of the three at the back, one a quarter of which is free
    (freed in one go, still in the cache: the page taken before this change); else, of the eight at the front, one
    that is nearly empty (an eighth in use at most) or one that has lost no block since it was last looked at and
    has a quarter free; else an empty page, cut in order, unless memory would have to grow for it (then the front
    page as it is). A page that is still being emptied is left to empty. The alloc and free fast paths are
    untouched. The scene's loop 101.4 to 96.3 us a frame without the static read's change, 89.9 to 81.9 with it; the births 5.4 to 5.0.
    What it costs is memory: the heap's top at the end of the run, churn 2,887 to 3,271 KB (the particles' pages 44 to
    68 at 16 KB, 414 KB of them free against 30), tree 9,561 to 9,881 KB (its three sizes' pages 269 to 314);
    collections, strings and bigdrop the same; the memory itself (what frames.mjs calls the peak) 16 MB in all five
    as before, since no page is taken that memory would grow for. Hello world 27,089 to 27,426 bytes.
    Test 196 (objects of one size dying one by one, in runs, with a few kept in every page, all let go, and again
    with the memory nearly full and past it; every object read back; the ring's front not moved on at an unlink, or
    [12] left set in a page taken, change its output).
  - The look at the back first came from a measurement that the quiet machine does not show: with other programs
    at work on it (the other session's builds), ten runs in turn, the tree scene with the front alone had median
    0.060 ms against 0.054 and p99.9 4.7 against 4.3, every run; quiet, the same either way. With the back looked
    at first, under that load again (six runs in turn): median 0.051 to 0.052 against 0.051, p99.9 4.61 against
    4.37 (lowest of six; one sample, not repeated). **Not settled: the tree scene's rebuild frame under load.**
  - Result, quiet machine, `node tools/frames.mjs --runs 5 --js-young 32` twice, the switches set (main's paths)
    and not, in turn; median / p99.9 / worst, ms:
    churn 0.104-0.105 / 0.169-0.184 / 0.270-0.325 to **0.087** / 0.134-0.173 / 0.157-0.307 (NativeAOT-LLVM 0.049 /
    1.12-1.15 / 1.14-1.18: **2.14x to 1.78x**); tree 0.047-0.048 / 2.71-2.86 / 2.94-3.17 to 0.048-0.049 / 2.68-2.73
    / 2.84-3.00 (0.029 / 2.39-2.46 / 3.10-3.18); collections 0.008 / 0.173-0.174 / 0.187-0.190 to 0.008 /
    0.175-0.176 / 0.201-0.203 (0.006 / 1.04 / 1.28-1.29); strings 0.009 / 0.020-0.024 / 0.047-0.055 to 0.009 /
    0.021-0.025 / 0.053-0.056 (0.008 / 0.064-0.068 / 0.23-0.24); bigdrop 0.003 / 0.96-1.02 / 1.00-1.09 to 0.003 /
    0.95-0.96 / 1.01-1.02 (0.003 / 6.2-6.3 / 8.9-9.0). Each alone, five runs in turn, churn median: 0.104 main,
    0.093 the static read, 0.098 the pages, 0.086 both. `tools/churn-parts.cs`, us a frame, main / the static read
    / the pages / both / NativeAOT-LLVM: birth 5.6 / 5.5 / 4.9 / 4.9 / 8.0; the loop 101.6 / 88.5 / 95.4 / 82.0 /
    44.7; the removal 0.5 / 0.5 / 0.5 / 0.5 / 0.2; the loop without its move 57.7 / 48.7 / 51.4 / 43.6 / 30.1.
    bench.mjs three times in turn: 0.94x to 0.95x against 0.95x (no kernel moved).
  - What is left of churn's loop, 81.4 us against 45.0 (`tools/churn-move.cs` on the branch): the order in memory
    17 (81.4 less 61.9 in order, less the frees; NativeAOT 3.8), the frees 2.6, the store and its counts 24.6
    against 11.6, the read loop 7.7 (37.3 against 29.6: the NaN test 2 to 5, the rest the array loop itself, 0.60
    ns a particle against 0.49). What counting costs and stays: the two counts of a move, about 12, and the frees in
    the frame, 2.6 (they are what the scene has in place of the collector's pauses: worst frame 0.16 to 0.31 ms
    against 1.14 to 1.18). What the memory model leaves open: the order in memory, which only more memory buys
    without moving objects (pages used again only when empty: 76.3 for 80.3, and 1.6 times the particles' pages);
    the list setter's checks beside the counts (about 1); the read loop's 3 to 4.
- **Tests that differ under load, the part that is on main (2026-11-23, claude/load-flakes-safe).** How such a
  test is hunted: tools/stress-cycles.sh for rates; a test made of many rounds of the one that fails rarely
  (tests/Load/219: 3 runs in 100 where test 131 gave 1 in 600); a build with CS2WASM_ARENA_REPORT=1 whose whole
  output is kept for the runs that fail; tools/oracle-load.sh for what .NET itself prints when eight processes run
  the test at once; and tools/selfhost-time.sh after any change of the runtime, because the suite's programs are
  too small to show a check that walks a whole structure at every store (a8a50fc's did: the self-hosted compiler
  did not end hello world in ten minutes, and nothing in the suite or the stress said so).
  - Test 087's trap: an async iterator's promise was completed without a count of its own; the consumer, on
    another thread, let it go while the completing thread was still inside its TrySetResult.
  - Where the statics fall: with threads they start on a cache line, the runtime's first and the program's
    after them, the locks' words and s_failedTops on lines of their own (Driver.StaticAddress). Test 139 at ten
    times its length was 3.00 s or 3.43 to 3.47 s by the same code, by whether the test's Node.Live shared a
    line with the allocator's words (CS2WASM_STATIC_PAD, CS2WASM_PRINT_STATICS).
  - What tests asked that .NET does not promise (088's timer, 135's two timers, 127's and 187's count right
    after Task.WhenAll): by .NET's sources; .NET's own output never differed in 400 loaded runs of each.
  - Open, with their programs in tests/Load: a parent and its children left for good when a task dies on its
    pool thread after the main thread let the tree go (219, 215b: closed on claude/load-flakes-1 by Rt.TurnHold,
    the stale note taken off a held top and Rt.TakeInAbove, not merged: see docs/FINISH.md's row); List.Add from
    four threads at once (219_no_lock).
- **The rebuild, round 5, and test 139's trap (2026-11-23, branch claude/tree-rebuild-5, from main 77b2aad; the desk
  machine, shared with another session's timings; not merged when this was written).**
  - **Test 139's trap under load is the engine's view of a grown memory, not the cycle runtime's.** The trace was on
    the homelab all along: tools/stress.mjs appended a failed run's whole stderr to `<module>.fail.txt` (it printed six
    lines of it), and the file of the run that trapped was still in hl/claude_tree-rebuild-4/tests/out/shipped:
    `RuntimeError: memory access out of bounds` at wasm-function[1127]:0x23f77, called by [13]:0xfc3, by
    `Program.Work(Int32 t)` [1152]:0x25aa6, the task's delegate and the thread pool below. The same test built here
    from main with and without `CS2WASM_NAMES=1` (the code sections byte for byte the same; every offset of the trace
    falls in the body its index names): [1127] is `Rt.LoadRetainSlow`, offset 50 of its body its second atomic load,
    of the count of the object whose reference it has just read from the place; [13] is `rt.load_retain` at its call
    of it. So: a thread read a reference from a place the threads share (the lock-free read first saw a member with no
    reference from outside, count -2, and went the slow way), read the place again, and the word at that address was
    "out of bounds". Nothing of round 4 is on that path.
    What it is (tools/grow-race.mjs, new, Node 24.19, V8 13.6): a shared memory grown by one thread is not grown for
    the others at once. Each instance keeps the size it last saw (`memory.size` answers that number) and V8 brings it
    up to date at the thread's next stack check (a function's entry, a loop's head) or when the thread grows the
    memory itself. Plain loads and stores are not checked against it (the pages are there). Atomic accesses in code
    of the baseline tier, and copies and fills in either tier, are. One worker inside a function without a stack
    check, the main thread grows the memory, writes a word in the new pages and hands the worker its address: the
    atomic load traps in 20 trials of 20, the plain load in none, a fill in 20, `memory.size` answers the pages
    before the growth; with `--no-liftoff` the atomic load traps in none, the fill in 20. Test 139 grows its memory
    once in every run, 256 to 512 pages (`CS2WASM_PRINT_MEMORY=1`: its sixteen threads' stacks), while the first
    threads already run; LoadRetainSlow is cold code, in the baseline tier for the whole run. A thread stopped
    between its last stack check and its read of the place, while another grew the memory, made a node in the new
    pages and stored it there, traps: a window of a few instructions, once a run. By the runs: one in 3,800 as shipped on
    main 6e854e4 (run 114 of the first 200), none in 36,000 of this branch before the fix (6,000 twelve at once,
    30,000 twenty-four at once, built with names).
    Done (`src/Cs2Wasm/Wasm/MemSync.cs`, the last pass over the bodies of a program with threads): before each
    atomic access and each copy or fill, `if ((address >>> 16) >= memory.size) memory.grow(0)`, which brings the
    instance up to date (the address was handed out after its pages were there). Not before an access at a constant
    address (a static: below the size the memory starts with); where the access's other operands are constants or
    locals the test goes in before them, else they are set aside in locals. A first version asked a word the growing
    thread wrote (the size in pages): every count of sixteen threads read one cache line, test 139 with ten times
    its iterations 3.02 to 3.45 s; by the address, a shift and a compare of what the thread has at hand, 3.03 and
    2.97 s: no cost seen. Test 139's module 453,750 to 460,099 bytes (260 places). `CS2WASM_NO_MEM_SYNC=1` leaves it
    out (a measurement). tools/grow-race.mjs with the same test written before the access: no trap in 20 trials,
    load or fill.
    **Shown on the module itself:** without the engine's stack checks (`node
    --no-wasm-stack-checks`: a thread then learns of a growth only by growing) main's module of test 139, as
    shipped, traps in 7 runs of 60, each at the trace's own two frames (wasm-function[1127]:0x23f77 called by
    [13]:0xfc3); this branch's module built without the pass in 3 of 60 (two there, one at another atomic access);
    with the pass in none of 300. Test 197 (new: threads that count, lock, add to by Interlocked, copy from and
    clear what another thread has just made in pages the memory grew by, six growths) so run: 60 of 60 without the
    pass, none of 60 with it; with the stack checks it passes either way (30 runs each here; on the
    homelab 20,000 loaded runs without the pass with `--liftoff-only`, no trap: in a compiled program every call
    and every loop is a stack check, and the window stays a few instructions). The suite's runner runs a program a second time with the flags of a `// node-flags:` line,
    and test 197 has `--no-wasm-stack-checks`: it fails with `CS2WASM_NO_MEM_SYNC=1`. What is not shown is a run
    with the stack checks, as the one that trapped was, made to trap again: at one in tens of thousands the runs
    before and after the fix tell nothing apart (36,000 before, 30,000 as shipped and 4,000 checked after, on the
    homelab, none).
    The host had a failure of its own, seen in these runs: at the entry's return it waits for the threads that
    keep the process alive by `Atomics.waitAsync(c, 1, n).value.then(..)`, and when one of them ended between the
    load of n and the wait, the answer is no promise ("not-equal") and `.then` a TypeError, exit code 1: test 143
    once in round 4's suite, test 197 in 4 of those 20,000 runs. host/engine.mjs looks again
    then.
    Found by reading on the way, both real, neither shown to fail a run: `Rt.s_waitSlot`, `s_waitObj`, `s_waitVal`
    (round 4's store whose value waits for its owner) and `Rt.s_trExt` (31c6310: how many other tops a check at the
    drop took in) were plain statics that two threads' links and checks of two groups wrote at once; each is the
    thread's own now, as the lists they go with. (In test 139 the pair a link looks up is never the store's own
    container, and its threads' nodes are one group after their first stores, under one lock: by reading, not what
    trapped.)
  - **`_items = s_emptyArray` in `List<T>`'s constructor is a fresh store** (`Driver.CtorKeepsThis`,
    `CS2WASM_NO_CTOR_FRESH=1`): a constructor's stores into its own object are plain counted stores when that
    constructor, the one of its class it hands over to (`: this(...)`) and the bases' constructors let `this` out
    nowhere; `CtorsKeepThis` asked every constructor of the class, and `List(IEnumerable<T>)` calls `Add`. The
    other constructors do not run for this object, a derived class's field initializers cannot name `this`, and
    its constructor's body runs after this one returns. Rt.ArenaLink 42,967 to 23,436 a build. tools/tree-build.cs,
    main and the branch in turn five times, the middle run (and the five): the objects alone (variant 4) 0.57 to
    0.45 ms (0.57 with the switch), no parent fields (1) 1.35 to 1.25, the scene's build (0) 1.86 (1.83 to 1.91) to
    1.81 (1.72 to 1.99; with the switch 1.90), the capacity set first (3) 1.69 to 1.62, fan 4 (5) 1.95 to 1.85;
    tools/tree-parts.cs build 2.01 to 2.04 against 1.92 to 1.98 ms; tools/tree-store.cs 100 to 102 ns an entity
    against 98 to 99. Between 0.05 and 0.1 ms of a rebuild (the pinned store's early return had made the call cheap
    already, round 3).
    **A leak found in the class's test, on main too:** a lambda or a local function in a constructor that names a
    field of `this` captures it, and CtorsKeepThis passed every `this` whose parent is a field reference: `_next =
    () => _count++;` was taken as a store into an object nothing else knows, a plain counted store of a delegate
    that refers to the object, and the object was never freed (test 194's `counter 20` for 0; none left with
    `CS2WASM_NO_FRESH_STORES=1`). A constructor with such a lambda lets `this` out.
  - **The parent set after the add** (`e.Children.Add(c); c.Parent = e;`, tools/tree-build.cs variant 2): the six
    hold changes a child are gone where the store is the local's last use (`CS2WASM_NO_LAST_CONTAINER=1`). The
    runtime cannot tell that the reference that makes the child hold its owners is about to go; the compiler can:
    `c.F = v;` as the last reference to the local c in the text (Compiler/LocalMoves.cs, IsLastContainer: c declared
    by its own statement and never assigned again, the store a statement, not in a loop c is declared outside of,
    every other use of c in a call or a store statement that declares nothing and makes no lambda, so nothing read
    through c is still held after the store). Around the link `Rt.ArenaLastBegin(c)` and `c = Rt.ArenaLastEnd(c)`;
    where the link makes the slot an up-reference (LinkMembers, ReevaluateSlot), `Rt.LastGiveBack` gives the local's
    count back first, as ArenaRelease's first case would take it had the release come before the store (the arena
    lives by other members, c owned, referred to from inside, holding nothing, no up-reference below it yet, no
    note of a failed check), and only if the value does not need the hold c would have made (it is referred to
    from inside, or from outside by more than the store's count); the local is cleared, so the release at the end
    of its scope finds nothing. Otherwise nothing changes. c lives on by its owner while the store is written.
    Not with threads. By the counts of a build: hold changes 117,180 to 0, releases the long way 23,437 to 3,907.
    Variant 2, five runs in turn, the middle one (and the five): main 2.33 ms (2.26 to 2.44), the branch with this
    switch 2.29 (2.19 to 2.30), without 1.91 (1.87 to 2.11); the scene's order 1.81: what is left is the child's
    general link, its count being two at the add.
    The scene sets the parent first, so tools/frames.mjs has nothing of it. A child handed to a method that does
    both (`void AddChild(Node c) { Children.Add(c); c.Parent = this; }`) keeps its holds: the caller's reference
    is there, and stays.
  - **A leaf's list left a plain object: still not done.** Nothing new against round 4's account (a hint in the
    plain list, checked wherever it becomes a member; 0.1 to 0.2 ms a rebuild in the prototype's sound cases, paid
    back by the nodes with children in the scene's order). With the constructor's store out of ArenaLink a leaf's
    list costs its join, one visit that finds the pinned array, and a member's place in the drop.
  - **Where a build's time is now** (`node --cpu-prof` on a module with names, tools/tree-build.cs variant 0, 3,000
    builds; of the 2.4 ms a build and its drop take there; the engine puts LinkJoinedSlot, JoinOwned and ArenaJoin
    into their callers): Rt.ArenaVisit, a joining object's slots, 0.41 ms; Rt.ArenaLink 0.30; LinkMembers (the
    parent fields' up-references: OnChainOwned's walk, UpPath's two) 0.28; the program's own Build 0.24;
    AllocObjectArena 0.23; ArenaFree 0.17 and ArenaFreeList 0.10 (the drop); ArenaGrowArray 0.09; ArenaUnlinkOld
    0.08; List.AddWithResize 0.07; the clears 0.08; List's constructor 0.05; LinkWaiting, ArenaLinkCore, ArenaMerge,
    PageAlloc 0.03 to 0.04 each. By the counts of a build (19,531 entities): 23,436 links, 46,874 joins (32,812 in
    one piece), 42,968 visits of a joining member, 12,500 up-references linked in LinkMembers, 14,060 Reevaluates,
    10,935 TakeOwnerships, 3,124 merges, 3,906 growths, 3,907 releases the long way, no hold change. The objects
    alone (variant 4, 0.45 ms, 11.5 ns an entity with its list, their constructors, two random numbers and the
    release of both): the allocation is two calls of AllocObjectArena (a block off the size's run, three stores),
    the constructor two exchanges through rt.store_ref with the release of an old value that is null (`Children`,
    by the fresh store's inline test; `Name = ""`, whose placeholder InitStores leaves a release because the
    object initializer writes Name too). An initializer's old value is always null in a class (its
    field initializers are the first code that has the new object). Tried and removed: such a store written as the
    link and a plain store, with no exchange and no release (the way an object initializer's store into a field
    its constructors leave null already goes): variant 4 0.45 to 0.44 ms, variant 0 inside its spread; the engine
    has the two small functions in line already. The allocator's own
    path was not touched (another session's).
  - Numbers (main 77b2aad built beside the branch, run in turn three times; `node tools/frames.mjs --runs 3
    --js-young 32`): tree median 0.047 to 0.048 ms on both (NativeAOT-LLVM 0.028 to 0.029: 1.65x), p99 0.072 to 0.075
    and 0.074 to 0.076 (0.051 to 0.057), **p99.9 2.667, 2.681, 2.690 on main and 2.653, 2.689, 2.666 on the branch,
    against NativeAOT-LLVM's 2.492 to 2.751 in the same six invocations: not told apart from main, and above
    NativeAOT's in three invocations of six, below it in three** (its p99.9 is a collection, which moves by 0.25 ms
    from one invocation to the next; this side's is the middle rebuild, the same to 0.04 ms); worst 2.80 to 2.88 and
    2.81 to 3.05 (3.18 to 3.63). The rebuild's 0.07 ms does not show at p99.9. The other scenes as on main (churn
    median 0.104 to 0.106, collections 0.008, strings 0.009, bigdrop 0.003; their p99.9 and worst within each
    other's spread). bench.mjs 0.95x and 0.97x (main 0.97x twice). Hello world 27,089 bytes (no group, no thread).
    **Not reached: the tree's median (1.65x) and a p99.9 reliably at NativeAOT's.** What a rebuild still does that
    NativeAOT does not is join 42,968 members one by one and take each off the group again; the parts above are
    each a few hundredths of a millisecond, and none of them is the 0.2 to 0.3 ms that would put p99.9 below
    NativeAOT's lowest.
  - Checked: test 194 (constructors that keep `this` in and that let it out, by a static list, a method, their
    argument, a lambda, a local function, `: this(...)` to either kind, a base, the object's own field; a list class
    built as List<T> is; a cycle closed after the constructors; the last store in its shapes: the child used before,
    a value that is the top of the child's own chain with no other reference, the child in its own field, in no
    list, moved, the local declared outside the loop, the parent past the sixteenth word and by a property, nodes
    with finalizers, what was read through the local used after the store; counted against .NET while the trees
    live; on main `counter 20` for 0) and test 197 (above). On the homelab at e8ba580: the suite 206/206 checked and
    206/206 as shipped (and at 4f8e35b, before the runner's second run of test 197); `tools/stress-cycles.sh` (200
    loaded runs a mode, 8 at once): 139 the same in every run, 127 differing in 3 of 200 as shipped and 1 of 200
    checked (`async 5` / `pending 1`, main's rate; 2 and 3 at 4f8e35b), 193, 194 and 197 the same in every run of both
    modes. Test 139 with the pass: 30,000 loaded runs as shipped (24 at once) and 4,000 checked (12 at once), the
    same in every one. Without the engine's stack checks (`NODE_FLAGS=--no-wasm-stack-checks`, 8 at once): built
    without the pass, 7 runs of 1,000 as shipped end in the trap (at LoadRetainSlow's load, at the copies of TrPush
    and Defer); with it, none of 3,000 as shipped and none of 1,000 checked. One of those 3,000 printed `16 3`: two
    nodes left, the kind of leak the test was written for, not a trap; none such in the 34,400 runs with the stack
    checks; not followed. Not run: the self-hosted compiler.
- **The build, round 6: where an entity's 100 ns are, and the new object that joins in one step (2026-11-22 to 23,
  branch claude/tree-build-6, from main 1f7a807; the desk machine, shared with another session's timings; not merged
  when this was written).** The question was why an entity with a list of children and a parent reference costs six
  times NativeAOT's to make, and what a tree built in the scene's order needs. Measured first, nothing built.
  - **The memory model has no floor above NativeAOT's here.** A program of the parts (19,531 entities each with
    its list, made in a loop, kept, then let go; not kept in the repository): a class that cannot be on a cycle,
    kept in a local array, build 0.15 to 0.17 ms and drop 0.17 to 0.18; Entity itself, with its member words, kept
    in a local array nothing else knows (plain counted stores, no group): 0.19 to 0.20 and 0.19. NativeAOT-LLVM's
    build of the same objects in a rebuild frame is 0.26 ms (round 3). tools/tree-build.cs's variant 4 (0.45 ms)
    makes and frees one block over and over and says less than this. So allocation and the frees are 10 ns an
    entity with its list, at NativeAOT's, and all of the rest of a build's 1.75 ms (the lists' arrays and their
    growth, about 0.2) is the bookkeeping of the group: about 1.3 ms, 67 ns an entity.
  - **What the bookkeeping is, by the same program:** the entities added to one node's list that has room, no
    parent field: 0.70 to 0.73 ms, so 26 ns for the two joins of a leaf and its list; with the parent field 0.95 to
    0.97: 13.6 ns an up-reference. **A node with its first leaf, in the scene's order** (`e = new { Parent = p };
    e.Children.Add(new { Parent = e }); p.Children.Add(e)`, 9,765 such pairs under one root): 2.5 ms, 256 ns a
    pair, where two leaves cost 98. By the counts of a pair: five joins, five visits, three links, an arena made
    for the list and its first array and merged into the tree's, side words for the node (a waiting owner), four
    Reevaluates, three TakeOwnerships, two early give-backs. From tools/tree-build.cs's variants 0 and 5 (fan 5
    and fan 4): a leaf 52 ns of links, a node 90 and 33 for its list's growth. By function, with the engine's
    inlining off (`node --no-wasm-inlining --cpu-prof`, a build with names; the times double, the shares are what
    it is for): the program's Build 5.0%, ArenaVisit 4.4, ArenaFree 4.1, rt.release 4.0, ArenaJoinOnly 3.7,
    ArenaLinkJoined 3.4, rt.arena_clear 3.2, ArenaLink 3.1, rt.store_ref 3.1, HoldSettled 2.9, and fifty more
    between 0.7 and 2.5: no function is the cost.
  - **The cause, then, is not a part that is slow but how an object becomes a member:** it is made a plain object,
    its slots filled by plain counted stores (FreshStores), and at the store that puts it into the structure it
    joins and every slot it holds is gone through again as the general store it stands for (the list of joined
    objects, the visit by type, ArenaVisit's modes, LinkJoinedSlot, JoinOwned or LinkMembers with all their
    questions): some hundreds of instructions for a leaf whose outcome a few loads decide. And in the scene's
    order a node is joined from below, through its first leaf's parent field, before anything owns it: its list
    and first array are a group of their own by then, the node waits for an owner in side words, LinkWaiting
    finds it above the leaf, its own parent field makes the first child the owner of the parent (a counted
    reference, ownership turned round when the node is put into its parent's list).
  - Tried first and not kept, each alone: the joined object's slots read from a word of its type in
    ArenaLinkJoined, with no visit by type (build 1.77 to 1.76 ms: the engine has the visit in line); the fused
    step entered at ArenaLink's top, before its questions (0.53 against 0.53 ms in the flat program). The
    interpretation's overhead is in neither place by itself.
  - **Done: `Rt.JoinFresh`** (`CS2WASM_NO_FUSED_JOIN=1`; `Rt.ArenaSlots`, a type's slots as a word, eight at most
    among the first fifteen words, 0 for anything else). A new object (count 1) stored into a member's slot whose
    own slots hold only what fixes the outcome is joined and linked in one step, nothing left in ArenaJoin's list:
    an empty slot, a pinned object, an object of another group of classes: nothing; a new object of its own that
    only that slot refers to and whose slots hold nothing to link (its list): its own, after it on the ring, in
    the slots' order, as the visit joins them; and one reference up, either to a member on the container's chain
    of owned links that code still refers to from elsewhere (a leaf's parent: an up-reference, its count given
    back, UpPath as LinkMembers runs it), or to a plain object that code still refers to and that holds, in one
    slot, the member at the top of the container's chain, which nothing owns and nothing else refers to (the node
    whose list takes its first item): the node joins as nothing's, the top of the chain becomes the node's, the
    field an up-reference, and the node's other slots that hold members go through the general link of a joining
    object's slot (its own parent, with the merge), then LinkWaiting. Anything else is refused before a word is
    written and goes JoinOwned's way: another kind of reference in a slot, a type without the word, the member's
    last reference from outside, a second reference up, a plain object of the group in a slot of the node, a busy
    or merged arena, threads. The end state is the general way's (argued in the function's comment: the order of
    the node's list and the up-reference against the node's other slots is the one difference, and those links
    read the node's owner, which is none either way); the node gets no side words. By the counts of a build:
    12,476 leaves and 2,500 nodes' first leaves take it; the 625 first leaves whose node's own parent is a plain
    object still, and what they join above them, go the general way.
  - Numbers. The flat program: two joins 0.70 to 0.56 ms, with the parent field 0.96 to 0.74, a node with its
    first leaf 256 to 205 ns. tools/tree-build.cs, the switch on and off in turn five times while the machine was
    loaded (the middle run, and the five): variant 0 build 2.04 ms (1.97 to 2.06) to 1.74 (1.70 to 1.84), drop
    0.41 and 0.40; 1 (no parent) 1.38 to 1.27; 2 (the parent after the add) 2.10 and 2.09; 3 (capacity first) 1.82
    to 1.57; 5 (fan 4) 2.00 to 1.82; on the quiet machine before, variant 0 1.77 to 1.62. tools/tree-store.cs: the
    build 99 to 112 ns an entity to 90 to 92, the store 11.7 to 12.2 ns a member and 11.1 to 12.0. tools/tree-frame.cs,
    an ordinary frame: the build 17.0 to 18.3 us to 15.4 to 16.6, the store 3.8 to 4.6 and 4.2 to 4.3, the walk
    30.8 to 33.2 and 30.6 to 31.5 (NativeAOT-LLVM's, from round 5: 2.3, 0.1, 29.3). tools/tree-parts.cs build 2.02
    to 2.17 ms to 1.87 to 2.06. `node tools/frames.mjs --runs 3 --js-young 32`, off and on in turn three times,
    the desk loaded: tree median 0.049, 0.052, 0.050 off and 0.052, 0.051, 0.048 on (NativeAOT-LLVM 0.029 to
    0.031); p99.9 2.776, 3.043, 2.698 off and 2.847, 2.869, 2.545 on, against NativeAOT-LLVM's 2.49 to 2.74 in
    those six invocations; worst 2.87 to 3.50 and 2.77 to 3.54 (3.23 to 3.46): not told apart there. On the
    homelab, this side alone (`node tools/frames.mjs --no-aot --runs 3 --js-young 32 tree`, new: the homelab has
    no Emscripten SDK; off and on in turn three times): median 0.061, 0.061, 0.060 off and 0.058, 0.058, 0.059 on;
    p99.9 3.276, 3.292, 3.283 off and 3.024, 3.017, 3.012 on; worst 3.46 to 3.64 and 3.08 to 3.17: **the rebuild
    frame 0.27 ms shorter (8%), the median 4%.** By that ratio the desk's 2.67 ms would be 2.45, at
    NativeAOT-LLVM's 2.42 to 2.48. bench.mjs, off and on in turn twice: 0.94x and 0.95x (this side's own times the
    same both ways, 11.8, 1.6, 1.0 and 3.3 to 3.4 ms; NativeAOT-LLVM's shapes ran 0.9 and 0.8); its module 324.1
    to 326.1 KB. Hello world 27,426 bytes, as on main (no group in it).
  - **Not reached.** A rebuild's build is 0.2 to 0.3 ms shorter (12 to 15%), an ordinary frame's 2 us of 50; the
    tree's median is still 1.6x NativeAOT-LLVM's and its p99.9 is at NativeAOT's, not below it in every
    invocation. The remaining build, 1.6 ms for 19,531 entities, 82 ns each, against 13: the allocations 10 ns;
    a fused leaf's two joins and up-reference 28; what still goes the general way is every node: its list and
    first array as a group of their own (an arena record made, merged, freed: 3,125 a build), its own parent
    field as a counted reference that makes its first child-node its parent's owner until it is put into the
    parent's list (Waiting, LinkWaiting, TakeOwnership, the turn at the add with EarlyGiveBack's walk and a
    Reevaluate by a visit), the 625 first leaves under a plain parent, the lists' growth. None of that is a floor
    of reference counting; it is what the owners' chain asks when a tree is made parent first: the runtime
    learns that a node is a parent only when a child is already below it. What would end it is not one more
    path: a node made with a member for its parent would have to join as the parent's at that store, nothing's
    until its list takes it (round 1 tried the ownership left open until the member's last release and took it
    back for test 127), or the parent field proven a reference back (step 7.E), which takes Entity out of the
    groups altogether and leaves the 10 ns. Both change when something is freed and are the owner's to decide.
  - Seen on the way, on main too (the same with the switch off), not followed. **A program that assigns a
    node's list of children anywhere keeps every subtree that is stored over until the whole tree goes**
    (`tools/tree-assign.cs`, new: `built 121 replaced 161 again 201 gone 0` for .NET's `121 121 121 0`; the line
    `s_tree.Kids = new List<Node>();` behind a test that is never true is enough; test 198's first version, with
    such a line in a method the tree in question never ran, 6,653 nodes for 655 after 300 frames). With a second
    place that stores the field the list is no longer borrowed across `e.Kids.Add(Tree(e, ..))`: the building
    method counts it for the length of the call (one more rt.retain and its release in `CS2WASM_DUMP=P.Tree`), a
    list with up-references below it that is referred to from elsewhere than its owner holds its owner
    (UpdateHold, a hold that is kept), and what those holds leave is not found when the subtree is let go. The
    scene tree of tools/frames.mjs has no such line. Test 131 (`0 5 5` for `0 0 0`): once as shipped in the
    homelab's suite at a377166, and in one run of 3,000 each with the switch on and off, 32 at once (the runs that
    differed were not counted, one of each was kept); 400 runs 8 at once the same every time, both ways. What
    may stay until its group goes, as designed, and is counted in test 198 only when its tree has gone: a node
    that refers to a node beside it or to itself, to the root from nine owners below (ChainMax is eight), by a
    delegate that captures it, by a field of a struct.
  - Checked: test 198 (new objects that join in one step: a leaf by what its slots hold, its parent, a parent
    further up, things not of the group, a new node of its own, empty and with content that refers up, new
    arrays, the same new node twice, a plain node a static holds, into a field and into a member's array; a
    node whose list takes its first item, by its other slots, with two children made first, a first child with
    content, a first child in its array, a list something else refers to; classes whose slots are a base's,
    nine, past the fifteenth word, inside a struct, with finalizers, held by delegates; 300 frames of the
    scene's kind; counted against .NET while the trees live and when they have gone). It fails with each of six
    rules of the step broken by hand: the type's word a slot short, the chain not asked, a new object's own
    slots not looked at, the node's other slots not linked, the top of the chain not asked what refers to it,
    the member's last reference from outside given back in the step. Its output is the same with the switch
    off, the first version's too, whose lines differed from .NET's by what stays until a group goes.
    On the homelab: the suite at a377166 (the step alone) 208/208 checked and 207/208 as shipped (test 131,
    above); at c3b22e0 209/209 checked and 208/209 as shipped (088, its timer line, another session's suite
    running beside it); at a440778, the last change of code, 209/209 checked and 209/209 as shipped (419 s), and
    088 by itself passes both ways. `tools/stress-cycles.sh` there at a440778 (200 loaded runs a mode, 8 at once):
    198, 192 to 197 and 139 the same in every run of both modes; 127 differing in 1 of 200 as shipped and 4 of
    200 checked (main's rate, 5 of 400 at 80ae1ce; it starts threads and takes none of the step). Not run: the
    self-hosted compiler; frames.mjs against NativeAOT-LLVM on a quiet machine.

- **The collections and strings scenes (2026-11-22, branch claude/colstr-1, from main 1f7a807; the desk machine,
  shared; not merged when this was written).** Nobody had looked at these two: collections' median was 1.24x to 1.27x
  NativeAOT-LLVM's (7.5 to 8.2 us against 5.9 to 6.6), strings' 1.21x to 1.25x (9.1 to 9.9 against 7.5 to 8.0), their
  spikes far below. **Now collections 1.09x to 1.15x (7.0 to 7.1 us against 6.2 to 6.4) and strings 1.01x to 1.03x
  (7.9 to 8.0 against 7.7 to 7.9), three interleaved pairs of `frames.mjs --runs 3 --js-young 32`; collections is
  not reached, and what is left of it is counted below by cause.**
  - Measured first, both sides (`tools/colstr-parts.cs`, new, with `tools/parts.mjs` and `tools/parts-naot-wasm`,
    new: any by-phase tool with a `Parts.Run()` built by cs2wasm and by NativeAOT-LLVM, run in turn, the lowest of N
    runs a row; a compiler switch as a third column). Each frame does what the scene's does with a timestamp between
    the phases; a row is the mean of the middle half of the 6,000 frames (the clock steps by 100 ns, so a median is
    a multiple of it). The phases timed apart do not add up to the frame on NativeAOT's side (7.1 us against 6.1
    whole: a clock call there is a call out to JavaScript), so the collections frame is also cut short after each
    phase and timed whole, a phase's cost the difference of two rows. ns, NativeAOT / cs2wasm before / after:

    | collections, whole frames | NativeAOT | before | after |
    |---|---|---|---|
    | to the list's 200 adds | 300 | 300 | 300 |
    | to the map's 100 sets (new objects) | 2,019 | 2,233 | 2,229 |
    | the same, one object made before | 1,831 | 1,950 | 1,944 |
    | to the map's 30 removes | 2,497 | 2,768 | 2,759 |
    | to the queue's 50 | 2,622 | 2,952 | 2,961 |
    | whole (300 TryGetValue) | 6,095 | 7,620 | 6,763 |
    | whole, ContainsKey for TryGetValue | 6,198 | 6,285 | 6,298 |
    | (100 objects made and freed, alone) | 227 | 686 | 700 |

    | strings, phases | NativeAOT | before | after |
    |---|---|---|---|
    | 40 lines (interpolation) and their appends | 2,387 | 4,762 | 3,824 |
    | sb.ToString | 236 | 139 | 160 |
    | Split | 1,140 | 757 | 760 |
    | Join of 20 | 197 | 288 | 270 |
    | 60 names (string + number, two dictionary calls) | 3,416 | 2,981 | 2,757 |
    | ToUpperInvariant, IndexOf | 631 | 400 | 400 |
    | whole frame | 7,663 | 9,298 | 7,938 |
    | (the interpolation alone) | 2,003 | 3,149 | 2,230 |
    | (the appends alone) | 431 | 1,443 | 1,416 |
    | (string + number alone, kept in an array) | 880 | 1,700 | 1,467 |
    | (the dictionary's two calls alone) | 2,704 | 1,734 | 1,756 |

  - **Collections, cause 1 (fixed): the lookups went through the table.** With `ContainsKey` in place of
    `TryGetValue` the frame was NativeAOT's (6.29 against 6.20 us): the whole gap of the lookups, 1.2 us, was in what
    `TryGetValue` adds to `FindValue`. `Dictionary<int, Particle>.TryGetValue` (28 instructions) shared one body with
    `Dictionary<string, CultureInfo>.TryGetValue` (the canon sharing of 2026-11-21: a family's members that call
    different functions share a body that calls through the table, the callee's index a hidden parameter), so every
    lookup made a `call_indirect` to its own `FindValue`, and V8 inlined neither the body into the loop nor
    `FindValue` into it (`--trace-wasm-inlining`: with the body the member's own, all of `TryGetValue`, `FindValue`,
    the count calls go into the frame). `CS2WASM_SHARE_INDIRECT=0`: 7.56 to 6.91 us. Now a family's body under 32
    instructions, a member of which is called from inside a loop, is not shared where its members call different
    functions (Wasm/ShareConstants.cs; `CS2WASM_NO_DIRECT_SMALL=1`, `CS2WASM_SHARE_INDIRECT_MIN=n` the length;
    `CS2WASM_SHARE_STATS=1` names them): 7.62 to 6.76 us, the lookups' phase 4.65 to 4.12 (NativeAOT 4.13). The parser
    bench's module 2,382,150 to 2,389,001 bytes (+0.29%), hello world the same. The first form, every short body:
    `List<Entity>.Grow` (8 instructions, from `AddWithResize`) then called its `Capacity` setter directly, V8 inlined
    the setter up the chain into the tree's build, and `tools/tree-parts.cs` read the build 4% slower on the desk
    (1.92 to 2.07 against 2.04 to 2.13 ms, four rounds in turn; the homelab's eight rounds did not tell them
    apart); with the loop test `tree-parts`' module is byte for byte the one built with the switch. Test 210 (the
    small methods of a dictionary, a sorted dictionary and a set over eight instantiations, comparers and
    exceptions; with a member deliberately given another's callee its output changes).
  - **Collections, what is left (0.67 us of 6.1), by cause, none built.** (a) The lookups, 0.33 us, all of it the
    value handed out (the frame with `TryGetValue` less the frame with `ContainsKey`: 0.46 us here, -0.10 on
    NativeAOT's side, whose `ContainsKey` frame is the slower of its two): on a
    hit (one lookup in five) the `out` value is counted into the caller's frame slot and counted off at the end of
    the turn, on every lookup the slot's old value is read, released and the slot cleared again. An `out` value
    borrowed from the dictionary while nothing stores to it (as a returned field's is, FieldBorrows.cs) would take
    it away; that is a new rule across a call, not attempted on the last day. (b) The sets, 0.21 us: 0.11 with one
    object made before (TryInsert keeps a count on its `entries` local for the whole method, because `Resize()`
    stores the field, though the local is assigned again before it is read after that call; and the new object is
    counted by the store and let go by the caller: `OwnedParams` takes neither, TryInsert is past its size and the
    stores are in loops), and 0.10 more for the objects themselves (100 objects made and freed alone: 0.70 us
    against 0.23, NativeAOT's bump allocation; the allocator's paths are other rounds'). (c) The removes 0.05, the
    queue 0.07.
  - **Collections' p99 (0.034 to 0.038 ms against 0.025 to 0.028: 1.3x to 1.4x, not changed).** The frames above
    p99 are the dictionary's resizes (and, 20 of the 60, the clears: without them p99 is 18.6 us against 16.1). A
    resize frame is 29.5 us against 22.3 with new objects and 23.7 against 22.0 with one object: the 6 us are
    `Resize`'s `Array.Copy`, which counts every value into the new array, and the old array's end, which counts
    every one off again, each a touch of an object the copy itself never reads. The old array has two references
    at the copy (the field and TryInsert's local, cause (b) above), so nothing can take the counts over there; with
    the local uncounted it has one, and the copy could move them (as `Rt.GrowInto` does for a list in a group), with
    care for what an exception between the copy and the store leaves. Not built.
  - **Strings, cause 1 (fixed): a line of eight parts was three strings.** `$"{word} x{i}: {n} hp {m}/100"` was
    lowered to `Concat` in fours, each call a new string that copied what the calls before had made (profile: the
    two `Concat`s 25%, `rt.copy` 14%, `FreeObject` 16% of the interpolation). Five parts and more are now one call
    and one string, eight parts at a time (`String.Concat8`, a null for a place not used; Compiler/Expressions.cs;
    `CS2WASM_NO_CONCAT_ONCE=1`), as .NET's `Concat` of a span of strings or its handler make one: the 40 lines'
    interpolation alone 3.06 to 2.32 us against that switch. It was also a difference from .NET: in fours `a + "" + "" + "" + ""` was `a` itself, in .NET
    a new string (test 211, which fails on main at that line: texts of five to twenty-three parts, nulls, every
    kind of part, the order of evaluation, a part that throws, and which results are new objects).
  - **Strings, cause 2 (fixed): the strings of small numbers.** The library is compiled as Mono's, whose cache of
    number strings is ten long; CoreCLR's and NativeAOT's is 300. `299.ToString()` is one object every time in .NET
    and was a new string every time here (test 212, which fails with the switch), and every number below 300
    in a line or a key was an allocation, a copy and a free: `(i & 63).ToString()` 11 to 3 ns. An edit of
    Number.Formatting.cs in the manifest sets 300 (`CS2WASM_NO_SMALL_NUMBERS=1`: ten): the interpolation alone 2.66
    to 2.17 us against that switch (NativeAOT 2.0), the names' phase 3.11 to 2.80. Hello world 27,426 to 27,456 bytes with both.
  - **Strings, what is left, not built: StringBuilder's chunks are its cycle type.** 40 appends of a string and a
    character into a new builder are 1.42 us against 0.43. Profile (`CS2WASM_NAMES=1`, `--cpu-prof`): 52% of it is
    the arena's (ArenaLink 12%, ArenaUnlinkOld 11%, ArenaVisit 6%, ArenaFree 5%, LinkMembers 8%, PageAlloc 3%,
    AllocObjectArena, AfterStore, ArenaRelease, ArenaJoin): `m_ChunkPrevious` is a StringBuilder in a StringBuilder,
    so each expansion (`m_ChunkPrevious = new StringBuilder(this)`, some seven for the scene's 1,080 characters) links two members and unlinks
    the old chain, and the builder's end frees a group. The chain cannot close: every store of the field is null,
    a constructor's own, a new chunk made from the builder it is stored into, or a link further down the same
    chain; proving the last two is the typed-edge proof of 7.E for a self-typed field (the value comes from the
    container's own chain), which no rule has yet. Worth 0.7 us of the strings frame, which would put it below
    NativeAOT's; also `Join` of 20, 0.27 against 0.20, not looked at.
  - Checked: `frames.mjs` three pairs in turn, before (the three switches) / after: collections median 8.0 to 8.2 /
    7.0 to 7.1 us (NativeAOT 6.2 to 6.6), p99 36.3 to 37.7 / 36.3 to 37.7, p99.9 0.186 to 0.192 / 0.187 to 0.190 ms
    (NativeAOT 1.10 to 1.17), worst 0.21 to 0.24 / 0.21 to 0.27 (1.34 to 1.47); strings 9.7 to 9.9 / 7.9 to 8.0
    (7.7 to 7.9), p99 14.9 to 15.6 / 12.9 to 13.5 (19.9 to 22.2), p99.9 0.021 to 0.024 / 0.021 to 0.023 (0.075 to
    0.091), worst 0.050 to 0.058 / 0.048 to 0.054 (0.27 to 0.33); churn 1.76x to 1.78x both, tree's median 1.68x to
    1.70x / 1.61x to 1.65x and p99.9 2.88 to 2.97 / 2.77 to 2.96 ms (2.59 to 2.65: not reached, not this round's),
    bigdrop 0.86x to 0.97x both; memory 16.0 MB against 59.3 (71.1). `bench.mjs` three pairs: 0.94x to 0.95x /
    0.93x (word_count 0.85x to 0.80x, dictionary 1.13x to 1.11x). On the homelab at bc2ba98 (the last commit that changes code): suite 211/211 checked and 211/211 as shipped (461 s); at ebeaa42, the first form, 209/211 checked and 210/211 as shipped (088's timer line in both; 087 checked the trap named in FINISH.md's entry, which main has too). Not run: the self-hosted compiler, stress-cycles.
- **The churn loop, round 2: where its time is, measured again (2026-11-22, branch claude/churn-2, from main 1f7a807;
  the desk machine, shared with other sessions' timings: ranges, medians of frames, runs in turn; not merged when
  this was written).** Two leads were given: the order in memory (17 us of the loop's 81 by the last round's
  subtraction) and the two counts of a move (12). Both figures were wrong, and what was built follows from the
  measurements that showed it.
  - **The order in memory costs 2 to 3 us, not 17** (`tools/churn-layout.cs`, new: the scene run, then the particles'
    addresses taken in the list's order, and the loop timed over that layout with nothing changing). Median ns a
    frame on main: the scene's loop 87.5 to 89.0; the same particles where the scene left them, none dying, each
    stored into its own slot 70.2 to 71.3; as many made in order 66.9 to 68.4. So the layout itself is 3 us. The
    17 the last round took for it (the scene's loop less the loop over particles in order less the frees) is
    mostly not the layout: the scene's own loop over the settled layout, none dying, is 5 above the loop without
    the decrement (`--p.Life` is one more store a particle: 0.26 ns, a cycle); the dead all over the list and held
    elsewhere (the branch that is taken for one in 48, the moves down) 3.5; the dead freed inside the loop 0 to 1
    more (a free in its place is 2.5 ns, not 6.6); and about 7 that are there only while the layout changes every
    frame (each frame's 400 new particles between old ones). What the loop pays is instructions: every simple
    thing added to it costs about a cycle a particle, 0.25 ns, 5 us a frame (`tools/churn-raw.cs`, below).
  - **Why a frame's new particles lay between old ones: the page policy's test was fooled, and two of its rules
    worked against the third** (Runtime.cs, PageAlloc; counters put into the allocator for the measurement, not
    kept). The last round's rule leaves a page alone while it is being emptied, and told "no longer being emptied"
    by the page having lost no block between two looks at it. A look is an allocation that needs a page. The scene
    makes its 400 particles and then frees 400, so all the looks of a frame come with no free between them: every
    page a quarter free counted as still and was taken. With that put right the page "an eighth in use" was the
    one taken (5,069 times in 7,000 frames against 1,235 empty pages): its blocks are handed out in the order they
    were freed, which is the order of death, all over the page. And a page passed over goes to the ring's back,
    where the look for "a page freed in one go" found it. Of the 19,000 steps of the list's walk 16,246 went to a
    64-byte line that was not the next one or the one before.
  - **Built: a page is left to empty by its size's own clock** (`CS2WASM_NO_HOLD_PAGES=1`: the last round's
    policy, the heap laid out as before). Each size counts the times it needed a page; the mark in a page's header
    ([12]: the blocks in use when it was last seen to have lost one, 12 bits, and the size's count then) says how
    long it has lost nothing, and a page counts as no longer emptied when that is four takes of its size
    (StillTakes; two gives the same choices in the scene, eight a few fewer). The nearly empty page is no longer
    taken on sight: it is empty soon, or it stops losing blocks and is taken by the rule above. The look at the
    back stops at a page that was passed over. **Bounded by rule:** the pages with freed blocks, of all sizes
    together, may be three quarters of what the heap has handed out; past that the last round's conditions hold
    again (nearly empty, or unchanged since the last look). And no memory is grown for an empty page, as before.
    The fast paths of allocation and free are untouched (a count of freed bytes kept on them was tried for the
    bound and taken out: an allocation from a page's list 12.2 to 13.2 ns). The scene: 16,246 steps off the line to
    4,270, the particles' pages 68 to 67, the heap's top 3,274 to 3,338 KB; "only when empty", which the last round
    measured at 1.6 times the pages against the policy before it, is not more memory than the last round's policy
    itself (68 pages either way: that policy held the same pages and filled them in the wrong order).
    Tree's heap top 9,881 KB both (its pages 476 both, 1,112 to 1,256 KB of them freed blocks), collections 4,645
    to 4,650, strings 3,280 to 3,286, bigdrop 13,499 both; memory 16 MB in all five (NativeAOT-LLVM 59 to 71).
    Test 205 (three sizes, one of 16 bytes; frames of births then deaths, pages a third free that stop losing
    blocks, one object in a hundred kept from the start so that the bound is passed, runs let go at once; every
    object read back; the allocator's counters showed every way of taking a page reached: the back 566 times, the
    nearly empty page past the bound 618, no longer emptied 296 of which 12 past the bound, an empty page 613;
    with the mark left in a page that is taken its output stops after the first line).
  - **The counted move (lead 2): no sound proof for the pattern, and less to gain than was thought.** What the
    two counts cost was measured by writing the store without them (`tools/churn-raw.cs`, new: the scene's loop
    over an array of 19,392 particles in order, every survivor moved 400 slots down, the array seen as nint[] for
    the uncounted forms; median us a frame): no store 31.1; an int written into another array 33.0; the
    reference written and nothing else 35.8; that and the slot it came from set to null 40.4; that and what the
    destination held read and tested 40.8; both, which is what a proof of "moved, not copied" would leave (the
    destination's old value must still be let go when it is a dead particle, and told from a survivor that moved
    away, which only a null in the slot it left can say) 45.3; the counted store 49.5. So a move proved would
    save **4 us of the frame's 80**, not 12: the store's cost is its instructions (the slot read, the two slots
    written, the tests), and the counts are a third of it. The proof that would save more, nothing written but
    the reference and the dead let go where they are found dead, is **not sound**: counter-example, the scene's
    own loop with one particle whose Tag is null. .NET throws NullReferenceException at that particle with the
    list as it was at that moment: slots below `w` the survivors, slots from `w` up to the particle what they
    held before, among them dead particles that a handler can still read (`s_particles[k].Life`); the changed
    loop has freed them. The compiler cannot show that `p.Tag` is never null (it is a field anyone may store),
    nor that `p` is not (a list may hold null), so every such loop has an exit in its middle, and after an exit
    every slot is observable. The weaker form (null in the slot left, counts made up at every exit, the
    exception's included) is sound only if nothing in the loop reads the list but the one read at `i`, nothing it
    calls can, no other reference to the list's array exists, and each exit's handler rebuilds what the slots
    held: a recognizer of one loop shape over List<T>'s insides, for 4 us. Not built. The old value being the new
    one (`w == i`) is the only case in which both counts go by a test alone, and it holds in no frame of the scene
    after its first death (last round).
  - **Built: no test of the array's type at a store into a sealed class's array in generic code**
    (`CS2WASM_NO_SEALED_STORE=1`; Expressions.cs, the array element's place). An array of a sealed class, of
    string or of a delegate type is of that very type, and its stores had no test, except in an instantiation of
    generic code, where the element type was taken as any reference type so that a family's bodies are one.
    List<Particle>'s setter is such a store: the array's type word read and compared for every survivor. Decided
    on the exact type now, as .NET's own compiler does: the instantiations for sealed classes are one body
    without the test, the others one with it. Churn median 0.086 to 0.083 ms on its own; hello world's size the
    same, the frames module 164 bytes smaller. Test 206 (one generic class and method for sealed classes,
    strings, delegates, open classes, interfaces, object and arrays, with arrays of the very type and of derived
    ones, and List, Stack and Queue of each; with the test dropped for an open type its "threw" become
    "stored").
  - Result. `tools/churn-layout.cs`, median us a frame, main / the pages / the store / both, three runs in turn:
    the scene's loop 87.5-89.0 / 82.4-84.2 / 82.6-84.0 / **78.7-80.6**; the settled layout, none dying 70.2-71.3 /
    65.9-68.4 / 66.0-67.0 / 61.4-63.6. `node tools/frames.mjs --runs 3 --js-young 32`, main's copy and the branch
    in turn twice, other sessions at work (NativeAOT-LLVM's column moved with them, so each line is its own
    invocation); median / p99 / p99.9 / worst ms, cs2wasm against NativeAOT-LLVM:
    churn main 0.088 / 0.122 / 0.150 / 0.332 against 0.049 / 0.064 / 1.131 / 1.178 and 0.089 / 0.117 / 0.158 /
    0.260 against 0.050 / 0.065 / 1.159 / 1.193 (**1.78x to 1.80x**); the branch 0.081 / 0.122 / 0.406 / 2.610
    against 0.051 / 0.080 / 1.224 / 1.728 and 0.082 / 0.135 / 0.143 / 0.260 against 0.052 / 0.067 / 1.210 / 1.247
    (**1.58x to 1.59x**; the first invocation's worst frame is the machine's: it is in no other run).
    tree main 0.048 / 0.076 / 2.756 / 2.940 against 0.029 / 0.049 / 2.433 / 3.147 and 0.050 / 0.075 / 2.906 /
    3.653 against 0.029 / 0.048 / 2.506 / 3.275; the branch 0.051 / 0.089 / 3.144 / 3.560 against 0.030 / 0.049 /
    2.582 / 3.315 and 0.057 / 0.153 / 3.712 / 6.443 against 0.033 / 0.070 / 3.283 / 4.249 (median 1.66x to 1.72x
    and 1.70x to 1.73x, p99.9 1.13x to 1.16x and 1.13x to 1.22x: the machine was busier in the branch's
    invocations, by NativeAOT's own column).
    collections 0.008 / 0.035-0.037 / 0.177-0.182 / 0.202-0.204 and 0.008 / 0.037-0.054 / 0.188-0.315 /
    0.200-0.861 (NativeAOT 0.006 / 0.025-0.028 / 1.04-1.15 / 1.31-2.94); strings 0.009 / 0.014-0.015 /
    0.022-0.025 / 0.046-0.051 and 0.009-0.010 / 0.015-0.021 / 0.025-0.081 / 0.053-0.133 (0.008 / 0.018-0.024 /
    0.066-0.104 / 0.24-0.33); bigdrop 0.003 / 0.007 / 0.954-1.056 / 1.022-1.114 and 0.003 / 0.009 / 1.086-1.123 /
    1.694-1.719 (0.003-0.004 / 0.010-0.018 / 6.3-8.7 / 9.0-10.3). Memory 16 MB against 59 to 71 throughout. Twice
    more later, the same way: churn 1.76x and 1.78x on main, 1.60x and 1.61x on the branch; tree's median 1.63x
    and 1.73x on main, 1.68x and 1.71x on the branch, its p99.9 0.98x and 1.03x, 1.09x and 1.23x. The modules
    alone, each run in its own Node, in turn five times, the machine quieter: churn median 0.087-0.091 on main,
    0.082-0.086 with the pages' change alone, 0.084-0.087 with the store's alone, **0.078-0.083** with both.
    `tools/bench.mjs` in turn twice: 0.94x and 0.95x
    on main, 0.95x and 0.95x on the branch (no kernel moved). Hello world 27,482 to 27,612 bytes (the limit 28,383).
  - **The tree scene with other programs at work:** not told apart from main's, on this machine with the other
    sessions' builds and timings going on (the load the last round's question was about). The modules alone, main's
    and the branch's in turn: ten runs under that load, medians 0.051-0.062 ms on main and 0.052-0.066 on the branch
    (the branch's at or above main's in nine of the ten pairs, by 0 to 0.004 and once 0.010), lowest p99.9 3.22 and
    3.11; eight runs four ways (main, the pages' change alone, the store's alone, both), medians 0.051-0.060,
    0.049-0.062, 0.051-0.063 and 0.051-0.087, the first three rounds of it, before the load rose, 0.051-0.052,
    0.049-0.052, 0.051 and 0.051-0.052; eight runs with the machine quieter, medians 0.047-0.051 and 0.048-0.050,
    p99.9 2.73-3.08 and 2.65-2.94 (the branch's median the lower or the same in six pairs of eight, its p99.9 the
    lower in six). So: the same when quiet; under load the branch's median was up to 4% above main's in one set of
    ten and not in the other sets, inside what the load itself moves a run by (10 to 60%). A load made for the purpose
    (two and six processes walking 64 MB each) moved every run by more than any difference (medians 0.06 to 0.7 ms):
    no use. The homelab has no Emscripten and its frames.mjs does not run: not measured there. Not settled further
    than that.
  - What is left of churn's loop (about 73 us against NativeAOT-LLVM's 45, by `tools/churn-layout.cs` and
    `tools/churn-raw.cs`), by cause: the store through the list about 14 above a bare store of the reference (the
    slot's old value read, the two counts, the list's and the array's checks twice over, the version), of which
    the two counts are 4; the three NaN tests 2 to 5 (SPEC 7.3 is about where bits are read; the stores are where
    they are made safe, for pointers, overlapping fields and the engine's copies alike: left); the order in memory
    2 with this round's policy (the particles are in order but with holes: 13,700 lines for 19,200 particles
    where 10,800 would hold them; no policy fills holes in order without moving objects); a particle of 36 bytes
    where NativeAOT-LLVM's is 32 (the header's hash word); the rest the read loop's own instructions (the list's
    null test three times a particle, the size, the array and its length read again for the store: the engine
    does not carry a load across a store, NativeAOT-LLVM's compiler does). **Not reached: churn's median is
    1.58x NativeAOT-LLVM's, tree's 1.7x.** What would close more is the compiler's own inlining of the list's
    indexers with the loads shared across them (the last round's trial of inlining alone made the engine stop
    inlining the count functions: 2.4 to 5.4 ns), which is a change to how every loop over a list is compiled and
    was not started on the last day.
  - Verified on the homelab: at 8a7895e (both changes) the suite 210/210 checked and as shipped (284 s); at 840d4be
    (the code's last commit: the switch's layout) 210/210 checked and 209/210 as shipped, the one test 088 at its
    timer line (line 7, the second of its five answers False), which run alone there passed both ways. Stress at
    840d4be, 200 loaded runs a mode: 205, 206, 196 and 139 the same in every run of both modes; 127 differed in 4 of
    400 (its "async 5" line: 1 as shipped, 3 checked; main's 5 of 400 in the last round), the other 200 of that test
    run before it all the same. Not run: the self-hosted compiler, tools/selfdet.mjs. At f54c2b1 (the code as at
    840d4be, with this text; the homelab busy with other sessions' runs, 713 s for the 284 before): 209/210 checked
    (135, its WhenAny between a 1 ms and a 50 ms timer: 60950 for 61050) and 209/210 as shipped (088's timer line
    again); each of the two, run alone there, passed both ways.

- **The churn loop, round 3: the three levers measured before building, and none built (2026-11-22, branch
  claude/churn-3, from claude/round-6a 19a2035; the desk machine; a tool and this text, no change to the compiler).**
  Round 2 ended on "the rest is the loop's own instructions, the list's loads read again around the store, which the
  engine does not carry across a store and NativeAOT-LLVM's compiler does", and the round was to build that: loads
  shared by field identity, then the header's hash word, then the NaN tests. Each was measured first, both sides in
  turn, medians of frames. **Round 2's sentence was wrong, and the three together do not reach the target.**
  - **The list's loads are not the gap: NativeAOT-LLVM pays more for the list than cs2wasm does.**
    `tools/churn-array.cs` (new) runs the scene through List<Particle> and the same by hand over an array and a
    count, the array read once before the loop: what the list's indexers come to when every load of `_size` and
    `_items` is shared, every size check gone, no version (more than any load elimination can reach: the checks
    and the version stay). Median us a frame, three runs in turn, NativeAOT-LLVM built from the same file: the
    list 53.5 to 55.8, the array **41.5 to 44.1**; cs2wasm the list 85.8 to 89.2 (80 to 82 with the machine
    quiet), the array **77.0 to 81.0** (71.6 to 73.1 quiet). The list costs NativeAOT-LLVM 12 us a frame and
    cs2wasm 8 to 9: LLVM does not share those loads either. With all of it gone cs2wasm is at 1.85x NativeAOT's
    array (1.57x on the lists): the gap is in the loop that is left, and a pass that shared every load would bring
    churn's median from 0.080 to 0.072 at the very most, 1.45x.
  - The compiler's own inlining of the indexers into the loop was measured again, the right way this time
    (`CS2WASM_INLINE_THROWING=1 CS2WASM_INLINE_THROWING_LOOP=80`: the getter and setter go into loops only; round
    1's trial inlined into the setter, which then no longer fitted the engine's budget): churn 0.078 to 0.081 ms
    with it, with `CS2WASM_RC_IN_LOOPS=1` beside it, with `CS2WASM_RC_FAST=0` beside it, and without: the engine
    already inlines all of it, and the compiler's doing so changes nothing by itself. It would only be the first
    step of sharing the loads.
  - **Where the gap is** (`tools/churn-raw.cs` on both sides: the loop over an array in order, every survivor
    moved 400 slots down; us a frame, four runs in turn, cs2wasm against NativeAOT-LLVM): no store 31.8-32.3
    against 23.4-24.2; the reference written and nothing else 36.7-37.5 against 27.1-28.4; the store as each side
    makes it (counted; card-marked) **50.6-51.4 against 30.8-31.6**. So of the 20 between them: the read loop 8
    (of which the NaN tests 2 to 2.5 and the object's four bytes 1.5 to 2, below; the rest its null tests and
    what the engine makes of the loop), and **the store 10.5: cs2wasm's counting is 14 us on top of the bare
    store (the slot's old value read, a count added, a count taken: 0.72 ns for each of 19,392 moves), the
    collector's card mark 3.5.** That 14 is what reference counting costs a moved reference; round 2 measured
    what a proof of the move would leave of it (10, the slot it left set to null and the old value still read and
    tested) and why the proof that leaves less is not sound.
  - The header's hash word (a particle is 36 bytes, NativeAOT-LLVM's 32): measured by making the particle larger
    (one unused int: 40 bytes; two: 44), three runs in turn: the list scene 84.4 / 85.9 / 88.5, the array scene
    75.5 / 77.4 / 79.4. 1.5 to 2 us for four bytes, so 32 bytes would give about 2 us of 80 (2%): the hash's
    place was not changed for that.
  - The NaN tests: built without them (`CS2WASM_NO_CANON=1`, the measurement's switch), three runs in turn: the
    list scene 84.4 to 82.0, the array scene 75.5 to 72.8: **2 to 2.5 us**. SPEC 7.3 asks that no payload be
    observable; Wasm/FloatStores.cs makes memory free of them at the store because bits leave memory in ways that
    have no single place to test at: a pointer or a `ref` cast, a span of bytes over floats, overlapping fields,
    a struct compared or hashed by its bytes, and the engine's own copies and comparisons of an instance's memory
    (section 7.2). A test only where bits are read would have to be at every one of those, and the last is not
    code the compiler writes. Left as it is.
  - So the three levers are worth at most 8 to 9 (every list load shared and every check gone, which no pass
    reaches), 2 and 2.5: 80 to 67 us at the very best, **1.35x NativeAOT-LLVM's**, and the pass by field identity
    alone, which is the large one to build (a key on every load, a summary of every call's stores asked per loop,
    each of its conditions with a test, the self-hosted compiler rebuilt and compared), perhaps half its 8. It was
    not built: the measurement that says it cannot reach the target came first, and what was left of the day did
    not cover a pass over every field load of every program with its proofs and the self-hosted compiler's
    check. **Not reached, and not reachable by these three: churn's median stays 1.58x to 1.61x.** The floor,
    measured: 14 us a frame of counting at the store where the collector pays 3.5 (10.5 of the 30 between the
    two frames), and the collector's side of the bargain is its pauses (churn's worst frame 0.18 to 0.35 ms
    against 1.2 to 1.7).
  - Also from the homelab, after round 2's text: test 135 under the stress tool, 200 loaded runs a mode, printed
    60950 for 61050 in 3 of 400 on claude/churn-2 and in 1 of 400 on main: the timer race of its WhenAny, on
    both.
- **The churn loop, round 4: the read loop taken apart against NativeAOT-LLVM's own code, and the header's word
  (2026-11-22, branch claude/churn-4, from claude/churn-3; the desk machine, for the second half of the round too
  busy with other sessions to time anything: said where).**
  - **The read loop, instruction by instruction** (`tools/churn-raw.cs` LoopNoStore: 31.8-32.3 us a frame against
    23.4-24.2; both modules disassembled with Emscripten's wasm-dis, cs2wasm's built with CS2WASM_NAMES=1). A turn
    of NativeAOT-LLVM's loop: the element read through a pointer that steps by 4, its null test; X, Vx read, added,
    stored; Y and Vy read, added, stored; **Vy not read again** but kept from the read before, 0.1 added, stored;
    Life read and tested; Tag read, its null test, its length added; the pointer stepped; a counter taken down and
    one branch back (the loop is rotated: the test at the bottom). No test of the array for null and no read of
    its length in the loop (both before it), no index check, no NaN test; its particle is 28 bytes (a header of
    one word; with the word before the object 32). A turn of cs2wasm's: **the array tested for null again**; **its
    length read again** and the index compared; the element's address from the index (times 4, plus 16; no index
    check: Loops.cs takes it out); the null test; for each of the three floats two reads, the add, **the NaN
    test** (a compare and a select), the store, **Vy read twice**; Life; Tag, its null test, its length; the
    index stepped; **two branches a turn** (the test at the top and a branch back). The differences, each
    measured by writing the loop that way by hand in the tool (three runs in turn, us a frame): as it is 32.7 to
    33.4; rotated by hand with the length read once 31.0 to 31.5 (**1.7**: the second branch, the length and the
    array's null test); Vy read once, before the first store 29.9 to 30.0 (**3.0**); without the NaN tests
    (`CS2WASM_NO_CANON=1`) 28.7 to 29.2 (**4.2** in this loop; 2 to 2.5 in the whole frame, round 3); without them
    and rotated 27.4 to 28.9, without them and Vy once 27.6 to 27.9. So the 8 between the two loops: the NaN tests
    4, the reads and the loop's shape 2 to 3 (they overlap: together with no NaN tests 1), the rest the object's
    size (36 bytes against 28) and the index against the pointer.
  - **Built: a load repeated across stores to other bytes of the same object is read once**
    (`CS2WASM_NO_SAME_BASE_LOADS=1`; Wasm/SameBaseLoads.cs, new, after FloatStores on the final bodies). The proof
    needs no knowledge of fields: in straight-line code the second load is from the same local plus the same
    constant as the first, the local was not assigned since, and every store between is through that very local
    to bytes that do not overlap the load's (so: the same address, bytes nothing wrote). What ends it: an
    assignment of the local; a store through anything else (another local may hold the same object or a place in
    it; an address that is not a local) or to bytes that overlap; a call; a copy, a fill, an atomic or vector
    instruction; any branch, block or loop. A volatile field is read with other instructions and is never one of
    these loads. The tool's loop without its store 32.9-33.2 to **31.0-32.1** (the hand's 29.8 reads Vy before
    X's store; the pass keeps the program's order); **the churn scene's frame the same, 0.083 to 0.085 ms with and
    without, five runs in turn; bench.mjs 0.93x-0.94x and 0.94x: nothing moved.** A small thing that is certain,
    not a step toward the target. Hello world 27,612 to 27,642 bytes. Test 207 (the place itself stored; another
    variable holding the same object; a ref, a ref walked along, a pointer; fields that overlap by explicit
    layout, narrower and wider; a method, a property, a delegate, a constructor between; the variable assigned;
    array elements by an index that is the same one; branches and loops between; other fields stored between,
    NaNs among them; each of the pass's five rules taken out changes a line: no overlap test: the place itself,
    the pointer, the overlapping fields, the property; other locals' loads kept: the other variable; calls
    passed over: the calls; an assignment passed over: the pointer moved on in a loop; a store without a local
    for its address passed over: the ref and the pointer).
  - **Not built: the loop's shape** (the test at the bottom, the array's null test and its length before the
    loop). 1.7 us of the read loop, and less with the NaN tests in (the two overlap). The length before the loop
    is the field-identity pass's (claude/field-loads-1: an array's length never changes), which this round was
    told to stay out of; the rotation alone was not measured apart from it.
  - **The NaN tests: which stores need them, and why none can go.** In this loop all three stores are of sums
    just made (`x + vx`, `y + vy`, `vy + 0.1`): FloatStores.cs leaves out only a constant and a value just loaded
    from memory, so all three are tested. Can a sum of values that came from memory (canonical there) be taken as
    canonical? WebAssembly says of an arithmetic result that is a NaN: when no operand is a NaN, or every NaN
    operand is canonical, the result is a canonical NaN **of either sign**; otherwise any arithmetic NaN. So by
    the specification alone the payload is right and the sign is not: the test could be skipped only if the two
    signs were the same thing to a program, and they are not (the bits are what section 7.3 is about: memory
    compared and hashed by its bytes, a span of bytes over floats). On the engine the suite runs on, V8 on x64, a
    NaN made by an operation is the processor's default, sign set, payload 0x400000, which is the one cs2wasm
    stores, and one passed through keeps its bits: there the test never changes anything for canonical operands.
    On arm64 the processor's default NaN has the sign clear: the same module would store 0x7FC00000 where it
    stores 0xFFC00000 on x64. Skipping the test would make memory's bits depend on the machine, which is what the
    section rules out. **Left: a floor of SPEC 7.3 as written, 4 us of this loop and 2 to 2.5 of the frame.**
  - **The header's hash word: not moved, and why.** `Types.ObjectHeader = 12` is the count, the type and a third
    word that is not only the hash: its bits 29 to 31 are flags every object can have (finalized, pinned by a
    static, marked), and with the share check bits 24 to 28 are the thread that made the object. Taking the word
    out means a new place for three flags that the count word (whose negative values are the arenas' and the
    immortal objects') and the type word (compared whole at every cast, dispatch and store check) do not have
    free, a table by address for the hash and those flags with its own cost at every free (a freed object's
    entry must go, so every free would ask whether it has one: a test on the free path that today has none), and
    the layout of everything after the header: an array's and a string's length are read at the literal offset
    12 in 71 places of Runtime.cs and 19 of the compiler, beside CoreLib's own code that knows where an object's
    first field is. What it would give was measured in round 3 on churn (1.5 to 2 us of 80 for four bytes) and
    could not be measured on tree in this round (the machine: tree's median ran 0.06 to 0.29 ms in consecutive
    runs of the same module). For 2% of one scene and an unmeasured amount of another, a change to every
    object's layout and to the free path, to be proved by the self-hosted compiler's own rebuild, was not started
    on the last day. **Not done; no measurement shows it unsound or a loss: it is unbuilt.**
  - Verified on the homelab: at b6ae0e4 (the code's commit) the suite 214/214 checked and as shipped (464 s); test 207
    under the stress tool, 200 loaded runs a mode, the same in every run; the self-hosted compiler built with the pass
    and without it (tools/selfhost-switch.sh CS2WASM_NO_SAME_BASE_LOADS=1: 31,816,209 and 31,817,447 bytes), each
    compiling Console.WriteLine(42) three times in turn: 10.3 to 10.7 s with, 10.2 to 11.2 s without, exit 0 and the
    same peak memory (the script's own comparison of the two modules found no file by the name it looks for: not shown
    there); tools/selfdet.mjs with the toolchain built from this commit (tools/proof5.mjs toolchain, 32,334,486 bytes,
    no threads, one core): test 207 compiled by the self-hosted compiler is the .NET-hosted compiler's module byte for
    byte (210,405 bytes). Not run: selfdet on more than that one program or with threads; frames.mjs against
    NativeAOT-LLVM for this round (the machine).

- **The collections and strings scenes, round 2 (2026-11-23, branch claude/colstr-2, from main 2dfc806; the desk
  machine, heavily shared; not merged when this was written).** What round 1 left, in the order of its size. By
  phase (`tools/colstr-parts.cs`, each change against its own switch, the lowest of 3 to 5 runs):
  **strings' whole frame 8.19 to 7.21 us against NativeAOT-LLVM's 7.91 (0.91x); collections' 7.21 to 7.27 to 6.78
  to 6.81 against 6.36 to 6.80, its p99 36.5 to 37.0 to 25.9 to 26.0 us against 26.8 to 30.4 (the lowest of six
  runs, the desk loaded).** `frames.mjs --runs 3 --js-young 32`, three pairs in turn at a quiet moment, before (the three switches) / after: **collections median 6.9 to 7.2 / 6.4 to 6.6 us against NativeAOT-LLVM's 6.0 to 6.4 (1.13x / 1.07x to 1.08x: not reached), p99 34.5 to 37.9 / 24.2 to 25.4 against 24.9 to 27.0 (at NativeAOT's)**, p99.9 0.175 to 0.193 / 0.174 to 0.183 ms (1.04 to 1.11), **worst 0.21 / 0.36 to 0.37 ms (1.29 to 1.37): one frame, the first clear after the warm-up (frame 200), 0.17 ms longer with the move in every run, the other clears 0.18 to 0.20 as before or below; the code that lets an array's values go ran at every resize before and now runs only at the clears, and is still in the engine's baseline tier at that one: with `node --wasm-tiering-budget=1000000` (the optimizing tier a thirteenth of the calls sooner) frame 200 is 0.21 ms, as the other clears (0.38 to 0.39 by default, three runs each; tests/out timing, the module unchanged). An engine tier-up moved, not work added; the worst frame it makes is 0.37 ms against NativeAOT's 1.3**; **strings median 7.5 to 8.0 / 6.7 to 6.9 us against 7.6 (0.99x / 0.88x to 0.91x)**, p99 12.0 to 13.0 / 10.3 to 10.5 (18.3 to 20.0), p99.9 0.018 to 0.021 / 0.017 (0.059 to 0.080), worst 0.04 to 0.05 / 0.04 to 0.06 (0.24 to 0.26); churn 1.75x to 1.81x / 1.76x to 1.80x, tree 1.63x to 1.66x / 1.61x to 1.67x with p99.9 2.64 to 2.77 / 2.66 to 2.74 ms (2.41 to 2.55: not reached, not this round's), bigdrop 0.87x to 0.88x / 0.84x to 0.88x; memory 16.0 MB against 59.3 (71.1).
  - **Stores that go down a structure (`Compiler/ChainStores.cs`, `CS2WASM_NO_CHAIN_STORES=1`): StringBuilder is no
    cycle type.** A store `x.F = v` closes a cycle only if v reaches x. It does not when v was read out of x
    (`x.F = y.G` or `x.F = y`, y come from x by copies and field reads, and for y itself the code has just found
    `x != y`), or when v is an object the method has just made, known to nothing else, whose constructor put into
    it only what was read out of x (`x.F = new C(x)` with the constructor storing `from.F`; `n = new C(.., x.F);
    ...; x.F = n`). Why reads made earlier still count: order a component's objects so that every reference made
    so far goes from a higher object to a lower; such a store goes down, an object nothing refers to yet can be
    put anywhere above what it refers to (a constructor's `this` on top, a new object filled from below x just
    below x, an isolated structure below its container), so while every store of a component's sites is one of
    those no reference goes up. The facts (b is a or what a reached, a != b, a is null, a is a new object filled
    from r) flow over Roslyn's control flow graph of the method, for `this`, parameters and locals that are only
    plainly assigned, never captured, never passed by reference; a fact about two variables says nothing when
    either is null, so it survives a join with a path on which one is null (`end = null; while (..) { if (end ==
    null) end = cur; cur = cur.F; }`). A method that returns its receiver or what it read out of it
    (FindChunkForIndex) and a constructor's contents by parameter are summaries of their text. A method with a
    `try` gets no proof. A proven store is linked as an isolated value's (`Site.LinkIso`): it does not make its
    site general, and links as any store where the site is cyclic through another. What the whole program must
    confirm: a `ref`/`out` parameter of a class type T counts as a local only if no call passes anything but a
    local of its own by reference for a `ref T` (`Arenas.RefWild`, noted at every by-reference argument; else the
    site is general after all). Reflection's SetValue, a `ref` handed out, an accessor: the site's address is
    taken or its stores are general, as before. Fixed on the way: a constructor's call that is not compiled
    (`[Conditional("DEBUG")]`, StringBuilder's AssertInvariants) was taken to let `this` out; a proven store into
    an owned local's object took the owned link, which makes the site general.
    All of StringBuilder's stores of `m_ChunkPrevious` are proven (ExpandByABlock, MakeRoom, Remove's two, Length's
    setter; the constructors' are stores into a new object) and its chunks are plain counted objects: 40 appends
    of a string and a character into a new builder 1.40 to 0.62 us (NativeAOT 0.45), the scene's lines and appends
    3.87 to 3.06 (2.53). A program that enumerates a builder's chunks (GetChunks:
    the enumerator passes its current chunk by reference from a field) or sets fields by reflection keeps them a
    cycle type, as before. Tests 213 (each rule in a class of its own by a marker type, with finalizers counting
    what is left: growth, a cut to what a walk found, two cursors, a new chunk wired in, an `out` cursor; and each
    store that looks the same and closes a cycle, a ring, a self link, the cursors untested, a constructor that
    keeps its argument, a value from above, a `ref` cursor over a field a call rewires, a local a lambda rewires;
    with `CS2WASM_CHAIN_BREAK=1`, a plain local taken as read out of anything, two of them leak) and 214
    (reflection, a `ref` handed out, an accessor, GetChunks).
  - **A collection that grows moves its items (`Rt.MoveRange`, Boundary/ArrayMoves.cs; edits of Dictionary.cs,
    HashSet.cs and List.cs in the manifest; `CS2WASM_NO_ARRAY_MOVE=1`): collections' p99.** Resize's `Array.Copy`
    counted every value for the new array and the old array's end counted each off again. Where the old array is
    full and nothing but the collection's field refers to it (its count is one, a plain object, no threads), the
    bytes are copied and the old array is left an array of bytes of the same size: nothing counted, its bytes as
    they were, freed at the same store as before. The two holders: TryInsert and AddIfNotPresent set their
    `entries` local to null before they call Resize (they assign it again right after), and Resize makes its
    buckets before the copy when it does not rehash, so nothing can throw between the move and the store of the
    new array. A method further up the stack that still reads the old entries (a key's Equals that adds to the
    dictionary under the insert that called it) holds a count, and the copy is made as before (test 220, which
    with `CS2WASM_ARRAY_MOVE_BREAK=1` throws in those cases). The resize frame (the frame to the map's sets, p99)
    30.8 to 20.3 to 22.8 us against 22.5 to 22.9; the whole frame's p99 35.0 to 24.7 to 27.5 against 24.9 to 26.1;
    without the clear frames 19.4 to 14.6 against 16.4. Queue is not done (its copy is two ranges, and the second
    reads the old array's length). **Found, on main too: a Span over a List's items (`CollectionsMarshal.AsSpan`),
    or a `ref` to a dictionary's value (`GetValueRefOrNullRef`), held across a growth reads the freed old array:
    as shipped the bytes are still there, a checked build traps (`list.AddRange(CollectionsMarshal.AsSpan(list))`
    is the library-shaped case). .NET keeps the old array alive through the span. Not fixed.**
  - **An `out` value borrowed (`Compiler/OutBorrows.cs`, `CS2WASM_NO_OUT_BORROW=1`).** `if (map.TryGetValue(key, out
    var v)) sum += v.Life;` counted the value into v's slot and off again. From the text alone, both sides: the
    callee (a method of a class, not virtual) only ever gives the parameter `default`, or a value it reads at that
    moment out of a place of the object's (a field or an element from `this` on, or through a `ref` local a call
    on `this` gave: `value = valRef` after `ref FindValue(key)`) with the return of a constant as the next
    statement, and has no local that holds a reference; a copy of it stores the value as it is. The caller: a
    direct call with `out var v`, the call an `if`'s condition or the left of its `&&`s, every use of v in the
    rest of the condition and the first branch, and nothing there but reads of locals, fields and elements,
    arithmetic, stores of numbers and `return` (no call, no allocation, no property, no static's first read): then
    nothing between the callee's read and v's last use changes any place, and v's slot is no owner. Not in a
    program with threads. The frame 7.08 to 7.14 us to 6.81 to 6.82 (NativeAOT 6.43 to 6.49). Test 221 (the shapes
    that qualify, and each that must count as before with a use after the place let the value go: the branch
    removes the key, clears or replaces, calls what does, v used after the `if`, captured, a TryGet that makes its
    object, returns a local's, is virtual, is a struct's; with `CS2WASM_OUT_BORROW_BREAK=1` two sums show items
    used after their finalizers).
  - **What an object costs (`tools/alloc-parts.cs`, new; 100 objects of 40 bytes a frame, ns, NativeAOT /
    cs2wasm, the desk loaded):** made and kept, nothing freed 538 / 406 (the allocation, the constructor's stores
    and the array's store are faster here than NativeAOT's bump allocation and write barrier); made over old ones
    406 / 1,114; 100 let go alone 149 / 810; 100 stores of one object 199 / 221. **The whole difference, 6.6 ns an
    object, is the free** (the release that finds the last reference, the object's destruction, the block back to
    its page: `rt.destroy_now`, `FreeSmallOld`), work NativeAOT's collector does later in its pauses (its p99.9 1.1
    ms on the scene against 0.19). The allocator's and the destruction's paths are other rounds'.
  - **Left of collections' median, 0.35 us, not built.** The sets 0.21: TryInsert's count on its `entries` local
    for the whole method (kept because a comparer's call can store the field while the local is read after: it
    could be taken only around the branch with those calls, a placement the compiler's scoped counts do not have)
    and the new value counted by the store and let go by the caller (`OwnedParams` leaves TryInsert out: past its
    size, the stores in loops, though each is followed by a return), 0.11 together; the frees above, 0.10 (of the
    30 removed and the overwritten values a frame). The removes 0.05 and the queue 0.07: not looked into. Strings:
    `Join` of 20, 0.27 us against 0.20, not looked into (the frame is below NativeAOT's without it).
  - Checked: On the homelab at 11b6497 (the last commit that changes code): suite 214/215 checked (088's timer line; alone it passes both ways) and 215/215 as shipped; at c3c4867, 215/215 both ways; stress, 200 loaded runs a mode: 213, 214, 220 and 221 the same in every run; the self-hosted compiler (tools/selfhost-switch.sh, which now compiles test 145's hello world: its own one-line program was top-level statements, an error in a library, and no module was ever written, on main too) with and without the round's three switches: the same module from both (27,393 bytes), 9.06 to 9.18 s as it is and 9.07 to 9.30 switched, peak memory 344 MB both. The module differs from the one the compiler run by .NET writes for the same file at byte 24,844 (a comparison added to the script this round, each given the file under another path: not followed, and not known for main).
- **The build, round 7: the list on top of its node (fixed), and what fewer members can give (measured, not built)
  (2026-11-22 to 23, branch claude/tree-build-7, from claude/round-6b 8022fbc; the desk machine; not merged when this
  was written).**
  - **tools/tree-assign.cs's leak: the cause and the fix.** A list of children that code refers to while its
    node's first child is put into it (in a program with a second store of the field the receiver of
    `e.Kids.Add(Tree(e, ..))` is counted across the call, not borrowed; the same for a local or a static that holds
    the list) waits for its owner as the node does when the child joins: LinkMembers leaves both to LinkWaiting,
    and the pairs stand in the order of the visits, (child, node) then (node, list). Taken so, the child's chain
    ends at the list, which nothing owns yet, the node is not found above the child, and the child becomes the
    node's owner; the second pair then finds the list above the node and makes the node's own field an
    up-reference to its list. Every node hung below its first child with its list on top, kept by holds alone, and
    a subtree stored over was never destroyed before its tree went (121, 161, 201 nodes for .NET's 121, 121, 121;
    the holds are not kept ones, `CS2WASM_STALE_HOLDS` is off: round 6's guess at the cause was wrong). Done:
    `Rt.WaitingTop` (`CS2WASM_NO_WAITING_TOP=1`): a pair that would make the container the value's owner first
    looks whether the value itself waits, further down the list, to own the top of the container's chain; that
    pair is settled first (the value owns the top, as its turn would have had it), and the value is then above
    the container: an up-reference. Test 199 (the receiver counted, a local, a static; subtrees stored over,
    children moved, a node of a replaced part held, nodes with finalizers; counted against .NET while the trees
    live): 259 nodes and more for 114 without the fix.
  - **What a node's way of holding its children costs** (the scene tree, 19,531 entities, the parent set as each is
    made; a program of three classes, not kept; build and drop, three runs): a List, the scene's, 1.70 to 1.75 ms
    and 0.42; an array of the node's own with room for eight from the first child (no list: two members a node
    with children, one a leaf, no growth) 1.53 to 1.59 and 0.18 to 0.20; first-child and next-sibling fields (one
    member an entity, nothing else) 3.48 to 3.54 and 0.24 to 0.26. **So the number of members is not the build's
    cost: without the list's membership the build is 0.2 ms shorter and the drop half; one member an entity,
    linked through siblings, is twice as slow to build, because each link there is a general one.** What a build
    pays is the links, by their kind (round 6: a fused leaf's two joins 19 ns, its up-reference 9; a node 200).
  - **The floor, and what is not one.** Entity cannot be taken out of the groups at compile time in this scene: its
    type is on a cycle through its own list (`Entity -> List<Entity> -> Entity[] -> Entity`) whatever is proven of
    the parent field, and the scene's move puts a node into the list of a node the program picks at run time
    (`to.Children.Add(moved)`): that `to` is not below `moved` is true and is not in the program's text. So a
    member an entity and an up-reference a child stay (SPEC: no collector, the cut subtree freed at its store),
    and with them, by the parts measured so far, about 10 ns of allocation, 10 for a join and 5 to 9 for an
    up-reference: 25 to 30 ns an entity, 0.5 to 0.6 ms a build against NativeAOT-LLVM's 0.26 (whose own rebuild
    frames then pay 2.4 ms collections: its p99.9). That sum is an estimate from measured parts, not a
    measurement. Today's 1.6 ms is three times it: not a floor.
  - **Not built in this round: the exclusive part** (a field's object proven at compile time to be the holder's
    alone: made by the holder's initializer, used only as the receiver of calls that keep nothing, never stored,
    returned or captured; and List<T>'s array likewise where the program reaches nothing that lets it out). What
    it takes, worked out: the part carries its holder in its owner word and a flag, never joins and has no place
    on a ring; every entry of the runtime that takes a container (ArenaLink, ArenaUnlink, ArenaArrayCopy,
    ArenaArrayClear, ArenaMove, ArenaGrowArray, ReplaceArray, the stores by address) maps a part to its holder
    first, the reference then a counted inside reference of the holder outside its slot bits (Far), a child's
    owner the holder itself (its parent field one step up the chain, not three); a list's growth is a plain copy;
    the holder's generated visit goes through its parts' slots; the holder's clear releases the part as the plain
    object it is. By the array variant above it is worth at most what the list and the array cost today: about
    0.2 to 0.4 ms of a build and half the drop (the store over a subtree in an ordinary frame with it), and it
    makes every leaf's parent field a one-step up-reference. It does not make the node's own path shorter (item
    2: the node joined from below, 200 ns), which stays the largest single part.
  - Checked. frames.mjs here, the fix off and on in turn twice (`--runs 3 --js-young 32`; the scenes do not take
    its path): tree median 0.047 and 0.048 to 0.049 ms against NativeAOT-LLVM's 0.029 to 0.030, p99.9 2.52 and
    2.62 off, 2.67 and 2.88 on, against 2.44 to 2.57 (the desk's spread; in the two quietest pairs this side's
    rebuild frame is 0.08 and 0.11 ms above NativeAOT-LLVM's: with round 6's step, not reached); the other scenes
    as on main; bench.mjs 0.98x and 0.94x; hello world 27,642 bytes. On the homelab at 1c7f1c4: the suite 214/215
    checked and 214/215 as shipped, the one 088's timer line in both (other sessions' jobs beside it; see the log
    for the run on the last commit); `tools/stress-cycles.sh`, 200 loaded runs a mode: 199, 198, 192 to 197 and
    139 the same in every run of both modes, 127 differing in 1 of 200 in each (main's rate); the self-hosted
    compiler built with and without the switch (`tools/selfhost-switch.sh CS2WASM_NO_WAITING_TOP=1`: 238 and 243
    s, 31.8 MB each; its runs 10.3 to 11.2 s either way, 490 MB; the script's own comparison found no output
    file, its `w42.cs` is not a program the compiler takes: not followed) and `node tools/selfdet.mjs --self
    tests/out/selfsw/as-is.wasm tests/Conformance/145_size_hello.cs`: the self-hosted compiler's module the
    .NET-hosted compiler's byte for byte (27,595 bytes, 9.1 s).

- **The collections and strings scenes, round 3 (2026-11-23, branch claude/colstr-3, from claude/round-6c 20cb183;
  not merged when this was written): references into a collection's storage; what was not done.**
  - **Fixed: a span over a list's items, or a `ref` to a dictionary's value, held across a growth
    (`Compiler/StorageKeeps.cs`, `CS2WASM_NO_STORAGE_KEEP=1`).** A span or a `ref` holds no count here; what keeps
    its array is whoever holds the array, and for `CollectionsMarshal.AsSpan(list)`, `GetValueRefOrNullRef` and
    `GetValueRefOrAddDefault` that is the collection's field, which a growth (or Clear with TrimExcess, or the
    collection's own end) stores over: the span then read the freed array (as shipped its bytes until the block was
    used again; a checked build trapped at the first read; `list.AddRange(CollectionsMarshal.AsSpan(list))` copied
    from it). Now the call site holds the array: the object the returned reference points into (`Rt.ObjectOf`, the
    allocator's table, no search) is counted into a local of the calling function, one a call site, found before
    the body is compiled so that every return and every unwinding lets it go; a later call at the same place lets
    the earlier array go. The span reads and writes the old array whatever the collection does, as .NET's does, and
    a growth meanwhile finds two holders and copies (`Rt.MoveRange` moves only an array nothing else holds). A
    method or a property of the program's own that returns a span or by reference and calls one of the three is
    treated as they are at its own call sites (one level: a wrapper of a wrapper is not seen, and a span such a
    wrapper returns through two frames is as before). Nothing changes for a program that makes no such call: the
    growth's code is the same. Test 222 (a span across growths, a clear and the list's drop; AddRange and InsertRange
    of the list's own span; a span taken every turn of a loop; a method and a property that wrap the call; a `ref`
    across insertions and a removal, to an added entry, to a missing key; items with finalizers that mark them:
    without the keep both modes differ at the first line).
  - **Collections' worst frame, 0.21 to 0.37 ms with round 2's move: the engine's tier-up, shown.** It is one
    frame, the first clear after the warm-up (frame 200), in every run; the other clears are 0.18 to 0.20 ms, as
    before or below. With `node --wasm-tiering-budget=1000000` (the optimizing tier a thirteenth of the calls
    sooner) frame 200 is 0.21 ms as the others, three runs of three; by default 0.38 to 0.39. The code that lets an
    array's values go ran at every resize before the move and runs only at the clears now, five times by frame
    200. Steady state: 0.18 to 0.20 ms. Not changed.
  - **The module the self-hosted compiler writes against the one the compiler run by .NET writes (round 2's
    difference at byte 24,844): the output's name.** The comparison added to tools/selfhost-switch.sh gave the two
    different output names, and the name is in the module; the script now asks tools/selfdet.mjs (the same paths
    and name). On the homelab tools/selfdet.mjs: self = net, byte for byte (27,591 bytes).
  - **Not done this round, as round 2 left them:** the free's 6.6 ns an object taken apart (`rt.destroy_now` is
    where a plain object's end is: the dispatch on the type's number, a release of each reference field, a string
    field's too, then FreeObject's block kept aside for its size and the one that was there back onto its page's
    list with the page's count: read, and timed once at 4.8 ns a free with the clock's share, not taken further:
    the desk gave no two runs alike for an hour); TryInsert's counts (0.11 us); the removes and the queue (0.12),
    the queue's growth as a move (its copy is a rotation: MoveRange takes a whole array from its first place);
    `Join` (0.07). Collections' median stays where round 2 left it, 1.07x to 1.08x NativeAOT-LLVM's.
  - Checked: On the homelab at 0aef607 (9db608e's code and the script): suite 220/220 checked and 219/220 as shipped (088's timer line; alone it passes both ways); at 9db608e 218/220 checked and 219/220 as shipped: 072_http in both (the oracle's own connection refused, the port taken by another run) and 131 checked (5 left, the async kind that differs under load; here it passes both ways with the keep and without, and the second run did not show it); stress, 200 loaded runs a mode: 222 the same in every run; the self-hosted compiler with and without the keep: the same module (27,579 bytes), 9.4 to 10.0 s, peak memory 344 MB. Hello world 27,642 bytes on this base. Not measured this round: frames.mjs and bench.mjs (no change to any scene's code: none makes such a call).
- **The free path taken apart; references into storage, complete; Dictionary takes a fresh value; the queue's move
  (2026-11-22, branch claude/free-path-1, from claude/round-6d a15be27; the desk machine, shared: lowest of N runs in
  turn, ranges; not merged when this was written).** "Before" is the same compiler with the round's switches set
  (`CS2WASM_NO_FLAT_DESTROY=1 CS2WASM_NO_DESTROY_IN_PLACE=1 CS2WASM_NO_OWNED_INSERT=1`). `tools/alloc-parts.cs`, 100
  objects of 36 bytes a frame, ns, NativeAOT-LLVM / before / now (five runs in turn): **made over old ones 226 / 600 /
  300; 100 let go 100 / 500 / 233**; made and kept 271 / 200 / 200; 100 stores of one object 100 / 122 / 133 (122 to
  158 on both sides over the round's runs). `tools/colstr-parts.cs` (five in turn, quiet): **the collections frame 6,405 to 6,101
  against 5,961 (1.07x to 1.02x by this tool; 1.02x and 1.03x in its other two rows)**, strings' 6,759 to 6,690 against
  7,458 (0.90x).
  `frames.mjs --runs 3 --js-young 32`, three pairs in turn on a quiet machine at the last commit, before / now:
  **collections 1.03x to 1.05x / 1.00x to 1.03x; churn 1.57x to 1.58x / 1.56x to 1.58x (not moved: its 400 frees are
  1% of its frame; not reached); tree 1.62x to 1.63x / 1.62x to 1.65x (not moved, not reached), its p99.9 2.44 to
  2.59 ms / 2.44 to 2.57 against NativeAOT-LLVM's 2.42 to 2.73; strings 0.89x to 0.91x / 0.86x to 0.87x; bigdrop 0.90x
  to 0.94x / 0.90x to 0.97x.** (Pairs taken with other sessions at work spread wider, 1.00x to 1.06x for
  collections either way.) The cs2wasm side alone, five pairs in turn, median ms before / now: tree 0.045 to 0.048 /
  0.046 to 0.049, churn 0.077 to 0.079 / 0.076 to 0.079. `bench.mjs`, five pairs: 0.92x to 0.93x both ways (0.89x to
  0.99x in one pair on a busy machine). Hello world 27,642 to 27,757 bytes.
  - **What a free was, to the instruction** (alloc-parts built with `CS2WASM_NAMES=1`, read with wasm-dis; `node
    --trace-wasm-inlining`). `kept[i] = null` in a loop: the slot read and cleared, the null test, the count read and
    compared with 1 (written out in the loop, RcFastPaths); then `rt.release`: the null test and the count again, the
    count compared with 1, the count word set to 0, the type word read and compared with string's, read again and
    compared with the number of leaf types, the nesting depth (a global) read and compared with 48, read, added to
    and written; a call of `rt.destroy_now`: the type word read, a jump through a table of 39 cases; in the
    particle's case its string field read and released (null test, count read, two compares: the literal is never
    freed), then `Rt.FreeObject`: the size rounded and compared, `s_hot` read, the size's slot read and written with
    the block, the block's count word set to 0 again, and for the block that was in the slot the allocator's table
    read (`MapGet`, two loads and two tests: is it a page's block), the page's count of blocks in use read, taken
    down and tested, its flags read and tested, its list's head read, the block's link and the head written, the
    count written; back in `rt.release` the depth read, taken down and written, read again and tested, the list of
    waiting destructions tested. About twenty loads, ten stores, twenty branches, one indirect jump, a jump through a table of some forty cases counted once above, and one call
    where the engine has inlined all it can (it put `rt.release` into the loop, the string's release and
    `FreeObject` with `MapGet` into `rt.destroy_now`'s case; `rt.destroy_now` itself, 882 bytes, stays a call).
    4 ns a free over NativeAOT-LLVM's store on a quiet machine (500 against 100), 6.6 loaded.
  - **Measured before building.** The nesting depth alone: `rt.destroy` made to call `rt.destroy_now` for every
    object (a prototype, not sound): let go 505 to 409, **1 ns a free**. No finalizer test, no weak-reference test
    and no visit of fields is on a plain object's path: a class without a finalizer has no test for one (its
    destruction is generated without), the weak references' test (`rt.on_destroy`, the mark bit) is in the program
    only when it has marks, and the fields released are those the class has. Clearing the block is the allocation's
    (`AllocObject`'s hot path without it: made over old ones 616 to 666 against 600 to 634 with: no difference). The
    table's lookup that tells a page's block cannot go: a small block that is no page's exists (what is left when a
    large block is cut), and it is off the path that the next instruction waits for.
  - **What the engine inlines decides more than the instructions do.** V8 inlines a call only if its count in the
    baseline code was above zero when the caller was compiled by the optimizing tier, and it never compiles a wasm
    function again (`--trace-wasm-inlining`: "not called often enough", count 0, for the release in a loop
    compiled while its array was still filling). A loop compiled before the first object died in it keeps the
    release's call for good. And by V8's tiering rule (a budget, `--wasm-tiering-budget`, that a function's size
    is taken off at every call) a function of 18 bytes stays in baseline code for several hundred thousand
    calls: not traced, but it is what the timings below fit. So the first form of the change, a release that names its class calling that
    class's destruction directly (no `rt.release`, no dispatch), was **slower** than the dispatch where the loop
    had been compiled early: `tools/colstr-parts.cs` "(100 objects alone)" 568 ns to 757, alloc-parts "made over
    old ones" 660 to 610 only, while the same code with the loop warmed on the freeing path first gave 400
    against 600. `rt.release` and `rt.destroy_now` are shared by every release of the program and hot from the
    start; a class's own destruction, called from one place, runs in baseline code through most of a measurement.
    What is built follows from that: nothing small and private to a class is called on the free path.
  - **Built: flat destructions** (`CS2WASM_NO_FLAT_DESTROY=1`; `RuntimeGen.IsFlatDestroy`). A class is flat when its
    destruction lets go of leaves by their very type (a string, an array of numbers, a sealed class without
    references) and frees the object: no finalizer, no field of a type whose object could hold a reference, on no
    cycle of types. It runs none of the program's code and nests one level at most, so (a) `rt.destroy` tells flat
    classes by their number (numbered after the leaves: a class's own are three ranges of numbers at most) and
    counts no nesting for them, from any release; (b) a release that names such a class
    (`FunctionCompiler.ExactTypeHint`: the place's type is the sealed class) is, in a loop or in a function of at
    most 60 instructions (`CS2WASM_IN_PLACE_SMALL=n`), **the destruction written in place**
    (`WasmModule.ExpandLeafReleases`, `CS2WASM_NO_DESTROY_IN_PLACE=1`; leaves' too, which were a call of their own
    function before): the count's test, the leaf fields' releases with their own count tests (a shared string or a
    literal costs a test and no call), and the free of a known size as `Rt.FreeFixed`, put there by the inliner:
    `s_hot` read, the size's slot exchanged with the block, and a call (`Rt.FreeSmallOld`, shared and hot) only for
    the block that was in the slot. Elsewhere a flat class's release is `rt.release` as before. Not with threads
    (a program with threads is compiled as before: its counts are the atomic ones or the plain ones of
    TypedCounts.cs, and neither is touched); `FreeFixed` not in a checked build or with the share check
    (`FreeObject` there).
  - **Built: the class named at more releases.** A release named its class only at a typed local's scope end and
    in the runtime's loops over an array's elements. Now also: a local assigned again (`StoreVar`); a store's old
    value in a field or an array's element (Arenas.cs, the plain site); the fields a destruction lets go
    (`ReleaseOfField`: the hint is carried through the forwarder, `WasmModule.ResolveForwards`); and **a store in
    generic code, by the type the code is compiled for** (`CS2WASM_NO_STORE_HINT=1`; `FunctionCompiler.StoreHint`):
    `List<T>`'s indexer and `Dictionary<K,V>`'s entries are compiled for each T, so the store knows the class when
    T is sealed. This is where the scenes' frees are: none comes from a place whose own type is the class. Only
    for a sealed class that looks flat by its fields (`Driver.LooksFlat`), not for a string or an array: with
    every store of a string in a generic collection's loops written out, the self-hosted compiler compiled hello
    world in 9.3 to 9.5 s against 9.0 to 9.2 (three rounds in turn on the homelab; at the commit before the hint the two were the same, and with the hint for flat classes only they are again, 9.18 to 9.48 s against 9.22 to 9.47), and no
    scene gained from them. A name that nothing used is cleared after the pass (`CS2WASM_NO_HINT_CLEAR=1`), so that bodies which differ by
    nothing else are merged as before.
    colstr-parts, with / without the store's hint (five in turn): map set 100 (new) 1,920 / 1,987, map remove 30
    540 / 567, the whole frame 6,746 / 6,910. alloc-parts through a `List<Particle>`: made over old ones 600 to
    400, let go 700 to 570-600. Modules 0.3% larger where a program has such collections; hello world the same.
  - **Tried and not kept: the dispatch's own cases written in place** (the leaf fields' tests and `FreeFixed` in
    `rt.destroy_now`'s cases, for the frees that reach it): churn 0.085 to 0.087 ms either way, collections and
    strings the same, three pairs; hello world 27,757 to 30,177 bytes, over its limit. The diff is not in the tree.
  - **What a free is now, and what is left.** In a loop over a place of the class: the count read and tested, the
    count word cleared, the string field's test, `s_hot`'s slot exchanged, and for the block that was there the
    call that puts it back on its page (the table's two loads, the page's count, flags and list: three loads and
    three stores in one line of the cache). 1.5 ns a free over NativeAOT-LLVM's store (254 against 100), from 4;
    where each free is followed by an allocation of the size the slot is empty and nothing else runs (made over
    old ones 300 against 239: 0.6 ns). That remainder is work NativeAOT-LLVM does not do in the frame at all (its
    collector does it in its pauses): the measurement is the 254, not a proof that nothing shorter exists. A
    release that does not name its class (an open class, an interface, `object`, a class with references to
    other classes) goes through `rt.release` and `rt.destroy_now` as before, without the depth's traffic when
    the object is flat.
  - **References into a collection's storage, complete** (`Compiler/StorageKeeps.cs`; round 3 covered the three
    calls and a wrapper one level deep by return value). A call now keeps the storage wherever a reference into it
    can come out, decided by a fixed point over the calls (`Driver.ExposesStorage`, depth first; an answer "no"
    that leaned on a method still being decided is not remembered and is asked again): the value returned (a
    `ref`, a span, a ref struct with spans or `ref` fields anywhere inside it, one keep local a reference); an
    `out` or `ref` argument that is a ref struct; the ref struct a constructor fills; the ref struct an instance
    method is called on; a virtual or interface method when it or any override or implementation in the program
    is one; a delegate that returns or gives out such a type (always: its target is not known). The library's
    methods are decided as the program's: **`Enumerable.First`, `Last` and `Any` with a predicate walk a list
    through `TryGetSpan(out ReadOnlySpan<T>)`, and a predicate that grew the list left them reading the freed
    array** (`l.First(x => { Grow(l); return .. })`: as shipped the old bytes, a checked build traps, with main's
    compiler too; now it answers as .NET). What flows where inside a body is
    not followed: a body that makes such a call anywhere counts for all its ways out, which keeps an array to the
    calling function's return, never shorter than .NET. A reference to the place just past an array's end (an
    empty slice at the end) names the block after it (`Rt.ObjectOf`), as before: that object, if there is one, is
    kept to the function's return too. Not covered: a function pointer.
    `CS2WASM_STORAGE_KEEP_SHALLOW=1`: as it was. Test 222 extended (two and three frames, recursion, `ref` returns
    up three frames, ref structs returned, constructed, loaded and nested, `out` and `ref` arguments, an
    interface, an abstract class, a delegate, LINQ under a growing list: shallow, the new sections' lines differ as shipped, freed
    items read, and a checked build traps at the first). Hello world the same size; test 008 (LINQ) 365,696 to 367,078 bytes.
  - **A queue that grows moves its items** (`Rt.MoveRotated`, Boundary/ArrayMoves.cs; an edit of Queue.cs;
    `CS2WASM_NO_ARRAY_MOVE=1`). The full array's two stretches (head to the end, the start to head) go to the new
    array as bytes under `MoveRange`'s conditions, the old array left an array of bytes; a queue trimmed or given
    room (not full) copies. 4,096 enqueues of objects into a new queue (ten growths) 31 us to 21. The scene's
    queue grows only while it warms up: its median was not this (below). Test 236 (growth from every position of
    the head; the old array held from outside by reflection keeps its items: with `CS2WASM_ARRAY_MOVE_BREAK=1`
    that line reads null four times).
  - **Dictionary takes a fresh value's count** (`CS2WASM_NO_OWNED_INSERT=1`; OwnedParams.cs `IsMapInsert`).
    `map[key] = new Item()`, `map.Add(key, Make())`, `map.TryAdd(..)`: the new value was counted by TryInsert's
    store and let go by the caller. TryInsert now consumes its value and its three callers pass it on, as
    `List<T>.Add` does. What let it in: it is far past the size limit (let in by name, as List's Add is), and its
    three stores of the value are in loops or after them; the rule: a store in a loop that is a statement
    followed by a return runs once at most (`ReturnsAfter`), and of two stores only one can run in a call when
    both are followed by a return, or one is and the other comes after it in the text and is in no loop
    (`OnceByReturn`; a method with a goto is not looked at). A value that is not stored after all (Add of a key
    that is there, TryAdd's false, a key whose GetHashCode or Equals throws) is released by the callee's end or
    unwinding, as any consumed parameter. An indexer's setter got its value already evaluated, so nothing was
    ever handed over through one: Dictionary's now notes the temporary that holds the fresh value's count
    (`PropertyLV.Store`; not with threads, not where the assignment's own value is used). colstr-parts, NativeAOT-LLVM
    / with / without, five in turn, quiet: map set 100 (new) 1,707 / 1,770 / 1,806; the whole frame 5,980 / 6,165 /
    6,447, again 5,947 / 6,127 / 6,369, without the clears 5,948 / 6,128 / 6,404. Test 237 (fresh values by `new`,
    a call, an initializer; overwrites; duplicates that throw; TryAdd refused; keys that throw; values the caller
    goes on using; the assignment's value used; every item counted by its finalizer; a program's own TryInsert
    that stores its value twice in a loop: with `CS2WASM_OWNED_INSERT_BREAK=1`, the rule not asked for, its
    second place reads null).
  - **Collections, what is left** (colstr-parts, NativeAOT-LLVM / now / before): the removes 499 / 508 / 542 and the
    queue 191 / 200 / 202 are within 2% and 5% (the removed values' frees were the removes' difference; the
    queue was not changed); the sets 1,699 / 1,762 / 1,905, and the frame up to the sets 1,993 / 1,984 / 2,145. **Left,
    not built: the count on TryInsert's `entries` local** (the frame to the sets with one object made before,
    which is not a fresh value, 1,768 / 1,903 / 1,882): it can go only in the path without a
    comparer's call (a key's Equals may store the field while the local is read after, and the local's count is
    what keeps the array: memory safety), which needs a count taken at the branch and a tail that knows whether
    it holds one. And the lookups: the frame with `TryGetValue` 6,101 against 5,953 with `ContainsKey`
    (NativeAOT-LLVM 5,961 and 6,319): the value handed out, outside `OutBorrows`' shape in this tool's loop. **By
    this tool the collections frame is 1.02x to 1.03x NativeAOT-LLVM's; by frames.mjs its median is 1.00x to
    1.02x. Not below it.**
  - **Not done.** `String.Join` (246 ns against 188 for 20 strings, 276 before): read, not built: `JoinCore` reads every
    element twice, once to add up the lengths and once to copy, each read counted into a local and counted off
    (the span's strings are kept by nothing the function can name), with a frame for the unwinding; a borrow of a
    span's element while nothing stores is a new rule. `Queue.Dequeue` and `Stack.Pop` read the slot (a count),
    clear it (the count off) and return the value: a pair that a "taken out of the slot" rule would remove, seen in
    the module, not built. **Found, on main too, not fixed: a `ConditionalWeakTable`'s values are not let go when
    their keys die while the table lives** (25 keys dropped, 25 values still alive after collections; .NET 0).
  - Checked: On the homelab at 66374eb (the last commit that changes code): suite 224/224 checked and as shipped (343 s); 088 alone both ways (at 97f3772 the suite was 222/223 both ways, 088's timer line; at 9e254f8 221/224 both ways, tests 055, 058 and 107 not compiled, "consumed struct parameter not moved in Dictionary<string, ResourceLocator>.Add": the owned insert for a value that is a struct, fixed in 66374eb, test 237 has the case; the earlier stages 222/222 at a7918b9, c5395a0 and 0e981fa). Stress, 200 loaded runs a mode: 235, 222 and 236 at 9e254f8 and 237 at 66374eb the same in every run of both modes; 139 the same in every run and 127 differing in 7 of 400 at 9b4639b (its "async 5" line, main's rate; 4 of 400 at a671855). The self-hosted compiler at 66374eb, built as it is and with `CS2WASM_NO_FLAT_DESTROY=1 CS2WASM_NO_OWNED_INSERT=1` (tools/selfhost-switch.sh: 32,558,663 and 32,292,082 bytes, 0.8% larger): both compile hello world to the same module (27,709 bytes), 9.18 to 9.48 s as it is and 9.22 to 9.47 s switched, three rounds in turn, peak memory 344 MB both; tools/selfdet.mjs: self = net, byte for byte (27,721 bytes); the same at 0e981fa and 97f3772. Not run on the homelab: frames.mjs (it has no Emscripten); nothing was measured with threads.

- **Field loads by the field's identity (2026-11-22 to 23, branch claude/field-loads-1, merged with main 3dbef69;
  the homelab for the pass against itself and for the checks, the desk for NativeAOT-LLVM).** V8 does not carry a load from memory across a store or a call: it cannot know the store is
  to another place. The compiler knows which field every load and every store by name touches.
  - **Built** (`CS2WASM_NO_FIELD_HOIST=1`: the module from before; Compiler/FieldLoads.cs decides, Wasm/FieldLoads.cs
    rewrites). A load of a class's instance field by name carries the field's tag (by declaration: one field for
    every instantiation), a store by name leaves a mark, each loop's instruction names the fields stored inside it
    (with what the inliner puts there), a call is asked through the summary of all stores the borrows use (direct,
    indirect by its type, the host by what it can call back, a release by what destruction can run). **Stage A:** a
    field of the object in a local the loop does not assign (or a copy of one, made before every read) is read once
    before the loop when nothing the loop runs can store that field of any object; an array's length likewise.
    **Stage B** (`CS2WASM_NO_FIELD_REUSE=1`): the same load of the same unchanged local, with nothing between that
    can store the field and the first on every way to the second, is the first one's value. **The inliner**
    (`CS2WASM_INLINE_READERS=0`): a callee of up to 80 instructions that reads fields of an object it is given (a
    list's indexer) goes into loops, so that its reads are the loop's own. A `readonly` field that only
    constructors of its own class store is asked about the loop's own code alone.
  - **What takes a field out**, and the line of tests 230 to 234 that prints something else when the condition is
    taken out (`CS2WASM_FIELD_HOIST_WITHOUT=<condition>`, `bash tools/field-hoist-proof.sh`): its address taken
    (`ref`, `out`, `in`, `fixed`, Interlocked, Volatile, Unsafe, a span over it, a ref return: `address`, 231 "ref
    local"), and with it every field of the same object (`walk`, below: 231 "walked", 207 "through a ref"); a class with [StructLayout] or a field with [FieldOffset] (`layout`, 231 "overlap"); reflection can set
    it (`reflection`, 232 "reflected"); a program with threads, unless the field is readonly (`threads`, 234: the
    wait under a lock never ends); the loop's own store, also through another reference to the same object, a
    handler's, a constructor's own (`loop`: 230 "own", 232 "handlers", 233 "generics", 234 "constructed"); a callee's
    store, a finalizer's, an iterator's, a filter's through rt.first_pass (`calls`: 230 "callee", 232 "delegates", 233
    "iterators"); a function pointer's target (`indirect`, 232 "pointers"); the local assigned in the loop
    (`assigned`: 230, 232, 234) and a copy that does not come before every read (`dominance`, 230 "copies"); bodies
    made one that differ in their tags (`merge`, 230 "merged"); a setter inlined into the loop (`inline`, 230
    "inlined"). **By construction, no switch:** a struct's field and a field of a struct's type have no tag (a struct
    assigned as a whole over the field read, a struct's method on it: 231 "structs" stays as it is); a captured
    variable, an iterator's or an async method's local, a primary constructor's parameter are fields the compiler
    makes, without tags. **Not shown by any loop:** `volatile` (a volatile field is read with other instructions,
    and no program with threads has a tag on a field that is not readonly: the condition is the third guard);
    `machine` (a state machine's own function is left alone: its filters are calls in its own code, which the
    calls' rule sees, and a caller's filter runs before the machine's finally reads, as .NET has it, with the rule
    and without: tried, 233 "machines"; the rule stays as a guard). **What rests on the program not doing it,** as
    the borrows do: an object read through another class than its own (Unsafe.As), a constructor run a second time
    on an object that exists.
  - **Found in this round, 1: a field of a field of a null read anywhere.** `for (...) s += n.A.T` with n null: A
    was read once before the loop from address 0 plus A's offset, where the program's data is (the module's data
    starts at address 16: 0xffffffff at 16 and at 40, a string's characters after), and T then once before the loop
    through that word: "memory access out of bounds" from a loop that takes no turn, or in place of the
    NullReferenceException of its first turn. LoopLoads.cs (on main, before this pass in the order) made the first
    read and this pass the second; this pass alone does the same in its second round. Both now put null in place
    of a word-sized value read from a null (a select on the local: three instructions before the loop, none in
    it), LoopLoads.cs only while this pass is on. `CS2WASM_FIELD_HOIST_WITHOUT=null`: test 230 traps at "null
    chains" (twelve offsets, one loop a call, do-while loops, a null and an object whose fields are null).
  - **Found, 2: a loop that takes no turn paid for what its turns read, and the tree scene was slower.** The
    homelab, with and without the pass in turn (five rounds, then three, `tools/measure-switch.sh`): churn's median
    0.093 ms against 0.103 and 0.089 against 0.100, but tree's 0.064 against 0.060 and 0.062 against 0.059, and
    `tools/tree-frame.cs`: the walk 39.1 to 39.5 us against 35.3 to 35.6, the indexed walk 26.8 to 27.4 against
    23.8 to 24.2. The walk is entered once a node and most nodes are leaves: each entry read the list's array, its
    length and the node's X and Y for a loop that took no turn. **Now a place the loop reads before its first test
    (`i < list.Count`: the size, on every entry) is read before the loop as it is, and a place it reads only after
    the test is read inside `test; if (the loop goes on) { ... }` before the loop**, the test's instructions copied
    as the loop will have them. They are copied only when they can be run twice for once: no store, no call (a
    check that throws aside), no local given a value made from its own (`i++ < n`): `CS2WASM_FIELD_HOIST_WITHOUT=
    twice` and 230 "tested" prints another number. A loop whose first instructions are no such test (an
    enumerator's MoveNext, a do-while) reads before it only what it reads on every entry. The benchmark program:
    63 fields and 66 lengths read before a loop, 58 loops with reads behind their test, 214 loads a second time
    (every place before its loop, as before: 168 fields and 75 lengths). On the desk after it, with and without in
    turn (four rounds, other sessions beside): tree-frame's walk 29.0 to 29.9 us against 29.2 to 30.7 (a foreach:
    nothing is read before it now), the indexed walk **16.9 to 17.8 against 18.1 to 19.2**, without the floats
    14.6 to 15.7 against 17.1 to 18.7; build, store and move the same. The scenes themselves on the desk
    after it (`tools/frames-ab.mjs`, six rounds in turn): **tree 0.0480 to 0.0487 ms with the pass and 0.0471 to
    0.0494 without: level**; churn 0.0674 to 0.0695 against 0.0800 to 0.0821. The homelab's five rounds after
    this change, on a loaded homelab, do not agree for the tree scene: under Checked.
  - **Found, 3: a reference to one field walked to the next.** The suite (test 207, main's, for
    SameBaseLoads.cs): `ref byte rb = ref p.B0; byte c = p.B2; Unsafe.Add(ref rb, 2) = 77; byte d = p.B2;` read B2
    once: only B0 had its address taken. A reference to a field reaches the fields beside it (Unsafe.Add, a
    pointer's arithmetic, a span made over more than the field), so the class of a field whose address some code
    has is noted, and every field of that class, of its bases and of the classes derived from any of them has no
    tag. `CS2WASM_FIELD_HOIST_WITHOUT=walk`: 207 "through a ref" and 231 "walked" print something else. The
    benchmark program after it: 1 field and 60 lengths read before a loop, 28 loops with reads behind their test,
    128 loads a second time (63, 66, 58 and 214 before the rule); the churn scene and `tools/churn-array.cs` as
    before it (below).
  - **Found, 4, and not this pass: collections that grow by moving broke the self-hosted compiler on every
    program larger than hello world; fixed here.** tools/measure-switch.sh was made to compile test 230 with the
    self-hosted compiler: "memory access out of bounds" in Inliner.RunOnce, with the pass and without it
    (`CS2WASM_NO_FIELD_HOIST=1` at the build is the code main makes), on tests 207, 220, 222, 230, 232 alike; once
    no end at all (a core busy for half an hour: tools/selfdet.mjs now stops a compiler after ten minutes). Built
    with `CS2WASM_NO_ARRAY_MOVE=1` it compiled them byte for byte. With every function named
    (`CS2WASM_NAMES=1`): Rt.RunUnlink under AllocHuge under AllocObjectArena under `List<Instr>.List(int)` under
    Inliner.RunOnce, and rt.retain under ArenaUnlinkAt under an Instr's copy. **The cause: Rt.MoveItems
    (Boundary/ArrayMoves.cs, claude/colstr-2) leaves the old array an array of bytes, so that its end counts
    nothing off; an array whose type can be on a cycle is allocated with the arena's 32 bytes before its header
    (AllocObjectArena) and its block is freed with them by its own type's destroy function alone
    (FreeObjectArena): as byte[] it went to FreeObject at the header's address, 32 bytes into its block and 32
    short.** It takes such an array that is still a plain object with one count when its collection grows: a new
    list, dictionary or set made with a capacity, filled past it with items that refer to no member of a cycle,
    given to its owner afterwards: Inliner.RunOnce's `new List<Instr>(f.Body.Count + 32)`. (Items that refer to a
    member make the array a linked one, which MoveItems refused already: test 220 and the first small programs
    tried here never moved one.) **Fixed: MoveItems refuses an array whose type can be on a cycle (SccOf of its
    type), and the collection copies as upstream;** `CS2WASM_ARRAY_MOVE_CYCLE=1`: the old move. Test 240 (lists,
    dictionaries and sets of such a struct grown among large blocks of other kinds that are kept and checked):
    with the old move "memory access out of bounds" in the allocator under the .NET-hosted compiler, with the fix
    the same as .NET both ways. **The self-hosted compiler with the fix (and with this pass), on the homelab at
    601c0e1: tests 145, 207, 220, 222, 230 and 232 byte for byte the .NET-hosted compiler's; the same source built
    with the old move: hello world alone.** `tools/selfhost-variants.sh` builds the compiler once a set of
    switches and runs each on some tests (SHOW=1: the stack); `tools/selfstack.mjs` pauses a compiler that does
    not end and prints its stack (written for this, not needed in the end).
  - **Numbers, with and without the pass.** `tools/churn-array.cs` (the churn scene's loop over a list and over an
    array; us a frame): the homelab before the change above, list 94.5 to 95.8 against 104.4 to 105.6, array 90.1
    to 91.5 against 94.1 to 95.6; the desk after it, list 73.6 to 74.9 against 85.3 to 87.9, array 72.4 to 74.4
    against 76.5 to 77.6: **the list's loop is the array's now** (its size, its array and the array's length are
    read once). `frames.mjs --runs 3 --js-young 32` on the desk, three rounds in turn, other sessions beside
    (NativeAOT-LLVM's churn 0.052 to 0.055 where a quiet desk gave 0.049): **churn 0.069 to 0.071 ms, 1.33x to
    1.35x NativeAOT-LLVM's, against 0.083 to 0.086, 1.56x to 1.60x, without** (main: 1.58x); tree 0.051 to 0.059,
    1.68x to 1.80x, against 0.049 to 0.056, 1.60x to 1.70x: the desk's spread in those runs (the six rounds
    above, the two builds alone, are level; main: 1.63x; p99.9 1.14x to 1.26x both ways: not reached, as on main); collections 1.00x to 1.05x against
    0.95x to 1.03x and strings 0.89x against 0.84x to 0.90x: **nothing** (`tools/colstr-parts.cs`: the whole frame
    6.50 to 6.65 us against 6.55 to 6.64, the map's set 1.92 to 1.97 against 1.98 to 2.00); bigdrop 0.89x to 0.91x
    both; memory 16.0 MB both. `bench.mjs` on the desk, twice each in turn: 0.88x and 0.91x against 0.93x and
    0.93x, but cs2wasm's own times are the same within their spread (linq 13.2 and 13.9 ms against 13.9 and 13.3)
    and NativeAOT-LLVM's side moved: on the homelab, five rounds, the geometric mean without the pass is 1.006x
    the one with it: **nothing a benchmark shows.** Size: hello world 27,657 bytes against 27,642 (below 28,383),
    the frames' module 224,213 against 223,882, the benchmarks' 359,315 against 358,467, the self-hosted compiler
    32,351,700 against 32,171,871 (0.56%; before the third rule). Build: the self-hosted compiler in 246 s with the pass and 250 s without
    (the homelab's quiet pair; 279 and 550, 373 and 267 with other jobs beside). The self-hosted compiler's run on
    hello world: 9.0 to 9.3 s against 9.1 to 10.3, peak memory 344 MB both: **the same.**
  - **Checked.** The homelab at 7393522 (the last change to the compiler and the tests; tools and docs after it):
    the suite 226 of 227 checked and 227 of 227 as shipped, the one 088's timer line (alone it passes both ways).
    Before that the desk at 512c366, one runner a mode: 227 of 228 both ways, the one test 207's "through a ref":
    the third thing found above, fixed since. Stress, 200 loaded runs a mode: 230 to 234, 240 and 139 the same
    in every run; 127 differed in 4 of 400 (main: 6 and 7 of 400). `bash tools/field-hoist-proof.sh` at the
    last commit: every condition but `volatile` and `machine` changes a line. The self-hosted compiler: above
    (601c0e1: six programs byte for byte). The homelab at the last commit, with and without the pass in turn (five rounds, other sessions' jobs beside: the rounds spread by a third): churn's median 0.095 ms against 0.107; **tree 0.069 against 0.065, the pass's higher in four rounds of five, where the desk's two sets were level: not explained** (the scene's Build and Walk are the same code in both modules, 345 and 356 bytes, Tree(int) 18 bytes shorter with the pass; tree-frame.cs there: build 17.9 to 19.2 us against 17.5 to 18.0, walk 37.3 to 40.3 and once 43.8 against 38.2 to 40.4, the indexed walk 24.0 to 25.4 and once 29.0 against 25.9 to 27.7); collections 0.0076 against 0.0070 and strings 0.0085 against 0.0084 at their lowest, inside the rounds' spread; bench-ab's geometric mean 1.004x without the pass; churn-array's list 94.8 to 99.1 us (once 109.8) against 104.9 to 111.2. The self-hosted compiler there: built in 259 s with the pass and 249 s without, 32,298,942 bytes against 32,189,404 (0.34%), hello world in 9.4 to 9.5 s against 9.4 to 10.0, peak memory 344 MB both, and built either way it compiles 145, 207 and 220 (with the pass 230 too) byte for byte as the .NET-hosted one.
    **Not checked or not done:** a host that calls an export back while a loop runs is covered by a rule (the
    host's calls are asked what every export and every table entry can store) and by no test: the suite's host
    calls nothing back. Main's own checkout was not built with the fix for the moved arrays (the branch's
    source built with `CS2WASM_NO_FIELD_HOIST=1` stands for it). What the third rule costs was counted (the
    benchmark program's reads before a loop) but not looked into class by class: a class with one field whose
    address is taken loses all of them, where only a walk from that field could reach the others.
  - **What is left for the churn loop** (round 4's list): the NaN tests (4 us of its 8), the loop's shape (the
    test at the bottom: the copy of the test made here is the first half of a rotation, the loop keeps its own
    test at the top), the object's size. A foreach reads nothing before its loop: MoveNext is no test that can be
    copied, so the tree scene's walk still reads the node's X and Y on every turn.

- **The build, round 9: the node path, three direct steps with the general path's end state (2026-11-22, branch
  claude/tree-node-path, from main 4ac4519; the desk machine, shared; not merged when this was written).** Rounds 6
  to 8 measured the node of a tree built parent first as the largest part of a build and built none of it. Built
  here: not a new kind of ownership, but each of the three runtime calls a node makes written out for the shape it
  finds, refused before a write for any other. **A build is 0.24 ms shorter (14%), not at a leaf's cost: what is
  left of a node is below.**
  - **What a node does, call by call** (tools/tree-build.cs variant 0, `CS2WASM_DROP_STATS=1`, a build: 3,906
    nodes, 15,625 leaves; and a profile with names, `node --no-wasm-inlining --cpu-prof`, 600 builds, 2,634 ms, of
    which under Rt.ArenaLink 1,127 and under List.Capacity's setter 430).
    1. `_items = new T[4]` in List.Add, the list and the array plain objects, the node plain (3,125 a build: the
       nodes above leaves; the 781 above them have a list that joined from below already and take JoinOwned).
       Found: container count 1 (its node's slot), value count 1, the list's other slots empty, the array's
       elements null. Done by ArenaLinkCore: a record, two ArenaJoins with a note each for a visit, LinkMembers
       (count given back as for an object that joined, owner without a walk), ArenaLinkJoined's two visits by
       type, which find nothing. Changed: both members of a new group, the list its top with outside count 1,
       the array the list's.
    2. `arr[0] = leaf` (2,500 with a member for the node's parent, round 6's JoinFresh; 625 with a plain parent,
       the general way, which joins every plain ancestor in that one step). JoinFresh joins the leaf, its list,
       the node (nothing's) and makes the list the node's, then links the node's parent field by LinkJoinedSlot:
       ArenaLinkCore's questions, ArenaMerge (five members' record words, the record freed), LinkMembers (the
       field's count given back; not on the node's chain: a counted reference; the parent has no owner while it
       is being filled: Waiting, side words for the parent, a pair in LinkWaiting's list), then LinkWaiting's
       turn of the pair: the node, without an owner, takes the parent (TakeOwnership, Reevaluate of the
       parent's slots). So between its first child and its add each child-node owns its parent.
    3. `parentArr[k] = node` (3,905). Found: the node a member, count -3 (the store's alone), no owner, its
       parent field a counted reference to the member three owners above the array, which the node owns.
       LinkMembers: EarlyGiveBack (two walks up the array's owners: is the node above it, and which member is
       right below the node; that one has no up-reference below it and holds nothing: yes), a counted element,
       TakeOwnership (the same two walks; the parent stops being the node's, the array owns the node),
       Reevaluate (the parent field becomes an up-reference: OnChain, UpPath over three members, UpdateHold).
    By the profile's inclusive times, in its doubled milliseconds: ArenaLinkCore 218, LinkJoinedSlot 181,
    ArenaLinkJoined 220, TakeOwnership 208, LinkMembers from ArenaLink (the add) 207, LinkWaiting 97, ArenaMerge
    34: about 600 of the 2,076 under Build, 0.45 ms of a build's 1.65 on the desk.
  - **Done** (`CS2WASM_NO_NODE_PATH=1` at compile time for the general way; none of it with threads: JoinPair and
    AddNode ask ThreadMode, NodeUp is reached from JoinFresh, which does).
    `Rt.JoinPair` (call 1): asked before a write: the container a plain object, not pinned, its words clear, the
    slot one it has bits for, its other slots (by the type's word, Rt.ArenaSlots) empty or holding what a visit
    passes by (a pinned object, one of another group of classes); the value's slots likewise, or, for an array
    of one reference an element, every element null; nothing waiting in the lists of a link under way. Then the
    record and the two joins written as ArenaJoinOnly and LinkMembers leave them, no visit.
    `Rt.NodeUp` (call 2's link of the parent field): only when that slot is the node's one other slot that holds
    a member (LinkWaiting runs after all of them: with one, its turn is this pair's alone), the parent has more
    references from outside than the field's (the count given back is not its last: nothing to decide), no note
    of a failed check is alive, the node has no owner, neither group is busy or merged. Then ArenaMerge as
    ArenaLinkCore calls it, the count, the slot's bits, and for a parent without an owner TakeOwnership at once
    (what Waiting and LinkWaiting come to for one pair); for a parent with one (a new subtree's top under a
    living node), the node noted as the container waiting to own it and its hold, as LinkMembers has it. The
    one difference to the general path's words: a parent nothing owns gets no side words (the general way makes
    them for the waiting owner and clears that word in the same link; they hold nothing after).
    `Rt.AddNode` (call 3): the node's count -3, no owner, not held, owned, queued, dying or an array, the group
    alive by other members, no note of a failed check; one walk up the array's owners, eight at most, that
    either finds the node (then the member right below it must have no up-reference below it and hold nothing:
    EarlyGiveBack's and TakeOwnership's own test) or ends at a member without an owner (the node is not above
    the array: a new subtree's top stored into a living tree). Then the count given back, the element counted,
    the turn, the owner, and Reevaluate and the hold by the general functions.
    By the counts of a build: join-pair 3,125, node-up 2,500, node-add 3,905; link-core 3,125 to 0, joined-visit
    10,516 to 4,266, take-ownership 8,435 to 4,530, give-back-early 7,030 to 3,125; merges, joins, Reevaluates,
    hold changes and releases as before (the end state is the same).
  - **Numbers** (the desk, the switch off and on in turn three times; the ranges of the three).
    tools/tree-build.cs: variant 0 build 1.66 to 1.70 ms off, 1.40 to 1.48 on, drop 0.37 both; 1 (no parent)
    1.17 to 1.20 and 1.07 to 1.08; 2 (the parent after the add) 1.96 to 1.99 and 1.82 to 1.90; 3 (capacity
    first) 1.48 to 1.54 and 1.23 to 1.24; 4 (objects alone) 0.47 and 0.47 to 0.48; 5 (fan 4) 1.71 to 1.78 and
    1.48 to 1.53. tools/tree-frame.cs, an ordinary frame's medians: build 15.3 to 15.6 us off, 13.5 to 13.8 on;
    store 4.3 to 4.4 and 4.3; walk 30.6 to 31.1 and 30.8 to 31.2. tools/tree-store.cs: build 91.0 to 92.3 ns an
    entity off, 77.6 to 78.8 on; store 11.7 to 13.1 ns a member and 11.1 to 12.2.
    `node tools/frames.mjs --runs 3 --js-young 32` against NativeAOT-LLVM, off and on in turn three times (all
    five scenes the first time, the tree after; the desk was loaded by other sessions: NativeAOT-LLVM's own tree
    p99.9 was 2.98 to 3.24 ms in these six invocations, 2.38 to 2.47 on a quiet desk): tree median 0.049, 0.049,
    0.050 ms off and 0.049, 0.047, 0.048 on (NativeAOT-LLVM 0.029 to 0.032: 1.54x to 1.63x either way); p99.9
    2.912, 3.025, 3.090 off and 2.790, 2.650, 2.985 on (each below NativeAOT-LLVM's of its own invocation: 0.95x
    to 0.98x off, 0.86x to 0.95x on); worst 3.05 to 3.47 off and 2.91, 4.25, 4.47 on (NativeAOT-LLVM 3.55 to
    4.22: two of the three `on` invocations had one frame above NativeAOT-LLVM's worst, on a machine that was
    not quiet; not shown to be the step's or the machine's). The other scenes, one pair: churn median 0.080 and
    0.079, collections 0.007 both, strings 0.007 both, bigdrop 0.003 both, their p99.9 and worst within each
    other's spread. bench.mjs: 0.94x off and 0.93x on, one run each (its module 328.5 to 330.1 KB). Hello world (test 145) 27,642 bytes, as on main
    (the limit 28,383); a module with groups is 1.6 KB larger (tools/tree-build.cs 223,918 to 225,515 bytes).
  - **Not reached, and what is left of a node.** A node does not cost what a leaf costs. With the three steps a
    build is 1.40 to 1.48 ms against NativeAOT-LLVM's 0.26; the tree scene's median is where it was within the
    desk's spread (the ordinary frame's build is 1.8 us shorter of 49), its p99.9 0.1 to 0.3 ms lower in each
    pair on a loaded desk. The steps take the general path's questions and walks out; they leave what the
    general path's end state itself asks for, because that state was kept exactly: the group of its own for a
    list and its first array (a record made, merged into the tree's five members later, freed: 3,125 a build),
    the node made its parent's owner at its first child and the turn at the add (the parent's owner word
    written twice a child), Reevaluate and UpPath for the node's parent field at the add, the 625 first leaves
    under a plain parent with the whole chain of plain ancestors joined the general way, and each list's growth
    from four to eight. To take those out the intermediate states have to change (the list and its array not a
    group before the node joins; the node provisionally under its parent from its first child on, the direction
    of this round's brief called (a)): then a node dropped before its add, a build that throws, and a parent
    let go meanwhile reach other states than today's, each to be argued and tested. Not attempted in this
    round's time. No floor is claimed: round 7's estimate (25 to 30 ns an entity, 0.5 to 0.6 ms a build) stands
    as the estimate it was.
  - **Test 201** (nodes built parent first: the scene's order; a node dropped before the add, under a tree
    being built and under a living one; a node added to another parent than its field names; the parent set
    after the add; a list a local or a static holds; nodes with finalizers; a node moved and one in two lists;
    a build that throws half way; a parent only the node's field refers to; a node with two slots that hold
    members; a child that refers to the node above its parent, the node then taken out; new objects with
    content stored into plain ones; counted against .NET while the trees live and when they have gone). The
    same lines with the switch off, checked and as shipped. With a rule left out by hand
    (`CS2WASM_NODE_PATH_BREAK=n`): JoinPair's two questions (1: what the new object holds, 2: the container's
    other slots) each leave six objects at the end; AddNode's count (6) traps at the first replaced subtree.
    **Three rules are reached by the test and leave its lines as they are when left out** (3: NodeUp, the
    field's count the parent's last from outside; 4: NodeUp with a second slot to link; 5: NodeUp, the parent's
    owner not asked: the general path's later checks, the check at the drop among them, mend what they leave,
    more slowly), **and one is not reached** (7: AddNode's turn with an up-reference below the member the node
    owns). So the test proves three of seven rules, not each of them.
  - Checked. On the homelab at 1e71501 (the three steps; the commit after it, 38e971c, adds test 201 and takes one unused test of the break's number out, which a constant folds away): the suite 222/222 checked and 222/222 as shipped (326 s); the self-hosted compiler built with and without the switch (`tools/selfhost-switch.sh CS2WASM_NO_NODE_PATH=1`: 234 s each, 32,014,434 and 32,012,829 bytes; hello world in 9.2 to 9.5 s either way, peak 344 MB, the two the same module, and self = net byte for byte, 27,597 bytes); `node tools/selfdet.mjs --self <as-is> tests/Conformance/220_collections_grow_by_moving.cs`: self = net byte for byte (271,882 bytes, 18.4 s), and the same for test 193 (252,953 bytes, 18.9 s). On the desk: test 201 and tests 190 to 199 checked and as shipped; test 201 with the switch off and with each break (above). **Not finished when this was written: selfdet for test 088 and `tools/stress-trees.sh` (127, 139, 201, 192 to 199, 200 runs a mode) were started on the homelab and had not come back (their output: tests/out/np/hl-selfdet.txt and hl-stress.txt in the branch's worktree on the desk); the suite was not run again at 38e971c (test 201 passed on the desk both ways); frames.mjs on a quiet desk.** Threads: none of the three steps runs in a program with threads (test 197 and 139 take the general way).

- **The build, round 9, second part: the node path's rules proven, the list's own group measured as no cost, the
  plain parents built but left on a side branch, and the spikes settled (2026-11-22, branch claude/tree-node-path-2, from
  claude/tree-node-path 35ddb45; the desk, quieter, and the homelab; not merged when this was written).** The owner
  stopped the tree's speed work during this round: what is in the branch is the tests and one correction of
  NodeUp. Of the three further levers one was built and measured as no gain and taken out ((c), the list and its
  array without a group), one was built, measured at a few hundredths of a millisecond and left on a side branch
  because it was not checked on the homelab (the first leaves under a plain parent), and direction (a) was not
  built (why, below). **The build is 1.40 to 1.50 ms; a node does not cost what a leaf costs, and the
  target of 0.6 ms is not in reach of exact-state steps.**
  - **What the first part's queued checks said** (homelab, at 1e71501's code): `selfdet` for test 088 self = net
    byte for byte (1,613,357 bytes, 134.6 s); `tools/stress-trees.sh`, 200 loaded runs a mode: 127, 139, 192 to
    199 and 201 the same in every run of both modes (127 too: none differing this time).
  - **Test 201 now proves each rule** (`tools/node-path-breaks.sh`, new: the test compiled with each rule left out
    against the plain build; exit 1 when a break leaves the lines as they are). Rule 3 is gone, not proven:
    Rt.NodeUp takes a parent whose last count from outside is the field's and gives it back as LinkMembers does
    (EarlyGiveBack, or after the store), the parent then the node's at once, as the general way has it for a
    parent that does not wait. Breaks 1, 2 and 6 change the test's lines as before. Breaks 4, 5, 7 (and 8, of
    the side branch's step below) are about who owns a member or what a joined object still holds unlinked, and what they
    leave is found and freed later by the check at the drop (Rt.Dropped): the program prints the same, more
    slowly. They are therefore compared with that check off on both sides (`CS2WASM_NO_DROP_CHECK=1`): the node
    path and the general way still print the same there, and each of those breaks does not. New cases for
    them: a node built under a node that another node's list owns, that node's own parent field then set to
    its holder and the holder let go (break 5: the parent's owner not asked; break 4, with the new node
    referring to the parent by two slots at its first child, one cleared before); a child that comes to refer
    to the node above its top before the node is put into the top's list, the node then taken out of the list
    and read through the child (break 7: reached now, the add refused because an up-reference passes the
    member the node owns); a plain parent that holds a new node with content (break 8). A small tree of the
    same group is kept so that nothing goes because its whole group does.
  - **Built, not in the branch: `Rt.PlainParents`** (branch claude/tree-node-path-2-plain-parents, commit
    a3ce95f, `CS2WASM_NO_PLAIN_PARENTS=1`; lever 3's first half; on the desk tests 127, 139, 187, 19x, 20x and 201
    pass with it both ways and test 201 prints the same with it off; **not run on the homelab: no suite, no
    stress, no self-hosted compiler**). JoinFresh refused a node
    whose one other slot holds a plain object of its group: its own parent, not yet a member because no child
    of it has a child (every node's first child; 625 a build, and the chain of plain parents above: by the
    profile's inclusive times ArenaLinkJoined 240 and ArenaLinkCore 85 of the 2,353 doubled milliseconds under
    Build were these). Now, asked before a write, level by level up to a member or to none: the level's slots
    hold nothing to link, new objects of its own with nothing to link (its list), and at most one reference
    up, to a plain object that code still refers to (the next level, no level twice, eight at most) or to a
    member that code refers to (its group not busy or merged and the larger of the two, no note of a failed
    check alive). Written in the order of the general way's joins: each level a member with the counts it had
    less the field's, nothing's, referred to once from inside; its new objects its own; the next level joined
    as its slot comes; the member linked by NodeUp at its slot (the merge with the members the general way has
    by then); and each level its child's (TakeOwnership, from the bottom, as LinkWaiting's pairs come). Not
    made: the levels' side words. By the counts of a build: 597 of the 625 take it (the rest: the member's
    group the smaller one, at the top of a new tree), joined-visit 4,266 to 977.
  - **(c), the list and its first array without a group of their own: built, measured, taken out.** As built
    (not in the tree): `Rt.LeavePlain`, a new empty array stored into a plain object stays a plain counted
    reference (the state `new List<T>(4)` leaves; no cycle can close through an array that holds nothing and
    that one slot refers to; whatever stores into it or stores its container next joins it); `Rt.JoinFirst`,
    at `arr[0] = leaf` with the array plain: the array, the leaf, its list, the node and the node's list made
    members of the node's parent's group at once, in the general way's ring order from that state, no record,
    no merge, the parent's link by NodeUp. By the counts of a build: join-first 2,500, leave-plain 3,125, merges
    3,124 to 624. tools/tree-build.cs variant 0, the first part's build and this one in turn three times: build
    1.51, 1.44, 1.46 ms and 1.52, 1.49, 1.50; drop 0.39, 0.38, 0.37 and 0.40, 0.34, 0.35. **No gain: the record
    made, merged and freed for each node is not a cost worth a path** (about 2,500 times a few tens of
    nanoseconds, and from the lazier state the first leaves under a plain parent went the whole general way
    with two more visits each), and it would change what every program's plain list of such items is until
    its first growth (the list no member, the growth from four to eight the general copy and handover instead
    of ArenaGrowArray). Not kept, not even switched off.
  - **Direction (a), the node under its parent from its first child on: not built, and what stands against
    it.** Two forms were worked through.
    The small one: the parent of a node that gets its first child is not made the node's (no TakeOwnership),
    only noted as waited for (the word a parent that has an owner gets today); the add then finds the node
    not above the array and nothing to turn. The open state: the parent without an owner, referred to by the
    node's field as a counted reference, the node named in the parent's side word. It resolves at the add as
    today (the node is the array's, its field becomes an up-reference, the side word cleared: the same words
    after the add); a node dropped before the add or a build that throws releases the field as any counted
    reference; a node added elsewhere leaves the parent free for the list that takes it next (today it stays
    the node's). But it saves only the two writes of the parent's owner word and one Reevaluate of the
    parent, and pays side words for every parent (8 bytes, an allocation): no gain to expect, and round 1's
    reverted attempt (ownership decided at the last release from outside: test 127's async nodes and a
    pending task's waiter left, held by counted references no owner took) is the warning that members
    without an owner that are referred to by counted references are what the check at the drop cannot free.
    The large one, which would take the add's cost out (Reevaluate of the node, UpPath over the node, the
    array and the list, the holds: what a leaf's up-reference costs too): the node the property of its
    parent's array from its first child on, its field an up-reference then, the add only a counted element.
    The open state is a member owned by an array that does not hold it. That breaks what the growth and the
    handover rest on (ArenaGrowArray and ReplaceArray take an array's property to be exactly its linked
    elements and move the owner word of those: the provisional node would keep an array that is freed; the
    list has no array at all before its first Add), and what the destroy of a subtree rests on (members
    reached through their owners' slots). Each needs its own rule and its own tests; it is a change of the
    ownership model, not a path, and was not started in the time of this round.
    By the variants the prize is bounded: with every node at a leaf's cost a build would be about 0.15 ms
    shorter. Where a build's 1.42 ms is, by tools/tree-build.cs's variants: the objects alone 0.47 (variant
    4), the parent fields 0.35 (variant 0 less 1: 18 ns for each of the 19,530 up-references, a leaf's too:
    three members' u and their holds), the lists' growth 0.1 to 0.2 (variant 0 less 3), the joins and links
    the rest. **So the leaf's own cost, not the node's, is what keeps the build at five times
    NativeAOT-LLVM's 0.26.**
  - **Not attempted:** the lists' growth (ArenaGrowArray: its tail already frees the old array without the
    general destroy; what is left is the allocation, the move and the checks).
  - **Numbers** (the desk; `CS2WASM_NO_NODE_PATH=1` off and on in turn three times; **measured on the side
    branch, PlainParents in**; the branch itself, without it, is the first part's code but for NodeUp's last
    count: tools/tree-build.cs variant 0 1.70 to 1.71 ms off and 1.46 to 1.49 on, tools/tree-frame.cs's build
    15.6 to 15.8 us and 13.8 to 14.2, and frames.mjs as in the next item). tools/tree-build.cs: variant 0 build 1.67 to 1.72 ms off, 1.40 to 1.50 on; 1: 1.15 to 1.20 and
    1.04 to 1.11; 2: 1.88 to 2.14 and 1.80 to 1.92; 3: 1.43 to 1.53 and 1.27 to 1.39; 4: 0.45 to 0.48 both; 5:
    1.65 to 1.75 and 1.35 to 1.45. PlainParents alone, before and after in turn three times: 1.45, 1.58, 1.47
    and 1.42, 1.50, 1.44 (0.03 to 0.08 ms; within the desk's spread in the table above). tools/tree-frame.cs:
    build 15.0 to 15.8 us off, 13.1 to 13.5 on; tools/tree-store.cs: build 92.3 to 96.2 ns an entity off, 73.7
    to 79.5 on, the store 13.4 ns a member and 11.1 to 12.0. `node tools/frames.mjs --runs 3 --js-young
    32` against NativeAOT-LLVM, the quiet desk, three pairs: tree median 0.050, 0.050, 0.049 ms off and 0.047,
    0.047, 0.048 on (NativeAOT-LLVM 0.030: 1.63x to 1.67x and 1.57x to 1.60x); p99.9 3.039, 3.047, 3.118 off
    and 2.618, 2.641, 2.716 on (NativeAOT-LLVM 2.579 to 2.635: 1.16x to 1.19x off, 1.01x to 1.05x on: **0.03 to
    0.14 ms above it still**); worst 3.430, 3.294, 3.686 off and 2.752, 2.694, 2.857 on (NativeAOT-LLVM 3.319
    to 3.378: 0.80x to 0.85x on). All five scenes once with the steps: churn 0.079 (0.050), collections 0.007
    (0.006), strings 0.007 (0.008), bigdrop 0.003 (0.003), tree 0.047 (0.030) with p99.9 2.841 (2.660).
    bench.mjs 0.94x off and 0.93x on, one run each (its module 329.2 and 331.7 KB). Hello world (test 145)
    27,642 bytes (the limit 28,383); a module with groups is 2.5 KB larger with the steps (tools/tree-build.cs 224,628 bytes off, 227,111 on).
  - **The spikes: the machine.** The first part saw two of three `on` invocations of frames.mjs with a worst
    frame above NativeAOT-LLVM's on a loaded desk. On the homelab, this side alone (`node tools/frames.mjs
    --no-aot --runs 3 --js-young 32 tree`, off and on in turn four times, the first part's code): median 0.059,
    0.058, 0.058, 0.058 ms off and 0.056, 0.057, 0.056, 0.057 on; p99.9 3.072, 3.067, 3.052, 3.072 off and
    2.803, 2.802, 2.805, 2.834 on; worst 3.141, 3.120, 3.107, 3.127 off and 2.885, 2.930, 2.848, 2.882 on:
    **the worst frame is 0.2 to 0.3 ms lower with the step in every pair, the rebuild frame 0.25 ms shorter
    (8%), the median 3%.** On the quieter desk against NativeAOT-LLVM, three pairs (the first part's code):
    tree median 0.048, 0.050, 0.049 ms off and 0.048, 0.048, 0.045 on (NativeAOT-LLVM 0.029 to 0.030: 1.62x to
    1.65x off, 1.53x to 1.61x on); p99.9 3.001, 3.090, 2.917 off and 2.895, 2.712, 2.694 on (NativeAOT-LLVM
    2.474 to 2.610: 1.14x to 1.21x off, 1.05x to 1.12x on); worst 3.277, 3.288, 3.290 off and 3.218, 2.920,
    3.024 on (NativeAOT-LLVM 3.174 to 3.357: 0.98x to 1.03x off, 0.88x to 0.97x on). **p99.9 is still above
    NativeAOT-LLVM's in every invocation (frames.mjs flags it), the median 1.5x to 1.6x.**
  - Checked, on the homelab at 88f2a6e (the branch's code: the first part, NodeUp's last count, test 201's new cases, the script): the suite 223/223 checked and 223/223 as shipped (435 s); the self-hosted compiler built with and without the node path (`tools/selfhost-switch.sh CS2WASM_NO_NODE_PATH=1`: 32,014,763 and 32,013,104 bytes; hello world in 14.1 to 15.4 s either way on a loaded machine, peak 344 MB, the two the same module, self = net byte for byte, 27,599 bytes); `selfdet` with that compiler for tests 220 (271,925 bytes), 193 (252,996) and 088 (1,613,340): self = net byte for byte each; `tools/stress-trees.sh`, 200 loaded runs a mode: 139, 192 to 199 and 201 the same in every run of both modes; **127 differed in 4 of 200 as shipped and 5 of 200 checked (`async 5` for 0: the kind and the rate earlier rounds give for main, 1 to 4 in 200; it starts threads and takes none of these steps; in the first part's run at 1e71501 it differed in none; not followed here)**. On the desk: `tools/node-path-breaks.sh` exits 0 (on and off the same with and without the check at the drop; each of the six breaks changes the lines). Not checked: the side branch on the homelab; the suite with `CS2WASM_NO_NODE_PATH=1` for every program (the switch is checked by test 201, the two trees' tools and the self-hosted compiler); frames.mjs on the homelab at this commit (the four pairs above are the first part's code).

- **The build, round 10: where a frame of the tree scene is, the walk's `foreach`, the unlink that is not called,
  and a leaf's list left a plain object (2026-11-22 to 23, branch claude/tree-3, from claude/round-9 d430594; the
  desk, shared and loaded most of the night, and the homelab; not merged when this was written).** The brief was
  the build of the tree, part by part (the objects alone, the up-reference, the list's growth, the destroy at the
  store). Measured first, by phase and by step, and the two largest things found were none of them: the walk's
  `foreach`, and a call after every store that did nothing. **The tree's median frame is 0.038 to 0.043 ms where
  it was 0.045 to 0.052 (1.27x to 1.43x NativeAOT-LLVM's, from 1.52x to 1.68x in the same pairs); the rebuild
  frame, the scene's p99.9, was 0.76x to 0.85x NativeAOT-LLVM's in five pairs where it was 0.95x to 1.14x, and
  on a desk under heavy load later the two sides could not be told apart at all. The median is not reached: the
  build is not at NativeAOT's cost by any step that keeps the model.**
  - **An ordinary frame by phase** (tools/tree-frame.cs, the desk, quiet: the new subtree's build 12.2 to 12.5 us,
    its store over the old one 3.9, the move 0.6, the walk 27.6 to 28.8; NativeAOT-LLVM's, round 5: 2.3, 0.1,
    29.3). **The walk is three fifths of the frame**, and it was level with NativeAOT-LLVM's only because both
    paid for `foreach` over a List<T>. The build and the store are 16 us against 2.4: to bring the frame to
    NativeAOT-LLVM's 30 us with the walk as it was they would have to be 2 us, less than NativeAOT-LLVM's own.
    (`walk-indexed`, 16.4 to 17.3 us in the same tool, is not what the `foreach` walk could be: it runs second,
    over memory the first walk left in the cache. With the lowering below the two loops are the same
    instructions but for one compare, and the first walk is still 24 to 26 us.)
  - **A node by its steps** (tools/tree-node.cs, new: N nodes with K leaves each made under a living node in the
    scene's order; the desk, quiet): a leaf 34 ns (two allocations with their constructors about 12, the store
    that joins it the rest); a node with one leaf 150; each further leaf 45; the fifth, with the list's growth
    from four to eight, 108 (the growth 63: with the capacity set first a node of five is 329 for 392). So of a
    scene node with five leaves, 392 ns: the leaves' joins 100 to 150, six allocations 70, the growth 63, and
    what a node costs beyond a leaf (the list's first array as a group of its own, the merge, the parent's
    ownership turned at the add, two Reevaluates) about 110. NativeAOT-LLVM: 13 ns an entity, 78 for the six.
    A profile with the engine's inlining on (`node --cpu-prof`, 600 builds and drops, names; shares of the 2.5
    ms a build and its drop took there): Rt.JoinFresh 16.6%, the program's Build 12.5, ArenaFree 7.8, ArenaLink
    7.4, AllocObjectArena 6.8, ArenaVisit 6.4, ArenaFreeList 5.4, ArenaGrowArray 4.4, the clears 5.3,
    List.AddWithResize 3.5, rt.release 3.4, and thirty more below 2. JoinFresh's own code was read as the
    module has it (1,827 bytes, the type tables read in line, no call it does not need): nothing in it is
    waste by its writing. **Tools/tree-build.cs's variant 4 (0.45 to 0.47 ms, "the objects alone") is a build
    and the free of every object, as round 6 said: the allocations of a build are 0.19 ms, below
    NativeAOT-LLVM's whole build of 0.26, and the brief's first item is not a cost above NativeAOT's.**
  - **Done, 1: `foreach` over a List<T>** (FunctionCompiler; `CS2WASM_NO_LIST_TEST_FIRST=1`,
    `CS2WASM_NO_LIST_THROW_PATH=1`). The lowering asked the version, then the size, at every turn, by loads
    without a field's tag, and kept the enumerator for the changed list's throw as a temporary of the
    statement. So every function with such a loop took a frame off the stack (rt.frame), cleared it, and had a
    handler around its whole body for a path that runs when the program has a bug; and Wasm/FieldLoads.cs,
    which copies a loop's test before it only when the loop starts with it, read nothing before the loop
    ("a foreach reads nothing before its loop", the field loads' own item). Now: (1) the size's test first,
    the version asked after it and once more where the list ends (not when no turn was taken: nothing ran
    since it was read; with threads asked all the same). The two tests read two fields and write nothing, so
    their order cannot be told; a changed list throws whatever its size says, as the enumerator's MoveNext
    does. (2) The loads of `_version`, `_size`, `_items` and the array's length carry the tags a load by name
    carries: a loop that stores none of them, by its own code or by what it calls, reads them once, and the
    body's own fields with them (the node's X and Y in the walk). (3) The path of a changed list ends in
    MoveNext's throw (MoveNextThrows reads that from List's source): its enumerator is made there, below the
    stack's pointer (asked for room first: a trap otherwise, as a frame's), refers to the list without a
    count and is never released; the catch that takes the exception puts the pointer back
    (RuntimeGen.RestoreStackAtCatches). The walk's function has no frame and no handler: 356 bytes to 351, its
    loop the indexed loop's.
    Test 203 (a list changed by the body, by a call, through a static that refers to it, through another
    instantiation, in the last round by a removal and by an addition, before a `break` and a `return`; the
    capacity alone; nested loops, the inner's throw caught inside; a change far below a recursive walk, seen
    when the walk is back; the node's own fields changed by the body and by a call; the exception's frames;
    a function that catches its own loop's throw and goes on with structs on its stack; 3,000 throws one after
    the other) and `tools/list-foreach-breaks.sh`: the same lines with each switch off, with the whole
    lowering off and with the field loads off; other lines with `CS2WASM_LIST_FOREACH_BREAK=1` (the version
    not asked at the list's end), `=2` (the enumerator handed to MoveNext with the list's present version)
    and with the field loads' rules for calls and for the loop's own stores left out
    (`CS2WASM_FIELD_HOIST_WITHOUT=calls`, `=loop`). Not proven by a break: that the stack's pointer comes back
    after the throw. As shipped 3,000,000 such throws in a loop ran to their end in the stack's megabyte (16
    bytes each would be 48 MB; a program outside the suite, within three minutes: a throw is some tens of
    microseconds); the test has 3,000, because a checked build runs out of memory at 200,000 caught
    exceptions of any kind (below, seen on the way).
  - **Done, 2: a member's plain part** (`Rt.PartHolder`, `CS2WASM_NO_PLAIN_PARTS=1`; rounds 4 and 5 worked this
    out as "a leaf's list left a plain object" and did not build it). JoinFresh joined a leaf's empty list with
    the leaf: a second member for each of the 15,625 leaves of a build, with its place on the ring, destroyed
    as a member when the leaf goes. A new object's own new object that only its slot refers to and that holds
    nothing to link is now left a plain object: the member holds it by a plain counted reference (the slot's
    bits 0, as a reference to a pinned object), it is released at the holder's clear, and it keeps the holder
    and the slot in the two words a member has its ring in (free while it is plain, written by every join).
    What that gives up is "a member refers to no plain object of its group of classes", which is what kept a
    cycle from closing unseen. It holds again by one rule: **wherever a plain object that code still refers to
    becomes a member, its note is asked and its holder's slot is linked first, as the store it stands for**
    (ArenaLinkCore, for the container and for the value; JoinPair's container, JoinFresh's node and
    PlainParents' levels refuse such an object and so come to ArenaLinkCore). The note can be stale (the slot
    stored over, the holder gone or plain again): it is checked by what is there (the slot's address is a slot
    of a living object of a group, by the allocator and the type: SlotOwner; that object the one named, a
    member; the slot one it has bits for, the bits 0, the slot holding the part), and a member that passes
    does hold the part unlinked, whatever wrote the note. A part only its holder refers to joins as the
    holder's (JoinOwned), any other the general way. Not left a part: an object of a class with a finalizer
    (it would run at the holder's clear, not with the group's), and one a weak handle refers to
    (ObjectMarks' bit); a part that gets a weak handle later becomes a member there (ObjectMarks.SetAt,
    rt.part_weak, Rt.PartMarked: the store of the part into its holder's slot once more, the general way),
    because the weak references to the members of a group that goes are cleared before its finalizers run
    (test 245) and a part is released after them. Not with threads (JoinFresh is not taken there).
    By the counts of a build (tools/tree-build.cs, `CS2WASM_DROP_STATS=1`): joins 46,874 to 31,301, parts left
    15,573, none linked later (the scene never puts a child into a leaf).
    Test 202 (a leaf that gets children later and is then cut; a leaf's list a second node holds, filled
    through either, the leaf cut, the loan let go; a part of another class with references back to its holder
    and above it; a list taken out of its leaf, with the slot null, with another list, with the same list
    again, filled through a local; a leaf that left its group; weak handles made before and after the leaf
    joined, read by the finalizers of the group they die with; parts of a class with a finalizer; lists an
    array holds; 300 frames of the scene's kind with leaves grown and lists lent; counted against .NET while
    the structures live and when they have gone) and `tools/plain-parts-breaks.sh`: the same lines with the
    parts off, with and without the check at the drop; other lines with each of
    `CS2WASM_PLAIN_PARTS_BREAK=1` (a part that joins as a container is not linked to its holder), `2` (as a
    value), `3` (JoinPair takes a part), `5` (the note is taken without asking that the slot still holds the
    part), `7` (a part with a weak handle stays one). **Not proven by a break: 4** (JoinFresh's node or a plain
    parent is a part: such an object would have to hold a member in a slot, and a store that puts one there
    links the part first by rules 1 and 3; the test is kept as a second line) **and 6** (a part of a class
    with a finalizer: the test has such parts and its lines are the same; what could differ is the order of
    two finalizers of one group, which .NET does not fix).
  - **Done, 3: `Rt.PlainParents` taken in from the side branch** (claude/tree-node-path-2-plain-parents a3ce95f,
    round 9's, `CS2WASM_NO_PLAIN_PARENTS=1`, test 201's case and break 8, tools/node-path-breaks.sh): it had not
    been run on the homelab then, and is now, with the rest. With the parts: a plain parent that is a member's
    part with a living holder is refused, as JoinFresh's node is.
  - **Done, 4: the unlink that is not called** (Compiler/Arenas.cs, `CS2WASM_NO_UNLINK_SKIP=1`). After every store
    into a slot a cycle can close through, the compiler called Rt.ArenaUnlink with the slot's old value. For an
    old value that is null (a list's Add, a field set once: 19,530 of the 23,436 stores of a build) that call
    does one thing: it gives back the counts the store's link kept until the slot was written (Rt.AfterStore,
    when `t_backN` is not zero; none in a build). It was two calls deep and read the thread's statics twice to
    find that out. The two questions are asked at the store now (the old value, and `t_backN` at its place
    among the thread's statics), and the call is made only for a yes; what ArenaLink noted of the slot's bits
    stays noted, and the next unlink that reads the note follows a link that set it. Not in a program with
    threads (the unlink ends the store's locked region there). **The largest step of the round for the
    build: 0.14 to 0.17 ms of 1.75 on the homelab, 0.12 of 1.40 on the desk**, three times what the profile
    gave the two functions (3.2%): a call that does nothing is not free in its caller either.
    Test 204 (a member another member owns, stored by a temporary into an empty field, an empty element, a
    new list; over a value; into a plain and a new object; groups that only such a store's value holds;
    a thousand such statements in a row; counted against .NET after each) and `tools/unlink-skip-breaks.sh`:
    the same lines with the skip off; with `CS2WASM_UNLINK_SKIP_BREAK=1` (the kept counts not asked) groups
    stay alive until the next store that links something.
  - **Done, 5: Rt.Reevaluate by the member's slot bits again** (`CS2WASM_NO_SLOT_WALK=1` for the visit by type;
    it was `CS2WASM_SLOT_WALK=1` to turn it on). Round 1 wrote it, round 2 switched it off: a long frame of the
    strings scene was laid to it (the host's own collection, found later in round 2) and it gained nothing in
    the tree then. Found again here from the profile: Rt.ArenaVisit was 6% of a build and its drop, called
    twice through the type's dispatch for each of the 7,866 Reevaluates of a build (a node's add, and the
    parent a node takes at its first child). `tools/slot-walk-breaks.sh` (test 201: off the same lines;
    `CS2WASM_SLOT_WALK_BREAK=1`, the walk without the member's first counted slot, other lines).
  - **Numbers.** `node tools/frames.mjs --runs 3 --js-young 32` against NativeAOT-LLVM on the desk (loaded by
    other sessions; the six switches of this round off and on in turn, five pairs, the first with all five
    scenes). **Tree, median: 0.052, 0.051, 0.047, 0.045, 0.047 ms off and 0.040, 0.043, 0.038, 0.039, 0.042 on,
    against NativeAOT-LLVM's 0.029 to 0.033: 1.52x to 1.68x off, 1.27x to 1.43x on. p99.9 (the rebuild frame):
    2.919, 3.263, 2.522, 2.263, 2.922 off and 2.113, 2.416, 2.000, 2.111, 2.458 on, against NativeAOT-LLVM's
    2.394 to 3.239 in the same ten invocations: 0.95x to 1.14x off, 0.76x to 0.85x on, below it in every
    pair.** Worst frame: 5.815, 5.360, 3.040, 2.353, 4.559 off and 3.340, 2.834, 2.292, 2.405, 3.082 on, against
    3.114 to 4.917: 0.68x to 0.78x in four invocations, and 3.340 against 3.328 in the fifth (frames.mjs flags
    it; a desk on which the same invocation's `off` side had 5.8 ms). p99 0.060 to 0.093 on (0.067 to 0.198
    off; NativeAOT-LLVM 0.047 to 0.074). The mean frame of the tree 1.03x to 1.12x NativeAOT-LLVM's (1.26x to
    1.47x off). The other scenes, one pair: churn 0.070 ms both (NativeAOT-LLVM 0.052: 1.35x), collections
    0.006 both, strings 0.007 both, bigdrop 0.003 both, their p99.9 and worst within each other's spread; the
    mean frame's geometric mean over the five 0.74x off and 0.70x on, the medians' 1.14x and 1.07x.
    An earlier set with the `foreach` and the parts alone (1d4abf2, four pairs): tree median 0.045 to 0.048 off
    and 0.043 on in every one (NativeAOT-LLVM 0.029 to 0.031, 0.034 once); p99.9 not told apart there.
    **The same on a desk under heavy load half an hour later** (cd93b54, fourteen pairs two minutes apart;
    NativeAOT-LLVM's own median 0.030 to 0.066 ms and its p99.9 2.6 to 8.4 in them, where a quiet desk gives
    0.029 and 2.4): median 1.28x to 1.46x on in the ten pairs where NativeAOT-LLVM's own was below 0.047 (1.54x
    to 1.94x off); **p99.9 0.71x to 1.50x on (below NativeAOT-LLVM's in six pairs, 0.99x to 1.00x in three, above
    in five) and 0.92x to 1.79x off (below in two): under that load the rebuild frame is not shown to be below
    NativeAOT-LLVM's in every run, only lower than without the steps in eleven pairs of fourteen.** A quiet desk
    was not had again before this was written.
    On the homelab, this side alone (`node tools/frames-ab.mjs --rounds 5 --js-young 32`, at 1c47445, the
    `foreach` alone): tree median 0.0541 ms on and 0.0565 off (each round's: 0.0541, 0.0571, 0.0543, 0.0551,
    0.0543 and 0.0595, 0.0610, 0.0565, 0.0594, 0.0606), p99 0.074 and 0.080, worst 2.86 and 2.97; churn 0.0891
    and 0.0888, collections 0.0065 both. **Not measured: the scene's frames by step on the homelab** (the job
    waited forty minutes in its queue behind eight others and was given up for the checks below).
    By step, `tools/tree-build.cs` variant 0 on the homelab (`tools/tree-ab.sh`, new; each switch against the
    build with all steps, six pairs; that build is 1.57 to 1.62 ms there, its drop 0.39 to 0.43): the unlink
    not called 0.14 to 0.17 ms of the build (1.73 to 1.79 without it); the plain parts 0.05 to 0.08 of the
    build (1.61 to 1.67) and 0.07 of the drop (0.47 to 0.49); the plain parents 0.05 to 0.12 (1.61 to 1.73);
    the slot walk 0.03 (1.57 to 1.62 by the visit, 1.54 to 1.59 by the walk, the lower in each of six pairs).
    On the desk, the six off and on in turn three times: variant 0 build 1.52, 1.54, 1.63 ms off and 1.29,
    1.30, 1.31 on (it was 1.40 to 1.48 at the start of the round), drop 0.37 to 0.39 and 0.32 to 0.37; variant
    3 (capacity first) 1.22 to 1.37 and 1.14 to 1.16; variant 5 (fan 4) 1.56 to 1.62 and 1.26 to 1.30; variant
    1 (no parent field) 1.05 to 1.06 and 0.99 to 1.10; variant 2 (the parent after the add) 1.79 to 1.88 and
    1.72 to 1.82 (it takes the general way, which only the unlink's step touches); variant 4 0.45 to 0.49 both.
    `tools/tree-store.cs`: a frame's build 12.5 to 12.8 us off and 11.1 to 11.3 on (80 to 82 ns an entity,
    then 71 to 72), its store 4.0 to 5.2 and 3.6 to 3.8. `tools/tree-frame.cs`, before the desk was loaded:
    the `foreach` alone took the walk from 30.2 to 32.9 us to 27.3 to 28.6 in each of four pairs (the desk
    was 10% slower than in the quiet numbers above), the build and the store as they were.
    By the counts of a build (`CS2WASM_DROP_STATS=1`, 19,531 entities): joins 46,874 to 31,301; visits of a
    joined object 4,266 to 977 (the 781 first arrays of the nodes above the leaves' parents, and 196 more);
    15,573 parts left, none linked later; 597 of 625 first leaves by the plain parents; unlinks called 23,436
    to 3,906. The drop of a group (27,395 members where it had 42,968): the walk with the clears 0.26 to 0.28
    ms where it was 0.29 to 0.30 (the parts are released there, one by one, through the generic release and
    the dispatch on the type: rt.destroy_now is 4.7% of a build and its drop), the frees 0.065 where they
    were 0.12.
    `node tools/bench.mjs` on the desk, off and on in turn twice: 0.91x and 0.90x off, 0.92x and 0.89x on
    (this side's own times within 0.2 ms of each other but `tree`, 7.4 to 7.6 ms both); its module 336.5 KB
    off and 338.6 on. Hello world (test 145) 27,784 bytes, as at the branch's start (the limit 28,383).
  - **Not reached: the median.** With all of it the tree's median frame is 1.27x to 1.43x NativeAOT-LLVM's, and
    no step in sight ends that inside the model. A frame is now, by phase (the desk, from the tools above):
    the walk 24 to 26 us (NativeAOT-LLVM's 29.3: this side's is the faster one now), the build 11.1 to 11.3,
    the store 3.6 to 3.8, the move 0.6: 40. NativeAOT-LLVM's 30 are its walk and 2.4 for the build and the
    store. To be level, the build and the store together would have to be 5 us, a third of what they are:
    29 ns an entity for both where the build alone is 71. What an entity's 71 ns are, from tools/tree-node.cs
    and the profile: two allocations with their constructors 12 (the engine does not put AllocObjectArena in
    line: 6.4% of a build and its drop); a leaf's join with its up-reference about 15 (Rt.JoinFresh, the
    largest function still, 19%); and for each node with children its list's first array as a group that is
    merged a step later, the parent's ownership turned at the add, two Reevaluates, and the list's growth
    from four to eight: about 170 ns a node, 30 an entity. Round 9's sentence stands: these are the general
    path's own states, written out; to have less, the states must change (round 7's exclusive part, which
    the brief of this round says not to build; a node under its parent from its first child on, round 9's
    direction (a), which needs an owner that does not hold what it owns). **The brief's items 2 to 4 as
    such were measured and not changed:** the up-reference is about 9 ns of a leaf's join (round 6's figure;
    three members' u and four looks at a hold, each a few loads: nothing to take out but one look, 1 to 2
    ns); the list's growth is 63 ns a node (an allocation, the move in one step, the old array's clear and
    free by the dispatch on its type, the setter's count given back: the clear and the dispatches could go,
    about 10 ns); the destroy at the store is 10 to 11 ns a member with the parts (it was 12).
    Not built, each worth a per cent or two of a frame by these measurements: the old array of a growth
    freed without its clear and the dispatches; AllocObjectArena's first case written at the allocation; a
    part released by its class's own destruction where the field's class has no subclass in the program
    (today only a sealed class is released so: Driver's ExactTypeHint); the first look at a hold left out
    for an object that has just joined.
  - **Seen on the way, not followed.** (1) A checked build runs out of memory in a loop of 200,000 caught
    exceptions of any kind (`for (..) try { throw new InvalidOperationException("x"); } catch
    (InvalidOperationException) { }`, nothing else in the program: `Insufficient memory`; with a changed
    list's `foreach` the same, with this round's lowering and with the old one): something a checked build
    keeps for each exception. As shipped the loops run, some tens of microseconds a throw (3,000,000 plain
    ones had not finished after 100 seconds; 3,000,000 of a changed list's finished within three minutes).
    Test 203 has 3,000 throws for that reason. (2) Test 236
    (`236_queue_grows_by_moving`) printed `live 0` where .NET printed `live 191` once on the loaded desk
    (the lines `after grow` and `after held`), and passed when run again and in both homelab suites.
    (3) tools/tree-node.cs's first figure (`k=0`, a lone leaf) is 35 to 38 ns in some runs and 48 to 53 in
    others, whatever the switches: the engine's tiers during the first measurement, not a step's.
    (4) `walk-indexed` of tools/tree-frame.cs runs after the walk, over a warm cache: it is not what the
    walk could be (the first item).
  - Checked. The code is cd93b54 (the commits after it are tools and these documents). On the homelab: the suite
    at 1d4abf2 (the `foreach` and the parts) 238 passed, 0 failed and the 2 known gaps, checked and as shipped
    (499 s); at 5fb8575 (all five steps; cd93b54 differs from it by the three instructions that clear the
    unlink's note) 239 passed, 0 failed, 2 known gaps, both modes (493 s). The self-hosted compiler at e92a608
    (the `foreach`, the parts, the plain parents; before the unlink's step and the slot walk),
    `tools/selfhost-switch.sh` with those four switches: built in 262 s and 244 s, 32,933,542 and 32,934,950
    bytes; hello world in 8.27 to 8.55 s as it is and 8.21 to 8.37 s switched, peak memory 272.0 MB both; the
    two wrote the same module, and `tools/selfdet.mjs` for test 145: self = net byte for byte (27,731 bytes,
    8.1 s). The per-switch builds of tools/tree-build.cs above, at 860ce82. On the desk, at cd93b54: the five
    scripts of breaks (`tools/list-foreach-breaks.sh`, `plain-parts-breaks.sh`, `node-path-breaks.sh`,
    `unlink-skip-breaks.sh`, `slot-walk-breaks.sh`) exit 0; tests 190 to 199, 200 to 207, 120 to 129 and 130 to
    139 pass checked and as shipped; tests 202, 203 and 204 as shipped 60 runs each, six at once, one output.
    **Queued on the homelab when this was written, at 25b5f6b (cd93b54's code), not yet run:**
    `env SET=202_,203_,204_,145_ bash tools/overnight-check.sh` with the six switches: the suite in both modes
    again, the self-hosted compiler with and without the six switches and `tools/selfdet.mjs` for tests 220, 193,
    088, 202, 203, 204 and 145, and `tools/stress-trees.sh` (127, 139, 192 to 199, 201 to 204; 200 loaded runs a
    mode). Its output: tests/out/overnight/ in the homelab's worktree of this branch (C:/Users/Levi/hl/claude_tree-3)
    and the session's log. **So for cd93b54 itself: the suite, the self-host check with the unlink's step and the
    slot walk in it, selfdet beyond test 145, and every stress run are not done yet; the next entry of
    docs/FINISH.md's log says how they came out.** Threads: none of the steps runs in a program with threads but
    the `foreach`'s size-first loop and its throw path (the tags are not given there; the version is asked at
    the list's end whatever the turns); tests 139, 143, 197 and 234 are in the suites above.
    A job of this session is left in the homelab's queue by mistake: process 30140 there (`heavy.sh node
    tools/frames-ab.mjs ..`, the scene by step), whose session was closed; it will take a slot for some four
    minutes when its turn comes and its output goes nowhere.
  - **Checked on the merge with main (37c6b15: main eba0f0d into the branch; the one conflict, docs/FINISH.md,
    two appended entries, both kept; Runtime.cs merged by itself).** The queued `tools/overnight-check.sh` (with
    the six switches) ran there: the homelab's worktree was moved to the merge and built before the job's turn
    came, so what it checked is the merge. **Suite 241 passed, 0 failed, 2 known gaps, checked and as shipped
    (595 s).** The self-hosted compiler built as it is (286 s, 33,203,338 bytes) and with the six switches off
    (262 s, 32,944,300 bytes); **hello world by it in 8.59, 8.82 and 8.95 s** (switched 8.56 to 9.40), peak
    272.0 MB both, the two modules the same (27,722 bytes); `tools/selfdet.mjs`: **self-1 = net, byte for byte on
    145 (27,734 bytes), 220, 193, 088 (1,616,223 bytes, 82 s), 202, 203 and 204.** `tools/stress-trees.sh`, 200
    loaded runs a mode: 139, 192 to 199, 201, 202 and 203 the same output in every run of both modes; **127
    differed in 3 of 200 as shipped and 4 of 200 checked, each time `async 5` / `pending 1`**, the open gap of
    docs/FINISH.md's table (item 2: about one run in a hundred on main, 22 of 2,400 and 3 to 5 of 400 in earlier
    rounds); not run against main the same hour, so whether this branch's 7 of 400 is main's rate is not shown
    here. Test 204 was not in the script's set (it takes 192 to 199 and 201 to 203); its own 200-run stress was
    queued after. On the desk at the merge: the five scripts of breaks exit 0; tests 12x, 13x, 19x, 20x and 21x
    pass checked and as shipped; hello world (145) passes its size limit as shipped.
  - **Frames on the merge, main against the branch** (main eba0f0d and 37c6b15, `node tools/frames.mjs --runs 3
    --js-young 32`, all five scenes, main and branch in turn, nine pairs in three sets of three; the desk loaded
    in the first set: NativeAOT-LLVM's own tree median 0.031 to 0.037 there, 0.028 to 0.029 in the second, 0.028
    to 0.036 in the third). **Tree, median: main 1.49x to 1.65x NativeAOT-LLVM's (0.044 to 0.061 ms), the branch
    1.30x to 1.50x (0.038 to 0.049); in the six pairs of the second and third sets main 1.49x to 1.60x and the
    branch 1.30x to 1.37x, lower in every pair.** p99.9 (the rebuild frame): main 0.81x to 1.40x (0.81x to 0.91x
    in those six), the branch 0.70x to 0.92x (0.80x to 0.84x in those six): below NativeAOT-LLVM's in every pair
    on the branch, above it in two of nine on main. Worst frame: main 0.67x to 1.32x, the branch 0.65x to 1.27x;
    on the branch above NativeAOT-LLVM's in four of nine pairs, the three of the loaded set and the first of the
    third (1.05x to 1.27x: frames.mjs flags them), at or below 0.81x in the five others; main above in three.
    p99 in those six: the branch 1.23x to 1.50x, main 1.35x to 2.30x. The tree's mean frame 1.08x to 1.12x on
    the branch and 1.23x to 1.31x on main in those six (1.14x to 1.41x and 1.28x to 1.63x in the loaded set).
    The other scenes, the same on both within the desk's spread (second set: churn median 1.33x to 1.34x,
    collections 1.02x, strings 0.88x to 0.89x, bigdrop 0.90x to 0.94x; their p99.9 and worst 0.10x to 0.37x on
    both); the mean frame's geometric mean over the five 0.69x to 0.74x on the branch, 0.70x to 0.86x on main
    (0.70x to 0.71x and 0.73x in the second set). `node tools/bench.mjs`, two pairs: geometric mean 0.93x and 0.91x on main, 0.92x and
    0.92x on the branch (`tree` 7.4 to 7.5 ms both); the bench module 335.5 KB on main and 339.0 on the branch.
    **Not reached, as the round says: the median (1.30x to 1.37x on a quiet desk), and the worst frame is not
    below NativeAOT-LLVM's in every run on a loaded desk.** No further step of the "not built" list was taken:
    each is a per cent or two of a frame by the measurements above (the old array of a growth freed without its
    clear and dispatches about 10 ns a node, 8 us of a 1.3 ms build), below what the desk can show, and each
    would have needed a second full check on a homelab whose queue held nine to thirteen jobs this night.
  - **Test 127 against main the same hour** (main 4cf95fe on the homelab, `env TESTS=127_ RUNS=400 bash
    tools/stress-cycles.sh`): `async 5` / `pending 1` in 3 of 400 as shipped and 4 of 400 checked, against the
    branch's 3 of 200 and 4 of 200 above. Seven of 800 on main and seven of 400 on the branch: twice the rate in
    one sample each, counts too small to tell apart (a Poisson spread of seven is 2 to 13). Not followed further.
  - **Merged with main again (e682484: main 4cf95fe, claude/churn-5's ColdExits, the store forwarding and the
    setter in loops).** Conflicts: docs/FINISH.md (the table's three rows that both changed, each side's words
    kept; the appended entries both kept) and tools/selfhost-check.sh, which the two sessions wrote the same
    night: one script now, the switches passed on, hello world, 220, 193 and 088 and SET's tests by selfdet,
    main's last line ("selfhost-check: all the same"). On the desk at the merge: the five scripts of breaks
    exit 0, tests 19x, 20x (with 208 and 209), 26x and 145 checked and as shipped. Frames, main 4cf95fe and the
    merge in turn, six pairs, a desk loaded by other sessions in most of them (NativeAOT-LLVM's own churn median
    0.049 to 0.088 ms): in the two pairs where both sides' NativeAOT-LLVM tree median was 0.029 to 0.030, tree
    median 1.53x and 1.54x on main and 1.33x and 1.35x on the branch, p99.9 0.82x and 0.91x against 0.80x and
    0.86x, worst 0.67x and 0.89x against 0.66x and 0.77x, churn 1.22x to 1.24x on the branch and 1.23x and
    1.36x on main (its NativeAOT-LLVM churn 0.088 ms in the second: the desk); over all six the tree's
    median lower on the branch in five (1.30x to 1.59x against 1.41x to 1.96x). The worst frame of the tree on
    the branch was above NativeAOT-LLVM's in two of the six (1.45x, 1.21x, both on the loaded desk).
    **Checked on the homelab at e682484** (`env SET=202_,203_,204_,208_,209_ bash tools/overnight-check.sh` with
    the six switches): **suite 244 passed, 0 failed, 2 known gaps, checked and as shipped (772 s)**; the
    self-hosted compiler built as it is (323 s, 33,252,200 bytes) and switched (456 s, 32,994,322 bytes: the
    homelab under other sessions' builds), **hello world by it in 10.2 to 11.4 s** (switched 10.0 to 10.3; 8.6 to
    9.0 s at 37c6b15 an hour and a half before on a quieter homelab, and main's own session gave 8.29 to 8.34 s
    for 4cf95fe's code: not measured here side by side, so whether the merge costs the self-hosted compiler
    anything is not shown; the switched build, without this branch's six steps, is as slow, which lays the
    difference to the homelab or to main's side), peak 272.0 MB, the two modules the same (27,721 bytes);
    **self-1 = net, byte for byte on 145 (27,733 bytes), 220, 193, 088, 202, 203, 204, 208 and 209**. Stress, 200
    loaded runs a mode: 139, 192 to 199, 201 to 203 the same in every run; 127 `async 5` / `pending 1` in 3 of
    200 as shipped and 1 of 200 checked (the open gap, as above).

- **Known differences from .NET, taken in turn (2026-11-22, branch claude/known-diffs-1, from main cb287fb; the
  desk for the work, the homelab for the checks; not merged when this was written).** Seven differences earlier
  sessions had found and left. Five are closed, each with a test that fails without the fix; two are left, with a
  test each that the suite shows as a known gap.
  - **1. A reference into storage through a function pointer (closed, test 241).** `delegate*<List<T>, Span<T>>`
    whose target returns `CollectionsMarshal.AsSpan` (or a `ref` from GetValueRefOrNullRef / GetValueRefOrAddDefault,
    a ref struct with spans, an `out` or `ref` argument): the call is kept as a delegate's is, by the pointer type's
    signature (StorageKeeps.cs; `CS2WASM_NO_FNPTR_STORAGE_KEEP=1`: not). Three things the test found on the way,
    none of them the function pointer's alone. (a) **The keep ended with the scope of the call, for every kind of
    call**: the sites were found before the body only in a state machine, everywhere else the keep was a local of
    the scope the call was written in, so `Span<T> s = default; if (c) s = CollectionsMarshal.AsSpan(list);` read
    the freed array after the list grew (test 222 has no call in an inner scope). PreScan notes the sites now, and
    lambdas, local functions and state machines prepare their keeps as methods do. (b) A function pointer's type in
    generic code was not substituted (Types.Substitute): the arguments were cast to `List<T>` and the call threw
    InvalidCastException. (c) `ref x = ref fp(...)` took the address of a copy (EmitAddressBase had no case).
  - **2. ConditionalWeakTable (closed but for one shape, test 243).** DependentHandle was Mono's `Ephemeron[]` with
    the key a counted field: the table kept every key, so nothing ever went (25 keys and 25 values of 25; the note
    of 2026-11-22 saw the values). A handle is a slot of the runtime's own memory, [target, dependent]: the target
    not counted, the dependent counted; when the target's last reference goes (ObjectMarks.OnDestroy, before its
    finalizer, as .NET clears a short weak reference) WeakSlots.Cleared clears the slot and lets the dependent go
    after the runtime lock is released. The slot lives until Dispose, as .NET's handle; the table's container
    disposes its handles in its finalizer, which upstream runs in two rounds (GC.ReRegisterForFinalize): a
    finalizer runs once here, so the manifest edits the first round out. **Left: a value that holds its own key
    (directly or through other objects) keeps both while the table has the entry**: a cycle of counted references
    through the runtime's slot, which no link knows of; .NET's collector frees both. Roslyn's and the compiler's
    own tables whose value is the key itself (Types.Canonical of a type that is its own canonical form) are of
    this shape, and kept their entries before as well.
  - **3. A derived class's finalizer and its base's (closed, test 242).** The destruction called the most derived
    finalizer only; the C# compiler writes `try { body } finally { base.Finalize(); }` into a destructor for .NET.
    RuntimeGen.CallFinalizers: each finalizer up the chain at both places one is called.
  - **5. Weak references into a dying group (closed for groups, test 245).** What .NET guarantees and is matched: a
    short weak reference (WeakReference<T>, WeakReference, a Weak GC handle) is cleared before any finalizer of the
    objects that became unreachable with its target runs. Each member's handles were cleared at its own turn, so a
    finalizer of one member read a weak reference to a later member alive (855 of 1,680 reads in a ring of 40).
    Rt.WeakDyingAll: once the members that die together are known and before a finalizer runs or a member is
    cleared, the weak handles to each are cleared (the whole group, a part cut from a living one, the deferred
    destruction with threads); the dependents of dependent handles go after the last member's. A program that sets
    no mark has no code for it; one that does pays a call and a test of a count (the objects that are a weak
    handle's target now, ObjectMarks.s_weak) for each group or part destroyed. Not asked: a weak reference that
    tracks resurrection. **Left, plain objects**: a finalizer reads a weak reference to an object that only its
    own object holds as alive (.NET: dead); the object is counted by that field until the holder is cleared.
  - **6. `Node[,]` in a member's field (closed, test 246).** Arenas.ArraySite gives the site for every rank
    (`CS2WASM_NO_RANK_SITES=1`: rank one only). The visit, is_slot, the copy and clear and the allocation were
    rank-aware already; the element store of a rank above one did not hand the array to the link
    (EmitArrayElementAddress set the container's local for a vector only), which is why the site alone left the
    array's own stores unlinked.
  - **4. A finalizer that grows a list of its own dying group (left, test 244, a known gap).** The cause: the new
    objects refer back to the dying members; after the finalizers the members' counts are above the bias by those
    references, which FinPhase reads as "a finalizer stored one of them where the program can reach it", so the
    group lives on, unreachable, with the additions (the finalizers have run: "owners 0 items 40"). Telling these
    references from a real one means following the objects only the dying members hold, plain objects among them:
    a walk the runtime does not make and the owner's rules exclude. Not fixed; no way seen that is sound without it.
  - **7. A member that refers back to itself through an array's element or a far field (left, test 247, a known
    gap).** `n.Raw[6] = n`, `n.W.Back = n`, `n.Far = n`, and the same from a child: cut from a living tree the
    member stays until the group goes. Tried and not kept: Dropped passing a waiting container that the member
    itself owns (`Under(PLW(x), x)`) changes nothing by itself; the reference is not one the trial takes off.
    Seen beside it, the same family, with arrays of one dimension too: the items of an array that was replaced in
    a living member (`var old = b.Cells; b.Cells = new Cell[4];`), which refer back to that member, stay until
    the member's group goes (test 246 does not count them while the board lives).
  - Checked: each closed item's test fails without its fix (241 with `CS2WASM_NO_FNPTR_STORAGE_KEEP=1` and before the scan, the generic and the `ref` fixes; 246 with `CS2WASM_NO_RANK_SITES=1`; 242, 243 and 245 on the commit before theirs) and passes checked and as shipped. The branch was merged with main 7932c93 (the node path; no conflict) at 14779aa, and what follows is of that commit unless said. On the homelab: the suite 236 passed, 0 failed and the 2 known gaps, checked and as shipped (460 s; the job was queued at a46bd11 and its first line names that commit, the checkout was at 14779aa when it ran: tests 201, 244 and 247 are in its count); the self-hosted compiler (`tools/selfhost-switch.sh CS2WASM_NO_RANK_SITES=1`): built in 240 s, 32,886,612 bytes, hello world in 9.06 to 9.17 s, peak memory 344 MB, the same module with and without the switch, and `tools/selfdet.mjs`: self = net byte for byte on 145 (27,738 bytes), 220 (276,626), 193 (258,680) and 088 (1,617,408); before the merge, at a63e075 (item 1 alone) the suite 231/231 checked and as shipped, and at dbae95a (items 1 to 3) the self-hosted compiler byte for byte on 145 (9.3 to 10.2 s, 344 MB). **Not run: `tools/stress-cycles.sh`** (127 and 139, and the new tests, 200 runs a mode): queued on the homelab behind other sessions for forty minutes and not started when this was written; the desk refuses heavy work. On the desk, the merge: tests 241 to 247 (244 and 247 as known gaps), 030 to 039, 190 to 207, 220 to 222, 020, 086, 087, 125, 127, 139, 143, 180 and 187 and the five of 22x pass both ways; hello world 27,784 bytes as shipped (the limit 28,383; the same number at the first commit of the branch). `node tools/frames.mjs --runs 3 --js-young 32 --no-aot`, main and the merge built in turn, two rounds each (main first): tree median 0.048 and 0.048 ms against 0.047 and 0.048, p99 0.073 and 0.070 against 0.070 and 0.070, p99.9 2.78 and 2.83 against 2.70 and 2.66, worst 3.33 and 3.14 against 2.96 and 2.97; churn median 0.066 and 0.067 against 0.068 and 0.067, p99 0.076 and 0.076 against 0.090 and 0.082 (main's own in a full run half an hour before: 0.140; the scene has no group, no weak reference and no finalizer, and none of its code changed), p99.9 0.116 and 0.100 against 0.124 and 0.124; bigdrop median 0.003 both, p99.9 1.09 against 1.08 to 1.11: level. With NativeAOT-LLVM beside it, before the merge (bd6177b): churn 1.34x its median, collections 0.97x, strings 0.89x, tree 1.69x (p99.9 1.27x, the node path not yet in), bigdrop 0.94x. Nothing was measured with threads beyond tests 139, 143 and 197 in the suite.

- **Known differences from .NET, second round (2026-11-23, branch claude/known-diffs-2, from claude/round-9
  d430594; the desk for the work, the homelab for the checks; not merged when this was written).** The two known
  gaps of the round before are closed (the suite has no `known-gap` test), three more differences that were on
  record are closed or narrowed, and a sweep of small programs around them found one large one, which is closed
  for the program's own code. What is left is listed at the end, each with its smallest program.
  - **1. A finalizer that grows a list of its own dying group (closed, test 244; and test 248).** The members'
    counts after the finalizers, beyond their own references, were read as "stored where the program can reach
    it", and the whole set was made immortal (count -1): a real resurrection was never freed either. Which of the
    two it is is no longer asked. `Rt.FinRevive`: the members become what they were before any store linked them,
    plain objects with every reference counted where it is held (during the finalizers every reference to and
    from a dying member is a counted one already: ArenaLinkCore refuses the link). Then the references are linked
    as the stores they stand for: (a) each member that had no owner among the others, with all that its slots
    reach, top down (ArenaLinkContents: a container's links before those of what it holds, as the program made
    them); (b) the slots of members that live on which took a dying member during the finalizers: noted where the
    link was refused (`FinRecord`, the container counted once more while it is in the list), found by one visit
    of each noted container (mode 21) while the dying members carry a tag on their counts, so nothing is walked
    that the finalizers did not touch; (c) what neither reached. The count each member was given for the while is
    let go last, bottom up. What the program can reach then lives on as any object does and is freed when it is
    let go later (its finalizer not run again unless GC.ReRegisterForFinalize asked: Rt.FinalizedBit); the rest
    goes by the counts at once, or as a group of its own whose finalizer phase runs the finalizers of the objects
    made meanwhile with every reference in place: .NET's next collection. Three things the tests found on the
    way: a noted container nothing else refers to any more (an array a list grew out of during the finalizer) is
    let go before the links, else it kept what it owned from the array that holds the same items now; during
    these links a member that code still refers to is not made the property of one that came back (the garbage
    owned the living root it referred to, and the check at the drop, finding a member code holds under it, kept
    it); the record of a whole group that came back is freed (it stayed, busy, for good).
    `CS2WASM_NO_FIN_REVIVE=1`: as before. Without threads only: with threads FinPhaseCuts is as it was (the
    members never freed).
  - **2. A member that refers back to itself through an array's element (closed, test 247).** Let go by its
    owner, the member is adopted by the other container that holds it (Adopt), here an array it owns itself: the
    ownership turns round, the array is the top, nothing's, with no reference from outside, kept by what it owns
    alone, and nothing asked the check at the drop of it (the member is owned, so no candidate).
    ArenaUnlinkOld asks it of the new top (`CS2WASM_NO_ADOPT_CHECK=1`: not). The far fields of the test (a field
    past the sixteenth word, a field of a wide struct) were freed already once this was; the test's last line was
    .NET's own artefact (the first node's reference stayed in a slot of Main's frame: made in a method of its own
    now).
  - **5. The cells of a replaced array that refer back to the array's holder (closed, test 249).** One, two and
    three dimensions, jagged, lists, with and without a local across the store: all freed already but one shape,
    a ring of cells kept for a while by a cell of another array. The ring's top failed its check by that one
    reference from outside what it owns, and the number the note keeps is taken down only at a store
    (NoteOutside at the unlink): when the other array was let go and its cell destroyed, the reference went with
    its holder and the number stayed. VisitDying and DyingSurvivor ask NoteOutside as the unlink does
    (`Rt.DyingNote`), and DrainKills checks the top when the destruction is done (`CS2WASM_NO_DYING_NOTE=1`: not).
    Rt's NoteOutside itself is unchanged (it is called from one more place).
  - **3 and weak handles (narrowed, tests 265, 266, 248).** .NET clears a short weak reference before the
    finalizer, but a handle that tracks resurrection and a dependent handle (a ConditionalWeakTable's entry) only
    when the object is freed. All were cleared before the finalizer: a finalizer did not find its own table
    entry, and an object brought back had lost its entry and its long weak references. Now a chain block has a
    bit for a long handle (WeakSlots.LinkLong), `Cleared(addr, all)` clears the short ones or all;
    ObjectMarks.OnFinalize (rt.on_finalize, before a finalizer) and WeakDying clear the short ones, OnDestroy all
    (after the finalizer of an object that did not come back, and in a group member's clear function when its
    class has a finalizer). **A value that is its own key** (`table.Add(k, k)`, Roslyn's and the compiler's
    tables of a type that is its own canonical form) is not counted by the slot, so the key is freed. **Left: a
    value that holds its key through a field** (see the end).
  - **6. Reflection on compiler-made classes (one of three closed, test 270; branch
    claude/known-diffs-2-reflection, merged).** `Delegate.Target` of a capture-free lambda is the `<>c` singleton
    (one object a class, its type the method's declaring type), of a lambda that captures `this` alone the object,
    of a capture-free local function null, of a lambda made further in the method's display: the delegate's
    layout and call are unchanged, the property asks a function the compiler writes (rt.delegate_target, a
    compare on the delegate's function; the singleton a 16-byte immortal object in the data). Gated on the
    program reading Delegate.Target: hello world the same bytes, +31 B where the property is reachable and not
    asked, +291 B on test 155.
  - **7. Test 139 with the engine's stack checks off (not reproduced).** 6,000 runs as shipped, 24 at once,
    `--no-wasm-stack-checks`, on the homelab (562 s): every run `16 1`. With the 4,000 of the round before: one
    `16 3` in 13,000. Not followed further.
  - **8. The sweep, and what it found: a span or a `ref` did not keep its object (closed for the program's own
    code, tests 271, 272, 268).** `Make().AsSpan()`, `ReadOnlySpan<T> s = h.Arr; h.Arr = other;`,
    `var s = a.AsSpan(); a = other;`, `ref T r = ref h.Arr[i];`, `Span<T> F() { var a = new T[3]; return a; }`,
    `F(ref h.Arr[0], h)` where F replaces `h.Arr` (test 272): under .NET the span or the place keeps the array. Here neither
    holds a count (StorageKeeps kept a collection's storage only), and the array went with its last holder: of 38
    small programs 34 read freed memory, a trap or another object's bytes, checked and as shipped. The remedy is
    StorageKeeps' own, widened, in the functions of the program (the core library's code is compiled as it was):
    - the site counts the object the reference that came out points into (Rt.ObjectOf) into a local of the
      function, let go at its return, for a call, property, constructor, conversion operator or the language's
      own span conversion through which a reference can come out, unless what it is given is stable for the
      function (a parameter or local never written again, `this`, a constant, a static readonly field; a local
      of an inner block only where the result stays in its statement) and the callee is one of the library's
      that give out only what they got (a ref struct's members, MemoryExtensions, MemoryMarshal, Unsafe, string,
      Memory, ReadOnlyMemory, ArraySegment); and for a `ref` local or `ref` assignment whose target is a place in
      an object that is not stable (FunctionCompiler.NoteSpanSites, after the captures are known);
    - a function that returns a span, a ref struct or a `ref` counts what the result points into before it lets
      go of its locals and temporaries (Rt.ReturnKeep, four at a time), and the calling site, which counts it for
      itself, gives that count back (Rt.ReturnDone);
    - a place passed by reference (`ref`, `out`, `in`, the struct a method is called on) that is in an object
      read from a field, an element or a call keeps that object for the statement, as a reference argument is
      kept (ToCallSafe, with the field borrow's own decision whether the count is needed).
    `CS2WASM_NO_SPAN_KEEP=1`: none of the three (tests 271 and 272 trap). The sweep's other programs: finalizers
    of plain objects with inheritance, brought back from the derived or the base finalizer, asked again,
    suppressed (267); ConditionalWeakTable enumerated, cleared, replaced, with values that hold other keys, keys
    in cycles and under a living tree (266); spans and refs into collections across growth through every way of
    passing them (268; its one failure was the span over `stack.ToArray()`, the case above); function pointers,
    generic, by `ref`, in fields, arrays and generic classes (269): the same as .NET but for what is listed below.
  - **9. Two more the sweep found (one closed, test 274; one found and left, test 273 goes round it).** (a) *A
    static's initializer pins its object*: the object of `static Node s_root = new Node(0);` is kept out of
    groups (Rt.ArenaPin) as a static readonly field's is, by the initializer's store alone (EmitStoreField pins
    any static; a later assignment to the same field does not): once in a cycle it is never freed when the static
    is set again. Unpinned (commit b27e90f) it was freed, and tests 247, 248 and 249 failed: their roots are
    statics set by initializers, and with the root a member of the group, as a root assigned in code is, the
    cells' references back to it are up-references, a cell another cell refers to holds its old array, and
    pairs of cells that refer to each other stay until the group goes (SPEC 4.3's allowance; test 249 "kind 1
    how 3": 2, 6, 6 and 8 cells too many). So the pin stays the default (`CS2WASM_UNPIN_STATIC_INIT=1`: pinned
    only when readonly), and **what tests 247 to 249 prove, they prove for a root that is a plain pinned object**.
    One of the three holds for a member root too: Rt.Dropped refused a member with a container waiting to own
    it; when that container is one the member itself owns (`n.Raw[6] = n`: the member holds its old owner, is
    therefore not adopted, and nothing else looked) the check is made now (247 passes either way). (b) *A
    container let go while one of its items is held from elsewhere*: `g.A = null` where one cell of the array is also in `g.One` and each cell refers back to g:
    the other cells stayed until the held one was let go (three of four for .NET's none), because the held
    cell, with an up-reference below it and a holder other than its owner, holds the array. `Rt.ShedHolds`,
    asked where a member is let go by its owner (ArenaUnlinkOld) or by the code (ArenaRelease) and only its
    items' holds are left: each held item changes owner to the container waiting to own it, when that one is the
    array's owner or below it (its up-references then pass the same members: the u of the members between is
    moved), or, when it owns nothing that refers up, has its own up-references made counted ones (Reevaluate
    makes them up-references again under its next owner); the array then has no reference and goes with the
    items nothing else holds. Arrays of any rank and members by their slot bits; nothing done when an item
    refers up to the container itself, has a note of a failed check, or with threads.
    `CS2WASM_NO_SHED_HOLDS=1`: not (test 274 fails).
  - **Left, each with its smallest program.**
    (a) *A table's value that holds its key through a field*: `var k = new K(); table.Add(k, new V { Key = k });`
    and k let go: .NET frees both (the ephemeron rule), here both stay while the table has the entry (`keys 1
    values 1` for `keys 0 values 0`). The slot's count on the value and the value's on the key are a cycle no
    link knows of; to free it the key's last reference from elsewhere would have to be told from the value's at
    every release of a key.
    (b) *What only a dying finalizable object holds*: `sealed class P { public object Q = new(); ~P() { .. W.TryGetTarget(out _) .. } }`
    with W a weak reference to `p.Q`: the finalizer reads True where .NET reads False (the short weak references
    to everything that became unreachable together are cleared before the first finalizer); and of the same
    kind, when the finalizer brings its object back, what the object holds has been finalized under .NET at that
    collection (it was unreachable with it), here it is finalized when it is freed (tests 265 and 267 do not
    count such objects meanwhile). For a group's members both are as .NET (test 245). For plain objects it needs
    a pass over what the dying object alone holds before its finalizer: not made.
    (c) *GC.ReRegisterForFinalize twice on an object not yet finalized*: .NET runs its finalizer twice (two
    entries in its queue), here once.
    (d) *Reflection*: `typeof(delegate*<int, void>).IsFunctionPointer` is False; the compiler-made classes' own
    member lists (`GetNestedTypes`, `GetFields` of `<>c`, of a display class, of a state machine: empty; .NET
    lists Roslyn's delegate caches and hoisted locals), generic compiler-made classes as constructed types (the
    definition is reported, and a capture-free lambda of a generic method has one Target for all instantiations),
    `MethodInfo.Invoke` and `Delegate.CreateDelegate` on a compiler-made method (NotSupportedException), a
    capture-free lambda evaluated twice (two delegate objects; Roslyn caches one).
    (e) *With threads*: FinRevive is not applied (the members a reference was left to are never freed, as
    before).
    (f) *Spans in the core library's own code* are compiled as before (no case of it is known: the suite and the
    self-hosted compiler run the library's paths).
    (g) *Compositions of the held-item shape*: an item taken out of its array while a plain copy of the array
    holds it, then the copy stored in the array's place and let go
    (`var copy = (Cell[,])g.A.Clone(); g.A[0, 1] = null; g.A = copy; .. g.A = null;` with cells that refer back
    to g and to each other across arrays): cells stay until the group goes (3 of 21 in the program test 274 was
    cut down from; the item holds its old array without being in it, so ShedHolds does not find it).
    (h) *A place passed by reference in the core library's own code*, as (f).
    (i) *A root that is a member of its group* (a static assigned in code, or any with
    `CS2WASM_UNPIN_STATIC_INIT=1`): two cells of an array that refer to each other and back to the root, taken
    out of the array one by one, stay until the group goes (test 249's "kind 1 how 3" with the switch; with the
    switch test 248's "star under a living tree, mode 5" differs too: not looked into). And the other side of it:
    the pinned object of a static's initializer, once in a cycle, is never freed when the static is set again
    (`static Node s_root = new Node(0); .. s_root.Kids.Add(new Node { Parent = s_root }); s_root = new Node(1);`:
    a weak reference to the first root reads alive for .NET's dead).
    (j) *Not this branch's, seen under load*: test 108 as built checked traps in about 1 run of 100 with four at
    once on the desk (`operation does not support unaligned accesses` in rt.retain under
    Interlocked.Exchange(object, object) in Task.FinishContinuations), the same with every switch of this branch
    off; 088's timer line; 085's oracle (.NET had not collected yet).
  - **Checked at the merge with main 4cf95fe (1cae0b2, the branch's last code; the homelab unless said).** The suite
    255 passed, 0 failed, **no `known-gap` test**, checked and as shipped (1,088 s, the machine shared). The
    self-hosted compiler (`tools/selfhost-check.sh`): built in 251 s, 33,041,673 bytes; hello world in 13.1 to
    13.6 s on the loaded homelab (two slots in use), peak 272.0 MB, the same module with the switches; 145
    (27,740 bytes), 220 (283,039), 193 (266,314) and 088 (1,618,568) each "self-1 = net, byte for byte". The time
    against main's: main 4cf95fe's self-hosted compiler (claude/main-check-1's build) and this one's, copied to the
    desk and run in turn on hello world, five rounds: main 9.80 to 12.19 s, this branch 9.68 to 12.12 s, the same
    module (27,785 bytes): level, the homelab's 13 s is its load. On the desk, the merge: the tests matching 24,
    26, 27 and 208, 209, 260 pass both ways (12, 8, 7, 1, 1, 1). **Stress (`tools/stress-cycles.sh`, the new tests
    and 127, 139, 200 loaded runs a mode, at 1cae0b2): 244, 247, 248, 249, 265 to 274 and 139 the same in every
    run of both modes; 127 as shipped the same in 200, checked 1 of 200 differed (its "async 5 / pending 1", the
    load flake on record).**
    The last session's uncommitted change (DrainKills looks again only at a top whose DyingNote brought its
    number to zero) was not finished (no switch, no test, not measured): parked untested on
    claude/known-diffs-2-drainkills (9145a28).
  - **The merge with main 4a42328 (claude/tree-3; branch claude/known-diffs-2-main2): the plain parts are off by
    default.** Merged without a conflict in code, tests 248, 265 and 266 failed checked and as shipped on the desk
    (248 "star, mode 0, let go: live 8 finalized 5 short dead long alive" for .NET's "live 1 finalized 12 short
    dead long dead"; 266 "a pair, mode 1, let go": things 0, finalized 7, the long weak reference True for -2, 9,
    False): objects a finalizer brings back and objects behind weak handles stay alive. Of tree-3's switches only
    `CS2WASM_NO_PLAIN_PARTS=1` puts all three right (NO_PLAIN_PARENTS, NO_LIST_TEST_FIRST, NO_LIST_THROW_PATH,
    NO_UNLINK_SKIP, NO_SLOT_WALK each leave all three failing). So the plain parts (Rt.PartHolder, JoinFresh's
    plain part) are off by default, correctness first; `CS2WASM_PLAIN_PARTS=1` (or a `CS2WASM_PLAIN_PARTS_BREAK`)
    turns them on for later work, and tools/plain-parts-breaks.sh asks for them so. **The cause is not found**:
    where a part's finalizer or weak handle is reached through its holder after FinRevive or with the weak handles
    that stay until the object is freed is the place to look. Checked on the desk at that commit: the tests
    matching 19, 20, 24, 26, 27, 145, 127, 139, 187, 088 pass checked and as shipped (12, 12, 12, 8, 7 and one
    each); hello world 27,786 bytes as shipped. Not run on the homelab (the suite and the self-host check are
    those of 1cae0b2 above). `node tools/frames.mjs --runs 3 --js-young 32` on the desk, off and on in turn, two
    rounds (07:02 to 07:04): tree median 0.038 and 0.037 ms off, 0.039 and 0.038 on (1.32x and 1.27x NativeAOT-LLVM's
    off, 1.36x and 1.31x on), p99.9 2.01 and 2.03 off, 1.97 and 1.93 on, worst 2.38 and 2.08 off, 2.13 and 2.04 on;
    the module 239,094 bytes off, 239,684 on; churn, collections, strings and bigdrop the same either way (churn
    median 1.21x, mean frame over the scenes 0.69x to 0.70x NativeAOT-LLVM's): what the tree gives back is about
    2 to 5 percent of its p99.9 and nothing measurable of its median.
- **The churn loop, round 5: 1.35x to 1.17x-1.22x, what the engine does with a call in a loop, and what is left by
  measurement (2026-11-22 to 23, branch claude/churn-5, from claude/round-9 d430594; the desk machine, shared with
  other sessions' timings: ranges, runs in turn, the lowest of several rounds where said; the homelab for the
  checks).** Asked for: the churn scene's median at or below NativeAOT-LLVM's, or every nanosecond that is left
  shown to be a floor. **Not reached: `frames.mjs` churn 0.063-0.064 ms against 0.051-0.054 (1.17x to 1.22x; it
  was 0.068-0.070 against 0.050-0.052, 1.35x to 1.36x, in the same hour); the mean frame 1.12x to 1.17x (was 1.24x
  to 1.25x).** Two changes are in (a list's setter in loops, a stored value read back); one pass is built and off by
  default because it gives the scene nothing (the calls of a count's other path outside the loop); three things were
  tried and not kept; the header's word is not built, with what stands in its way.
  - **Measured first, both sides** (us a frame, cs2wasm against NativeAOT-LLVM; `tools/churn-parts.cs`,
    `churn-raw.cs`, `churn-move.cs`, built for NativeAOT-LLVM from the same files). The scene by phase: births 4.0
    against 7.7, the loop 66.0 against 44.1, the removal 0.4 against 0.2, **the loop without its move 29.4 against
    29.7** (level: field loads and the cheaper free path closed the read loop in the scene). `churn-raw` (an array in
    order, every survivor moved 400 slots down): no store 28.3 against 23.0, the reference written and nothing else
    32.0 against 26.4, the store as each side makes it 42.8 against 29.5; the read loop rotated by hand 29.2-29.6
    against 23.7, **slower than as it is on both sides** (round 4 had 1.7 us for it: FieldLoads reads the length
    once now, and what was left of "the loop's shape" is nothing here; a pointer stepped along the array cannot be
    written by hand without `fixed`, whose pin is counted: not measured). `churn-move` (through the list, particles
    in order, none dying): no store 27.5 against 29.8, every survivor into its own slot **55.3 against 40.5**: the
    array's store costs 14.5 on top of the read loop, the list's 27.8, where NativeAOT-LLVM's list costs it 4 more
    than its array. So the first thing was in the list, not in the count.
  - **Built: a list's setter goes into loops as its getter does** (`CS2WASM_INLINE_READERS` 80 to 96;
    Wasm/Inliner.cs). FieldLoads' inliner takes a callee of up to 80 instructions that reads fields of an object it
    is given; List<T>'s setter for a class's elements is 81 to 90 with its two counts, so the getter went in and the
    setter stayed a call, which the engine inlines itself but with the size, the array and its length read again,
    and with its counts' calls in the loop (below). `churn-move`: own slot 55.5 to 45.9, 400 slots down 58.0 to
    47.0-50.9, the scene's loop 69.9 to 61.7; the frames module 55 bytes larger, hello world the same. No new rule:
    the conditions are FieldLoads' own (tests 230 to 234, `inline` among them).
  - **Built: a value stored and read back is the stored value** (`CS2WASM_NO_STORE_FORWARD=1`;
    Wasm/SameBaseLoads.cs). `--p.Life > 0` stores the field and reads it again for the test: the engine's code had
    the store and then two loads of the place. By the pass's own proof (the same local plus the same constant, the
    local not assigned since, also not inside the stored value's own code; every store between to other bytes of
    that object; no call) the read is the value that was stored, kept in a local before the store. Only a store of
    the place's whole width read with the load of the same type (a byte's or a short's store keeps part of the
    value: left as it is). `CS2WASM_SAME_BASE_WITHOUT=overlap|other|calls|assigned|value|address` takes one of the
    pass's rules out (the switches are new; round 4 proved its rules by editing): test 207 (loads) prints something
    else for overlap, other, calls, assigned and address, test 209 (new: stores read back in loops, narrow places,
    NaNs, the variable assigned, stores between through other variables, overlapping fields, refs, pointers, calls)
    for overlap, other and calls; `value` (the variable assigned inside the stored value's code) no program shows:
    the compiler keeps the address in a temporary there. It stays as a guard. Hello world one byte smaller.
  - **The two together** (`tools/frames-ab.mjs`, eight rounds in turn, each round's median, ms): churn before
    0.0652-0.0681, the setter alone 0.0624-0.0660, the stored value alone 0.0631-0.0660, **both 0.0589-0.0631**;
    collections 0.0062 all four, strings 0.0068-0.0069, tree 0.0472-0.0489, bigdrop 0.0031-0.0032: nothing else
    moved. `bench-ab.mjs`, four rounds: 0.999x and 0.991x of before (level). `frames.mjs --runs 3 --js-young 32`,
    before (the two switches) and after in turn, median / p99 / p99.9 / worst ms and the ratio to NativeAOT-LLVM's
    median: churn before 0.068 / 0.090 / 0.121 / 0.145 (1.36x) and 0.070 / 0.112 / 0.170 / 0.283 (1.35x), after
    0.063 / 0.089 / 0.120 / 0.143 (1.22x), 0.064 / 0.112 / 0.197 / 0.207 (1.22x), 0.063 / 0.142 / 0.262 / 0.348
    (1.17x) and 0.064 / 0.107 / 0.125 / 0.147 (1.22x); collections 0.006 / 0.021-0.024 / 0.165-0.178 / 0.294-0.304
    before and after (1.00x to 1.03x); strings 0.007-0.008 / 0.010-0.014 / 0.019-0.032 / 0.046-0.120 (0.88x to
    0.93x); tree 0.045-0.048 / 0.062-0.102 / 2.29-3.04 / 2.58-4.59 before, 0.047 / 0.074-0.080 / 2.34-2.55 /
    2.58-3.10 after (1.54x to 1.58x, p99.9 0.91x to 1.11x: the desk's spread, not this round's); bigdrop 0.003 /
    0.007-0.008 / 1.02-1.18 / 1.05-1.74 (0.94x to 0.97x); memory 16.0 MB against 59.3 (71.1). The mean frame: churn
    1.24x-1.25x to 1.12x-1.17x; the geometric mean over the scenes 0.74x-0.76x to 0.72x-0.73x. `bench.mjs`: 0.91x
    before, 0.93x after (one run each; its two sides move by more).
  - **The engine's own code read** (new for this record: `node --print-wasm-code host/run.mjs m.wasm` prints V8's
    machine code for every function, by name with `CS2WASM_NAMES=1`; the optimizing tier's listing is the one
    marked TurboFan). The counted store's loop in `churn-raw` had, on the path every survivor takes, **five stores
    of the loop's values to the stack and one load back a turn** (the sum, the particle, the write index, the read
    index, the old value), beside the loads and stores the loop itself asks for. They are there for the calls on the paths
    that are not taken (rt.retain for an object in a group or never freed, rt.on_destroy, Rt.FreeObject for a
    string, Rt.FreeSmallOld): the engine writes a value that is alive across a call to memory where the value is
    made, on every turn, unless the call's block ends in `unreachable` (NoReturn.cs is about that case) or the
    value is a loop's own (a phi at its top), which it writes only where the call is. By hand in
    `tools/churn-count.cs` (new: the counted store in its parts, the count read and written through a pointer; us
    a frame): no store 28.3, the reference written 31.7, **the counts by hand as the compiler tests them 38.7, the
    compiler's own 42.4**; without their tests 37.2, one added only 34.6, the old value read and one taken 34.7,
    the old value read and tested alone 33.3. So of the counted store's 10.7 on top of a bare store: **the two
    counts 5.5 (a load and a store each), their tests 1.5, the engine's writes for the calls 3.7.** NativeAOT-LLVM's
    store with its card mark costs 3.1 on top of its bare store: counting a moved reference is 2.4 above the
    collector's barrier at the least, without a proof that it is a move (rounds 2 and 3: none is sound). **The two
    counts sharing work for a class known to be plain** (sealed, flat: the retain without its test, the release
    with the zero test alone): 39.5 by hand against 38.7 with the tests as they are: nothing to share, the tests
    are a compare each that the processor predicts. **A range's release in one pass** (RemoveRange, Clear): with
    the free's usual case written out in the range's loop the 396 particles of the scene that end after the loop
    go in 1.5 us for 2.4 (the side branch, below), but 20,000 objects cleared in a loop that runs often
    (`drop-range.cs`, all ending) in 35.4 for 34.3: a gain only where the release runs cold; and the churn scene's
    own RemoveRange lets none go (its tail holds survivors that were moved, counted twice, and the frame's
    newborn). Not built.
  - **Built, and off by default: the calls of a count's other path outside the loop** (`CS2WASM_COLD_EXITS=1`;
    Wasm/ColdExits.cs, its summary has the shape). Shapes were tried by hand first (`churn-count.cs`): the calls
    after the loop, reached by leaving it: nothing (what the turn made and the call's code uses is written where
    it is made, and the engine shares an address computed in the loop with the code after it); at the top of an
    outer loop: no writes, but what the loop does not change was read from the stack on every turn (the engine
    moves a value's place in memory up to the top of the loop that has the call when no use lies between); **after
    the loop, reached from the outer loop's top by its branch back: 40-41 us, the hand's 39-40**: what the call's
    code uses then comes to it as the outer loop's own values. The pass does that for an innermost loop: the
    path's code and what was left of the turn are written after the loop, the loop is entered again; control flow
    only, the same code at the same point with the same locals; a value on the operand stack there is kept in a
    local where it is made. The counted store 45.7-46.3 to **40.5-41.1**; a range cleared whose objects do not end
    14.8 to 10.5. **But a turn that leaves the loop pays 5 to 7 ns** (the loop's values through three loop tops):
    `tools/drop-range.cs` (new: an array of 20,000 objects cleared or stored over, all of them, one in 2, 4, 10,
    50 or none ending there), all ending 36 us with the call in the loop, 45 with every turn leaving it. So the
    loop as it was is kept as a second copy for the turns after such a path (a loop with a counter goes there when
    the last one was within 8 turns and comes back after 8 without; one without a counter stays 4 turns, 8 more
    after each). With that, cleared and stored over, against the call in the loop: all ending 38.9 against 33.8
    and 44.4 against 42.6 (15% and 4% slower; with the turns counted down in a local instead, 34.5-36.3 and
    44.9-49), one in 4 25-26 against 21.6 and 37.3 against 31.9 (17%), one in 10 33.0 against 34.2 and 46.9 against
    43.5, one in 50 16.7 against 19.0 and 31.1 against 30.3, none 10.5 against 14.8 (30% faster) and 25.1 against
    25.6. **The
    churn scene: 0.0578-0.0585 ms with it against 0.0582-0.0591 without, eight rounds in turn: nothing** (one
    particle in 48 ends in the loop: what the survivors' turns gain, 2 us, the 400 that end give back); the other
    scenes the same; `bench-ab.mjs` level but for list_sort, 3 to 4% slower in every round. A gain where the other
    path is rare, a loss where it is taken every few turns, and the compiler does not know which: **off by
    default.** Test 208 (loops whose stores take the other path in every way a turn can go on: more stores, `?:`,
    break, continue, a goto past the code after the loop, switch, return, exceptions) passes with and without;
    `bash tools/cold-exits-proof.sh`: with the pass (24 of its loops) the same output, without `stack` or `rest`
    the module is not valid, without `labels` all 26 lines differ. The self-hosted compiler built with the pass on
    (commit 0b50ec9, where it was on): 33,103,522 bytes against 32,966,301, hello world in 8.64-9.33 s against
    8.61-8.69, byte for byte the .NET-hosted compiler's.
  - **What a particle that ends inside the loop costs** (`tools/churn-deaths.cs`, new, both sides: the scene, and
    the scene with its dead held by a second list until after the loop; us a frame of the loop alone): cs2wasm
    55.6-58.1 and **50.1-52.9**, NativeAOT-LLVM 45.2-46.3 and 45.5-48.5; letting the 396 go after the loop 2.4-2.9
    (7 ns each; NativeAOT-LLVM 0.2). So **the 400 ends cost the loop 6 us where the same work after it costs 2.5
    to 3, and in a loop where every object ends under 1 ns each** (`drop-range.cs`: stored over, all ending 43.5
    us against 26 for none, 20,000 objects): what the scene pays for is the branch the processor cannot predict
    (one particle in 48, at random) and a path of about 80 instructions and two calls run once in 48 turns.
    NativeAOT-LLVM pays none of it in the median: its collector frees in its pauses, **5.5 to 6 us a frame in the
    mean** (its mean frame 0.0566-0.0580 ms against its median 0.051-0.052; cs2wasm's 0.0645-0.0673 against
    0.063-0.064). Freeing after the loop would be a deferred free: not done. With every particle given the same life (the tool's last two scenes: a frame's dead are its oldest, side by side at the list's start, so the branch is predicted and the path runs 400 times in a row) the loop was 54.3 us with the dead ending in it against 53.6 with them held: **one sample, on a loaded desk; not repeated** (a quiet run was waited for and did not come before this was written).
  - **Tried for the ends, and not kept** (branch claude/churn-5-free-inline, the code as it was when measured;
    its suite was not run). (1) The pass on by default only for loops whose every call is rare by what it is (the
    retain of an object of a flat sealed class, said by the compiler on the call; rt.on_destroy behind its own
    test of the mark; the frees' slow cases), with the two calls a free makes written out in the loop
    (Rt.FreeSmallOld's and Rt.FreeObject's usual cases as functions, Rt.FreeBack and Rt.FreeSmall), so that an
    object ends in the loop without a call: `churn-deaths` 55.6-56.5 to 53.3-54.5 with the string's free left a
    call, **57.8-59.0 with it written out too** (its values are more than the engine has registers for beside the
    loop's own: three of the loop's values written at its top on every turn, by the listing); the range's release
    of the 396 after the loop 2.4 to 1.5 us; **the frames scene itself 0.0604-0.0665 ms against 0.0598-0.0613:
    nothing.** (2) What no call on that path at all would give, measured by leaving the block unfreed (a leak, for
    the measurement only): 51.1-53.7; with the frees in, the tool's 53.3-54.5 was the most that came of it. (3)
    rt.on_destroy's call behind the test of the mark's bit at every end written out in a loop: 58.4 against 59.0,
    eight rounds: nothing (the engine inlines the function). Test 260 (objects with weak references and table
    keys that end inside loops) was written for it and stays as a test of those paths.
  - **The NaN tests**: `CS2WASM_NO_CANON=1` (measurement only) on `churn-raw`: the read loop 29.9 to 27.1, the
    counted store 40.4 to 37.3: **2.8 to 3.1 us**. The test as a select on the bits, stored as an integer (no
    branch in the engine's code): slower, 29.9 to 34.8; the float select stays. The owner ruled that the
    canonical NaN stays: a floor.
  - **The header's third word: not built; what stands in its way, found by reading the runtime for it.** (1) Bit
    31 of the word (pinned by a static) is read on about fifteen arena paths (`Mem.LoadI32(x + 8) < 0`), several
    before the object's type is asked: with two header sizes each must ask the type first. (2) Bit 30 (a mark:
    weak references, a suppressed finalizer) is read at every end (rt.on_destroy). A side table by address needs
    one bit an object that has an entry: the type word is compared whole at every cast and dispatch; the count
    word's negative values are the arenas' and the immortal objects', and wherever the runtime tells a plain
    object from a member by the count's sign (about twenty places) a plain object with a negative count would be
    read as a member, its arena header taken from the words before it. A bit a page ("an object here has an
    entry") would do for blocks in pages, at a second load an end and a lookup for every end in such a page, but
    small blocks are not all in pages and frame objects and data objects are in none. (3) SPEC 7.3's identity
    hash is a counter in the order hashes are first asked for, kept in the word: made from the address it would be
    other numbers (a freed address's next object the same hash), which is a change to the specification's text,
    and kept in a table it needs (2)'s bit to be taken out at the end. (4) So only a class's own choice is left: a
    two-word header for a hierarchy that has no class that can be on a cycle and none with a finalizer (the pinned
    and finalized bits are never set there), the hash and the mark in tables with a page's bit, and every read of
    the word in the runtime behind a test of the type's header size (HashOf, ClearFinalized, the marks, the
    statics' pin, the memory statistics, the share check's thread, MemberwiseClone's first byte, boxes, the
    reflection's field offsets, the free list's link at +8 in a block of 8). That is a change to every object's
    layout and to the end's path, a day's work with the self-hosted compiler's rebuild after each step, for
    round 3's 1.5 to 2 us of this frame (4 bytes of 36) and the memory of small objects. **Not started this
    round; no measurement says it is a loss.**
  - **What is left of the churn frame, by measurement** (0.063-0.064 ms against 0.051-0.054; cs2wasm's births are
    3.7 ahead, its read loop level): **the particles that end in the loop 6 us** (5.6 to 7: the collector's 0 in
    the median, 5.5 to 6 in its mean; a floor while frees are not deferred, of which the calls' share, 1 to 1.5,
    is what (1) above could not turn into a gain); **the NaN tests 2.8 to 3.1** (a floor by the owner's ruling);
    **the two counts of a moved reference 5.5, 2.4 above the card mark** (a floor without a proof of the move);
    their tests 1.5 and the engine's writes for the calls 2 to 3.7 (what ColdExits takes away in a loop where no
    object ends, and gives back where one in 48 does); the header's word 1.5 to 2 (unbuilt). The scene's worst
    frame stays a tenth of NativeAOT-LLVM's (0.14-0.35 ms against 1.17-1.48).
  - Sizes: hello world 27,784 to 27,783 bytes (limit 28,383); the frames module 230,372 to 230,417; the
    benchmarks' 366,131 to 366,120; the self-hosted compiler 32,969,222 to 32,970,943.
  - **After, by phase** (`churn-parts.cs`, us a frame, the desk busier than for the first table): births 4.4-4.5
    against 8.5, the loop 58.6-59.4 against 45.9-46.1 (it was 66.0 against 44.1), the removal 0.46 against 0.21,
    the loop without its move 32.1-32.5 against 30.9-31.0.
  - **Checked** on the homelab. The suite at ba966f6 (the last change to the compiler is 25e2f1b; tests 208, 209
    and 260 in it): 239 passed, 0 failed, 2 known gaps (244, 247), checked and as shipped (489 s); before it at
    c459a81 (ColdExits on, as first built) 237 and 2, at 25e2f1b 238 and 2. Stress (`tools/stress-tests.sh` 208, 209, 260, 127 and 139, 200 loaded runs a mode): 208, 209, 260 and 139 printed the same in every run of both modes; 127 differed in 1 of 200 as shipped and 2 of 200 checked, its "async 5 / pending 1" (the load flake on record: 3 to 7 of 400 on main). The self-host check at cb43eb9 (`bash tools/selfhost-check.sh
    CS2WASM_INLINE_READERS=80 CS2WASM_NO_STORE_FORWARD=1`, new: tools/selfhost-switch.sh, then tools/selfdet.mjs on
    four programs): the self-hosted compiler built in 246 s both ways, 32,970,943 bytes against 32,969,222 with
    the two switches; hello world in 8.29-8.34 s against 8.25-8.35, exit 0, peak memory 272.0 MB both, the same
    module; **145_size_hello, 220_collections_grow_by_moving, 193_subtrees_replaced_and_moved and
    088_feature_sweep_2 each "self-1 = net, byte for byte"** (27,731, 276,608, 259,824 and 1,617,302 bytes; the
    self-hosted compiler 8.1, 18.7, 16.8 and 79.6 s). `bash tools/field-hoist-proof.sh` with the setter inlined:
    every condition but `volatile` and `machine` changes a line, as before. `bash tools/cold-exits-proof.sh`:
    above. On the desk, with `CS2WASM_COLD_EXITS=1`: the suite's 21 tests whose names have 19 or 20 in them pass
    both ways. **Not checked or not done:** the suite and the stress runs with
    ColdExits on beyond that (it is off by default; the suite at c459a81 ran with it on, as first built, without
    the second copy of the loop and the counter); the side branch claude/churn-5-free-inline (no suite); the
    scenes on a quiet machine (every figure above is from the shared desk, with its ranges); a program with
    threads for the two changes that are in (the stored value read back is the pass's own proof, which holds for a
    field another thread may write only as far as any plain read does; the suite's thread tests pass).

**A3.6 Programs with threads: the measurement, and the thread escape analysis made to hold (2026-11-23, branch
claude/threads-1 from claude/round-9 d430594). [run]** SPEC 1 asks for threads that are worth using and for every
program to be faster than under NativeAOT; until now every speed figure was of a program without threads, and the
optimizations of the last days are switched off in one with.

*The measurement.* `node tools/frames-threads.mjs` (sources in tools/frames-threads; `tools/frames-threads-quick.mjs`
for one module without NativeAOT's side): tools/frames's five scenes on the main thread while one worker waits on a
monitor (idle_*; aidle_*: the scene's work handed over as an `Action`), N threads each with a particle system or a
scene tree of its own made on that thread (pchurnN, ptreeN, N = 1, 2, 4, 8), 64 work items a frame made on the main
thread and handed to N workers through a queue under a lock, their results back through another (sharedN), and the
churn's and the tree's parts each timed alone on a worker (parts1, tparts1). Each against the same work in a program
without threads, cs2wasm's and NativeAOT-LLVM's. **NativeAOT-LLVM's WebAssembly with threads does not link here**:
`-p:WasmEnableThreads=true` reaches the linker, which refuses `--shared-memory` because the package's runtime
libraries are built without atomics (`allocheap.cpp.o`); so the comparison is with NativeAOT without threads, said
on every line. Median frame, ms, the desk, three runs, lowest (before: the base's compiler on the same sources):

| scene | NativeAOT, no threads | cs2wasm, no threads | threads, before | threads, now | now against NativeAOT |
|---|---|---|---|---|---|
| churn, one idle worker | 0.050 | 0.068 | 1.99 | 0.100 | 2.0x |
| collections, idle | 0.007 | 0.006 | 0.065 | 0.012 | 1.8x |
| strings, idle | 0.008 | 0.007 | 0.032 | 0.013 | 1.65x |
| tree, idle | 0.034 | 0.046 | 1.21 | 0.120 | 3.6x |
| bigdrop, idle | 0.003 | 0.003 | 0.031 | 0.009 | 2.8x |
| private churn, 1 worker | 0.048 | 0.070 | 2.33 | 0.107 | 2.2x |
| private churn, 4 workers | | | 3.12 | 0.116 | 2.4x |
| private churn, 8 workers | | | 3.15 | 0.158 | 3.3x |
| private tree, 1 worker | 0.006 | 0.009 | 0.160 | 0.022 | 3.6x |
| private tree, 4 workers | | | 0.199 | 0.034 | 5.6x |
| private tree, 8 workers | | | 0.246 | 0.056 | 9.2x |
| shared, 1 worker | 0.102 | 0.071 | 0.170 | 0.113 | 1.1x |
| shared, 2 workers | | | 0.231 | 0.081 | 0.8x |
| shared, 4 workers | | | 0.405 | 0.264 | 2.6x |

Worst frames now against NativeAOT's without threads: below it in churn (0.57x), collections (0.49x), bigdrop
(0.30x) and the private churn (0.53x to 0.88x); above in the tree (17.4 ms against 3.7: the whole tree rebuilt,
35.3 before), in strings in one of the two forms (0.58 against 0.30 ms; 0.23 the other), and in the private tree
(3.5 to 11.3 ms against 2.9). Memory 16 MB (32 to 48 with four and eight workers) against NativeAOT's 59 to 71.
Scaling of private work (the work of N threads in the time of one, N the ideal): churn 1.94, 3.23, 5.28 at 2, 4,
8; tree 1.89, 2.44, 3.20 (before: churn 1.98, 3.40, 6.4 of a frame thirty times as long; tree 1.99, 2.76, 4.1).
The shared scene's medians move between 0.11 and 0.20 ms from run to run on one build: the wake of a worker is
most of its frame, and four workers for 64 small items are slower than two.
The parts on a worker, us (before -> now; without threads; NativeAOT): 20,000 particles made 1,073 -> 296 (194;
159), a pass over them 1,450 -> 130 (112; 110), let go 315 -> 316 (40; 4); a tree of 3,906 built 2,600 -> 1,638
(270; 42), walked 555 -> 15 (15; 17), 100 subtrees replaced 2,350 -> 1,541 (316; 33), dropped 204 -> 195 (59; 0).

*What the cost was.* Not the atomic counts as such. Sharing.cs decides at compile time which types' objects another
thread can reach, and code for the others is as without threads; for the scenes' program it answered **every type**
(156 of the 155 ids its count is taken against). Each cause was found by asking the analysis why (CS2WASM_SHARING_DEBUG=1; new:
CS2WASM_SHARING_ADD and CS2WASM_SITE_ADD print where a source was recorded) and each now says where its values come
from instead of "any object of the type"; each has its switch:
- a struct's place handed to `Unsafe.Add`, `Unsafe.Subtract`, `Buffer.Memmove` (and inside those): the ref stays of
  the struct's type, a copy adds nothing to what its fields hold (the values of every AsyncLocal were any object).
  CS2WASM_NO_SAME_TYPE_REFS.
- a slot whose every compiled store stored null (`Thread.StartHelper._startArg`): nothing. CS2WASM_NO_NULL_SITES.
- what stores by address can leave in an array (Arenas.AddRefStores gave any object of the element type to every
  array): the sources of the stores through refs of the type; for the runtime's `Array.SetValue` and `Array.Copy`
  across element types, the types those values can be (Sharing.PtrIds: what `Cs2WasmArrays.GetBoxed` returns is an
  element of some array or a box of a value array's element type), of which each array takes its own. Kept as a
  set of types: the walk's own filters are dropped after four at a node. CS2WASM_NO_BY_ADDRESS_SOURCES,
  CS2WASM_NO_PTR_IDS.
- a closure's slot of a captured local whose every write the declaring body shows, and of a parameter no code
  writes (an `object gate` a thread's lambda captures was any object). CS2WASM_NO_DISPLAY_SOURCES.
- an array made with no element (`List<T>.s_emptyArray`, a static every thread reads): the type can be reached,
  nothing through it; and such a type has no slot another thread could read or store, so reads and stores of a
  worker's own `T[]` are plain (Sharing.MaySlotsBeShared against MayBeShared: a count asks the second).
  CS2WASM_NO_EMPTY_ARRAYS.
- a static is a root only if code a worker thread can run names its slot (Driver.WorkerStaticSlots, over what
  `__thread_entry` reaches); the engine thread's own statics are read with a plain load, borrowed from, and read
  once by a loop, as without threads. CS2WASM_NO_ENGINE_STATICS.
- a delegate made of a lambda is a source that names the place in the text (Sharing.LambdaTag): reaching it
  reaches that lambda's closure, not every closure a delegate of the type was made with; and a lambda is code a
  worker can run only if a delegate of it, or any delegate of its type, can be reached by another thread, or code
  a worker runs makes it. Without this, handing the main thread's work over as an `Action` in a program whose
  workers invoke an `Action` put the idle scenes back at 1.96 ms a frame of churn. The roots and the worker's code
  grow together in rounds (two here). CS2WASM_NO_LAMBDA_SITES.
Now 113 of 155; the scenes' own types (the particles, the entities, their lists, each worker's world) are no
other thread's, and 17 statics are the engine thread's.
Then, with the types known, what still cost (each found by timing the parts, not by reading code: field loads read
once by a loop and the plain counts written out in loops were built on a reading, measured at no gain and taken
out again):
- `foreach` over a `List<T>` counted each element: a walk over a tree took the group's locks at every child (30x).
  It borrows the list and each element when neither the list nor its array can be reached by another thread.
  CS2WASM_NO_LOCAL_LIST_LOOPS.
- with threads every release reaches `Rt.PendDestroy` (the thread's list of objects that wait for the outermost
  destruction), whose raw store the store summary took for a store of an array's element: nothing was borrowed
  across any call that may release, in any program with threads. The same for `Rt.RegionExchange`, the exchange
  of every store in its region. Both are the runtime's own now (StoreSummary.Internal), as Rt.Alloc and Rt.Free
  are. (No switch: it is a correction of what the summary takes for a store.)
- a getter that returns a field (List<T>'s indexer) returned it counted; borrowed now when `this` and every
  object on the way are no other thread's. CS2WASM_NO_LOCAL_GETTERS.

*A fault of the analysis, found by its test.* Sharing.Analyze took the types a pointer store can leave in an array
with the filter of the visit that reached the array's slot first; a later visit with a wider filter was refused as
seen. Test 278 (a `Gem` stored by `Array.SetValue` into an `object[]` a static holds) ran a worker's plain count on
it: the check stopped it, the build without the check hung. Now once for each filter.

*Tests.* 275 to 279 (the engine thread's statics; captured locals; arrays of no element; `Array.Copy` and
`SetValue`; private work and handed results), each racing, each printing the same whatever the interleaving.
`bash tools/thread-escape-breaks.sh`: each compiled with CS2WASM_SHARE_CHECK=1 (a plain count of an object another
thread made stops the program) prints the same; with its rule deliberately weakened (CS2WASM_EXP_ALL_ENGINE_STATICS,
_DISPLAY_FIRST_ONLY, _EVERY_ARRAY_EMPTY, _NO_PTR, _LAMBDA_SITES_UNREACHED, _NO_LAMBDA_RUNS, CS2WASM_SHARING_NONE)
the check stops it; with the rule off it prints the same. The two borrows (the list's loop, the getter) rest on the
list being no other thread's, which CS2WASM_SHARING_NONE breaks; a weakened form of their own (every list's
foreach borrows) was not made to fail: an element another thread lets go of stays until the borrowing thread's next
safe point, and a safe point inside the loop is a store the borrow is refused for.

*Programs without threads are compiled as before*: 145, 220, 193, 201 and tools/frames's module byte for byte the
base compiler's, as shipped and checked (hello world 27,784 bytes by the command line, the same).

*Not done, in the order of what it costs.*
- **The groups with threads.** A tree's build and a subtree's replacement are 5 to 6 times the program without
  threads (1,638 against 270 us; the whole tree's rebuild is the idle tree's worst frame, 17 ms), and private
  trees scale 3.2 at eight threads. The node path and the fused steps are off with threads; a store into a member
  takes LinkLock's walk, and a member's first and last reference from outside the runtime lock, which every
  thread shares. What eight private trees wait for, by the profile (`node tools/profsum.mjs`, `profcallers.mjs`):
  `Rt.StripeWait` is the first function, 14.5% of all samples, called from LinkLock under ArenaLink under the
  build and the lists' Add: objects that are no members take the stripe their address gives, of 256, so threads
  that share nothing meet on stripes. (Tried on a reading and measured at nothing: the skip of the pinned empty
  array every new list starts with, which a program without threads has in ArenaLink, allowed with threads.)
  Leaving the groups' stripes out when no cycle-capable type can be reached by another thread was
  built and measured (build 1,790 -> 1,170 us with every stripe off): never the case as a whole (Thread, its start
  helper and the thread's closure are cycle-capable and reached in every program with threads), so it needs the
  answer by group of types (the runtime's SccOf), a change in GroupLocks.cs, the other session's file tonight.
- **Strings and other types every thread can reach by their type.** The churn's drop is the atomic release of each
  particle's string (316 against 40 us) and its making the locked read of a `string[]` element; the type is one
  another thread can reach, the objects here are not. This needs the answer by where an object is made, not by
  its type.
- **A delegate with no tag** (a method group's, a local function's, a clone made by `Delegate.Combine`) is still
  any delegate of its type: every lambda of the type is then a worker's code and every closure reached.
- **The threaded self-hosted compiler** (docs/FINISH.md check 5): not touched; Roslyn's one group is one lock.
- `tools/thread-board-hang.cs`: four workers storing small trees over the elements of a list a static holds
  (through its span) while the main thread's foreach reads them hangs in about one run of six, with the base's
  compiler too (2 of 12). Not looked into: the groups' locks.
- claude/load-flakes-1's layout of the statics by cache lines (5837122) is on this branch (cherry-picked, 43f1dd1).

*A hang of the base's that the new tests met.* The homelab's suite at 170ac22 stopped test 277 as shipped at the
time limit, nothing printed. On the desk (`node tools/stress.mjs`, 8 at once) the test's first form hangs in 7 of
300 runs, built by the base's compiler d430594 in 5 of 300, and with each of this branch's rules off in 2 to 8 of
300: not this branch's code. The shape: threads that read and replace, unsynchronized, the elements of an array a
static holds, whose class has a finalizer (`tools/thread-notes-hang.cs`). Variants built by the base's compiler,
400 loaded runs each: the notes alone 5 hangs; no finalizer 0; the read and the store under a lock 0; the store
without the read 0. In 600 runs of the notes alone, 14 hangs and one run that ended with a note never finalized;
with CS2WASM_DEADLOCK_DUMP=1 the hung runs say nothing. Test 275's first form had the shape too (1 of 300). Both
tests take a lock around the shared objects now (what they test is the counting, not the race; test 139 has the
racing stores): 500 loaded runs a mode each all the same, 276, 278 and 279 300 each. Not looked into further.

*What the machine gives.* N threads of arithmetic alone (scenes spinN, beside the private scenes' scaling in the
tool's output): about 1.9, 3.3 and 6.3 at 2, 4 and 8 threads on the desk (24 logical processors, other sessions
at work): the private churn's 5.3 at eight is most of it, the private trees' 3.2 half.

*Checks* (the code is 170ac22; later commits are tests, tools and these documents). The homelab: the suite, checked
240 passed, 1 failed, 2 known gaps, as shipped 239 passed, 2 failed, 2 known gaps (961 s): 088's timer line in
both (the known one under load) and 277's hang above; the self-host check (`tools/selfhost-switch.sh`): the
self-hosted compiler built in 340 s (33,404,357 bytes), hello world in 8.7 to 9.4 s, peak 272.0 MB, self-1 = net
byte for byte on 145. The desk: `tools/thread-escape-breaks.sh` as said above, on the final tests;
`tools/thread-share-check.sh` (every conformance test that is a program with threads, 59 of them, once as it is and once
under the share check): all 59 print the same. Queued on the homelab when this was written, and in docs/FINISH.md's entry when in:
`tools/stress-cycles.sh` (127, 139; 139 without the engine's stack checks), `tools/stress-threads.sh` (275 to
279, 187, 131), `tools/thread-scale.sh` (the threaded self-hosted compiler, not yet built with this analysis),
`tools/selfdet-set.sh` (220, 193, 088 through the self-hosted compiler), 088 and 275 to 279 alone.

*On the merge with main 4cf95fe (612cd56; the session that took over, 2026-11-23 04:40 to 07:45).* Main's code
since the base (claude/churn-5: a list's setter in loops, the stored value read back, ColdExits off) merged
without a conflict in code. Programs without threads are compiled byte for byte as by main's compiler 4cf95fe:
145 (27,786 bytes), 220, 193, 201, 208, 209 and tools/frames's module (230,417 bytes); so `tools/frames.mjs` is
main's: `--runs 3 --js-young 32` on the desk, medians churn 1.23x, collections 1.02x, strings 0.89x, tree 1.61x
(worst 1.21x: tree's spikes above NativeAOT's, as on main), bigdrop 0.97x; the mean frame 0.74x NativeAOT's.
`node tools/frames-threads.mjs --runs 3 --js-young 32`, the desk, main's compiler (before, two runs) and this
branch's (after, three runs) in turn; median frame, ms, the range over the runs (NativeAOT-LLVM without threads):

| scene | NativeAOT, no threads | cs2wasm, no threads | threads, main | threads, this branch |
|---|---|---|---|---|
| churn, one idle worker | 0.050-0.052 | 0.061-0.063 | 2.016-2.030 | 0.096-0.100 |
| collections, idle | 0.006 | 0.006 | 0.039-0.040 | 0.012-0.013 |
| strings, idle | 0.008 | 0.007 | 0.021 | 0.013 |
| tree, idle | 0.033-0.035 | 0.046-0.049 | 0.937-0.986 | 0.122-0.126 |
| bigdrop, idle | 0.003 | 0.003 | 0.028 | 0.009 |
| private churn, 1 / 4 / 8 workers | 0.049-0.050 | 0.066 | 1.90 / 2.49-2.59 / 3.09-3.27 | 0.104-0.108 / 0.110-0.114 / 0.160-0.161 |
| private tree, 1 / 4 / 8 workers | 0.006 | 0.009 | 0.160 / 0.174-0.198 / 0.243-0.259 | 0.022-0.023 / 0.034-0.042 / 0.056-0.057 |
| shared, 1 / 2 / 4 workers | 0.104-0.106 | 0.072 | 0.174-0.191 / 0.101-0.176 / 0.260-0.262 | 0.111-0.170 / 0.082-0.170 / 0.309-0.316 |

Worst frames, this branch: idle churn 0.29 to 0.52 ms (NativeAOT's 1.24 to 1.79), idle tree 16.3 to 23.2 ms
(3.7 to 3.8; main's 21 to 32), private churn at eight 0.8 to 5.5 ms (main's 10 to 39). Scaling (the work of N
threads in the time of one): private churn 1.75-1.86, 3.06-3.22, 4.76-5.38 at 2, 4, 8 (main 1.78-1.88, 2.88-3.05,
4.67-4.70, of frames twenty times as long); private tree 1.57-2.00, 2.19-2.75, 3.04-3.55 (main 1.85-1.98, 2.73,
4.14-4.20); arithmetic alone 1.73, 3.43, 5.64. The parts on a worker, us (main -> this branch; NativeAOT without
threads): 20,000 particles made 1,074-1,088 -> 294-303 (159-161), a pass 1,445-1,465 -> 125-129 (103-109), let go
316-320 -> 315-327 (4); a tree of 3,906 built 2,582 -> 1,648-1,703 (42), walked 558 -> 15 (17), 100 subtrees
replaced 2,317-2,335 -> 1,553-1,601 (33), dropped 202-211 -> 201-204 (0). The shared scene with four workers
looked slower here than on main (0.309-0.316 against 0.260-0.262); timed alone in turn it is not a difference of
the builds: main 0.256-0.304, the merge 0.242-0.331, the branch before the merge (3f73a93) 0.179 and 0.313, from
run to run of one build (its frame is mostly the workers' wake). Against NativeAOT-LLVM without threads (its build with threads does not link
here), at or below its median only in the shared scene with one or two workers in their best runs; everywhere
else 1.7x to 9x: what is left is the list above (the groups with threads, types reached by their type).
Checks on the merged code (the desk): `bash tools/thread-share-check.sh` 61 programs with threads (main brought
217 and 218), all the same under the share check; `bash tools/thread-escape-breaks.sh` every test the same under
the check, every weakened rule (seven) stopped, every rule off the same; `node tools/suite.mjs --shards 2` 27x,
145, 208, 209, 260, 139 pass both modes; 300 loaded runs each of 275 to 279 (8 at once), all the same; 200 loaded
runs each, this branch's compiler and main's in turn: 139, 218, 215b, 131, 127 all the same on both, 187 3 of 200
on both (`rounds: result 3`, the open leak of the row in docs/FINISH.md). The homelab's jobs: docs/FINISH.md's
entry.

### A4. Plugins (goal 3)

**A4.1 Generated code cannot reach private members. [read]**
- Source: `AddSource` builds a separate compilation that references the game (`Compiler/Plugins.cs:552-559`), with
  default access checks; field `[UnsafeAccessor]` is refused (`DynamicDelegates.cs:103`).
- Needed by: gates, solvers, per-tick steps, typed get/set (Invisible API 3.1, 7, 8.8).

**A4.2 No hook sees or changes a value. [read]**
- Source: hooks take `()`, `(int)`, `(object)` or `(object, int)` (`Instrumentation.cs:106-119`).
- Needed by: sending a branch outcome and following the told outcome (3.3), redirecting a draw to the private
  random stream (3.2), treating an `await` result as private (4), wire rounding (3.4), field limits (17.3).

**A4.3 Write hooks are per member, not per write. [read]**
- Source: `WriteHooks` is keyed by member symbol with one tag (`Compiler/Plugins.cs:561-568`). The design classifies
  each write statement (3.3). `InstrumentBefore` is per site but runs before and has no receiver.

**A4.4 Collection entries are invisible. [read]**
- Source: `Accesses` records field, property and event references only (`Compiler/Plugins.cs:193-204`);
  `Scores[p] = 1` is a read of `Scores` and a call. `Dictionary<Player, T>` is the design's per-player mechanism
  (5, 6, 8.2).

**A4.5 Analysis and instrumentation disagree. [read]**
- Source: `KindOf` counts `ref`/`out` arguments and deconstruction as writes (`Compiler/Plugins.cs:216-232`);
  `WriteTarget` hooks only `=`, compound, `++`/`--`, `??=` (`Instrumentation.cs:24-31`). A write classified as an
  interaction can go unmarked: a wrong game, not just bandwidth.

**A4.6 Closed forms are too weak. [read]**
- Source: one affine rule per symbol with exactly one write, outside branches, no calls
  (`Compiler/Plugins.cs:428-509`); loops found by block ordinal. Fails the design's own examples: the spawner timer
  (two writes), velocity/position (coupled), `Lerp` (a call), piecewise solving. The formula is a string. **Done: the solver, version 6 (2026-11-20), Step 9.**

**A4.7 Suppressed clones leak effects and cannot be called. [read]**
- Source: virtual calls go to the unsuppressed stub before the clone test (`Calls.cs:564`); lambdas are not cloned;
  the clone exists only as a named export (`Compiler/Plugins.cs:579-584`).

**A4.8 Conditions, aliasing, unsure spots. [read]**
- Conditions are syntactic and per method (`ConditionsOf`, `Compiler/Plugins.cs:235-257`): no early returns, no
  call-path conditions.
- `MayAlias`/`PointsTo` are type compatibility (`Compiler/Plugins.cs:512-548`), not the ownership analysis.
- Delegate calls are absent from the call graph; reflection warnings never reach the plugin.

**A4.9 Plugins run after code generation. [read; soundness part inferred]**
- Source: `Driver.cs:1134-1136`; hooked methods are recompiled (`Compiler/Plugins.cs:63-76`). Escape analysis never
  sees hooks and its memo is not invalidated, so a hook that stores its receiver may be given a frame-allocated
  object from a caller that was not recompiled.

**A4.10 "Potential needs" cannot be met by hooks.** Each new need is a new hook. SPEC 7.1 describes an LLVM-style
pass over the compiler's lowered form; no lowered form is exposed because none exists (A2.2).

**A4.11 Almost untested.** One test plugin; before-hooks, read hooks, statement hooks, `MayAlias` and the
control-flow graph are never called (`tests/Cs2Wasm.Tests/ApiChecks.cs:390-479`).

### A5. Spec conformance

| # | Problem | Source |
|---|---|---|
| A5.1 | "Every program compiles" is not met | about 156 `throw new CompileException` sites become CW0101 (`Api.cs:132`, `Driver.cs:1298-1309`); CW0100 for missing library members (`FunctionCompiler.cs:121-123, 2551`). Await inside `?:`, `??`, `?.`, `&&`, `\|\|` is one (`StateMachine.cs:1066`) |
| A5.2 | Missing capabilities are build errors, not run-time exceptions (SPEC 8) | `[MissingCapability]` makes reaching a member CW0003 (`Driver.cs:783-789`, `FunctionCompiler.cs:2530-2551`); about 190 uses |
| A5.3 | A silent trap with no diagnostic | `FunctionCompiler.cs:2544` |
| A5.4 | Two-pass filters depend on the program's own source | `FindFilters` scans `Inputs` only (`Filters.cs:22-30`) |
| A5.5 | Generic sharing is merge-after-the-fact only | `Types.cs:252-288`, `WasmModule.cs:470` |
| A5.6 | Stack traces assume V8's stack text | `host/engine.mjs:141-186`; each throw captures and parses a JS stack |
| A5.7 | Secure randomness is a PRNG from a 32-bit seed [run] | `Boundary/Interop.Sys.cs:27-50`; host returns 12345 (`host/engine.mjs:177`) |
| A5.8 | String hash seed comes from the host import (SPEC 7.3: fixed seed) | upstream `Marvin.cs` seeded through `random_seed` |
| A5.9 | Identity hash depends on scheduling under threads, unmarked | `Runtime.cs:845-855, 1030-1046` |
| A5.10 | Output depends on about 60 environment variables | A6.1; `CS2WASM_NO_CANON` turns NaN canonicalization off (`FloatStores.cs:16`) |
| A5.11 | Pipeline order differs from SPEC 2 | `Driver.Run` (`Driver.cs:1118-1185`): reachability, lowering, analysis and emission are one drain |
| A5.12 | A program with `Main` and no export compiles to an empty module, silently [run] | roots are exports only (`Driver.cs:1344-1346`) |
| A5.13 | Keep-whole attribute is type-level only; CW0201 suppressed after `Assembly.GetTypes` | `Reflection.cs:168-176` |
| A5.14 | Export parameter types unchecked; diagnostics without locations | `Driver.cs:1344-1346`; CW0101 throw sites that pass no location |
| A5.15 | Hand-written code beyond the runtime boundary | 63 modified Mono runtime files; 18 `edit` lines on non-boundary upstream files (`CoreLib manifest.txt:40-56`); `ManagedZLib.cs`, `EnumFormat.cs`, `TextScan.cs` |
| A5.16 | No Wasmtime path; no check that self-hosted output equals .NET-hosted output | host needs JS imports and workers; `ApiChecks.cs:101` compares same-host only |
| A5.17 | Proof 4 and proof 5 not met | self-host: traps, 1 GiB, hang on more than one core (`proof5_hello2d/README.md`, "What stops proof 5"); browser build of the game fails; hello world 23-30 s in the browser. Proof 5's engine half met 2026-11-19 (step 12.5 status); **2026-11-21: proof 5 met in the browser** (stream D below): the game built in headless Chrome by the self-hosted compiler, its log equal to the oracle's; the threaded toolchain ends on GodotSharp on 1 and 4 cores |

### A6. Structure

**A6.1 Configuration is ambient.** 84 `CS2WASM_*` names read by about 105 `GetEnvironmentVariable` calls in 30
files; 43 frozen in `static readonly` fields; 34 `NO_*` kill switches and 3 `EXP_*`. `CompileOptions`
(`Api.cs:35-54`) exposes none, not even `NO_THREADS` (`Driver.cs:142`). Source: each optimization was added with its
own switch for bisection and none was retired.

**A6.2 The library is not reentrant.** `Inliner.Module` (`Inliner.cs:27`), `Emitter.RcSites`/`RcCauses`
(`Emitter.cs:50-54`), `[ThreadStatic]` ambient parameters in `Locals` (`WasmModule.cs:178-193`), `Driver.s_libModels`
never evicted (`Driver.cs:872`), `Library.s_built` and public static sets (`Library.cs:65-71, 128-150`). The library
also shells out to `git` and a Godot editor (`Library.cs:405-532`) and writes to `Console.Error`.

**A6.3 Two god classes.** `FunctionCompiler` is 34 partial files (about 924 KB), `Driver` 19. Analyses have no
result objects: 73 string-keyed dictionaries on `Driver`, `HashSet<SyntaxNode>` (`OwnedParams.cs:16`), placeholder
calls patched later (`Arenas.cs:52-53`).

**A6.4 Passes recognise functions by name.** `"rt.nonnull"`, `"rt.release"`, `"static init "`,
`"Cs2Wasm.Runtime.Rt.AllocObject("` (`WasmModule.cs:195`). A rename silently disables an optimization.

**A6.5 Name special-casing.** 426 `"System.…"` literals and 121 `.Name == "…"` comparisons in `Compiler/`; no symbol
table. Workarounds among them: `foreach` over `List<T>` reading private fields (`FunctionCompiler.cs:1503`),
`EqualityComparer<T>.Default` replaced by name (`Calls.cs:46-50`), thread starters by name (`Scheduling.cs:38-47`),
capability guessed from substrings of type names (`FunctionCompiler.cs:2493-2567`).

**A6.6 Hand-assembled runtime glue.** Positional locals and literal offsets (`RuntimeGen.cs:703-715`); 45 literal
offset loads beside `Types.ObjectHeader`; `WasmModule.Encode` is 298 lines of pipeline, switches and encoding.

**A6.7 Stale comments.** Limbo and the shared bit are gone but still described (`FieldBorrows.cs:383-388`,
`SharedCounts.cs:7-13`, `Runtime.cs:1079-1081`); `Rt.Share` calls compile to drops.

### A7. Tests, evidence, repository

- End-to-end only: no unit test for any analysis or pass; none of the 34 kill switches is ever exercised.
- No Roslyn or dotnet/runtime test programs (SPEC 10); no program for stack overflow, out of memory or
  `SynchronizationContext`; Godot checks SKIP, not FAIL, when packages are missing (`GodotChecks.cs:36, 41`).
- No CI. No LICENSE file (SPEC: MIT), and no notice for the vendored upstream and fdlibm code.
- README describes `src/Cs2Wasm/Lib`, which no longer exists; prerequisites (emsdk 3.1.56, Godot editor, the git
  fetch of dotnet/runtime) are undocumented; `tools/selfhost.mjs:68` depends on untracked `tests/out/up/bin`.
- `host/web/cs2wasm-web.mjs` re-implements the import surface of `host/engine.mjs`.
- Commit subjects are paragraphs (median 290 characters).

---

## Part B. Plan

### Progress

- **Step 1: partly done (2026-11-18).** `CompileOptions.Checked` (`--checked`; the suite's default, `--unchecked`
  for the shipped form): freed memory is filled with a pattern and never used again, and every function is
  type-checked by the validator, whose findings are build errors. The validator learnt the 128-bit instructions.
  Still to do: the count of blocks left at exit (1.2; tests 119 and 121 count live objects through finalizers
  instead), CI (1.5).
- **Step 2: done except A1.4 (2026-11-18).**
  - A1.1 fixed: a local or parameter assigned inside an expression, or whose address is taken, is no longer a stable
    borrow (`_nestedWrites`, `WrittenDuringUse`). Test 118.
  - A1.2 fixed: an object its finalizer stored somewhere lives on and is not finalized twice (`Rt.FinalizedBit`,
    cleared by `GC.ReRegisterForFinalize`). A member of a dying group brought back keeps the whole group, which is
    then never freed: step 7 replaces this. Test 119.
  - A1.3 fixed: the weak table's busy mark is per thread.
  - A1.5 fixed: a field returned by reference counts as written after construction.
  - A1.6 fixed: a function takes its frame through one small function, `rt.frame`, which traps when the stack has
    not that much left and clears the frame (`Wasm/Frames.cs`: the last rewrite before a module is encoded, of the
    twenty-odd bytes each such function began with); `stackalloc` checks its size. For every thread, with the
    stack where it was and 1 MiB as before. Modules are 1 to 4% smaller for it (hello world: 27,629 bytes; the
    bench: 333 KB, 341 before) and no slower (bench 0.95x, micro in the baseline tier 2.29x). An earlier form of
    this fix put the stack under the data and shrank it to keep addresses short: the owner called that a hack, and
    it is gone. Worker stacks are still 1 MiB whatever `StackSize` says. Test 120.
  - A1.4: no fault found (2026-11-18). Test 132 makes 70,000 stores chosen by a fixed pseudo-random sequence
    among objects with twenty-two reference fields (more than a member keeps bits for), arrays, lists, a tuple, a
    dictionary and an object field, a third of them in a group that statics keep alive: what is reachable sums
    to what .NET's does all the way, checked and as shipped, and nothing is left at the end. Where there are no
    bits, `ArenaUnlink` goes by the counts, and that is right as long as every reference between two members of
    one group in a slot a cycle can close through is an inside one: a store links it, and an object that joins
    has its slots linked. Bits for every slot (7.B2) would let a parent link be an array's element or a late
    field; it is not needed for correctness.
  - **Found by the same test, and fixed: a finalizer that stores a reference into its own object while its group
    is destroyed** (`F19 = F2;` in `~Wide()`). The members had count zero then, so the stored one looked like a
    new plain object and the two were joined into a new group over the words the destruction was using: a hang,
    or a read out of bounds, checked and as shipped. Now a dying member joins no group; while the finalizers run
    every reference to a member is counted (those put back in their slots too) over a bias that keeps a release
    from destroying it twice; afterwards the references the dying members hold to each other come off the counts
    (their slots are visited, as when they were let go), and a count that is left is a reference from where the
    program can reach it: then they all live on, as before, with their fields as the finalizers left them
    (`Rt.FinPhase`).

- **Step 4, first part (2026-11-18): which types another thread can reach, decided when compiling; their counts
  atomic, the others plain.** Measured first: a program is in thread mode as soon as a thread start is reachable,
  and `await Task.Yield()` or `await Task.Delay(..)` makes one reachable (the thread pool), so most async code
  pays it. The bench compiled so (a `Task.Run` it never runs) took 18.4, 61.0, 50.1, 52.2, 12.2, 22.8, 5.4, 45.0,
  12.9 ms (dictionary, linked_list, linq, list_sort, nbody, shapes, strings, tree, word_count) against 7.1, 3.5,
  12.9, 37.8, 1.6, 1.0, 2.8, 8.2, 2.5 without threads; with every count plain it would still take 32 ms for tree.
  - `Compiler/Sharing.cs`: at every store into a field, an element, a static, a struct's field, at every argument
    and every `return`, where the value comes from (a new object of a type, a parameter, a result, a field, an
    element, a local's assignments, an `out` parameter's stores, a lambda's delegate's invocations, a
    reflection thunk's member), else its static type. The types another thread can reach are what the statics and
    the thread a thread start hands over lead to, through what those types' slots hold: a slot holds its declared
    type's objects only; a `ref T` lands in a T slot (Unsafe.As recorded); an element read is of the arrays its
    expression can be. A parameter's sources count only if every call to the method is one made from recorded
    arguments (marked on the call instruction); generated code, stubs, delegates, the host make it its type.
  - `Compiler/TypedCounts.cs`: retain, release, the read that counts and the store take the static type at about
    a hundred places; once every function exists each becomes the atomic function or a plain one (rt.retain_plain,
    rt.release_plain with a leaf's release written in place, rt.store_ref_plain; a read from an object no other
    thread can reach is a plain read). `CS2WASM_SHARE_CHECK=1` makes the plain counts trap on an object another
    thread made: the 33 tests that use threads, tasks or Parallel pass with it, and with every type taken as
    thread-local (`CS2WASM_SHARING_NONE=1`, a test of the check) they trap.
  - The bench in thread mode now: 18.1, 33.3, 36.0, 51.1, 2.7, 3.4, 5.4, 25.5, 11.6 ms. Of 413 types, 239 are
    reachable by another thread, none of the bench's own. Without threads nothing changes (0.94x NativeAOT,
    16.0 MB, hello world 27,943 bytes).
  - What is left of thread mode's cost, measured: `string` is a type another thread reaches (the culture data is),
    so every string count is atomic (word_count, dictionary); `List<int>` too (the runtime's weak-reference table
    holds one): a per-type answer cannot separate an object from others of its type. Field borrows, the
    allocator's single-thread path and the loop that frees a list are still off in thread mode, and a store into a
    cycle-capable object takes the runtime lock. Next: borrows for objects no other thread can reach (they are
    decided while compiling, before the answer is known: a second pass or deferred borrows), the runtime's own
    structures in types of their own, and the allocator.
  - **Second part (2026-11-18): a program with threads is compiled again, knowing the answer.** Whether a value is
    counted or borrowed shapes the code around it, so it cannot wait for the end of the compilation that finds out
    which types another thread can reach. `Cs2WasmCompiler.Compile` compiles such a program once more with the first
    answer (`Driver.SharingPrior`); where that says no other thread can reach an object of the static type, a field
    or element of it is read as without threads (no counting read), and borrowed from: locals that borrow a field
    (FieldBorrows.cs, LocalBorrows.cs), a field held for a call; the borrow's resolution needs no look at other
    threads' stores (`Driver.ThreadLocalBorrows`). Statics are never the thread's own (a thread's own static is).
    The new compilation's own answer must name no shared type the one it used did not, else it compiles again with
    both (three rounds; then once taking every object as shared). The answer goes from one compilation to the next as type keys, each with whether an object another thread can reach can be one: each compilation has symbols of its own (one that adds generated code, dynamic call sites or a Godot game's, builds its own program compilation). Cost: a threaded program's compile time, the
    bench's 18.3 s to 23.1 s. `CS2WASM_NO_SECOND_PASS=1` compiles once.
  - The bench in thread mode now: 17.2, 8.8, 15.0, 47.1, 1.6, 1.1, 3.7, 20.3, 7.1 ms (nbody and shapes as fast as
    without threads). The 33 threaded tests pass under the share check. Left: string and List<int> are shared types
    (dictionary, list_sort, word_count), and tree's 20 ms against 8.2: the allocator's thread path and the loop
    that frees a chain, still off in thread mode.
  - **A deadlock found on the way, older than step 4 (2026-11-18).** Test 102 timed out once in the suite; run in a
    loop it hung every time, also built with all of step 4 off (about once in five hundred runs of the test as it
    is). Paused through the inspector protocol: the engine thread held the runtime lock while it freed a group
    of objects that refer to each other, and one of them, a SafeHandle, ran its finalizer, which takes the wait
    subsystem's lock; the timer thread held that lock and waited for the runtime lock (a read that counts, of a
    group's member). .NET runs no finalizer under a lock of its runtime. Now, with threads, a group's
    destruction takes its members out under the lock and leaves the rest (finalizers, clears, frees) to a list of
    the thread's own, run when its outermost RuntimeUnlock gives the lock back (`Rt.RunDeferredDestroys`); the
    references cut for the finalizers go with it, in a block of their own, and a part's arena record is dealt with
    while still under the lock (it may be merged or freed by others before the rest runs: test 111 caught that).
    Test 135 is the loop: 150 rounds, every one hung before. `CS2WASM_DEADLOCK_DUMP=1` makes a long wait for the
    runtime lock throw, naming its holder.
  - **Thread mode's allocator and releases (2026-11-18).** Measured first: tree in thread mode spent 37% in the thread
    caches' exchange with the shared lists (a flush walked each list to its tail, a refill walked the shared list a
    block at a time: 2 MB of nodes freed, then built again). A shared list is now a stack of the batches threads gave
    back whole, so a batch goes either way in a few stores. AllocObject and FreeObject have the one-piece path on the
    thread's own list that single-thread has on its own. The exact releases (a sealed type's count test and a direct
    call of its destruction) are written out for the types no other thread can reach, after the sharing is known.
    The optimizer's count passes (Peephole, LocalsRc, Fresh) knew rt.retain and rt.release by name only and so missed
    every count of a program with threads (still the per-type placeholders then): they know them all now (Wasm.Rc).
    `rt.lock_exit` no longer runs the deferred destructions before the thread has its thread statics (AllocFirst:
    it read a word of memory that was not the list). Thread mode: tree 20.3 to 12.0 ms (7.8 without threads),
    linked_list 8.8 to 5.8, the rest a little faster; without threads unchanged (0.96x NativeAOT).
  - **The runtime's own types, and a thread's own pages (2026-11-18).** CS2WASM_SHARING_DEBUG named why the
    program's Dictionary<int, int> and List<int> were shared: the runtime's tables of object marks and weak slots
    were a Dictionary<int, int> and a Dictionary<int, List<int>> in statics. They are now maps in memory of the
    runtime's own (IntMap, ObjectMarks.cs), which share no type with the program. A thread now cuts its small blocks
    from pages that are its own and puts a block it frees back on its page, which counts its blocks out, and is cut
    again in order once they are all back, as the pages without threads (the lists by thread handed blocks out in
    the order they were freed: a tree made again after one was freed lay all over memory). A block another thread
    frees takes the old way. The inliner puts the plain counts into loops as it does rt.retain and rt.release; a
    list's node is freed in a loop for a type the last compilation found no other thread reaches. Thread mode:
    dictionary 17.0 to 7.3 ms, word_count 7.0 to 5.0, tree 12.0 to 9.5, linked_list 5.8 to 4.4. What is left is
    strings: string is a shared type in every program that has a static string (CultureInfo's, a program's own
    `static readonly string[]`), so every string's count is atomic; deciding that per object rather than per type
    needs the allocation site and the thread that reads a static, not yet done.
  - **Found by the share check, and fixed: two types' destructions taken for the same code (2026-11-18).** Test 088
    under `CS2WASM_SHARE_CHECK=1` failed one run in three: a plain count of the thread pool's TaskScheduler on the
    timer thread, or a hang. The destruction dispatch gives types whose destroy functions are the same code one
    case (`WasmModule.BodyKey`), and a call was keyed by its callee's order in the module; the per-type count
    placeholders are in no module, all of order 0, so a continuation's destruction and that of a thread-local type
    with references at the same offsets shared a case, and the continuation's scheduler was released with the
    plain count. A callee with no order is now keyed by its name. In the shipped build it was a count that could
    go wrong when two threads let go of the scheduler at once. 088 under the share check: 40 of 40 runs pass.
- **Step 7.1, isolated values (2026-11-19).** A store of a structure that a call has just built of new objects, none
  of which refers to anything but each other, into an object that existed before the call, closes no cycle: the
  structure reaches nothing older (C# evaluates the target of `a.F = M()` first). `Driver.IsolatedValue` finds such
  values: null, a `new` whose constructors bring no reference in, or a call of a static method with no parameter that
  can bring one in, whose locals holding its new objects get only isolated values and are used only as store targets,
  for their plain fields, in comparisons and in returns, which stores into its new objects only isolated values and
  stores them nowhere else (a recursion is taken as closed while it is decided). Such a store links through
  `Site.LinkIso`, which does not make its site general; on a site that is cyclic through other stores it is the
  general link. `CS2WASM_NO_ISOLATED=1` turns it off. Test 136: a subtree replaced under a long-lived tree, beside
  four near misses that must stay cyclic (a structure that refers back, one a static held while it was built, one
  whose field was handed out, one stored where parent links are), each in a class of its own: only the first class
  loses its cycle note, and every kind is freed as under .NET. Micro `tree_replace`: 193 us to 21 (NativeAOT 7, which
  leaves the old subtree to its collector).
- **Step 6.4, in part (2026-11-19): stores into a new object.** A field an object initializer sets (`new T { F = v }`,
  not `with`) on a new object whose constructors never write F (`Driver.CtorsNeverWrite`) is a plain store: no
  exchange and no release of an old value; on a site that can close a cycle the link of a store into a new object
  stays (a constructor's own store can have made the object a group's member: tests 090, 127 and 128 leaked
  cycles without it). Test 137 covers the fields a constructor, a base constructor or an initializer set, and `with`;
  it found that a field's initializer was not seen (`GetOperation` of a declarator is not its `= v`), in this and in
  the isolated values' check of constructors (`MemberInitializer`; test 136 has the near miss now). And `Wasm/Fresh.cs` follows the object its allocation's `local.tee`
  pushes (it began one instruction later, so whatever took that first push went unseen) and knows a field store whose
  address is in a local. Micro: `llb_build` 720 us to 642 (a list of 100,000 nodes of a cycle-capable type made and
  freed), `link_build_free` 297 to 279.

- **Step 3: answered from measurements (2026-11-17 and 2026-11-18); the bench fixes (3.1, 3.2) are still to do.**
  - Optimizing tier (what a game runs in), `tools/bench.mjs`: 0.95x NativeAOT's time, 32 MB against 71 MB.
  - Baseline tier (what a short run over many functions stays in: the self-hosted compiler), `tools/micro.mjs
    --baseline`: 2.32x on 2026-11-18. The engine runs the module as written there, and NativeAOT's module was
    optimized by LLVM first. Worst: replacing a subtree 38x, `foreach` through an interface 11x, dictionary set and
    get 4x, allocating and freeing a node 3 to 4x, a walk over a list or tree 1.7x.
  - The self-hosted compiler on `Console.WriteLine(42)` (2026-11-17, interleaved runs): NativeAOT's WebAssembly
    build 6.8 s; built without threads 12.6 s (1.86x); threaded 19.7 s on one thread, 8.0 s on 24. In the
    no-threads profile counting is 2.2 s of 14.6 s; the rest is flat over Roslyn's parser.
  - So for the self-hosted compiler: code quality in the baseline tier first (about 1.6x), thread mode second (12.6 s
    to 19.7 s), counting third (about 15%). The order of steps 4 to 7 stands. The build with counting and freeing
    switched off (3.3 c, d) was not made: the profile already bounds what it could show.
  - What the generated code showed (`CS2WASM_DUMP`): a walk over a list held by a static counted every node inside
    an exception region (fixed, below); a store of a new subtree into a new node goes through the cycle machinery
    (a check, `rt.store_ref`, a release of the old value that is known to be null) because any class that refers to
    its own type is cycle-capable.
- **Step 5.1: the spike is done (2026-11-18); the IR is built from the operation tree, not from Roslyn's graph.**
  Over the conformance sources (1,129 bodies) and the compiler's own (1,360 bodies) `ControlFlowGraph` built for
  every body with no failure, in 7 to 12% of the time the operation trees take.
  - (a) Flattened: blocks and declarations, loops, `if`/`?:`, `switch` and `switch` expressions, `try`/`catch`
    (regions), `?.`, `??`, `??=`, `using`, `lock`, `with`, object and collection initializers, interpolated string
    handlers, local functions.
  - (b) Left as operations: patterns, lambdas, `await`, `yield return`, tuples and deconstruction, compound
    assignment and increments, `dynamic`, collection expressions, ranges.
  - (c) Lost: `foreach` over an array or a string becomes `IEnumerable.GetEnumerator`, `MoveNext` and `Current`
    (64 of the conformance loops), and every `foreach` loses the fact that its element borrows from the collection.
  - Decision: (c) rules the graph out as the IR's source. The IR's own blocks give the same flattening (an
    `await` inside `?:` is then a block boundary like any other), with `foreach` lowered by the compiler as today.
- **Step 6, begun (2026-11-18).** A local borrows from a static as from a field (the stores of statics are noted
  for the scope question): `ll_walk` in the baseline tier 1.68x to 0.99x. Test 122.
- **Step 7.1, begun (2026-11-18).** A field only ever stored into objects that nothing else can reach yet
  (`Site.General` false: its general link is called nowhere) closes no cycle, like a field filled at construction:
  a component made of such edges alone is not cycle-capable (`CS2WASM_NO_FRESH_SITES=1` for the old rule). It
  takes one type out in the micro program (`Link`) and one of 40 in the bench. Trees and lists that are rewired in
  place, and anything that holds its own kind through a `List<T>` (an array site), stay cycle-capable: those need
  the isolated-value rule (a new structure that refers to nothing older, stored into an old one), the
  back-reference proof, and step 7.D.
- **Found on the way, not fixed:** a reference inside a struct-typed field of a class is stored without any link
  (`StoreToAddressFromStack`, the struct branch), so a cycle through such a field (`class A { (A, int) pair; }`) is
  counted plainly and never freed.

- **Step 8.1: done (2026-11-18).** Field `[UnsafeAccessor]` (an instance's, a struct's, a static's field: test 123),
  and code a plugin adds with `AddSource` is bound ignoring accessibility, so it names the program's internal types
  and private members directly (API check "generated code reads and writes private fields"). The second rests on
  setting a non-public property of Roslyn's options by reflection: it is not yet tried in the self-hosted compiler,
  where field accessors are the way that does not depend on it. Steps 8.2 and 8.3 are still to do.
- **Step 13.1: done (2026-11-18).** `LICENSE`, `THIRD-PARTY-NOTICES.md`, a README that matches the tree and names
  what a build needs.
- **Step 12.1, begun (2026-11-19): the compiler compiled by itself, on today's runtime.** Its build without threads
  took 24 minutes, 17 of them in one place: the arena visitor of every cycle-capable type asked all 51,000 sites
  which of them its type has (`Arenas.CyclicFieldOffsets`, a subtype test per site and type); the sites are now
  looked up by container, along the type's bases: 7.4 minutes. The threaded build ran out of memory (14 GB) in the
  sharing analysis of step 4, after 94 minutes: its "why" text grew along every path (now kept only for
  `CS2WASM_SHARING_DEBUG`), and its states, a node and the type that filters what it holds, were unbounded; a node
  reached with more than four filters is now visited once with none, which lets through whatever any filter would
  (more types taken as shared, never fewer); and which types may be subtypes of a type is asked of an index
  (`SubtypeIndex`, as Arenas does) instead of every type id, in `TopIds` and `MayBeShared` (checked against the old
  answers over the threaded tests and the bench: the same). The threaded build: 18.8 minutes, sharing 110 s and
  the counts' resolution 33 s per compilation (two). The isolated values' check of a method (step 7.1) went over
  the whole body for each local: now once.
- **Found by the same run, and fixed: the check at the drop (7.C) made the self-hosted compiler hang.** Every
  self-hosted build ran past 300 s on hello world (it took 13 s on 2026-11-17). Paused in the inspector: Roslyn's
  sorts and pooled builders, each move or clear a loss of a reference from a member, each one a check that went
  through all a structure owned (`CS2WASM_DROP_STATS=1`, a count at compile time: 82,000 checks had gone through
  950 million members, 11 freed anything; `CS2WASM_NO_DROP_CHECK=1`: 13.8 s). A check fails for one of two reasons,
  and its top's note now counts which: how many of what it owns outside code holds (TopHeld, as before), or how many
  references from members outside what it owns were left into it (`NoteOutside`: one more at such a store, one
  fewer at such a loss; a reference from inside changes nothing). It is looked at again when that number is zero,
  or when it loses its owner, not at every loss. The note has a word of its own (`MarkWord`, a ninth member
  word). Self-hosted hello world: done in 18.8 s with the statistics on (114,000 checks through 22 million members,
  1,594 of them freeing a structure). Test 138: a sort over references to 2,000 structures whose checks fail.
- **Open, being worked on (2026-11-19): the threaded self-hosted compiler on many threads.** Hello world, timed
  against NativeAOT's WebAssembly build (5.5 s): built without threads 19.0 s (3.4x; 12.6 s on 2026-11-17), threaded
  on one thread 39.5 s, on 8 or 24 threads no end in 300 s, or a crash: `rt.retain` on a reference that is no object
  (unaligned). Bisected over the self-hosted builds of 2026-11-18: commit 9070946 (every container in the graph of
  what can refer to what; a struct's copy and Interlocked's exchanges of references, with threads, as stores by
  address) is the first with it, and the first at 27 s on one thread (14.9 s before). Found in it and fixed: an
  Interlocked exchange or compare-exchange of a reference into a slot of a cycle-capable object linked the value
  before the atomic exchange and unlinked after, under no lock in common, so two threads' exchanges of one slot left
  its bits saying the other's value and its counts wrong; now the three are one step under the runtime lock, with an
  atomic exchange (the place's lock is taken before the runtime lock elsewhere: not inside it). Not enough: 24
  threads still do not end; a checked build of the compiler (freed memory never used again) is to say where.
  Also seen: 072_http once out of bounds in a worker in a checked suite run, then 366 runs passing (found and fixed
  2026-11-20: a compare-exchange that did not happen, below).
  Then, found with a checked build of the compiler (a node of Roslyn's read after it was freed) and two small
  programs that reproduce it (test 139: sixteen threads storing into the same slots of shared objects, a shared
  array of structs among them, as Roslyn's node cache does; 14 runs in 40 failed before):
  - Any store into a slot that can close a cycle, in an object another thread may reach, is one step under the
    runtime lock (`Site.StoreLock`, `Arenas.StoreLockAt` for stores by address and a struct's copy): its link sets
    the slot's bits, its unlink reads them, and two threads' stores of one slot between them left them saying the
    other's value. Not a store into an object the last compilation found no other thread reaches.
  - An object whose destruction begins while its thread holds the runtime lock waits in the thread's list until
    the lock is given back (rt.destroy, rt.lock_exit): its finalizer runs program code, as with the groups before.
  - The finalizers' phase of a group destroyed later puts the cut references back while the lock is still held, at
    the deferral (`FinPrepare`): put back afterwards, one could be to an object another thread had freed.
  - A placeholder for a local's borrow was made in the module before its first use: a local whose every value was
    owned left one with no body, which only a checked build's validator saw.
  Test 139: 40 runs of 40 pass checked, 30 of 30 as shipped. The price: every such store takes the one runtime
  lock, also into a plain object (unlocked, another thread can make the container a member mid-store and link the
  old value, which the plain unlink then leaves counted: two nodes leaked in 320,000). The threaded self-hosted
  compiler: 73 s on one thread, and on 24 it is all waiting for that lock. **Open, the next structural piece:** the
  runtime lock replaced by one per group for arena operations (a store, a link, a join lock the groups they touch,
  in address order), with the runtime's scratch state per thread; until then a program that shares cycle-capable
  objects among many threads is serialized on them.
  `node tools/suite.mjs` runs the suite in shards side by side, both modes: 7 minutes instead of 25.
  **Then (2026-11-20): the threaded self-hosted compiler ends on any number of threads.** Hello world: 23 s on one
  thread, 25 on four, 64 on twelve, 70 on twenty-four (it did not end on eight or more). Found on the way, each with a
  test that failed before:
  - A thread's new page of small blocks walked, under the runtime lock, a run of exactly a page left free for each page
    before it (quadratic; half the pages' memory lost): now an aligned window of a free run or of the heap's top.
  - The thread pool's limit was the browser's 10 threads (TARGET_BROWSER): pool threads blocked on work queued behind
    them waited forever (test 142). Now a 32-bit target's 1023.
  - Monitor.Pulse could be taken by the thread that pulsed and then waited (test 144): a queue of waiters now.
  - Two types whose static initializers use each other, started by two threads at once, waited forever (test 146):
    a thread follows the owners of the initializers waited for, as .NET does.
  - The cause of the hang on twelve threads: a Node worker started by another worker needs its parent's event loop,
    which a thread running the module never gives back; PLINQ's producers started that way never ran. Every thread is
    now started by host/spawner.mjs, a worker of the main thread that runs no module code.
  Was open (done by stream C, "Per-group locks" below): more threads make it slower, not faster (every arena operation takes the one runtime lock; the work
  item below). Tools for such hunts: tools/stacks.mjs (every thread's stack under --inspect), CS2WASM_WATCHDOG=1 (the
  pool's counts every 5 s), CS2WASM_DEADLOCK_DUMP=1 (a wait of 20 s for the runtime lock says who holds it).
  **Code quality for the baseline tier, 2026-11-20** (where the self-hosted compiler runs): `tools/micro.mjs
  --baseline` 2.18x to 1.85x NativeAOT's time (null checks taken out again on the final bodies and the rest written
  out in loops and what they call; AggressiveInlining honoured for methods no exception leaves; tee chains, constant
  arguments of inlined calls; the walk to a member's top only for members under a failed check). The no-threads
  self-hosted compiler on Console.WriteLine(42): 16.0 s to 12.45 s, against 4.6 s for NativeAOT-LLVM's build of it
  (2.7x). Its profile is flat now: the arena machinery about a quarter of the program's time (stores into slots of
  Roslyn's cycle-capable types: proving them away is step 7.2's), counting about a tenth, the host's file calls 6%.
  **Then (2026-11-20): test 139's leak under load and 072_http's rare out-of-bounds, both found and fixed**, each
  with a test that fails before (`tools/stress.mjs` runs one module many times at once and counts its outputs; a
  failed run's whole stderr goes next to the module):
  - Test 139 leaked a thread's anchor node and its last node, about one run in 45 at 15 runs at once. The two were
    consistent with each other; the anchor had one outside count nothing held. Its source: a node that had died alone
    was kept for good. A group's members whose finalizers run after the runtime lock is given back
    (`Rt.RunDeferredDestroys`) carry the count `FinBias` meanwhile, and the check after the finalizers reads any count
    above it as a reference a finalizer stored somewhere, and keeps the group with every reference it held put back
    counted (the dead node's to the anchor). A read that counts with no lock (`rt.load_retain`) counts the object a
    place held when it read it and gives the count back once the place says it is out of date: on a node in that
    window, such a count was taken for a finalizer's store. Now a count word with `FinBias` in it is counted only
    under the runtime lock, where that check runs, and only if the place still holds the object
    (`EmitLoadRetainLockFree`). With `CS2WASM_LOCKED_LOADS=1` the leak was gone before (0 in 300), which pointed
    there. Test 149 (writers replacing nodes that refer to an anchor in slots readers read) failed in most loaded
    runs before; none in 200 now. Test 139: none in 400 loaded runs, checked and as shipped.
  - 072_http: a group's member kept as its owner a member that had been freed, and the next store into it read the
    freed member's words (checked: out of bounds in `Rt.OnChain`, from `Adopt` or `LinkMembers`; as shipped: freed
    memory read silently). Its source: `Interlocked.CompareExchange` of a reference into a slot of a group's member
    linked the new value before the exchange, and when the exchange did not happen took it back as an unlink of the
    new value with the slot bits of the value the slot kept. When those said "not linked" (a sentinel from a static
    readonly field), the unlink was a plain release: the new value stayed owned by the container and counted inside
    it. `Task.AddTaskContinuation` does exactly that when the task completes as the continuation is added (the
    slot holds the completion sentinel): the continuation then named the task as its owner after the task was freed.
    The HTTP pump completes operations on its own thread, hence the race. Now the link (and the old value's unlink)
    happens only when the exchange did; otherwise the count taken for the slot is given back. Test 150 (failing
    compare-exchanges over a sentinel) failed every checked run before. A loop of 20 streamed GETs of 300 KB in reads
    of 256 bytes crashed in up to half the loaded runs before, none of 200 per mode now; 072_http, none in 600
    loaded runs per mode (it failed about one in 1,000 before).
  **Build cost, measured 2026-11-20 (tools/measure-build.ps1: wall time, peak memory of every process a build
  starts), the compiler with Roslyn:** `dotnet build` to IL 9 s, 0.73 GB; NativeAOT-LLVM published to WebAssembly
  80 s, 8.7 GB summed (ilc 2.8 GB, Emscripten's clang and wasm-ld the rest), a 129 MB module; cs2wasm 18 to 20
  minutes near 14 GB (one build ran out of 14 GB). cs2wasm is slower and heavier to build than NativeAOT-LLVM.
  Separately, and older: a program with threads whose cycle-capable objects are made on many threads at once gets
  slower with each thread (a list of 3,000 trees of 200 nodes, made in a PLINQ query: 2.9 s on one thread, 10.7 on
  four, 21.7 on 24; the same with the compiler of 2026-11-17): every arena operation takes the one runtime lock.
- **Step 13.5: done (2026-11-19).** `docs/UPSTREAM.md`, generated by `node tools/upstream-report.mjs` from the
  manifests and dotnet/runtime v11.0.0: every exact edit and replaced file with the reason tools/CoreLibManifest
  gives for it (18 edits, 3 replaced files, in four libraries), and System.Private.CoreLib's runtime half file by
  file: 63 changed (lines added and removed, and the comments marked cs2wasm that say why), 47 as upstream, 1
  cs2wasm's own.

- **Step 12.5, the engine's half: done (2026-11-19); the build in the browser: open.** The game
  (`tests/Godot/proof5_hello2d`, two C# scripts on a `CharacterBody2D` and a `Label`) compiled by the .NET-hosted
  cs2wasmc (3,449,802 bytes) plays in a browser in the **stock** Godot 4.7.2 web template (the build without threads,
  `v4.7.2.stable.official.ed1daf0bf`; the eight stock `web_*.zip`, taken by range requests from the official
  `Godot_v4.7.2-stable_export_templates.tpz`, replaced the mislabeled copies in the editor's template folder), through
  the JS bridge (`host/godot-bridge.mjs`, `host/web/godot-web.mjs`, `host/web/godot/*.gd`). Its engine-call log equals
  stock Godot .NET's on the desktop line for line: 1,667 lines with the fixed input, 1,669 with the key that makes
  the script throw (`node tools/proof5.mjs check --module tests/out/proof5/game-net.wasm [--panic]`, headless Chrome
  157); the same in the Claude app's browser pane (Chrome 156) with `node tools/proof5.mjs serve --build` and
  `?module=prebuilt.wasm&drive=1`, the page comparing its log with the oracle's itself; played by keys it moves and
  jumps (`--play`). The headless check without a browser is `node tools/proof5.mjs stub [--panic]`: the same module
  on Node with the oracle's answers replayed, equal too. The oracle, run again, gave the same bytes. Open: (1) the
  game built in the browser by the self-hosted compiler (`tests/Godot/proof5_hello2d/README.md`, "What stops
  proof 5", items 1 to 4: last tried 2026-11-17 with the self-hosted compiler of that day; not re-tried on today's);
  (2) `StackFrame.GetMethod()` is null in a module (`repro/StackTraceFrames.cs`: .NET prints `A`), a behaviour
  difference the game's log does not show (closed 2026-11-20, stream E below); (3) a host without JavaScript
  (Wasmtime) for the `godot.*` imports.

- **Stream D, proof 5 in the browser (2026-11-21).** Met: `node tools/proof5.mjs toolchain` (new: cs2wasm built by
  `tools/selfhost.mjs` without threads, Godot's source generators compiled in, SPEC 7.1; their sources fetched into the
  cache's sparse checkout at stock commit ed1daf0bf, and `toolchain/Common.Constants.cs` for what Godot's build writes
  beside them; 55.2 MB, 14 min), `web`, `check`: built in headless Chrome 157 in 220.9 s on one core (Node 292 s;
  .NET-hosted 32 s), 18.2 MB downloaded, 2,160 MB of memory at the end; played in the stock 4.7.2 web template, 1,667
  log lines equal to stock Godot .NET's, 1,669 with `--panic`. The page passes the .NET-hosted compiler's arguments
  (`-a Godot.SourceGenerators.dll`): no pregenerated sources. What stopped it (tried 2026-11-20 with that day's
  compiler): the ref-safety trap of 11-17 was gone; every program with `-r GodotSharp.dll` hit a premature free
  (a cycle of SyntaxList parents, SimpleSyntaxReference where a node was due). `repro/RoslynGodotProbe.cs` (Roslyn
  compiled `--checked` by cs2wasmc, binding all of GodotSharp's bodies) reproduced it in 16 s against a 15-minute
  toolchain build. Checked-only instrumentation named a BoundLambda freed while its BoundBlock still named it as
  owner, the block's count word -1 (the immortal mark: one release too many). Two causes, on main as ae57217:
  (1) a field borrow's temporary took its local from the pool counted temporaries use, and the exception handler,
  which releases every slot as its own kind, released the borrowed value (test 057's DelegateBinding.Invoke; test
  179); (2) MemberwiseClone of a cycle-capable object linked each slot twice: the copy's join queued it for
  ArenaLinkJoined after ArenaLinkContents had linked its slots (BoundExpression.WithSuppression; test 180). Checked
  builds trap at a member released with no outside count and at a store of a value with none. Blocker 2 of 11-17
  (more cores never ending): the threaded toolchain with stream C's per-group locks ends on Hello -r GodotSharp told
  one core (758 s; it spun 5.5 hours on 11-20) and four (624 s). Open: the self-hosted output on a GodotSharp program
  is not the same bytes across toolchain builds (no threads against threaded, one core: data) or core counts (one
  against four: code and data, a byte); the page's build against Node's with paths mapped: 67 bytes of data, not
  looked into. host/README.md (new): the hosts and the embedders' rule, never start a thread from a busy worker.

- **Stream I, value sources for the arenas (2026-11-21).** `Sharing.SiteTypeIds` on by default in every program: a
  class field's targets in the cycle graph are the types its values come from (followed through parameters,
  results, slots, `ref` parameters, struct fields), intersected with the static types' answer; computed once per
  strongly connected component of the source graph (SharingFlow.cs). Soundness rules: array elements stay as their
  static types say (the runtime's raw copies, Array.Copy, `Array.SetValue`, `Buffer.Memmove`, put references there
  that no recorded store says); a slot takes the stores through refs of its type only if its address may be kept
  (an escape analysis of `ref` parameters: a ref local, a ref return, a span, a pointer, an unknown callee);
  `Unsafe.As` of a struct's place makes its references any object; reflection's writes apply. Fixed on the way:
  `FieldInfo.SetValue` into a class field stored past the arena protocol and its edge was not in the graph (test 171's
  reflection case leaked before this branch). Open (runtime): `MemberwiseClone` of a cycle-capable object leaks with
  its cycle (the clone's copied slots are not linked; `tests/out/si/clone/c.cs` in the stream's worktree: .NET -1,
  cs2wasm 2). Measured: test 088 437 to 388 cycle types; the self-hosted compiler (main with J) 20,717 to 20,636 in
  its largest group, no change in time (14.24 s against 14.29 s). The bound with every source known
  (`CS2WASM_FLOW_OPTIMISTIC=1`, unsound) is 20,555: hub `object` slots keep ~3,800 types under a context-insensitive
  flow, and typed edges join the rest. Next for that group: context (call sites or containers' allocation sites),
  or the typed-edge proofs of 7.E.

- **Stream E, behaviour differences (2026-11-20).** What was on record and what a sweep against .NET found, each
  closed with a test (150 to 153, both modes):
  - `StackFrame.GetMethod()`, `Exception.TargetSite`, `MethodBase.GetCurrentMethod()`. A program that reaches one
    (`Meta.FrameMethod`, the manifest's substitution of `StackFrame.GetMethod`) gets a table of frame names and
    their methods' reflection records (`rt.frame_methods`, Compiler/Reflection.cs): a record for every compiled
    method a frame can show, of its definition as .NET's frames name it (`Holder<int>.Here`'s frame is
    `Holder`1[T]`'s), with methods that take a span (no thunk: reflection cannot invoke them, here as in .NET); the
    program's own get thunks, so a frame's method can be invoked. `GetCurrentMethod` is compiled where it is called,
    from the function's frame name. A throw in a method traces hide (`[StackTraceHidden]`: `ThrowHelper`'s) notes
    its method in a global for `TargetSite` (.NET's is the method that threw, hidden or not). None of it is in a
    program that asks no frame for its method: the notes are taken out at the end of the build (RuntimeGen.Finish).
  - `AppDomain.FirstChanceException` was never raised. Now raised at each throw and rethrow, before filters, with
    the domain as sender; the raising code is compiled only for a program that reaches
    `AppDomain.add_FirstChanceException` (`Rt.RaiseFirstChance`, the switch `rt.first_chance` folded at the end).
  - `typeof(G<>).ToString()` was `G`1` for the program's generic types (the library's were right): a definition's
    name now lists its parameters (`ClrName`), its `FullName` still has none.
  - Two methods with the same code were merged into one function, so a trace through either named one of them, at
    its lines (`First`/`Second` in test 153). A function that is a frame and can be on the stack when a trace is
    taken now merges only with one of the same frame name and lines: hello world +24 bytes.
  - Iterators: the frame is `M(...)+MoveNext()` as in .NET (was `M(...)`); in iterators and async methods a throw,
    a condition or a returned value that ends a block had no line of its own (a trace showed another method's line).
  - A static constructor that throws: the `TypeInitializationException` had no trace (thrown without `Rt.OnThrow`),
    the `.cctor` frame was missing from the inner exception's trace, and that trace ran on to the frame that touched
    the type instead of ending at the initializer (`rt.cctor_traces`, written at the end of the build only when code
    that throws is left: hello world has none of its own, but keeps `Rt.OnThrow`; the initializer frames' names and
    the cut cost it 369 bytes, 27,584 to 27,953 in this worktree's path).
  - An async method's exception: its trace ran on through the thread pool's frames (`YieldAwaiter.RunAction` ...
    `Thread.StartCallback`) instead of ending at the method's frame, where .NET's MoveNext takes it
    (`Rt.OnAsyncCatch`), and the `--- End of stack trace from previous location ---` line was written after an async
    frame, which .NET leaves out (`StackTrace.ToString`: "Skip EDI boundary for async"; `Exception._asyncTop`).
  Also checked and equal: exception messages and `ToString` with inner exceptions, `AggregateException`, `throw;`
  against `throw e;`, `ExceptionDispatchInfo`'s trace, a throw from a `finally`, `Data`, `HResult`, `Source`, filters
  with side effects (test 152), operators, conversions, indexers, nested generic types and generic methods in traces
  (test 153). **Open (not closable by cs2wasm alone, recorded):** frames inside .NET's own library and runtime are
  what its JIT and ReadyToRun inlining and CoreCLR's own helpers leave (`int.Parse` shows
  `Number.ThrowFormatException`, `Int32.Parse(String)` and nothing between in .NET, cs2wasm every library frame
  between; a failed static initializer's inner trace ends in .NET with CoreCLR's
  `InitHelpers.CallClassConstructor(Void* cctor, ...)` frame and a line break, cs2wasm's at the `.cctor` frame): the
  program's own frames are equal, the library's are not reproducible from source. ~~Open, a feature of its own:
  methods the C# compiler makes have no reflection record~~: closed 2026-11-21 (the entry below).

- **Reflection on what the C# compiler makes (2026-11-21).** Tests 155 and 156 (both modes; suite 198/198 checked and
  as shipped on the homelab at a975d62; `tools/names-check.sh`, debug and release: no program differs). What .NET reports for
  lambdas, local functions, iterators' and async methods' `MoveNext` and their classes, cs2wasm reports the same
  through `Delegate.Method`, `StackFrame.GetMethod`, `Exception.TargetSite`, `MethodBase.GetCurrentMethod`:
  - Names (Compiler/RoslynNames.cs, rewritten): Roslyn's closure conversion emulated on the operation tree, with
    its numbers exact: the method's ordinal among its type's members; the scope tree (blocks that declare variables,
    every lambda's and local function's body, the scopes lowering adds: a foreach's temporaries and its variable, a
    for loop's, using, lock, switch, switch expressions, object and collection initializers); an environment per
    scope with captured variables (a struct when nothing that captures it becomes a delegate, is async or an
    iterator), numbered in a walk of the tree; each function's place (the nearest class environment, the type itself
    for `this` alone, `<>c` (`<>c__N`1` in a generic method) for a lambda that captures nothing, else a static
    method) and its number in the same walk (a scope's own functions before those of the scopes in it: a lambda
    inside another is numbered after its outer method's other lambdas, `<Lambdas>b__9_9`); in a release build,
    environments merged into their parents when the same functions capture both, not across a loop's head (a
    backward jump). State machines `<M>d__N` (a lambda's `<<M>b__0_0>d`). The old names were source order and a
    scope per variable's block: wrong for nested lambdas and for loops' environments. Checked against Roslyn itself:
    `CS2WASM_CHECK_NAMES=1` (or `=only`, which stops after the check) emits the program with Roslyn and lists every
    method and class name it made that the emulation did not predict, and the reverse
    (Compiler/RoslynNamesCheck.cs; `CS2WASM_CHECK_NAMES_RELEASE=1` as a release build).
  - Records (Compiler/GeneratedReflection.cs): a method entry per compiled lambda, local function and `MoveNext`
    (name, return and parameter types and names, a local function's struct of captures as `<>c__DisplayClassN_M&`,
    static or instance, private for a lambda on the type itself, internal otherwise, `[CompilerGenerated]` on those
    in the program's own type); their classes as types without symbols (`Driver.GenTypes`): `Program+<>c`,
    `<>c__DisplayClass9_0`, `<Numbers>d__5` (an iterator object's `GetType()` too, and a closure's: the delegate's
    `Target.GetType() == Method.DeclaringType`), declaring type, namespace, sealed, nested private, `[Serializable]`
    on `<>c` (the flag), `[CompilerGenerated]` but on one nested in another such class, a value type for an async
    state machine in a release build. The kickoff method's `[IteratorStateMachine(typeof(<M>d__N))]` /
    `[AsyncStateMachine(...)]` and `[DebuggerStepThrough]`, their type the class of the state machine's object. A
    frame of an async method's `MoveNext` is its `MoveNext` (was the kickoff method). Found on the way:
    `ExceptionDispatchInfo.Throw` made itself the TargetSite of what it rethrew (it is `[StackTraceHidden]`); the
    first throw's site now stays (`Exception._capturedSite`).
  - Cost: none of it in a program that asks no frame or delegate for its method (records, entries, attribute
    factories are made only then); hello world 27,297 bytes as shipped, unchanged. The iterator classes' full names
    are longer in programs with iterators (`Program+<Numbers>d__5` against `<Numbers>d`).
  **Still different (recorded):** a lambda that captures nothing has a display or null as its `Delegate.Target` in
  cs2wasm, the `<>c` singleton in .NET (so `Method.Invoke(Target, ...)` of a lambda's method: no invoke thunk, and
  the target is not a `<>c`); the generated classes' own members (`GetMethods()` of `<>c`, a display class's
  fields but for expression trees') are not listed; a generic type's or method's generated class is reported as
  its definition where .NET reports a delegate's method on the constructed class; the emulation leaves out the
  scopes of lowering's other temporaries (a lambda inside `??`, `?.`, named arguments out of order is numbered after
  its scope's other functions in Roslyn), goto statements' effect on merging, variant interfaces, C# 14 extension
  blocks' members. Handled besides: interpolated strings built by a handler (a scope), params collections (none),
  initializers' lambdas (numbered in each constructor, after the body's own), primary constructors' parameters
  (`this` captured), expression trees' lambdas (numbered, no method, their parameters no environment's). The check
  over the whole suite (debug and release) finds no difference left; it compares names per class, so two lambdas
  of one class with their numbers swapped would not show: test 156 prints them in order.

- **Steps 8.2, 8.3: done (2026-11-18).** `MemberAccess.Instrumentable` says which accesses a hook runs at (not a
  `ref` or `out` argument, a deconstruction's target, an event's handlers); every hook form and failure path of the
  interface has an API check.
- **Step 9, in part (2026-11-18): plugin interface version 4, without waiting for the IR.** The measurements put the
  IR's first use in the optimizer, and what the Invisible API was blocked on turned out to need only the front end's
  one entry point for expressions:
  - `InterceptValue(expression, hook)`: the value of any one expression goes through a hook that returns the value
    the program goes on with: a branch's outcome seen and replaced (A4.2), a draw redirected, the value a write
    stores rounded or limited, an awaited answer. Works in async methods. A hook on something that is not computed
    as a value fails the build.
  - `InstrumentWrite(write, hook)`: a write hook at one write (A4.3).
  - `SuppressibleImports(imports)` and `Cs2Wasm.Effects.Suppressed`: effects are left out by a switch the generated
    code sets around a run, so it holds for virtual calls, delegates, lambdas and the library, and nothing is
    cloned (A4.7). `CloneWithSuppressedImports` stays as it was.
  - Generated code names the program's internal types and private members (step 8.1).
  - Version 5 (2026-11-19), collection entries (A4.4): `Accesses` reports an access to the entries of a collection
    a field or property holds (`MemberAccess.IsEntry`, `Key`): an element read or stored, a compound store, a library
    method that changes it (`Add`, `Remove`, `Clear`, `Sort`, `Array.Fill`, ...) or reads it (`TryGetValue`, `Count`,
    LINQ, a `foreach`), on the member, on an element of it (`Grid[x][y] = 1` is entry x of Grid), a field of a struct
    element, or a local whose every reaching definition is the member. `InstrumentEntryWrites` and
    `InstrumentEntryReads` call a hook after each, with the receiver and the key as the program computed them
    (`(object receiver, TKey key, bool keyed, int tag)`). One function (`Compiler/Entries.cs`) decides the sites for
    the analysis and for the code, so they agree. Plugins of versions 1 to 4 see the accesses they saw. A collection
    handed to the program's own method, or a `ref` to an element, is not followed (the list of unsure sites).
  - `InstrumentAfter(statement, hook)` (version 5): a hook where a statement's code ends, so it runs when the
    statement completes normally (an `if` whose branch did not return, a loop left by its condition or a `break`)
    and not when it is left by a `return`, a `throw` or an exception; a jump statement is refused. In an async
    method or an iterator (compiled from Roslyn's lowered graph) it runs after the statement's last lowered
    operation, and only expression statements and declarations can be hooked there.
  - Conditions and aliasing (A4.8, version 5): conditions count early returns (`if (c) return;` and a branch that
    always leaves put the statements after it under `c` false, up to a label). `CallersOf(method)` gives each call
    with its conditions (a virtual or interface call counts for every override and implementation; a property's
    accessor, a constructor), and `PathConditions(operation)` every path of calls from a method no source code
    calls (or from a delegate's invocation, a recursion, the cut) with the conditions along it, outermost first.
    `MayAlias` follows where references come from through the def-use chains: two different `new`s, a `new` and a
    parameter's incoming value, or a `new` its method owns alone (`IsOwned`, from the escape analysis: not stored,
    captured, returned or handed to code that keeps it) and anything from outside do not alias; then types.
    `AllocationSites(reference)` gives the `new`s a reference can name.
  - Plugins before the first compile (A4.9, version 5): a plugin that implements `ICompilerPluginBeforeCompile`
    runs before the first drain as well; what it adds there is compiled once, with every analysis knowing its
    hooks. Before the compile the reach is not known, so it sees every source method with a body, the source's
    direct calls and every source type. The soundness part: the escape analysis and the fresh, isolated and
    constructor analyses (`ParamEscape`, frames, `FreshLocalBefore`, `ReturnsFresh`, `IsolatedValue`,
    `CtorsKeepThis`, `ClosedCtors`, `IsoMethod`, stores into fresh objects) take a method with a hook as one that
    lets everything out, since a hook may be given its receiver, a value or a key. A hook added after the compile
    makes the compiler forget those answers and compile again what it instruments and every function (static
    initializers too) that used one of them. Test: a hook that keeps its receiver was given an object a caller had
    put in its frame (printed 20 for 2), now in both phases. Not looked at for hooks: the threaded sharing analysis
    (`Sharing.cs`) and the arena analysis; both run over compiled code that includes the hooks' own bodies, but not
    over the call edge from an instrumented site to its hook.
  - The list of unsure sites (version 5, `UnsureSites`): a delegate invoked whose target is not known there
    (`DelegateTargets` gives the lambdas and methods where the def-use chains know them), reflection (calls into
    `System.Reflection`, `Activator`, `Type.GetField` and the like, and the compiler's own CW0201 warnings),
    `dynamic`, a member or an entry used through a reference (`ref`, `out`, `in` arguments, `ref` locals and
    returns), a collection that leaves where its entries are followed (handed to the program's own method, stored in
    another place, returned, made a writable span or memory, or reached through a local that is not always the
    member), and addresses and reinterpreted memory (`&`, `Unsafe`, `MemoryMarshal`). A library method other than
    the known changing ones (`Entries.cs`) is taken to read a collection it is given.
  - The closed-form solver (A4.6, version 6, 2026-11-20): `PluginContext.Solve(method)` for a per-tick method,
    `Solve(loop)` for a `for`, `while` or `do` body (a `for`'s increments included, its condition left out);
    `Compiler/Solver.cs`, the public types in `src/Cs2Wasm/PluginSolver.cs`. Symbolic execution over Roslyn's
    operation tree: each state variable written (fields and auto-properties of `this`, statics, a loop's outer
    locals; int, long, float, double; several writes in order) becomes `x' = A x + b` with coefficients that are
    polynomials in the region's invariants (constants, parameters, members it does not write, and expressions of
    them it cannot split: a division, a shift, a pure Math call), each invariant carrying the C# that computes it
    as the program does. `Math.Min`, `Max`, `Abs`, `Clamp` become pieces (Clamp with bounds the wrong way round and
    `Abs` of the smallest integer become pieces that throw: the generated code runs that tick itself, so it throws
    as the program does); `Single.Lerp`/`Double.Lerp` their affine form (`a*(1-t) + b*t`, as .NET computes it); a
    program method or property getter that writes nothing is expanded; `if`, `?:`, `&&`, `||`, early `return` and
    `continue` make pieces with guards (a guard seen again on a path is not split again). For each guard of each
    piece, how its value moves under that piece: by a fixed step each tick (`c(A - I) = 0`, step `c·b`), or so from
    the first tick on (a clamp's bound), or neither: then not solved, "not monotone in the number of ticks", with
    the guard and the piece. Not solved, with the reason and the operation: a product of two state values, an
    integer division of the state, a widening of a state int to long (not affine modulo 2^64), a float state value
    converted to an integer, checked arithmetic, a call it cannot expand, a write of another object, a loop inside,
    a loop body that can throw, more than 64 pieces. Results are values: the variables, each piece's guards (the
    comparison as two affine sides, or an invariant condition), rows (A and b as `SolverTerm`s: monomials of
    `SolverAtom`s with their symbols and operations), and flips (kind and step); `GenerateAdvance(className)` is C#
    with `Advance(receiver, the tick's arguments, long n)` (for a loop: the state locals by `ref`, the invariant
    locals, n) that the plugin adds with `AddSource`. The generated code: invariants once; then, per stretch in one
    piece, the ticks until a guard flips (integers: the first tick a wrapping arithmetic progression crosses the
    other side, segment by segment between wraps, exact; floating point: `ceil` of the real crossing, adjusted
    against the double sum) and the piece's map to that power (a translation `x + k b`, a constant, or repeated
    squaring of the affine map: `log k` steps in long arithmetic, which wraps as the program wraps, so exact; no
    `(a^n - 1)/(a - 1)`). `Exact`: every variable is an integer and every guard an integer comparison or an exact
    invariant; floating-point closed forms compute in double and are marked approximate. Cost: one stretch per
    guard flip (a sawtooth timer is one per period), and for an integer guard one step per wrap before it flips.
    Test (API check "plugin v6"): the spawner timer (two writes and a branch, float), the same timer in integers,
    velocity/position (triangular A), Lerp as a program method (through a property) and `Single.Lerp`, health
    with `Math.Clamp` and a shield with `Math.Min` (7 pieces, one throwing), coupled wrapping int and long LCGs,
    and a `for` body; each Advance against running the ticks for n in 0..1000 (and 10^6 for the LCGs), exact
    where `Exact`, within 1e-4 otherwise, and the Clamp that throws against the ticks that throw; a guard that is
    not monotone and `x = x * x` reported unsolved with their reasons. Found on the way: generated code that calls
    a program method with parameters did not compile (the plugin's compilation sees the program's types as
    retargeted symbols, whose parameters are not the ones the operation trees name); `Types.Canonical` now maps
    such a type to the input's own (`Driver.InputType`).
  - The Invisible API's first slice (9.4, stream M, 2026-11-20), as a standalone sample in this repository (the
    owner's decision): `samples/invisible-api` (game `Game/Arena.cs`, plugin `Plugin/InvisiblePlugin.cs` against
    version 7, `driver.mjs`, `script.txt`), checked by `tests/Cs2Wasm.Tests/InvisibleCheck.cs` ("invisible-api
    slice", both modes). The plugin, all from `RunBeforeCompile`: classifies each write of the game's state
    (`Accesses` with entries; `PathConditions`, each path's conditions tested for a dependence on the input exports'
    parameters through the arguments along the path and the locals' writes; tick only; unknown for a member an
    unsure site touches or a write no hook follows), intercepts every read of the input exports' parameters (the
    server's input log, and one action replaced), marks the writes under input (`InstrumentWrite`) and the entry
    writes under input with the player key (`InstrumentEntryWrite`), generates one gate with `AddSource` (fire
    accepted now: `TryFire`'s own early-return condition rewritten over a receiver, `!(self._cooldown > 0 ||
    self._hp <= 0)`, reading private fields; exported with a view of a player's private state), and solves the wave
    timer (`Solve`, exact, `GenerateAdvance`: a prediction on a copy, a catch-up of the live timer). The check plays
    the script with the driver and under .NET (the game without the plugin; the server's answers from the private
    state by reflection and by running the ticks): the game's output, gate answers against whether the fire
    happened, views, predictions up to 1,000 ticks and a 100-tick catch-up equal; every member or entry an input
    changed is marked but for the unknown ones; no tick marks anything; the classification equals
    `expected-writes.txt`; `CompileResult.RecompiledFunctions` is 0. A missing mark fails it (tried both ways).
    Gaps it found:
    - Fixed: a hook at one entry write (version 7, `PluginContext.InstrumentEntryWrite(MemberAccess, hook, tag)`;
      `EntryHook.Site`, filtered in `FunctionCompiler.EntryHooksAt`). `InstrumentEntryWrites` hooks every entry
      write of a member, so the tick's deterministic `_coins[id] += 2` was marked with the inputs' writes; the
      per-site form is to it what `InstrumentWrite` is to `InstrumentWrites`. Older plugins unchanged.
    - Fixed: nothing showed whether a plugin made the compiler compile again; `CompileResult.RecompiledFunctions`
      (functions `Driver.Recompile` compiled a second time, static initializers included).
    - Open: whether a value depends on an input. `PathConditions` gives the conditions, not what they depend on;
      the plugin follows parameters to the arguments of the calls on the path and locals to their writes itself,
      over syntax and flow-insensitively, because `GetDefUse` keys the control-flow graph's operations, not the
      tree's that `Accesses` and `BranchCondition` give (an `out var` from `TryGetValue` is followed by hand). A
      query `DependsOn(operation, path, sources)` over the def-use chains, with calls on the path, would replace it.
    - Open: a condition as C# over a receiver. The gate rewrites the condition's implicit `this` reads to
      `self.Name` itself; the solver's `SolverAtom.Code` does this for invariants, a `ConditionCode(test, receiver)`
      would give it for any condition (calls, properties and locals included, or a refusal).
    - Open, small: field initializers (`int _hp = 10`) are not writes in `Accesses`; their values are the program's,
      so a server knows them, but a plugin that must list every write reads them from the syntax.

- **Step 11.1, in part (2026-11-18): programs written to use every form, compared with .NET.** Tests 124 to 126
  removed three refusals (`await` in conditional expressions was already handled; lifted compound assignment
  through an operator method; a spread with an element conversion) and found five wrong results that no refusal
  announced: `x ??= await f()` and `a[i] += await g()` lost the assignment; `x++` on a nullable struct with its own
  operator; relational and constant patterns on a `decimal` (compared as addresses); a NaN constant pattern; an
  empty nested initializer read its member. Writing such programs finds more than reading the refusal sites does.
- **Step 11.2: done (2026-11-18).** What the sandbox or the library lacks is a warning and
  `PlatformNotSupportedException` at run time (A5.2, A5.3).
- **Found 2026-11-18, open, and it comes before speed: most cycles are never freed (A1.8).** Test 127 holds objects
  every ordinary way, lets go, and counts what is left by finalizers. Freed: trees with parent links through a
  list, rings, parts cut off a living structure, replaced values, iterators, exceptions, weak references. Never
  freed: a cycle through
  - a closure or a delegate (an event's subscriber that the publisher refers to; a callback that captures its
    owner; LINQ over objects with a lambda that captures one),
  - a task or an async method's state,
  - a reference inside a struct: a tuple field, a dictionary's entries (`Dictionary<string, Node>` in a `Node`), a
    `List` of structs, a boxed struct.
  - Source: the type graph and its link sites (`Arenas.cs`) know class fields of reference type and arrays of
    references only. Display classes and synthesized types are left out of the graph on purpose
    (`Arenas.Analyze`: `!D.DisplayById.ContainsKey(id) && !D.SyntheticById.ContainsKey(id)`), a captured variable
    is stored plainly (`StoreCaptured`), and a struct is copied with plain counts (`StoreToAddressFromStack`, the
    struct branch; `rt.CopyAssign`).
  - Fix (step 7, first): every reference a heap object holds is a site, whatever holds it: a class field, an
    element, a struct inside either, a box, a display, a delegate, a state machine. The suite shows the test as a
    known gap at every run until then (`// known-gap:` in a program).

- **Step 7.A: done (2026-11-18).** The heap is handed out in granules of 1 KiB with a table word for each: a page
  of small blocks, a block of its own, or a free run. `Rt.BlockOf(address)` says which block an address is in, for
  programs with and without threads (with threads a page keeps its kind and size for good, and a freed large block
  is kept for one of its size). Without threads a freed run is joined with the free runs beside it, where freed
  large blocks used to be split and never joined: the bench's memory is 16.0 MB (32.0 MB before; NativeAOT
  71.3 MB), its time unchanged (0.95x), hello world 27,840 bytes. A checked build asks the table about every block
  it frees.
- **The suite runs twice from now on, checked and as shipped** (`--unchecked`; CI does both). A checked build never
  uses freed memory again, so it cannot see a fault in how memory is reused: the allocator's first version passed
  it and crashed the bench. The 153 programs pass both ways.
- **Step 7.C: done but for one shape (2026-11-18).** Every container is in the graph of what can refer to what,
  in programs with and without threads: a reference inside a struct that is a class's field, an array's element or
  boxed (tests 129, 130); a closure's captured variables and its enclosing closure, a state machine's variables,
  a delegate's target, an anonymous type's properties (a closure's object has no type of the program's: its sites
  are by its type id); a field-like event's field (its accessors store by address); a slot whose address is taken
  holds whatever its type can. Test 127 went from ten kinds of objects left to two.
  - **Two faults in the runtime, found by these shapes, both about the moment a store is made.** `ArenaLink` gives
    back the count the store took, before the storing code writes the slot. When that count was the value's last
    outside one, (a) a group nothing outside referred to any more was never freed (`self.Slots[0] = self` with the
    last reference: nothing looked at the group's number then), and (b) a value that became unreachable was
    destroyed at once, and its container with it, before the slot was written and its old value read
    (`t.Tag = new object[] { t }`: a write to freed memory, which the checked build showed). Now such a count is
    kept until the store is written and given back then as any release (`Rt.GiveBackLater`, `AfterStore`; every
    link is followed by an unlink, an array initializer's elements too). An object that joins its group for the
    store gives the count back at once as before, so building a structure costs what it did (bench 0.96x,
    unchanged; 16.0 MB against 71.3 MB; hello world 27,943 bytes).
  - **What is left of test 127 (`async 5`, `pending 1`), and why it is not a missing container.** With a task in a
    cycle (a continuation that captures what holds its task), tasks, state machines and delegates are of one
    component with the program's objects, and every object an async method touches ends in one group with the
    task machinery, which lives as long as the program. Inside it only what is shaped like ownership is freed when
    let go, and an async method breaks the shape: the object it makes is first held by the state machine (its
    owner then), so its reference to its parent is a counted one, and when the machine goes and the parent's list
    becomes the owner, the reference to the parent stays counted (it is looked at again only if the new owner is
    the direct one at that moment: here the result array took the object first). A counted cycle in a group that
    lives: it stays. This is the limit the model states ("a part of a group that is let go while the group lives
    may stay"), met by ordinary async code, so it is the owner's to weigh: steps 7.D and 7.E do not remove it. Ways
    that keep the decisions (no search for garbage): keep the task machinery out of the program's groups (a
    continuation's target linked only while it waits), or look at an object's counted references again whenever
    any object above it gets an owner (a visit of what that object owns, at that store).
  - **Both ways were tried the same day; neither is what fixed it.** Keeping the task machinery out has no rule
    that is automatic and sound: the group that lives on holds the thread pool's queue, its per-thread parts, the
    threads and the timer queue (measured: 39 members, 12 referred to from outside), their storage is ordinary
    arrays and `ConcurrentQueue` segments shared with the program's own, and an object that never joins a group
    (pinned) is never freed as part of a cycle, which a fired `Task.Delay`'s promise and timer are. The visit of
    what a re-owned object owns changed nothing. What the counts showed instead: the object an async method makes
    refers to its parent first and so *owns* the parent; when the state machine that held it goes, the next
    owner was the latest other holder (the task's result, gone by then) or was refused because the object was
    above it. Now the first other holder is remembered, and an adoption turns the ownership round as a first
    store does (`Rt.Adopt`, `LinkMembers`): constant time, no visit. Test 131: one child and four through
    `Task.WhenAll` are freed (5 left each before). Suite 157 passed checked and as shipped, 1 known gap; bench
    0.96x, 16.0 MB against 71.3 MB, unchanged.
  - **Closed (2026-11-18): the check at the drop, the owner's choice.** A member left with no reference from
    outside and no owner, kept only by holds from what it owns, has what it owns gone through once at that store
    or release: the counted references among them come off, and if none of them is left with any reference, they
    are destroyed together (`Rt.Dropped`). Otherwise the counts go back, and the top keeps how many of them outside
    code holds; it is looked at again when that number reaches zero. Chosen over keeping a kind for every slot,
    which every store would pay for. Its cost is at the drop only: a subtree of 7,381 nodes indexed twice is
    released in 4.2 ms at worst against 1.9 ms for the same tree indexed once; when something outside still holds
    a part, the failed check adds about 20% (1.27 against 1.08 ms). Tests 127 (no known gap any more) and 134;
    suite 161 passed checked and as shipped; bench 0.96x, 16.0 MB against 71.3 MB; hello world 27,943 bytes.
  - **What test 127 left before that, exactly:** children held by two containers of one tree (`root.Children` and
    `root.Tag = results`). A member that something besides its owner refers to holds its owner, since that other
    holder may be outside the tree; here it is inside, the tree holds itself, and in a group that lives on it
    stays. Not specific to async; freed whenever the group goes (a program without a task on a cycle: at once).
  - **Closed (2026-11-21): test 127's intermittent `async 5` / `pending 1` (about 1 run in 8 of its tasks under
    load), a garbage structure with two tops.** Found with `CS2WASM_ARENA_REPORT=1` (at compile time: GC.Collect
    prints each arena left, the members referred to from outside or under a failed check, who refers to each such
    top, and a log of the last checks, owner losses, adoptions and queued checks). What was left: the pending
    TaskCompletionSource is a top that owns, through its continuation and the lambda's closure, the root and the
    array Task.WhenAll returned; one task result is a top of its own (no owner, nothing outside refers to it),
    because its task let it go while the one container noted to own it next (Owners) was gone too, so the WhenAll
    array that still holds it does not own it. The array refers to the result, the result's `Parent` to the root:
    each tree refers into the other, and each check, over what its top owns, failed by the other's reference. Why
    timing: whether a result's task dies before or after the noted container, and whether the TaskCompletionSource's
    last reference goes while the method's other locals still hold the root (then its check fails by them).
    Fixed, in `Rt.Dropped`: the trial takes in, with what they own, the other tops that what it owns refers to
    directly when no code refers to them and nothing owns them. What the top owns is gone through first; then the
    tops taken in, and the tops those refer to. The trial stops taking in at the first member under a top taken in
    that code refers to (the set cannot die then): a live structure that small ones refer into is not gone through
    at each of their checks, which made the self-hosted compiler run past 300 s when every reference to an owned
    member walked to its top (`Top`) and took in what that top owned. The trial is exact for any set (every counted
    reference among its members off, none left is the proof), so taking in is never wrong; it only finds more. If
    that fails by a reference from outside, the check is made again by what the top owns alone (the first part of
    the same list, not gone through again), whose numbers the note keeps; if it fails only by members code still
    holds, all of them are what the top owns, and that is the note (held kind), so the last of them let go looks
    again, now with the other top. Not covered: two tops that refer only to members under each other, not to the
    tops. Test 187 (the WhenAll shape, with a pending TaskCompletionSource keeping
    the group: 4 results and the root left on every run before). `CS2WASM_NO_TAKE_IN=1` (at compile time) is the
    old check. Tried and left out: checking a member at the end of a destruction that left it with inside
    references alone (QueueCheck without stale holds): it found nothing more here, and test 133 then leaked 4,937
    nodes, since every check that fails leaves a note whose numbers are kept by references coming and going, not
    by owners changing; more failed checks, more numbers that go stale. That staleness is open: a top whose
    structure gains or loses members by an adoption or a handover keeps the number it had. One case of it, known:
    a note of the outside kind counts the references from outside the top's tree; when a member that refers into
    the tree is adopted into it (Adopt, TakeOwnership), its reference is inside from then on, but the note's number
    still counts it, so the last outside reference let go does not bring the number to zero and the check is not
    made again.

- **Self-hosted compiler's size (stream B of docs/FINISH.md, 2026-11-20): 63,966,970 to 42,664,712 bytes.**
  `tools/size-report.mjs module.wasm [--packed]` (names from a `CS2WASM_NAMES=1` build) splits a module: its
  sections; code by kind (methods, generic instantiations, specialized copies, dispatch stubs, lambdas, arena and
  counting functions, static initializers), by namespace, by type, by source method with its instantiations; data
  expanded and split into string literals, manifest resources (found by content from the build's .rsp) and the rest.
  What the 64 MB were: code 36.1 MB, data 23.3 MB, stack-trace frames 1.2 MB. Of the data, 13.8 MB string literals,
  12.5 MB of them the full names of 37,841 generic instances (`Type.FullName` of every type the module numbers, each
  repeating its arguments' names: Roslyn's run to 1,300 characters); 4.0 MB the library sources the compiler
  compiles (needed); 5.5 MB type tables and reflection records. The data was not packed: packing (SPEC 5) needed the
  packed bytes to fit the first megabyte of heap. Fixed, behaviour unchanged:
  - A generic instance's (or an array's or pointer's of one) full name is kept as pieces whose join is the name:
    strings (a definition's part up to its bracket, shared by every instance, and separators) and type ids (an
    argument whose own entry is its name, found in the name in order; one with no id is looked into). Each record is
    checked at compile time against the name. `rt.lazy_type_name` joins them the first time the name is asked for,
    makes the string immortal (a literal's header) and puts it in the table's slot (a compare-exchange with threads;
    the loser frees its own). .NET keeps a type's name as long as the type, so nothing is kept that .NET frees. A
    program saving under 8 KB keeps its literals (`CS2WASM_ALL_LAZY_NAMES=1` forces it; `CS2WASM_NO_LAZY_NAMES=1`
    turns it off). The Type.GetType table stores minus such a name's length and makes the name only for a lookup of
    that length (`TypeInfo.GetTypeByName`). Self-hosted compiler: 38,412 names, expanded data 20.9 to 12.8 MB.
  - Packed data that does not fit the start's memory is expanded through a window of up to 1 MB at the heap's start,
    refilled from the segment with memory.init (`DataCompression.EmitStreamDecoder`; a match that does not overlap
    what it copies is one memory.copy). Small programs keep the old expander byte for byte. Self-hosted compiler: 2.8
    MB of data in the module. `CS2WASM_PACK_WINDOW=n` forces a window (the suite passes with 4096).
  - Type tables' id lists (interfaces, generic arguments, nested types, constraints) are shared between types with
    the same list.
  - A layout that reflection pinned (an address taken as a number before the final layout) no longer keeps dead
    chunks' bytes: they are zero (packed to almost nothing), and a chunk whose address was taken counts as live.
  Hello world 27,541 bytes before and after; bench 372,676 to 372,166 bytes (0.91x NativeAOT, 16.0 MB vs 71.3 MB);
  test 036 528,820 to 512,342. The self-hosted compiler's hello world is byte-identical to the old one's, in the same
  time (14.2 s against 14.3 s, interleaved). Suite 179 passed checked and as shipped (also with lazy names and a
  4 KB window forced everywhere; 088's timer line and 135's WhenAny race fail under heavy load, before as after).
  **What is left is code, 36.2 MB:** generic instantiations 14.9 MB (81,417 functions), other methods 11.3, specialized
  copies (constant arguments) 2.7, dispatch stubs 2.0 (a binary search per stub; IEnumerator.MoveNext's is 115 KB),
  lambdas 1.4, arena machinery 1.1 (per-type dispatch: rt.retain_contents 274 KB, rt.destroy_now 188 KB,
  rt.arena_clear 173 KB), counting and destroy functions 1.0, static initializers 0.6. By namespace: Roslyn's C#
  8.3 MB, System.Linq 7.6 (one set of Select/Where iterator classes per instantiation, with ToArray, ToList, Fill,
  Contains... each), System.Collections.Generic 2.9. Byte-identical bodies left after merging: 0.5 MB (kept apart
  by their frames). The levers there: sharing code between instantiations over reference types (as .NET's
  canonical forms do), and which virtual overrides of the Linq iterators a call can reach. Also left in the data:
  3,476 full names (1 MB) the cast-failure helpers keep (`rt.throw_cast`, one literal per target type).
- **Baseline-tier code quality (stream K of docs/FINISH.md, 2026-11-20).** Measured first, side by side with a
  NativeAOT-LLVM build of the same sources (main de9e598; the old `naot-wasm-measure` was built from an older compiler
  whose Driver does other work), both profiled with `node --no-wasm-lazy-compilation --cpu-prof`: with lazy
  compilation a sample can wait 5-40 ms while a function is compiled on its first call and the wait is charged to
  whatever runs next (5 s of a 25 s profile; "2.8 s in SwitchExpressionBinder under Driver.ParamEscape" and
  "1.3 s in ImmutableArray.Builder.ToImmutable" were that, 3 ms and nothing in the eager profile). Unprofiled times
  are the same either way. Profiled, hello world: code 11.8 s against 4.1 s (the methods found on both sides 1.84x),
  runtime 11.7 s (arenas about two thirds) against GC and allocation 1.8 s, host 4.0 (1.7 of it the eager compile)
  against 2.1; by area, Roslyn's parser and lexer 5.3 s against 2.4, the rest of Roslyn 2.5 against 0.8, CoreLib 0.8
  against 0.3, dispatch stubs 0.6. Changes, each with a test or the suite's and its measurement:
  - `rt.array_clear` clears up to 256 bytes word by word: `memory.fill` is a call into the engine in the baseline
    tier, and Roslyn's `SyntaxListBuilder.Clear` clears its whole array at every reuse (403 ms of the function's own
    time).
  - A returned struct is made in the caller's destination: `return new S(..)` and `return F(..)` of the return type,
    outside every `try`, construct into the struct-return address as a declaration's value is made in its local, so
    such a method (`ImmutableArray<T>.Builder.ToImmutable`, and the many like it) has no frame temporary, no exception
    region to release it on the way out and no copy (`CS2WASM_NO_DIRECT_RETURN=1`: the old way).
  - `x = default` and `new S()` with nothing to run, into any struct place: copied from zeros nothing writes
    (`SyntaxParser.MoveToNextToken`'s `_currentNode = default`, a 72-byte frame temporary cleared, copied and released
    before; `CS2WASM_NO_ZERO_DEFAULT=1`).
  - Borrows: an element of an array a static holds borrows as an element of a stable array does (no threads: the
    static's key and the elements' key); a type's static initializer's own stores of its statics do not break a borrow
    of one of them (the borrow began with a read after the check that runs the initializer, so the initializer has run
    or is running further up the stack: what it stores next, it stores after the borrow's scope). 152 borrows in the
    parser probe, 138 before. `SyntaxNodeCache.TryGetNode`'s `var e = s_cache[i]` still counts: its call of
    `IsCacheEquivalent` reaches element stores (`GreenNode.GetSlot` of a token throws, and the message is built with
    `string.Concat`, number formatting, `Memmove`), and `[]` is every element store of the program; what that needs is
    which arrays a store can write, not a pass. `CS2WASM_REACH_WHY=fn>>store:key` lists what a function reaches that
    stores a key.
  - Inliner, second round (the final bodies, now run up to three times): a leaf of the program or the library up to
    the first round's 12 instructions goes in at every site (`Hash.Combine`, 5 instructions, was too large in the
    first round only for the local traffic it had then). And fast paths: a function of the shape `test; if { .. }
    end; value` whose test is whether a value read from memory is made yet (a field that is null, a type's
    initializer flag not set) and whose test and value are pure and short (8 instructions), called from 3 places or
    more, has the test and the value copied to its call sites and the call itself left on the other path: what is left
    out runs in the function's own frame, so a trace is the same (`SyntaxParser.CurrentToken`, `_currentToken ??
    FetchCurrentToken()`, from 874 sites; `Array.Empty<T>()`, `ArrayPool<T>.Shared`, `Encoding.UTF8`). A first try took
    any test, and `StringBuilder.Append(string)`'s `value != null`, which holds most of the time, made micro 2% slower.
    `CS2WASM_TINY_BODY`, `CS2WASM_FAST_PATH`, `CS2WASM_FAST_PATH_CALLERS` (0: off), `CS2WASM_INLINE_DEBUG=1`.
  - Constant locals (`ConstBranches.ConstLocals`): a local given a constant at the function's top level before
    anything reads it, and never written otherwise, is that constant at every read; a block whose body is a constant
    is the constant; `const; const; compare` folds. `switch (Unsafe.SizeOf<T>())` in a generic method then folds to
    its arm: Roslyn's `SegmentedArray<T>.SegmentShift` and `OffsetMask` were two calls each per probe of
    `StringTable.FindSharedEntry`'s loop (10x NativeAOT's time there) and are constants at the site now
    (`CS2WASM_NO_CONST_LOCALS=1`).
  - Tried and reverted: dispatch stubs through a `br_table` on the type id instead of halving the stretches
    (`GreenNode.GetSlot`: 256 stretches over 1,118 ids). V8's baseline tier compiles a `br_table` as a binary search
    over its index range, wider than the stretches' count: no faster for GetSlot, and interface stubs got slower (micro
    `iface_foreach` 1.21x, `iter_select_where` 1.21x, `virtual3` 1.18x).
  Measured: micro in the baseline tier (`--liftoff-only`, 5 interleaved rounds, best of each) 1.876x to 1.847x
  NativeAOT's time (geometric mean 0.985 of before: `sb_append_int` 0.80, `distinct` 0.93, `hashset_add` 0.95);
  the parser bench (`tools/roslyn-parse-bench.cs`: 400 classes parsed and walked, baseline tier, interleaved) 375 to 350.5 ms, median of 8 rounds (best 363 to 347, faster in every round); the self-hosted
  compiler (no threads, the same sources built by each compiler) 77,718,084 to 77,170,647 bytes, hello world on one thread 13.7-14.5 s to 13.3-13.6 s in the five interleaved rounds where the machine was quiet (NativeAOT-LLVM 5.0-5.1 s: 2.74x to 2.65x; median 3% less wall time, 1% less processor time; loaded rounds moved 15% either way and are left out), build time unchanged within the noise (675 s and 641 s). Hello
  world 27,868 to 27,745 bytes. Suite 188 passed checked and as shipped (rebased on main: 188 passed both ways, 088 once failing on a temporary file another shard held, then passing alone twice).
  Left, in the order the profile gives: the arena machinery (streams I and J), counting calls (rt.retain and
  rt.release 1.7 s profiled; inlining them everywhere was measured before, no gain), `rt.frame` (0.35 s; a call per
  function with a frame, kept for size), struct helpers (`rt.copy_assign` and its retains 0.4 s), dispatch stubs (a
  call through a binary search: inlined vtable calls need the table and its analysis buckets), and the parser's own
  code at 2.2x, flat.
- **Stream K, round 2 (2026-11-21), on main 3810a98 (green nodes out of the cycle graph).** Profiled again against a
  NativeAOT-LLVM build of the same sources, both eager: hello world 13.1 s against 5.9 s; code 7.2 against 3.0 (the
  methods found on both sides 1.40x), runtime 4.7 s, of it the count functions' own time 1.36 s (rt.release 0.64,
  rt.retain 0.48, rt.release_range 0.23), rt.frame 0.14, dispatch stubs 0.32. Measured on `tools/roslyn-parse-bench.cs`
  and micro in the baseline tier (one locked round per variant), the self-hosted compiler at the end. Kept:
  - Range releases pass all-zero elements eight bytes at a time (`rt.release_range` of 4- and 8-byte elements): a list
    builder's array is cleared whole at every reuse and is nulls past its count (0.20 s of rt.release_range was under
    rt.array_clear). Neutral on the parser bench and micro, which reuse no large builders; part of the self-hosted
    result below. `CS2WASM_NO_ZERO_SKIP=1`.
  - The counts' common case written out where they are counted (`Wasm/RcFastPaths.cs`, programs without threads): a
    retain of an object with a positive count adds one in place, a release of one whose count is above one takes one,
    anything else is the call as before. The upper bound first: the whole functions inlined everywhere
    (`CS2WASM_INLINE_RC=1`) made the parser bench 7% faster at 2.1x the module size. The fast path everywhere gave
    6%; only in the functions a loop calls, three calls deep, 4% (280.5 to 269 ms) at 13% more code there, micro 0.972.
    Not in the destroy functions: in hello world their many field releases were nearly all the growth (27,843 to
    30,823 bytes with them, 27,912 without). `CS2WASM_NO_RC_FAST`, `CS2WASM_RC_FAST_DEPTH`, `CS2WASM_RC_FAST_ALL`,
    `CS2WASM_RC_FAST_DESTROY`, `CS2WASM_RC_FAST_DEBUG`.
  - Frames of up to 64 bytes taken in place in the same hot functions (`Frames.cs`: the stack's end checked as
    rt.frame checks it, the pointer moved, the frame cleared word by word): parser bench 265 to 257.5 ms (median of
    12, quiet machine). Hello world unchanged. `CS2WASM_FRAME_INLINE=n`.
  Tried and dropped: the runtime's struct helpers (copy, move, retain and release of a struct's references) inlined in
  the hot functions: no gain (best 253 ms either way), 6% more code. Parked on branch `claude/k2-throw-only-wip`:
  borrows across calls whose stores happen only on the way to a throw (a second store summary of what a call can
  store and still return, with which functions can catch). Alone it adds no borrow in the parser probe (152); taking
  releases as storing nothing would give 185, so the lever is typed releases: what a release of static type T can
  destroy (`SyntaxNodeCache.TryGetNode` goes through `ConditionalWeakTable.Container`'s finalizer, reachable from any
  release today).
  Result: the self-hosted compiler (no threads, the same sources built by each compiler) 10.26 s to 9.94 s median wall
  time over 8 interleaved rounds on a quiet machine, faster in every round (NativeAOT-LLVM 5.03 s: 2.04x to 1.98x;
  processor time 1% less); 72,095,418 to 81,240,724 bytes (still 0.63x NativeAOT-LLVM's 129 MB). The four functions'
  own time 1.50 s to 0.92 s in the profile (taken on a busier machine: 20.4 s total against 13.1). Hello world 27,745
  to 27,912 bytes. Suite 195 passed checked and as shipped (rebased on main 5f7c98d).

- **Stream K, round 3 (2026-11-21), on main 26150c1: size back, speed kept.** Each of round 2's pieces measured on
  the self-hosted compiler itself, a build with it off against main, interleaved (6, 8 and 10 rounds; then 12 with the
  machine's load checked under 30% before each): the count fast paths were within 1% either way for 8.7 MB of the
  module's 82.3 MB; frames in place within the noise for 0.8 MB; the zero-skipping range releases within the noise, no
  size there, 98 bytes of hello world. The parser bench's 4% and 2.8% are taken back by the engine: it compiles the
  larger module too, and the self-hosted compiler runs over far more code than the bench. All three are off by default
  now (`CS2WASM_RC_FAST=1`, `CS2WASM_FRAME_INLINE=64`, `CS2WASM_ZERO_SKIP=1` to measure them). Added, each fewer bytes
  and the same meaning, a final tidy on the finished bodies (`Peephole.FinalTidy`): `i32.const 0; i32.ne` and
  `i32.eqz; i32.eqz` before an if or br_if, `local.set x; local.get x` left by the passes after the peephole, a pure
  push dropped at once, `i32.const k; i32.add` (k >= 0) before a load, or before a store whose value is one push, as
  the memory instruction's offset, and a body's last `return`. Tried and dropped: `call ThrowHelpers.X; unreachable`
  at a block's end without the unreachable. Fewer bytes, but in the baseline tier the code after the block then merges
  with the throwing arm's state (register moves): micro 6% slower (1.059 against 1.005 with every other step). Also
  dropped: count fast paths without their own null test (a null's count word at address 0 reads zero, so it takes the
  call; nulls are released often: micro 2.3% slower).
  Result: hello world 28,288 to 27,580 bytes on 26150c1 (27,869 to 27,194 on 3be4326, after stream N); the self-hosted
  compiler's module (named, no threads) 82,343,265 to 72,442,968 bytes, its time the same within the noise (12 rounds
  with the load checked: 10.53 s against 10.47 s for main without the count fast paths, processor time 20.59 against
  20.64 s; main itself within 1% of that build; NativeAOT-LLVM 5.17 s). Suite 197 passed checked and as shipped on the branch rebased on 3be4326.
  The profile of main (loaded machine, eager): the arena operations (stream N) and the counts' calls (0.66 s) the
  largest shares, dispatch stubs 0.48 s, `SyntaxNodeCache.TryGetNode` 1.4x NativeAOT's time; the parked borrow work
  (`claude/k2-throw-only-wip`) still waits for typed releases. Lesson for these measurements: the parser bench and micro
  overstate what inlining buys, and the self-hosted compiler's own timing needs a quiet machine (a check of the load
  before each round) to resolve 2%.

- **Stream K, round 4 (2026-11-21), on main 480a101: where the self-hosted compiler's time goes, and denser code.**
  Measured with V8's own switches on the self-hosted compiler and NativeAOT-LLVM's build of the same sources (hello
  world, one thread, interleaved, the machine's load checked before each round):
  - Compiling is not the large piece. A run compiles 30,000 of the module's 168,600 functions with Liftoff, lazily
    (NativeAOT's: 16,500 of 89,300); compiling every function eagerly adds only 0.5 s (11.2 s against 10.7). Tier-up
    to the optimizing compiler carries the hot code: `--liftoff-only` takes 17.2 s against 10.7 (NativeAOT's 7.0
    against 5.4); `--no-wasm-tier-up` changes neither (it does not turn the dynamic tiering off). So the
    baseline-tier proxies (micro `--baseline`, the parser bench) measure what most functions run in but not the hot
    paths, which is why inlining that pays there was within the noise on the compiler, and the optimizing tier's
    bench has to be checked as well.
  - Code density: our code section is 38.9 MB in 168,500 functions against NativeAOT's 21.7 MB in 89,300 (its 129 MB
    file is mostly data); per KB of code 21.9 locals against 10.2, 57 calls against 23.6, 26.7 ifs against 0.3, 2.9
    try_tables against 0. Liftoff makes 5.2 bytes of machine code per byte of ours (54.5 MB for the 10.4 MB a run
    compiles), 2.9 per byte of NativeAOT's (18.0 MB for 6.2). The static calls: rt.release 20.6% of them, rt.retain
    5.5%, null checks 8.6% (written out and as calls), the arena operations and frames most of the rest.
  Changes, each measured on the self-hosted compiler (10 to 12 interleaved rounds) and on the bench:
  - Locals coalesced on the final bodies (`Locals.Coalesce`, after every other pass, since the passes before read a
    local written once as one value): locals of one type never live at once share one, parameters included once
    they are dead (interference from each write against what is live after it, and among what is live at the entry;
    exception edges included). Locals in the parser bench 51,280 to 26,561 (p99 per function 39 to 12); micro in the
    baseline tier 0.923; the self-hosted compiler 1.5% to 2.3% faster in calm rounds (faster in 18 of 20), the
    machine code a run compiles 54.5 to 52.2 MB. Hello world 27,194 to 27,142 bytes. `CS2WASM_NO_COALESCE`,
    `CS2WASM_NO_PARAM_REUSE`.
  - The counts in loops: the whole of rt.retain and rt.release went into every loop (62,500 copies of rt.release in
    the compiler, each with its calls of rt.destroy and Rt.ArenaRelease, 6.5 MB). Without them the compiler was the
    same (wall 0.5% either way, processor time 2.2% less) but the bench lost 3% (0.94x to 0.97x NativeAOT: tree and
    linked_list free nodes in their loops). Now their fast path is written out in loops (`RcFastPaths`, default
    `loops`): a retain or release that moves a count above zero in place, the last reference's release as rt.release
    does it (count cleared, rt.destroy called), anything else the call. Bench 0.94x again, its module 343.7 to 334.0
    KB; the compiler the same speed (wall +0.2%, processor time -0.3%, 10 calm rounds) at 72.3 to 70.1 MB. Hello world
    27,142 to 27,297 bytes (loops of the runtime's own functions get the path too). `CS2WASM_RC_IN_LOOPS=1` for the
    whole functions, `CS2WASM_RC_FAST=0` for none.
  - Measured and kept as they are: null checks written out (as calls instead: the compiler 4.9% slower).
  - Not done, measured: 1.0 MB of the code is byte-identical bodies (12,458 functions; 1.3 MB counting calls of
    identical functions), kept apart for their frames in traces; the rest of the 1.8x code is generic instantiations
    over reference types not shared (a compiler change, not a pass).
  Result: the self-hosted compiler 72,430,423 to 70,080,934 bytes (named, no threads), 1% to 2% faster (processor time
  2.5% less against main in the last round, where the machine's wall times wandered); bench 0.94x NativeAOT as
  before; hello world 27,194 to 27,297 bytes. Suite 198 passed checked and as shipped (rebased on 05e8cd0), bench 0.94x there.

- **Fewer count operations (2026-11-21, branch claude/rc-fewer-counts, on main ec13733): where the counts come from.**
  - The measure: `CS2WASM_COUNT_RC=1` now keeps each count call's cause apart from a release's exact-type hint
    (`Instr.Cause`; before, the cause sat in Imm2, which ExpandLeafReleases reads as the hint: the measure could
    miscompile), charges a temporary's or a variable's release to the code that made the slot, names unproven borrows
    (with why a temporary's borrow failed and through which chain), stores' old values and inlined runtime counts, and
    counts the struct helpers' calls; `tools/rccauses.mjs run.txt [sites.txt]` sums a run by cause, the helpers'
    own counts charged to their callers. Build with `CS2WASM_RC_FAST=0` (the fast paths in loops hide calls).
  - The self-hosted compiler (named, no threads, main), hello world: 931 million calls of rt.retain and rt.release
    (348 million changed a count), 887 million count operations with the struct helpers'. By kind: clearing arrays of
    one-reference structs element by element (rt.release_range calling rt.release_struct: Roslyn's
    InternalSyntax.SyntaxListBuilder.Clear clears its whole array at every reuse) 15.6%; struct copies into fields
    (rt.copy_assign: the blender's BlendedNode, Cursor, tuples) 8.3%; declared locals' releases at their scope's end
    6.3% (their retains 1.9%); arguments' temporaries 4.6% (call results passed on, and retained fields 1.2%); a call's
    receiver held in a temporary 4.4% release, 3.1% retain; stores' old values 4.1% + 3.3% (by address); retains of
    stored values 3.6%; returns of a borrowed value 3.0%; struct results of calls held to the statement's end 2.5%;
    **unproven borrows** of a receiver 2.5% + 2.5%, of an argument 0.8% + ..., of a declared local 0.8%; destroy
    functions' field releases 1.7% + 1.1%. The parser bench's ranking is the same shape (61.2 million calls).
  - Why the receivers' borrows failed, in the parser bench: half of them through a destruction: a release can run
    `~AssemblyLoadContext`, which raises its Unloading event (`Action<AssemblyLoadContext>.Invoke`), and a delegate's
    call reached every function reflection can make a delegate of, reflection thunks among them, which reach the
    whole program. Now an indirect call reaches only the functions of its own wasm type (a call_indirect traps on any
    other before it runs): `IndirectTargets` filters by type and the store summary has a node per bucket and type.
    The parser bench's calls 61.2 to 60.15 million, counts changed 25.67 to 24.53 million (4.4% fewer).
    `CS2WASM_NO_INDIRECT_TYPES=1` for the old way. In the self-hosted compiler it changed nothing (930.8 against
    931.0 million calls): there the same finalizer's `Action<AssemblyLoadContext>.Invoke` reached a lambda of
    CSharpCompilation's (another `Action<>`, of the same wasm type), and through a `Func<,>` the parser's lambdas.
  - So a delegate's Invoke now reaches the targets of delegates made as its own constructed type, or as another
    instance of its definition that variance converts to it (`Action<object>` invoked as an `Action<string>`), not
    every instance of the definition (`Driver.DelegateCallBucket`, `DelegateConvertible`; unsure cases, type
    parameters, take the target). Test 187: borrows across variant invocations whose targets free the borrowed
    object (fails with variance left out). `CS2WASM_NO_DELEGATE_TYPES=1`. Parser bench 59.97 to 59.84 million calls.
    What still blocks the parser's receivers is the context-insensitive call graph itself: a virtual getter on the
    current token (`ContextualKind`) reaches SyntaxParser.ReInitialize somewhere below.
  - A switch expression whose arms are all borrowed is borrowed (as a conditional is; an owned arm makes the others
    counted, after the fact). Alone it moves the counts to the consumer (the parser's GetSlot bodies are
    `return index switch {...}`: counted at the return instead); `CS2WASM_NO_SWITCH_BORROWS=1`.
  - A store that runs only where its field holds null (`f ??= v`) breaks no borrow (it lets go of nothing; what v's
    own code stores is seen as before): `Driver.BorrowWrites`, FieldWrites without those stores (InitOnlyFields
    still sees them). `CS2WASM_NO_NULL_ONLY=1`.
  - A method returns a field borrowed also when its body is several statements that let go of nothing once a
    return has read its value (`Driver.BorrowableReturns`: no counted local, no parameter written, no closure, returns
    only inside blocks, ifs and while/do/for loops, the last statement a return; nulls allowed among several
    returns): `_f ??= Make(); return _f;` (LexerCache.TokenMap), loops that fill a field and return it. Checked where the borrowed form is compiled (a
    release on its way out is a compile error, not a premature free). `CS2WASM_NO_RETURN_FIELD_BODIES=1`.
  - Array.GetLength, GetLowerBound and Array.Copy at the boundary without a handle to a local copy of the array
    (`object arr = this; ObjectHandleOnStack.Create(ref arr)`: an address-taken local, so counted: 3.4 million pairs
    for GetLowerBound alone in the self-hosted compiler, from Array.Clear).
  - The parser bench after all of them: 59.97 million calls (from 61.22), 24.20 million counts changed (from 25.67,
    5.7% fewer); its time within this machine's noise (another session's heavy runs: medians 316 against 311 ms in the
    baseline tier, 6 interleaved rounds). Hello world 27,297 to 27,339 bytes. Suite 198/198 checked and as shipped on
    b2340ac.
  - The self-hosted compiler (0a968c8, named, no threads), hello world: 930.8 to 915.2 million calls, 348.3 to 337.7
    million counts changed (3.0% fewer), 887.4 to 871.9 million count operations with the struct helpers'. Its time
    on the homelab (shared with other sessions; 6 interleaved rounds, the first three quiet): main 10.47, 9.93, 10.32 s,
    the branch 10.08, 9.96, 9.76 s, NativeAOT-LLVM 4.30, 4.18, 4.38 s (2.39x to 2.32x); the three loaded rounds even.
    Module 70,235,738 to 70,355,112 bytes, peak memory the same (306.0 MB).
  - Merged with main fbe7688 (aa47231), on the homelab: tools/roslyn-bind-bench.sh, 5 interleaved rounds, medians:
    main 6,843 ms (parse 4,157, bind 2,136), the branch 6,713 ms (parse 4,036, bind 2,081): 0.981 of main, 2.16x
    NativeAOT-LLVM's 3,104 against 2.20x (the bench's resolution is about 2%). bench.mjs 0.92x NativeAOT, 16.0 MB
    against 71.3; micro --baseline 1.70x; suite 197/197 checked and as shipped (the homelab has no Godot sources);
    hello world 27,067 (main) to 27,109 bytes.
  - Where the counts are, by the code that runs them (the self-hosted compiler, hello world): two thirds in the lexer
    and parser (SyntaxParser, Blender, Lexer, the token caches), about 8% in symbols, 1% in the binder and bound
    nodes. So the binder's 4x against NativeAOT (the bind bench) is not its count operations; what is left of the
    counts is the parser's, where its parse phase is 1.8x.
  - Left, by size: the array clears of struct elements (a runtime matter: the zero-skipping range release, or knowing
    the builder's count), struct copies into fields (moves where the source dies), receivers that are fields of a
    struct `this` (`SyntaxListBuilder<T>.Add` forwarding to its `_builder`: a struct's field can be overwritten by
    whole-struct copies, which note no field key), borrowed returns through user-defined conversions
    (`return _lexedTokens[i];` through `ArrayElement<T>`'s implicit operator: FetchCurrentToken, PeekToken), and
    virtual methods whose every override returns a field (GreenNode.GetSlot).
- **Stream K, round 5 (2026-11-21), on main 1191d18: shared bodies, and what TurboFan refuses to inline.**
  Round 4 left the rest of the 1.8x code as generic instantiations over reference types, each a body of its own.
  - Shared bodies (`Wasm/ShareConstants.cs`, on by default; `CS2WASM_SHARE=0` off, `CS2WASM_SHARE_LEND=0` without the
    second pass, `CS2WASM_SHARE_STATS=1` prints the groups). The groups come by partition refinement: a function's key
    masks its i32 constants and names each call by the callee's group, until the groups stop splitting; so functions
    identical except for i32 constants and for which member of such a group they call share one body. The constants
    that differ become the shared body's trailing parameters; each member becomes a stub passing its own.
  - Traces stay exact. Pass 1 takes only groups whose members have one frame name: the shared body keeps that name,
    the stubs are hidden and direct calls go straight to the shared body. Pass 2, over what pass 1 left, takes groups
    whose names differ: each member stays a frame, a stub every call goes through, and the shared body lends its
    place (lines and inlined calls) to the frame of the stub that called it, through a new trailing list in the
    cs2wasm.frames section that host/engine.mjs and host/web read. Positions in the library's files are not compared
    (a trace shows no lines there). Pass 1 runs first because lending groups had kept one-name groups from their
    shared body (the self-hosted compiler lost 4.5 MB to that). `tools/k5/lend.cs` throws through 35 library paths
    that call back into user code; its output is the same with sharing on and off.
  - Result: the self-hosted compiler's module 70.2 to 56.8 MB, wall time 1.0% less than the same compiler with
    sharing off (12 interleaved rounds); hello world 27,297 to 27,067 bytes; bench.mjs 0.94x NativeAOT unchanged.
    Suite 198/198 checked and as shipped (rebased on 23d5188).
  - TurboFan inlining diagnosed (`tools/k5/tinl.mjs` over `--trace-wasm-inlining`, both compilers): with the inlining
    budgets x4 ours runs 5.6% faster, NativeAOT-LLVM's 3%. Most inlines refused for budget are calls of rt.release
    (6,768) and rt.retain (2,160). Conclusion: the budget is spent on counts; the lever is fewer count calls (borrows,
    counts elided at compile time), not larger budgets or inlining by hand.

- **The self-hosted compiler's phases on the three sides, and the libraries a program is compiled with (2026-11-22,
  branch claude/selfhost-speed-1 on main cb287fb).** Measured before anything was changed: one compile of
  `tests/Conformance/145_size_hello.cs` by the self-hosted compiler (built without threads, one processor), by
  NativeAOT-LLVM's WebAssembly build of the compiler in the same Node (the build of 2026-11-20: the homelab has no
  Emscripten SDK and this machine refuses heavy work, so it was not built again), and by the compiler under .NET on
  one processor (`DOTNET_PROCESSOR_COUNT=1`) and on all sixteen. `CS2WASM_TIMES=1` now splits the library's part
  (what the program needs; each library's files, characters, reading, parsing; the compilation), and
  `CS2WASM_HOST_TIMES` the module made from its instantiation; `tools/selfphases.sh` runs the sides in turn
  (`NAMES=1 PROF=1`: a profile of each; `ALLCORE=1`: both library rules; `VERIFY=1`: `tools/selfhost-verify.sh`).
  The homelab, the last of six interleaved rounds, milliseconds; "15" is every core library read (as it was, and
  as `CS2WASM_ALL_CORE_LIBS=1` still does), "9" the new rule below:

  | phase | self-hosted, 15 | self-hosted, 9 | NativeAOT-LLVM, 15 | .NET one processor, 15 | .NET one processor, 9 | .NET all, 9 |
  |---|---|---|---|---|---|---|
  | node up and the file read, the module made, instantiate and init | 23 + 29 + 18 | 23 + 29 + 18 | not split | | | |
  | what the program needs (Roslyn's used references) | 223 | 225 | in the next line | 453 | 451 | 472 |
  | the libraries read and parsed | 5,153 | 4,253 | in the next line | 4,851 | 3,775 | 843 |
  | library ready (the two above, the program's own parse, the references) | 5,916 | 4,963 | 2,977 | 5,758 | 4,727 | 1,766 |
  | program diagnostics, roots | 16 | 16 | 4 | 27 | 28 | 31 |
  | compile (first drain; 490 functions) | 1,979 | 1,951 | 447 | 1,085 | 1,102 | 1,158 |
  | exports, reflection (second drain) | 43 | 40 | 8 | 27 | 27 | 27 |
  | static initializers again, arenas, finish | 9 | 7 | 3 | 9 | 8 | 9 |
  | encode (the Step phases of WasmModule) | 192 | 192 | 59 | 381 | 361 | 320 |
  | the compiler's own total | 8,641 | 7,593 | 3,640 | 7,617 | 6,581 | 3,640 |

  - Where the gap to NativeAOT-LLVM is (like for like, 15 libraries): the library's part 5.9 s against 3.0 (2.0x,
    2.9 s of the 5.0 s between them), the first drain 2.0 s against 0.45 (4.4x, 1.5 s), the encoder 0.19 against
    0.06. Not at the start: Node, the module (made lazily) and the instantiation are 70 ms.
  - The engine's tiers (the build of a sibling branch, hello world, three interleaved rounds beside other sessions'
    jobs): as it is 10.4 to 11.0 s; `--wasm-tiering-budget=1300000` 10.1 to 10.8; `=130000` 9.5 to 10.2 (about 5%
    less: the optimizing tier at a hundredth of the default budget); `--no-wasm-lazy-compilation` 10.3 to 11.0;
    `--no-liftoff` 30.7 to 31.9; `--liftoff-only` 18.5 to 19.1. Nothing a host should set: the one that gains
    is an engine's tuning number, and its gain is inside the load's spread.
  - The profile (named build, `node --cpu-prof`, a run of 15.5 s beside other jobs, 14.0 s of it busy): counting
    1.77 s (rt.release 0.51, rt.retain 0.37, the BlendedNode copies 0.32), the arena functions 1.8 s
    (Rt.ArenaRelease 0.35, ArenaUnlinkOld 0.28, ArenaLink 0.25, LinkMembers 0.19, SlotOwner 0.16, ArenaRetain 0.16,
    ArenaUnlinkAt 0.14, ArenaVisit 0.13), dispatch stubs 0.50, allocation 0.26; the rest flat over Roslyn, its
    largest function `Lexer.QuickScanSyntaxToken` 253 ms where NativeAOT-LLVM's is 235 ms. NativeAOT-LLVM's 6.8 s
    profiled: its collector 1.5 s (relocate 0.42 and 0.05, mark 0.35 and 0.07, cards 0.30, plan 0.13, compact
    0.09), the file calls 0.66, `SyntaxNodeCache.TryGetNode` 0.17 (ours 0.23), `Array.Clear` 0.09 (0.08). So the
    runtime's own share is 3.6 s of ours against 1.5 s of theirs, and the compiled code 10.4 s against 4.6 in
    these two profiled runs, spread over everything.
  - The first drain, where the ratio is largest (`tools/proftree.mjs`, new: a profile under a function by what it
    calls, inclusive): of 3.3 s profiled under `Driver.Drain`, Roslyn's `GetOperation` is 1.96 s, 1.11 s of it
    through `Driver.ConsumingOf` (`ComputeConsuming` binds each callee's body for the ownership of its parameters;
    the "binding bodies" line of CS2WASM_TIMES counts only the 245 ms of the bodies compiled), `MakeAllMembers`
    0.61 s, `LoadAndValidateAttributes` 0.51 s; by self time a quarter is the arena functions. Binding makes and
    links symbols and binders, which are members of the one large group (Step 7); parsing makes green nodes, which
    are not. Not changed here.
  - **Changed: a program is compiled with the core libraries it can reach, not all fifteen.** Every program had
    the first fifteen libraries read and parsed, 35.3 million characters, whatever it used: for hello world
    System.Runtime.Numerics, System.Threading.Tasks.Parallel, System.Text.RegularExpressions,
    System.Linq.Expressions, Microsoft.CSharp and System.Linq.Queryable are 7.5 million characters parsed for
    nothing. `UpstreamSources.Needed` now starts from CoreLib and takes the libraries up to the last one whose
    reference assembly the program's code uses (Roslyn's `GetUsedAssemblyReferences`, which already found the
    optional libraries). Why that is all a program can reach: a core library is compiled against those before it
    and no other (`Library.References`), so none after the last one used is referenced by any that is read; a
    reference assembly's public types are in the core library of its own name or in CoreLib and in no other core
    library (`CS2WASM_REF_LIBS=1` lists, for every reference assembly, the libraries its types are found in: checked
    over the 176 of the pack); and what the C# compiler itself calls for a construct (a `dynamic` operation's
    binders, an expression tree's factories, a query's methods) is among the references Roslyn reports, which errs
    to the other side: a call written as an extension (`xs.Select(...)`) reports every assembly with a candidate
    of that name, so a program that uses System.Linq that way is reported as using System.Collections.Immutable
    and is given 22 libraries, as before (the same call written `Enumerable.Select(xs, ...)`: 8). A program that
    needs an optional library has every core library, as before. `CS2WASM_ALL_CORE_LIBS=1`: all fifteen.
  - The check, `tools/corelibs-check.mjs`: every conformance program, the benchmark programs (frames, bench, micro)
    and six programs without Console compiled
    both ways give the same module byte for byte: 202 of 202 on the homelab at 01fea5b (127 of them with 9
    libraries, 13 with 10 to 16, 62 with 22 or more), and six more here, without Console (`tools/corelibs/`:
    CoreLib alone and System.Collections' types 2 libraries, BigInteger 10, an expression tree 13, a `dynamic`
    operation 14, System.Linq 22). So no module
    changes: hello world's size and the frame and bench scenes' code are what they were.
  - Measured (the homelab, nine interleaved rounds over two builds, 66fd2b1 and ffc44b3, other sessions' jobs
    beside; hello world, wall time of the process): the self-hosted compiler 8.08 to 8.72 s (median 8.20)
    against 8.90 to 11.25 (median 9.11) with all fifteen; .NET on one processor 6.64 to 7.06 (6.69) against 7.57
    to 8.31 (7.72); .NET on all processors 3.73 to 4.07; NativeAOT-LLVM's build of 2026-11-20, which reads all
    fifteen, 3.76 to 4.66 (median 3.96). The self-hosted
    compiler's peak memory 344.2 MB to 272.0 MB (5,252 to 4,151 pages; NativeAOT-LLVM's 322.2 MB is the figure on record, not measured again). Test
    088 (22 libraries either way, so the same work as before): 82.7 s self-hosted against 30.9 s under .NET;
    tests 220 and 193: 18.4 and 17.2 s against 8.6 and 8.0.
  - Checked: the self-hosted compiler built at ffc44b3 (32,886,996 bytes, built in 244 s; the build at 66fd2b1 likewise) writes hello world, 220, 193 and 088 byte for byte as the .NET-hosted compiler (`tools/selfhost-verify.sh`: 27,741, 273,319, 255,397 and 1,616,434 bytes; hello world below the 28,383 of test 145, which the suite runs). The suite on the homelab at ffc44b3: 230 of 230 checked and 230 of 230 as shipped (349 s). Not run: tools/frames.mjs and tools/bench.mjs (their programs' modules are byte for byte what they were).
  - Not done, in the order of what they would give: (1) a program that calls System.Linq's
    methods as extensions (48 of the conformance programs are given exactly 22 libraries; most real programs
    would be) still reads 22 libraries, 38.4 million characters where 27.8 would do: the libraries Roslyn
    reports only for candidates. A rule that stays right: begin without the libraries reported but not
    named, and begin again with them when the compiler meets a type whose library was left out
    (`Types.Canonical`, where `Library.FindType` finds none), as it begins again for threads. (2) CoreLib itself
    is 25.7 of every program's characters; nothing here reads less of it. (3) The first drain's 4.4x and the
    parser's 2x are the group machinery and the counts (Step 7's value sources, 4.1's borrows), unchanged.
    (4) NativeAOT-LLVM's build of today's compiler, for its time with the same rule (by its share of the
    characters about 0.6 s less than its 4.0).

- **The self-hosted compiler's build time, second round: what it reads, what it keeps from one build to the next,
  and where the gap to NativeAOT-LLVM is (2026-11-23, branch claude/selfhost-speed-2 on claude/round-9 d430594).**
  Measured first, on the homelab, with `tools/selfphases.sh` (named build, `node --cpu-prof` of each side) and the
  new `tools/selfprof-report.sh` (the two profiles by phase, who calls the runtime's functions, the two sides method
  by method); the self-hosted compiler without threads on one processor, NativeAOT-LLVM's WebAssembly build of
  2026-11-20 in the same Node (it reads fifteen libraries and parses their documentation comments; it cannot be
  built on the homelab and was not built again).
  - **Where the time is, one hello-world compile, profiled** (after the documentation comments below; self-hosted /
    NativeAOT-LLVM, milliseconds): everything 7,740 / 4,073; compiled code 4,858 / 2,548; the runtime's memory
    management 2,424 (counts, arena functions, allocation) / 923 (its collector); the host's functions 329 / 539.
    The 230 methods both profiles name take 1,540 / 1,326 (1.16x): `Lexer.QuickScanSyntaxToken` 156 / 192,
    `SyntaxNodeCache.TryGetNode` 48 / 85. So the gap is in no one function: it is the memory management (2.6x the
    collector's cost) and the long tail of small functions, where a call costs what NativeAOT-LLVM's inlining
    removed (`StringTable.TextEquals` 23 / 2: `span.Equals(span, Ordinal)` through three calls that pass spans in
    memory; `IsCurrentTokenDefinitelyPartialModifier` 21 / 2: a method with a captured variable, see the leads).
    - The parse (under `Library.BuildOne`): 3,291 / 2,923, at unequal work (ours without the documentation
      comments, theirs with them and six more libraries). Ours: counting 134 (`rt.release`) + 81 (`rt.retain`) + 75
      (`rt.release_range` of the parser's token window, cleared whole after each file: 4,096 elements for each of
      1,813 files, which NativeAOT-LLVM clears as memory, 41) + 56 (`SyntaxParser._currentNode = default`, a
      struct of references assigned for every token), the arena functions about 150, dispatch stubs 39
      (`GreenNode.GetSlot`); theirs: the collector 650 (relocate 193, mark 143 + 29, cards 86 + 59, plan 74,
      compact 30). For the parse the two memory managements cost about the same.
    - The first drain (under `Driver.Drain`): 2,370 / 488. Ours by self time: the arena functions 21%
      (`ArenaRelease` 117, `ArenaRetain` 84, `ArenaLink` 67, `LinkMembers` 67, `ArenaVisit` 50, `SlotOwner` 42,
      `ArenaUnlinkOld` 36), counting 5%, the rest flat over Roslyn's binder and the compiler. Who calls
      `ArenaRetain` and `ArenaRelease`: getters that return a member (`Symbol.ContainingSymbol`, virtual, 12% of
      `ArenaRetain`; `LexerCache.LookupToken`; `Binder.FieldsBeingBound`): the member's first reference from
      outside comes and goes around each use, a call into the runtime each way. `SlotOwner`:
      `Interlocked.CompareExchange` on a field of a pooled object (`ObjectPool<T>.Allocate` and `Free`) 28%.
    - `Driver.ConsumingOf` binds each callee's body through the semantic model of its tree, which keeps what it
      bound: the compile of that callee then finds it bound, so the work is done once; and the libraries' models
      are kept for the process (`Driver.s_libModels`), so a later build in the same process, or on a snapshot
      (below), finds the bodies of the last one bound: the first drain 2.1 s to 1.3 to 1.4 s.
    - One exception is thrown in a hello-world compile: Roslyn's `CaseInsensitiveComparison` tries
      `new CultureInfo("en")` and falls back to the invariant culture, as under any runtime with invariant
      globalization. The throw asks the host for three stacks, and the first made it read all of the module's
      debug sections: 232 ms (`CS2WASM_HOST_TIMES=1` now counts them; `=2` prints each stack taken).
    - The engine's tiers: a build on a snapshot runs mostly in the baseline tier (its entry 2.1 to 2.3 s, where
      the same build as a later call in an instance that stayed is 0.64 to 0.68 s); `--wasm-tiering-budget=1300000`
      gave nothing there (3.52 to 4.52 s against 3.00 to 4.02, five rounds in turn). Node cannot keep a module's
      compiled code (`v8.serialize` of a module does not come back); a browser's cache of compiled WebAssembly
      would.
  - **Changed, each with its switch. No generated code changes: every module is byte for byte what it was.**
    - **The libraries' documentation comments are passed over as plain comments** (`Library.BuildOne`: the
      libraries are parsed with `DocumentationMode.None`; `CS2WASM_NO_LIB_DOC_SKIP=1`: parsed, as before). Parsed,
      every `///` comment is an XML tree made by Roslyn's second parser, and nothing reads them; the trees' text,
      the positions and the symbols are the same. CoreLib's parse 3,658 to 2,278 ms self-hosted (2.9 to 2.2 s
      under .NET on one processor), the peak memory 272.0 to 241.8 MB.
    - **A library Roslyn reports as used only for a candidate is left out at the start, and the program is
      compiled again with every reported library when it meets a type that one left out may hold**
      (`UpstreamSources.Needed`, `Library.Lookup` and `TypeByMetadataName`, `Cs2WasmCompiler.Compile`;
      `CS2WASM_NO_LIB_RESTART=1`: every reported library from the start, as before). A call written as an
      extension reports every assembly that has an extension method of that name: `xs.Select(...)` reports
      System.Linq.Queryable, System.Linq.Parallel and System.Collections.Immutable (`ImmutableArrayExtensions`
      is in the namespace System.Linq), and with the last came the thirteen libraries before it. At the start a
      reported assembly that has extension methods is taken only when the program's text has a word that names
      it: one of its public top-level types, or the last part of a namespace it has public types in, which no
      reported assembly earlier in library order has too (`Linq` is System.Linq's; `Queryable`, `ImmutableArray`
      and `Frozen` are their own); an assembly without extension methods is taken as reported. Why the result is
      the same: the libraries read are those all reported would give, without a tail (in library order, the
      standalone ones aside), so a reference assembly's type that the search finds visible in a library before
      the first one left out is the type the search over all of them finds (it goes in library order and stops at
      the first visible type). Anything else a reference assembly's type comes to (none, or one not visible, which
      a later library's would replace), and a type asked for by name that is not found, or found past the first
      library left out, sets `Library.NeedsAll` and throws; `Compile` then compiles the program with every
      reported library, which is the compile of before. The one question exempt is whether System.Linq.Queryable
      has kept generic instances, which exist only when the program reached its type. A program's own types are
      not asked about (they were not before either: the rule of the core libraries has the same gap).
      `CS2WASM_BREAK_LIB_RESTART=1` takes what the libraries read give and compiles nothing again: test 250 (a
      queryable sequence reached only through `AsQueryable()`, begun with 13 libraries) then gets
      PlatformNotSupportedException where it passes with the rule, and with the rule it builds the same module as
      with `CS2WASM_NO_LIB_RESTART=1`. A program that calls System.Linq as extensions: 9 libraries where it had
      22 (tests 008, 193, 220), 13 for test 088 (expression trees).
    - **The host keeps a module's state from one process to the next** (`host/snapshot.mjs`,
      `CS2WASM_SNAPSHOT=<file>`; SPEC 7.2 and 7.6). When the entry has returned 0 the module's memory (the pages
      that are not zero) and its mutable globals are written to the file; a later process puts them back in
      place of `init` and calls the entry on them. That is the second call of the entry in one process, in a new
      one: for the compiler, what `--watch` does, the libraries parsed and their bodies bound. A snapshot is used
      only while what it was made from is unchanged: the module's bytes (their hash), the environment block the
      module is given (every variable: a module reads it once), and every path the module asked the file system
      about while the state was made (opened, listed, asked whether there: the same size and time of change, or
      still absent), except the paths its command line names (an argument that is no option and no `name=value`,
      those under it, those in an `@file`) and the paths it wrote, which the entry reads anew on every call. None
      is taken of a module that started a thread (a thread's stack is not in memory), has a file open, moved its
      working directory or used HTTP. After a run on a snapshot the file is written again only when the module
      read files the snapshot does not cover (a library the earlier programs did not need), so otherwise every
      build starts from the same state; `CS2WASM_SNAPSHOT_KEEP=1` never writes one that holds again. One cut
      short, or larger than the memory may grow, is not used. `tools/snapshot-check.sh` proves each rule on a
      small module (nineteen steps against expected output); `tools/selfsnap.mjs` builds programs on a snapshot
      and compares each module with the .NET-hosted compiler's. It is the host's, for any module; nothing in the
      compiler knows of it.
    - **The host reads a module's debug sections when a stack asks** (`host/engine.mjs`, `debugInfo`, and its
      copy for the browser): names are decoded for the frames shown, the tables are numbers in typed arrays, and
      a throw's two frame counts need neither names nor lines. Compared with the reader of before at every code
      offset of four modules: the same frames. The compile's three stacks 232 to 143 ms. And one buffer for the
      file reads, where each read made a new zeroed one.
    - `CS2WASM_TIMES=1` also prints the time of the compilation that is thrown away when the start of a thread is
      met; `CS2WASM_REPEAT=n` (host) calls the entry n times in one instance.
  - **Tried and taken out: compiling for threads at once.** A program that starts threads is compiled without
    them up to the start of a thread, then begun again for threads: 6 to 9.5 s of test 088's 26 to 36 s under
    .NET on the desk, 17.0 of its 80 s self-hosted (4,632 of its 14,054 functions). Beginning for threads at
    once, when the text names a thread's start or awaits, or when the last build of that name in the process
    started one (kept only when the compilation does start one, so the module is the same: an API check proved
    it), was no faster under .NET: 47 to 56 s of processor time either way, three runs each, and the same within
    the noise by the clock. Much of what the first compilation does is Roslyn's first binding of what it
    touches, which the one that follows uses. Not measured on the self-hosted compiler (the job was withdrawn for
    the homelab's queue): by its phases about 5 of test 088's 80 s (the compilation for threads that has
    everything bound takes 14.7 s for the 14,054 functions, so the 4,632 thrown away are about 4.9 s of code
    generation in their 17.0; the rest is first binding, which is used). The two
    commits are decfc45 and 34c315c.
  - **Measured** (the homelab, other sessions' jobs beside, at times heavily; the self-hosted compiler built at
    d4ac9d6 without threads, 32,934,974 bytes in 252 s, told one processor; wall time of the process; "as it was":
    the same build with `CS2WASM_NO_LIB_DOC_SKIP=1 CS2WASM_NO_LIB_RESTART=1`; NativeAOT-LLVM's build of
    2026-11-20; seconds, lowest to highest and the median):

    | hello world (test 145) | self-hosted | NativeAOT-LLVM in the same rounds | peak memory |
    |---|---|---|---|
    | as it was, from the start (five rounds in turn) | 8.13 to 9.01 (8.45) | 3.88 to 4.37 (4.06) | 272.0 MB |
    | now, from the start (the same rounds) | 6.79 to 8.32 (7.19) | | 241.8 MB |
    | now, on a snapshot (five rounds, the build of a2ef8de) | 2.34 to 3.01 (2.65) | 3.89 to 5.13 (4.07) | 241.8 MB |
    | now, on a snapshot (five rounds, the build of d4ac9d6, the machine loaded) | 3.00 to 4.02 (3.66) | 4.49 to 5.95 (4.78) | 241.8 MB |
    | now, the second to fourth build in one instance (`CS2WASM_REPEAT=4`) | 0.68, 0.66, 0.64 (the first 6.99) | | |

    The phases from the start, as it was / now (ms): the libraries read and parsed 4,463 / 3,020, library ready
    5,144 / 3,716, first drain 2,178 / 2,093, encode 235 / 191, the compiler's total 7,974 / 6,412. On a
    snapshot: what the program needs 98, library ready 142, first drain 1,443, second drain 120, encode 375, the
    entry 2,301, the process 2,503; the snapshot 233.8 MB, written in about 200 ms, put back in 50 to 90 ms with
    its 2,023 paths checked.
    Larger programs, three rounds in turn while the machine was loaded (as it was / now / NativeAOT-LLVM): test
    220 (System.Linq as extensions: 22 libraries / 9) 27.2 to 28.7 / 23.8 to 29.8 (23.8, 24.2, 29.8) / 10.2 to
    10.5 s, peak memory 435.6 / 344.2 MB, library ready 10.6 / 6.5 s, the first drain 11.1 / 11.6 s (2,817
    functions); test 088 (22 / 13 libraries; threads) 79.9 to 87.5 / 78.2 to 91.0 / 92.9 to 94.9 s, 993 MB
    either way. On the snapshot hello world wrote (two builds each, loaded): 220 11.9 and 11.4 s; 193 9.9 and
    10.2; 088 76.4 (it reads four more libraries and the snapshot is written again: 915 MB, the size of that
    compile's memory) then 66.4; hello world on that snapshot 4.9 s at 993 MB. Test 220 built four times in one
    instance: 22.1, then 7.3, 7.2 and 6.1 s.
  - **What the numbers say.** From the start hello world is 1.77x NativeAOT-LLVM's build as it stands (it was
    2.1x), and that build does more work (fifteen libraries, their documentation comments): at equal work the
    ratio is not better than the 2.3x of the note above, because nothing here changed the code the compiler
    makes. What changed is the work. A build from the start does less of it, and a later build does not read the
    libraries at all: on a snapshot hello world is 0.65x to 0.77x NativeAOT-LLVM's build from its start, and in an
    instance that is kept it is two thirds of a second. That is the budget's "a few seconds" for a small
    program, for every build but the first. A middle-sized program stays at its own compile: test 220's later
    builds are 6 to 7 s for 2,817 functions with everything bound and the code optimized, two milliseconds a
    function of the compiler's own work, and from the start it is 2.3x NativeAOT-LLVM's. The largest, test 088,
    is below NativeAOT-LLVM's build (0.86x by the medians; its collector on a gigabyte of heap).
  - **Checked.** On the homelab. The suite at 713fdb0 (the last commit that changes code or the host; the compiler is
    d4ac9d6's): 237 passed, 0 failed and the 2 known gaps, checked and as shipped (493 s; test 250 among them, and
    test 145's limit of 28,383 bytes: hello world is 27,741). The self-hosted compiler built at d4ac9d6
    (32,934,974 bytes in 252 s) writes hello world, 220, 193 and 088 byte for byte as the .NET-hosted compiler
    (`tools/selfdet.mjs`: 27,741, 276,629, 258,686 and 1,617,407 bytes; hello world in 6.9 s), and so does every
    build on a snapshot (`tools/selfsnap.mjs`: those four, each on hello world's snapshot twice, and hello world
    again on the snapshot test 088 left), and the fourth build in one instance. Stress
    (`tools/stress-also.sh`, 200 loaded runs a mode, at b4f0840): test 250 and test 139 the same in every run of
    both modes; test 127 differed in 5 of 200 as shipped and 3 of 200 checked, each with `async 5 / pending 1`,
    the open item of docs/FINISH.md's table (nothing here changes its module). `tools/snapshot-check.sh`: as
    expected. The host's new reader of the debug sections: the same frames as the old one at every offset of four
    modules, and the suite's trace tests. Queued on the homelab when this was written, results in docs/FINISH.md's
    entry if they came: `tools/libs-rules-check.sh` (every conformance and benchmark program compiled with the two
    rules and with both switches: the same bytes; here it was run on some twenty programs, all the same),
    thirty more programs on a snapshot, and a profile of a build on a snapshot (`tools/selfsnap-prof.sh`).
  - **Checked again after main was merged (4746983, main 4a42328), 2026-11-23 morning, one homelab turn
    (`tools/selfhost-speed-2-verify.sh`; other sessions' jobs beside).** The suite 245 passed, 0 failed, 2 known
    gaps, checked and as shipped. The self-hosted compiler built (253 s, 33,265,837 bytes); hello world 6.77 to
    7.20 s as it is against 8.11 to 8.15 with `CS2WASM_NO_LIB_DOC_SKIP=1 CS2WASM_NO_LIB_RESTART=1` (the same
    module, 27,731 bytes), peak 241.8 MB against 272.0; NativeAOT-LLVM's build of 2026-11-20 3.76 to 3.96 s (three
    rounds in turn); four builds in one instance 11.4, 1.16, 1.10, 1.09 s, the fourth the same module.
    `tools/selfdet.mjs`: self = net byte for byte on 145 (27,743), 220 (279,559), 193 (262,743), 088 (1,616,137).
    **The snapshot, what is checked:** (a) `tools/selfsnap.mjs`: every module written on a snapshot is the
    .NET-hosted compiler's byte for byte: hello world five times, then on hello world's snapshot 220, 193, 088 and
    two programs with threads (139 and 197), each twice (088 read more and wrote the snapshot again; the second
    088, 139, 197 and hello world after them on that larger snapshot); the builds from the start also equal. (b)
    `tools/selfsnap-stale.mjs` (results in docs/FINISH.md's entry): another version of the source at the same path
    (read anew, the snapshot holds, the new module), a source that needs System.Linq, then the first again; another
    build of the compiler (let go); another reference directory (let go); a reference assembly's time of change
    (let go); a file added to the reference directory (let go); every module the .NET-hosted compiler's. (c)
    `tools/snapshot-check.sh` (the host's rules on a small module, on the desk at the merge): as expected. **Not
    covered:** a file rewritten with the same size and the same time of change (a path's state is size and time,
    as make and MSBuild judge; by design); references given with `-r`, which are named on the command line and so
    read anew (not tried); the threaded compiler and the browser's host have no snapshot. The snapshot is used only
    when `CS2WASM_SNAPSHOT=<file>` is set: off by default. Timed in turn (five rounds, the homelab loaded): hello
    world from the start 9.39 to 10.47 s (median 9.61), on the snapshot 3.41 to 3.83 (3.54), NativeAOT-LLVM's
    build 5.15 to 5.92 (5.24): on a snapshot 0.68x NativeAOT-LLVM's, from the start 1.83x.
  - **Not done, not reached, not measured.**
    - The ratio at equal work: the counts (rt.retain, rt.release, the struct copies), the arena functions and the
      cost of a call are where they were. Leads the profiles give, none built: a member's first outside reference
      taken and given back around a getter's result (`ArenaRetain`/`ArenaRelease`, 0.3 s: a borrowed return for
      virtual getters whose every implementation returns a field); a method with captured variables pays a frame,
      a fill, a count on `this`, an exception region and a drop on every call, even when it returns at its first
      test (`IsCurrentTokenDefinitelyPartialModifier`, 21 ms against 2: a frame display's `this` and unwritten
      parameters need no count, and a display with nothing counted no region); `x = default` of a struct with
      references retains and releases an empty temporary (`SyntaxParser._currentNode`, 56 ms); the token window
      cleared element by element (`rt.release_range`, 75 ms: `CS2WASM_ZERO_SKIP=1` exists and was measured as no
      gain before); spans passed in memory through small calls (`StringTable.TextEquals`, 23 ms against 2); the
      compiler's own two milliseconds a function (under .NET's profiler `Types.Resolve`, `Canonical` and `Key`
      are 10% of a drain, two thirds of it making a constructed symbol's key and weak-table entry anew; `PreScan`
      14%; the callee analyses that bind bodies 26%. A memo of `Types.Resolve` per map and type symbol was
      written and gave the same modules; the loaded desk could not resolve its gain, and it was not kept).
    - NativeAOT-LLVM's build of today's compiler (it needs the Emscripten SDK, which only the desk has, and the
      desk takes no heavy work): its time with the same rules is not measured. By its profile the documentation
      comments are 0.5 to 0.7 s of its 2.9 s parse and the six libraries about 0.6 s, so about 2.8 s for hello
      world where it measures 4.0.
    - A program with a standalone library (GodotSharp) that calls System.Linq as extensions is compiled again
      with every reported library at its first Godot type (found past the first library left out): no gain
      there, and the little done before that type is lost. Telling that no library left out has a visible type
      of that name needs each one's type names without reading it. No Godot program was built here.
    - The snapshot is the Node host's. The browser's host (`host/web`) has none; a compiler kept in a worker
      does not need one between builds (`CS2WASM_REPEAT` measures that), a page opened anew would. The threaded
      build of the compiler starts threads, so no snapshot is taken of it. A snapshot is the memory as the last
      build left it: 234 MB after hello world, 915 MB after test 088, and a build on it starts at that size.
    - Binaryen's optimizer over the self-hosted compiler's module, to see what a general WebAssembly optimizer
      finds: the version here (117) stops at `try_table`.
    - `tools/frames.mjs` and `tools/bench.mjs` were not run: no module changed (the rules' check compiles every
      conformance and benchmark program both ways to the same bytes).
    - The browser check of proof 5 (`tests/Godot/proof5_hello2d/check/browser.mjs`), which reads the debug
      sections through `host/web/debuginfo.mjs`: the copy is in step with `host/engine.mjs` (its own rule), the
      check itself was not run (no Godot on the homelab).
    - Found beside: `Queryable.Sum(IQueryable<int>)` and `Max` without a selector fail in
      `EnumerableRewriter.FindEnumerableMethodForQueryable` (IndexOutOfRangeException), with either rule of the
      libraries; test 250 does not use them.

- **Shared generic code (2026-11-21, on main fbe7688, branch claude/canon-sharing).** Round 5's pass shares bodies
  equal but for i32 constants and calls of one shared group; what it could not share, measured with the new
  `CS2WASM_SHARE_WHY=1` (families of instantiations still several bodies after sharing, the first instruction that
  differs, the calls one member makes that another does not, and why identical members stayed apart): calls of
  functions that are no shared group (a sealed type argument's own method, devirtualized: `GreenNodeExtensions.
  WithAnnotationsGreen<T>` calls each T's `SetAnnotations`; each T's `rt.isinst`; `rt.release of` an exact type), the
  limit of 6 hidden parameters, the covariant store check left out for a sealed element type (`List<string>` against
  `List<A>`), null checks written out in one member and not in another (whether it is called from loops), a release's
  exact-type hint in the key, and loads at a thread static's offset. As .NET's shared code over `__Canon` takes the
  exact types from its generic dictionary, a family's body here takes them as hidden parameters:
  - The compiler marks each instantiation with its generic definition (`WasmFunction.Family`, Driver.GenericFamily).
    Within a family, a call whose callee differs between members and is not one shared group is made through the
    table, each member's callee's table index a hidden parameter (`call_indirect`); so are loads whose offsets differ
    (the offset added to the address). A family may have 24 hidden parameters (`CS2WASM_SHARE_MAX_FAMILY`), other
    groups 6 as before.
  - Decisions the compiler made on the exact type argument where the code names a type parameter are made as for any
    reference type in a family (`FunctionCompiler.OfTypeParameter`): the covariant store check into a `T[]` is made
    (it passes for a sealed T). Behaviour is the same: the check cannot fail there.
  - A family's members get the written-out null checks alike (one called from loops, all). The same for the
    inliner's caller counts was measured within 0.1% and is off (`CS2WASM_FAMILY_CALLERS=1`).
  - Soundness: what is shared is only bodies that are the same instruction for instruction once the differing
    constants, callees and offsets are parameters, so each member runs exactly its own code: counts, arena
    operations and cycle-capable answers are each member's own (instantiations whose arena answers differ have
    different bodies and stay apart: the largest part left, `ArenaLink`/`AllocObjectArena` in one member and not in
    another). Struct type arguments are never made alike (their bodies differ). Traces: frames as round 5
    (`tools/k5/lend.cs` the same with sharing on and off).
  - `CS2WASM_SHARE_INDIRECT=0` (with `CS2WASM_NO_CANON=1`) for round 5's rules.
  Result: the self-hosted compiler (named, no threads) 58,572,027 to 47,163,399 bytes (-19.5%), its code 33.3 to 25.9
  MB (generic instantiations 10.2 to 4.7 MB; what over reference types would still share at most 6.3 to 1.5 MB); hello world on the
  homelab (selfhost-ab.sh, 6 interleaved rounds) 10,444 to 9,990 ms median (-4.3%, faster in each round; NativeAOT-LLVM
  4,250 ms there: 2.46x to 2.35x), peak memory the same. Parser bench 2,575,966 to 2,363,055 bytes; hello world
  27,067 to 27,053 bytes. tools/roslyn-bind-bench.sh (homelab, 6 rounds): 6,742 to 6,654 ms median (-1.3%, below
  what it resolves; its module 16.2 to 14.3 MB). bench.mjs (homelab, 3 interleaved pairs): 0.97x to 0.98x NativeAOT;
  cs2wasm's own times the same to 0.1 ms but word_count and shapes (+2 to 5%, tens of microseconds); keeping the
  calls through the table out of loops (`CS2WASM_SHARE_INDIRECT_LOOPS=0`) was no faster. Building: sharing takes
  1.7 s of the parser bench's build (1.6 s before), the build itself shorter (later passes have less code). Suite
  196/196 checked and as shipped on the homelab (Godot not installed there). Also fixed: ShareConstants named
  references by their identity hash code, which two objects can share (members would have been given another's
  callee or address); now by an object's own number. Open: 108_records_async_misc failed once, checked, in the first
  of two suite runs (exit 1 at its cancellation line, `await foreach` with a token cancelled), and once in 36 runs of
  the test alone here (each a new build: builds are not byte-for-byte the same from run to run, with sharing off
  too); round 5's rules passed 22 of 22 and one module run 60 times passed each time. Not traced; it may be this
  change or an older rare failure.
  - What is left (`CS2WASM_SHARE_WHY=1` on the self-hosted compiler): families whose members differ in arena
    operations (cycle-capable in one instantiation, not in another), loads and stores of a thread static in one body
    (only loads are made alike), and small groups whose stubs cost more than the body. The hidden parameters are
    passed at each call; taking them from the object's type word, as .NET does for a generic type's methods, would
    save the pushes at call sites (not done).

- **Self-hosted compiler's memory (stream L of docs/FINISH.md, 2026-11-20): 568.9 MB to 482.9 MB.**
  - The report: `CS2WASM_MEMSTATS=1` at compile time. The runtime goes through the heap at every growth of the memory
    (the table's word per granule, each page's blocks: a block's first word says what it is) and prints a line;
    the export `cs2wasm_memstats` (host/run.mjs calls it at the end) prints the last growth's state and the end's in
    full: pages and blocks per size class (in use, objects, cycle-capable, members), large blocks, free runs, empty
    pages, members and which of their words are used, arena records, the 60 types with the most bytes. Its records
    are made at the heap's start, so it allocates nothing while it looks; its hooks are intrinsics the compiler
    emits only when asked (`if (MemStatsOn())` around a call still cost hello world 430 bytes). A diagnosis, not a
    walk the program does: nothing is compiled in without the variable.
  - Found (hello world, no threads): the heap only grows, and at its peak holds the objects .NET's heap holds after a
    full collection at the same point of the compilation (dotnet-dump of the .NET-hosted compiler: 4.74 million
    objects, by type the same counts): what cs2wasm frees, it frees when .NET could, and the gap is layout. The
    allocator is not where it goes: 4 MB held free in pages of small blocks, 1 MB in free runs, about 1.5 MB in the
    granule rounding of large blocks. Where it went: 3.7 million arena members each had a block of 48 bytes of words
    (175 MB) and an 8-byte prefix (30 MB), and almost every cycle-capable object was a member, so the words being
    made only when first written saved nothing; objects 204 MB small and 86 MB large (String 110 MB, the library
    sources mostly); 36 MB of data below the heap.
  - Cut 1: the words are in the member's block, before its header. A word is one load (was two, the pointer first).
  - Cut 2: of the nine words, two are rare: the container waiting to own the member (6% of members) and the note of
    a failed check at the drop (5%). They are in 8 bytes of their own, made when one is first written and freed with
    the member (or when it leaves its group); the block's first word, the tag that says a block holds a
    cycle-capable object, is PrefixMark less their address (below -1 either way, so ObjectOf still knows the block).
    The owner took the waiting container's place. A member: 56 bytes to 32. With threads 40: the group's lock word
    (stream C's, which another thread reads without the group's lock: KeyStripe) is the second word of the block,
    and the count word stays clear of a free block's link.
  - Cut 3: the table StackFrame.GetMethod looks frames up in (rt.frame_methods) kept each name as a string literal:
    57,000 in the self-hosted compiler, about 12.5 MB of its memory. One blob of UTF-8 names now (compared with the
    frame's string as decoded code points); data 35.7 to 29.2 MB.
  - Measured, interleaved (`tools/selfhost-ab.sh` prints each run's peak now), 4 rounds: main 199572c 14.6 s /
    568.9 MB; stream L 14.0 s / 482.9 MB; NativeAOT-LLVM 5.5 s / 322.2 MB (its peak.cjs, the same measure as
    tools/bench.mjs takes of both). micro --baseline 1.87x on both; bench 0.95x on both, 16.0 MB (the 16 MB floor);
    hello world 27,812 bytes unchanged; programs with arenas about 2 KB smaller. Suite 187/187 checked and as
    shipped. The threaded self-hosted build (layout of thread mode: tag, lock word, seven words) compiles hello
    world right on 1 and 12 threads; under the same load as stream C's last threaded build, 611 MB against 666.
  - **What is left above NativeAOT's 322 MB is per object:** 32 bytes before each of 3.7 million members (120 MB:
    streams I and J take types out of the cycle graph), a 12-byte header against NativeAOT's 8 (the hash word),
    29 MB of data (19 MB type tables, reflection records and the frame names, 4.7 MB string literals, 4.1 MB the
    library sources), and the memory's growth by an eighth
    (about 30 MB above the heap's top at the end).

- **Owned arrays (stream J of docs/FINISH.md, 2026-11-20): Roslyn's green nodes out of the cycle graph.** The type
  graph has one node per array type. Roslyn's `ArrayElement<GreenNode>[]` is both the children a syntax list keeps
  (made, filled, handed to its constructor, never written again) and its builders' arrays (written all the time,
  held by no node); an array type's element site is always one that closes cycles, so the 268 green types were one
  group through it (its only closing site: `CS2WASM_ARENA_DEBUG` now prints each component's `closing site` lines).
  - The rule (Compiler/OwnedArrays.cs): a field is *sealed* when every array it can hold is never written once there.
    A greatest fixpoint over carriers, each assumed until a use is found that does not fit: fields (every value
    stored is sealed, every read only reads: elements by value, the length, Array.Copy's source, a parameter that
    only reads); parameters that only read, and those whose every caller passes a sealed array (no virtual, interface,
    delegate or reflective way in; no primary constructor, attribute or record); locals that alias sealed arrays;
    *fresh* arrays (a local declared from `new T[n]`, a collection expression or a call that returns a fresh array)
    whose uses before their one hand-off are element writes and reads, the length, Array.Copy/Clear/Fill into them,
    or a *writing* callee (one that writes into its parameter and keeps it nowhere: not in a lambda, not async, every
    override of a virtual one), all textually before the hand-off with no goto or yield and no loop around the
    hand-off that the declaration is outside of. A struct element is used by value only in the uses listed (a
    mutating method, an await, a deconstruction, a pattern on it in place fail). Every compiled body, field and
    static initializer is indexed by the identifiers it names (and target-typed `new(` and constructor initializers),
    so every reference of a carrier is seen. Why it is sound: ordered by when each object stops being only its
    maker's, every element stored into a fresh array was a value already let out (earlier), and nothing stores into
    a sealed array afterwards, so its element edges all point back in time, as a readonly field's do.
  - In the graph (Arenas.SealOwnedArrays, after a first pass of the components): only fields on a cyclic component
    whose closing site is an array type's elements are asked; a sealed field's hubs lead to a node of its own per
    array type whose element hubs are copies with no site (they close nothing). If that node is still on a cycle
    closed elsewhere, it is joined back to its array type both ways and the components found again: such arrays are
    members as before. `CS2WASM_NO_OWNED_ARRAYS=1` turns it off; `owned arrays:` debug lines give each carrier's
    verdict and the use that failed it.
  - Reflection: a constructor reflection can call gets any array (test 176). In the self-hosted build every program
    constructor could be called: asking frames for their MethodBase gives every program method a record and an
    invoke thunk (stream E), and two things asked, the compiler's own CS2WASM_STATS/COUNT_RC aids (Wasm/Emitter.cs)
    and Exception.SetCurrentStackTrace through .NET's `StackTrace.ToString(TraceFormat, StringBuilder)`, which reads
    each frame's MethodBase; and Roslyn's analyzer loading (`Assembly.GetTypes`, then `Activator.CreateInstance(type)`)
    kept every constructor of every program type. Now: the aids read names from the trace text; that overload is
    substituted as `ToString(TraceFormat)` already was (`Rt.TraceTextInto`: SetCurrentStackTrace writes the trace with
    its lines, as .NET does; it wrote the names without lines before, test 177); CreateInstance(Type) and (Type,
    bool) keep only parameterless constructors (all they run); and the analysis counts as callable only members with
    an invoke thunk (`Reflection.ThunkedMembers`), not every member with a record.
  - Measured. Parse probe (tools/roslyn-parse-probe.cs): cycle types 907 to 639, its green group of 271 gone, output
    the same. Self-hosted compiler, named no-threads builds, `Console.WriteLine(42)`, interleaved 3 rounds
    (tools/selfhost-ab.sh): main 14.80 s, the reflection part alone 14.08 s, with the rule 11.93 s, NativeAOT-LLVM 5.93 s
    (2.50x to 2.01x). Profiled run: library ready 11.6 to 8.8 s, compile 3.26 to 3.13 s; memory 470 to 295 MB; arena
    self time 3.52 to 1.76 s (21.7% to 13.0%; ArenaRelease 704 to 346 ms, ArenaUnlinkOld 403 to 139, ArenaLink 347 to
    141). Cycle types 39,351 to 39,064 (reflection part) to 38,796; no group holds a green node, a syntax list or
    `ArrayElement<GreenNode>[]`. Module (named) 78.5 to 72.7 MB (the thunks) to 72.6 MB. The self-hosted compilers'
    hello worlds are byte-identical. The analysis: 1.5 s of the self-host build, 3,432 fields asked, two sealed
    (the other, SharedArrayPoolPartitions' partitions, joined back: its arrays are on a cycle through their elements).
    Hello world (test 145) unchanged. Suite 190 passed checked and as shipped.
  - Found on the way, fixed 2026-11-21 (below, stores by address): a store through a span alone
    (`items.AsSpan()[0] = v`) recorded no value at the array type's site, so a cycle made only that way was in no
    group and was never freed.
  - Left: what still joins Roslyn's big group (20,844 types) is the `object` and interface slots of stream I's
    finding; red nodes' child arrays (`SyntaxList.WithManyChildren._children`) are written lazily after the node is
    shared (by design) and stay edges.

- **Stores by address in the type graph (2026-11-21; Compiler/RefStores.cs).** A store through a `ref` (a span's
  element, MemoryMarshal.GetArrayDataReference, Unsafe.Add, a ref local or parameter, Interlocked's location) or a
  pointer names no site: the runtime finds the slot's object (Rt.ArenaLinkAt), but the type graph had no edge for it,
  so a cycle closed only that way was in no group and never freed (test 178: 50 of each of six kinds left before).
  Now each such store records its value's type against its slot type (Interlocked's and Volatile's at their callers,
  Calls.ArenaRefWrite), and before the graph:
  - a `ref T` can point into any T[]'s elements without any element's address being taken (a span), so every array
    type of element type T the program makes holds those values (its site made if no code stores into it);
  - a `ref` into a class's field comes only from that field's address taken by name, which already gives the site
    its declared type; a readonly one is then no field filled at construction alone (`Unsafe.AsRef(in f) = v`);
  - Unsafe.As<TFrom, TTo>(ref): stores through TTo refs land in TFrom slots (directed; a struct's reference slots for
    a struct). Interlocked's and Volatile's own reinterpretations are not counted: their callers record the store
    under the slot's own type. Raw memory seen as references can land in any slot, unless every such
    reinterpretation in its method is only read (RuntimeHelpers.Box) or it is GetArrayDataReference<T>'s (a T[]'s
    own elements);
  - a pointer store lands anywhere a value of its type fits (a value of a wider static type: anything of the slot's
    type), except the runtime's own (Cs2WasmArrays.SetBoxed for Array.SetValue and Array.Copy across element types,
    TypeInfo.Set for a QCall handle, Meta.CopyBack for a reflective call's by-ref arguments), which land in arrays'
    elements or slots whose address was taken. (The first form of that rule added only values narrower than the
    slot: an object stored by Array.SetValue reached no Node[]; unsound, caught while measuring.)
  Self-hosted compiler: 5,739 slot types recorded, 498 array sites given values, 21 readonly fields made edges,
  nothing lands anywhere; groups and cycle types the same with and without (61, 38,881), the green group stays out;
  hello world 10.68 s against 10.74 s without (noise), the same peak memory, output byte-identical; 0.4 s of the
  build. Parse probe unchanged (639 cycle types). Suite 193 passed checked and as shipped.
  `CS2WASM_NO_REF_STORES=1` turns it off; `by address:` debug lines name each pointer store and reinterpretation.
  Not covered: Unsafe.As<T>(object) (an object seen as another class): the type graph trusts static types there.
- **Self-hosted compiler's memory, round 2 (stream L, 2026-11-21): 344.2 MB to 306.0 MB, below NativeAOT-LLVM's 322.2.**
  - What the report showed on main 6a346d8 (J's green nodes no longer cycle-capable): 4.6 million objects, 0.54
    million arena members (17 MB of their words), the heap's top 301.7 MiB against the 291.8 MiB the memory had before
    its last growth by an eighth (to 328 MiB). What was left was the size classes: every block a multiple of 8 (a
    class whose fields need 4 bytes of alignment took 4 more half the time: SyntaxTokenWithValueAndTrivia<string>
    36 bytes in 40; NativeAOT's 32-bit layout is a multiple of 4), and a class per 8 bytes up to 2 KiB, so 380 classes
    above 512 bytes each held a partly used page (3.4 MB of blocks in 7.8 MB).
  - Cut: small sizes in steps of 4 bytes to 128, 8 to 256, then eight per doubling (Rt.RoundSmall). Objects stay
    aligned where they must: a class with a field that needs 8 bytes has a size that is a multiple of 8
    (Types.LayoutOf), displays and state machines likewise (Display.AllocSize, at their allocation and their free),
    boxes and arrays by their element, and a page's blocks start at a multiple of 8; memory that is no object
    (Rt.Alloc: stacks, words read atomically) stays at a multiple of 8. Without threads the smallest block is 8 bytes
    (a free block links at its second word; with threads at 8, clear of what a lock-free read looks at): side words
    in 8 bytes, an object of 12 (no fields) in 12.
  - Speed: the rounding in the allocator's hot path cost the baseline tier 10 to 26% on the allocation micro
    benchmarks (and SingleThreaded, asked in the free path, is a call: its answer is known at the end of the build).
    Now a call whose size is a constant goes to Rt.AllocObjectC / FreeObjectC (and the arena's two) with the size
    rounded at compile time (RuntimeGen.SizeAllocations); AllocObject rounds first (arrays, sizes known only at run
    time). micro --baseline 1.83x/1.82x against main's 1.87x/1.87x; bench 0.95x on both; hello world 27,744 to
    28,120 bytes (the second entry and its free).
  - Measured, interleaved (tools/selfhost-ab.sh), 3 rounds: main 344.2 MB, 10.6-11.1 s; stream L 306.0 MB,
    10.7-11.1 s; NativeAOT-LLVM 322.2 MB, 4.9-5.5 s. Output equal to the .NET-hosted compiler's but for the lines
    section's paths. Suite 192/192 checked and as shipped.
  - **The margin is thin:** the heap's top is 290.8 MiB and the memory 291.8 MiB (4669 pages): a megabyte more of live
    objects and the memory grows by its eighth, to 344 MB. What is left per object against NativeAOT: the 12-byte
    header (the hash word: about 18 MB over 4.6 million objects), 32 bytes before each arena member (17 MB), 13 MB of
    data below the heap. Removing the hash word (identity hashes and the three flags elsewhere) is the next structural
    cut; it changes every object's layout.
  - **Round 3 (2026-11-21): round 2's cost to small programs taken back.** Hello world had grown 376 bytes (28,287 on
    main with K round 2): the second fast entries for allocation and free (AllocObjectC and FreeObjectC, 431 bytes),
    RoundSmall as a function of its own called in the fast paths (81 bytes), the clearing loops' separate last 4
    bytes. Now the wide steps above 128 bytes are only in a program that constructs 1,000 types or more (Rt.WideSizes:
    a constant the compiler writes at the end of the build, its calls replaced and dead branches removed, as for
    rt.single_threaded); any rounding is right as long as allocation and free agree and a multiple of 8 stays one, so
    a small program keeps steps of 4 bytes, two instructions where they are used. One fast entry each again (the
    compile-time sizes gave less than the inline step); a block is cleared from its end down before its header is
    written. Hello world 27,868 bytes (27,869 before round 2); the self-hosted compiler 306.0 MB, as fast as main's.
  - Found on the way: 070_files names its directory with Guid.NewGuid, and the host's random seed is a constant, so
    two runs of it at once (the suite's checked and shipped shards) share it and fail; main's too. The heap report read
    a page on the empty list as a size class from its stale header (a trap in the self-hosted build): such pages are
    marked first now, and a first word below -1 that is no tag is counted, not followed.
- **Self-hosted compiler's memory, round 4 (stream L, 2026-11-21): 306.0 MB to 302.0 MB; what is left is per object.**
  - The report now also gives, at the end: objects, identity hashes made and asked, the header word's flags, every
    type with its members and hashed objects, and memory that is no object by size with its first words;
    `CS2WASM_LAYOUT_DUMP=<file>` each class's size and padding; `CS2WASM_DATA_DUMP=<file>` the data by the compiler
    method that made it (named by CallerMemberName: a first form with a stack trace made the self-hosted compiler ask
    frames for their methods, a reflection record for every method, 12.5 MB more data, which every measure of that
    build then carried).
  - Found (main 1191d18's build, hello world, no threads): heap top 298.3 MB in a memory of 298.8 MB (the eighth's
    step landed 0.6 MB above it: luck); 13.9 MB below the heap (data, statics, stack); small blocks 184.5 MB live in
    188.8 MB of pages; large blocks 92.8 MB, of them 86.2 MB objects (String: 110 MB in all, the library's sources)
    and 6.6 MB native memory (Roslyn's reference assemblies, 172 PE images, as NativeAOT's build holds them).
    4.67 million objects: 3.44 million green nodes, 0.37 million strings, 0.30 million arrays; 0.54 million arena
    members (red nodes, locations, syntax references, Ephemeron[], symbols); padding in fields 0.25 MB in all (the
    layout in declaration order costs nothing measurable: ordered by alignment, 0.11 MB less).
  - **The header's third word stays.** 1.66 million objects (35%) have an identity hash made, 1.55 million of them
    green nodes: Roslyn's SyntaxNodeCache hashes a node's children by RuntimeHelpers.GetHashCode; 8.0 million hashes
    asked. A table beside the heap for them would hold 1.66 million entries (16.8 MB at the least, 33.6 MB at a load
    of three quarters) against the 18.7 MB the word costs; and the word's flags (pinned, marked, finalized) would need
    a home every free looks at. NativeAOT keeps the hash in its header word too: what cs2wasm pays per object over
    NativeAOT is the count word (4.67 million times 4 bytes), and the members' 32 bytes before the header (17.2 MB),
    all seven words of which are in use for nearly every member (538,746 to 518,741 of 538,995).
  - Cut 1: manifest resources out of the memory. They were 4.1 MB of the data below the heap for good, each read once
    when the library is loaded. Now each is packed on its own into a passive segment of their own and expanded into
    native memory for the stream that reads it (RuntimeAssembly.ManifestResource, TypeInfo.ResourceInit and
    ResourceUnpack: memory.init and the data's expander), which frees it when disposed or destroyed (its finalizer;
    .NET's stream has none, which is not observable). Test program: 200 streams of a 600 KB resource let go
    undisposed, memory 16 MB.
  - Cut 2: reflection's lists that nothing writes or tells apart by address (a method's parameter words and names,
    attribute factories, a type's interfaces, a generic method's arguments) are one blob per distinct list
    (Driver.SharedInts); most repeat (an empty one for nearly every method). 154_frame_methods' data 955 to 766 KB with
    cut 3.
  - Cut 3: without threads, the frames' method table (StackFrame.GetMethod's names and entries) is packed in a segment
    of its own and expanded when first asked for (3.4 MB in the build whose code asked frames for their methods).
    Cuts 2 and 3 are for programs that reflect: the self-hosted compiler's own build makes neither records nor the
    table (its data by maker: type tables 2.6 MB, type names 1.7 MB, type info 0.5 MB, resources' table 5 KB).
  - Cut 4: a program without threads grows its memory by 16 MiB (more if needed), not by an eighth. Measured in V8, a
    growth costs about 0.15 ms whatever the size, shared memory or not (to 320 MB: 19 growths in steps of 16 MiB, 30
    ms; 15 by eighths, 29 ms); the eighth's tail is memory reserved and unused that the peak counts (33 MB at the
    self-hosted compiler's last growth), and it made the peak move in steps of 33 MB: a megabyte more of objects was
    344 MB. Now at most 16 MiB above the top, and the peak follows the heap.
  - Measured (report build): data below the heap 13.9 to 9.3 MB, heap top 298.3 to 293.7 MB, memory 298.8 to 294.9 MB
    (4,669 to 4,608 pages; 306.0 to 302.0 MB). Interleaved on the homelab (tools/selfhost-ab.sh, 3 rounds, the
    shipped no-threads builds): main 306.0 MB, 9.9-11.3 s; round 4 302.0 MB, 10.1-10.3 s; NativeAOT-LLVM 322.2 MB,
    4.0-4.4 s. Module 41.76 to 42.33 MB (the resources packed one by one, not with the rest of the data). Hello world
    27,297 to 27,290 bytes. The 20 MB asked for are not there in the heap's layout: the rest is the
    count word and the members' words, which the representation needs, and the library's strings, which .NET holds
    the same.

- **Arena operations without the calls of their usual cases (stream N of docs/FINISH.md, 2026-11-21).** The
  self-hosted compiler's runtime is mostly arena operations on Roslyn's red nodes and symbols, one genuine group
  (stream I). Counted first (`CS2WASM_DROP_STATS=1` now also prints each arena path's count, OpStat; nothing of it
  without the diagnosis): in tools/roslyn-parse-bench.cs, per 2.9 million links, 2.8 million are stores by address
  (ArenaLinkAt: struct copies through `ref`, a struct constructor's `this`, iterators' state), 1.6 and 1.8 million
  first and last outside references take ArenaRetain and ArenaRelease, UpdateHold is called 6.3 million times and
  changes something 0.8 million times, Top was walked before every inside unlink (a million walks of six hops) only
  to see the top was not far, 30,000 checks at the drop fail walking 260,000 members, 250,000 joins visit what they
  hold and 265,000 new owners visit their slots (Reevaluate). In the baseline tier each helper is a call.
  - Written where they are asked: UpdateHold's first look (HoldSettled: not a member, dying, no owner, or holding
    exactly when it should); ArenaOf's unmerged record (ArenaOfMerged for the rest); PLM as two loads; the first
    tests of Dropped (DropCandidate), TryLeave (LeaveCandidate), NoteOutside and TopHeld (a note on the member). The
    walk to the top before NoteOutside is gone (NoteOutside looks at the note and walks itself). Each of these is
    the function's own first test, so nothing is decided differently.
  - Stores by address: SlotOwner with ObjectOf, BlockOf and MapGet written out; a slot's block in a page found by
    `off * ceil(2^32 / size) >> 32` (off below 2^14, size at most 2,048: within 2^-18 of off / size, whose fraction
    is at least 2^-11 from the next integer; checked for every offset and size), without threads; the per-type slot
    tests copied into rt.arena_is_slot's cases, which answer 2 for an array's element (ArenaLinkAt no longer asks
    TypeInfo.Rank); rt.scc_of inlined (a table read); ArenaUnlinkAt calls ArenaUnlinkOld directly.
  - Measured: tools/arena-bench.cs (new: red nodes' shape) 63 to 48 ms, the parse bench 262 to 229 ms, under
    Liftoff, interleaved with main's build; the self-hosted compiler (named no-threads builds, hello world, 3
    interleaved rounds) 11.09 to 10.54 s against NativeAOT-LLVM's 5.27, arena self time in a profiled run 2.08 to
    1.64 s; a later round on a busier machine 11.63 to 11.26 s; byte-identical output, the same peak memory.
    Test 185 (each case a member's first and last outside reference leaves to the full path; freed when let go).
    Suite 197 passed checked and as shipped.
  - Tried and left out: the first and last outside reference written out in ArenaRetain and ArenaRelease: in a parse
    they apply to 3% (red nodes have side words, from a waiting container, or a hold that changes) and the function
    got slower. An inline guard in front of UpdateHold on the up-reference bits alone: red nodes have up-references
    below them (their parent links became up-references when they joined), so it passed 93% of the time.
  - What is left is work, not calls: a red node's first and last reference from outside changes its hold on its
    owner, and so on up (0.8 million changes a parse); the failing checks at the drop; joins and Reevaluate's visits.
    Cheaper there needs a different bookkeeping of holds (one that does not flip with every local reference), which
    is a design question for step 7, not a fast path.

- **Holds kept once made (stream N round 2, design, 2026-11-21).** What costs now is the hold. A member X with an
  up-reference below it (u > 0: a red node's parent link, uncounted since its parent's list keeps it) that something
  other than its owner refers to (its `others`: inside references not its owner's, plus outside ones) holds its
  owner A with a counted reference, so that whatever X keeps alive through its up-reference stays (a child held from
  outside keeps its parent, as in .NET). The runtime keeps that exact: Held exactly when wanted. So a red node's first
  outside reference (a local, an iterator's state) makes it hold its owner, and its last lets go; and up the chain
  where the owner's own wanting changes. In a parse of 400 classes that is 0.8 million real changes, each a call and
  a few words, for nodes that are in their parent's list the whole time.
  - The change: **a member that is owned keeps a hold once made.** UpdateHold sets Held when it is wanted (as now) and
    clears it only when the member is not owned (as now: its owner is gone or let it go, and the hold is put right at
    once, OwnerLetGo then UpdateHold, with Adopt). An owned member whose `others` fall to zero keeps holding: a stale
    hold. HoldSettled says the same. So a red node holds its owner from its first outside reference until it is let go
    or dies; its later first and last references change one word, its count. The invariant is now one way: wanted
    implies Held (what safety needs: nothing an up-reference relies on is freed); Held without being wanted is
    allowed on owned members only.
  - Why nothing is freed too early: Held is set whenever it is wanted, exactly as before; a stale hold only adds a
    count to an owner. Destruction still releases a dying member's hold (ArenaDestroyFrom).
  - Why nothing is kept too long, by the ways a structure becomes garbage (S, with its top t, the member of S that is
    not owned by a member of S; a stale hold is always from a member to its owner, so inside S):
    1. t loses its last support itself (its owner's slot, its last outside reference): as now, t is destroyed if
       nothing inside refers to it, else the check at the drop runs on it (DropCandidate: not owned, no outside
       reference, some inside one), whose trial counts holds as references inside the set, stale or not, and
       destroys S. Before, a cascade of let-go holds could have brought t's inside count to zero and destroyed it by
       count; now the check does, with the same result.
    2. t lost its support earlier, the check failed and noted t, and S's last support goes later (a member's last
       outside reference: TopHeld; a reference into S from a member outside it: NoteOutside): as now, the note's
       number reaches zero and the check runs again. Stale holds are not support: the trial subtracts them.
    3. **t's owner is destroyed**, and t has inside references left only from stale holds of what it owns: the
       destruction cuts the owner's slot (mode 0 of ArenaVisit), t survives the cut with a count above zero, and
       nothing looked at it again. This is the case the eager holds covered by themselves (holds were never stale, so
       a member that survived had real support). Now: a member that survives a destruction's cut with no outside
       reference is put on a list, and when the destruction is done (as DrainKills does for the killed) each is
       checked at the drop. And the survivor's hold on its dying owner is let go there (UpdateHold, now not owned):
       an owner that is dying needs no support.
    Every other way a member loses its owner is followed by the check already (ArenaUnlinkOld: Dropped(old)), keeps a
    counted reference to it (TakeOwnership's turn: the value's slot still holds it), or moves the owner and its holds
    together (Handover).
  - Cost moved, not made: what dies is found by the check at the drop (a trial over S) where it was found by counts
    falling (a cascade over S): about the same visits, once per structure that dies, instead of two words per first
    or last outside reference of every member. The failing checks stay what they are (they are notes of real support).
  - Kept apart: with threads the old way (a group's holds are changed under its lock; the stale-hold rule needs the
    survivors' list per thread, not done).
  - **Measured, and not the default** (`CS2WASM_STALE_HOLDS=1` turns it on, without threads). The suite passes with it
    in both modes, the cycle tests and test 185 included (a case added: a held member moved out of a let-go subtree,
    then let go). In the self-hosted compiler (hello world, counters on, `CS2WASM_DROP_STATS=1`) hold changes fall
    from 2.59 million to 0.25 million and those up the chain from 0.43 million to 0.01 million, but its time moves
    within the noise (10.47 against 10.25 s, interleaved, NativeAOT-LLVM 5.23 s) and **its peak memory rises 12%**
    (4,669 to 5,252 pages: 1,500 destructions fewer, 3,800 members fewer leaving their arena), which this
    design's argument above does not account for: some structure that died by counts falling is kept, by a path the
    cases above miss. Holds were not where the time is: the counters say the self-hosted compiler makes 27 million
    first and 28 million last outside references through the slow path, and 25 million walks to a top (102 million
    hops) for members under a failed check, against 2.6 million hold changes.
  - **What went in instead (default):** (1) a mark of the kind of failed check a member is under (MarkHeld: the
    check failed for members held from outside, whose number TopHeld keeps; MarkOutside: for references from outside
    what the top owns, NoteOutside's): a walk to the top only for the kind whose number is kept (25.3 to 19.6 million
    walks, 102 to 66 million hops). (2) ArenaRetain's and ArenaRelease's usual case first, with no call, without
    threads: the record not merged, no held mark, the hold settled as UpdateHold would leave it, and for a last
    reference the arena still referred to from outside, the member still from inside, owned and not dying (65% of
    first and 62% of last outside references; in the parse 79% and 80%). Self-hosted compiler 10.47 to 10.27 s
    (3 interleaved rounds; NativeAOT-LLVM 5.38 s), the same peak memory, byte-identical output; suite 197 passed
    checked and as shipped.

### Direction (revised 2026-11-18 with the owner)

What the tool must do, at its core:

1. Any C# in, one WASM module out (not WasmGC).
2. Plugin support good enough for the Invisible API.
3. The same speed as NativeAOT or better, lower memory, no frame-time spikes, no runtime.
4. The same for the self-hosted compiler and Roslyn.

How the plan serves them:

- **The input stays the operation tree.** It keeps what the ownership analysis and the plugin need and IL has
  already thrown away: captured variables are still locals, async and iterator locals are still locals, `foreach`,
  `using` and `lock` still have their shape, and every operation maps to the developer's source. IL input was
  considered and rejected for these reasons.
- **One mid-level IR with explicit ownership is the centre** (step 5). The optimizer (goal 3), the plugins (goal 2)
  and self-hosting (goal 4) all work on it. It is the only structural change.
- **The IR is built from Roslyn's `ControlFlowGraph` for every method**, not only async ones as today, so Roslyn does
  the flattening of control flow and the compiler's own lowering shrinks to closures, state machines and patterns
  (goal 1). To be confirmed by step 5.1 before anything depends on it.
- **Cycles: prove the common ones away, group the rest, release groups without visiting them** (step 7). A dropped
  part of a live group staying until the group goes is a memory cost, not a spike, and is accepted.
- **Build time comes from caching the library's IR and analysis summaries**, not from precompiled IL (step 12.4).

The order is by dependency: nothing built on an unsafe base, measure before changing, the IR before what needs it.
Each step ends with the suite, `tools/bench.mjs` against NativeAOT's WebAssembly build, and a commit.

### Step 1. Make the tests able to see memory errors (A1.7)

1. In `host/run.mjs` and the runtime, add a checked mode: a freed block is filled with a poison pattern and its count
   word set to a trap value; retain, release and field loads on a poisoned block trap with the block's type.
2. At exit of each conformance program, report blocks still allocated that nothing static reaches (a leak count, per
   type). Record the expected figure per program; a change fails the test.
3. Run the WASM validator in every test build and in Debug; a failure is an error diagnostic.
4. Run the suite in checked mode by default. Fix what it finds before going on.
5. Add CI (build, suite, checked mode) on every push.

### Step 2. Fix the known memory-safety bugs (A1.1-A1.6)

1. Add the probes as conformance programs first (`118_args_rewritten.cs`, `119_finalizer_resurrects.cs`,
   `120_stack_limit.cs`), each failing.
2. A1.1: `Stable` only when the local cannot be written during the call; else retain for the call.
3. A1.2: re-read the count after the finalizer.
4. A1.3: lock in `ObjectMarks.OnDestroy`.
5. A1.4: full per-slot inside/outside record; a stress test that links and unlinks through arrays and wide objects.
6. A1.5: address-escaping fields count as written.
7. A1.6: stack limit check and guard gap; `StackSize` honoured for workers.
8. Sweep every other `Own.Stable` producer for the same assumption, with a test per case found.

### Step 3. Measure where the time goes (A2.1, A2.8)

1. Fix the measure: NativeAOT side on the same .NET version, pinned; true peak memory (sample `memory.buffer`
   growth and the allocator's high-water mark); results written to `docs/measurements/<date>.json` by the script.
2. Replace the kernels with real programs: the self-hosted compiler over a fixed set (hello, the conformance
   sources, a GodotSharp game), a JSON round trip, a text-processing program, the game simulation.
3. Run four builds of the self-hosted compiler on that set, in the same engine:
   - (a) as shipped, threads; (b) no threads;
   - (c) counting off, nothing freed (the upper bound of what memory management can cost);
   - (d) (c) with allocation as a bare pointer bump.
4. Read the result: NativeAOT's time against (c)/(d) is the code-quality gap; (b) minus (c) is counting and freeing;
   (a) minus (b) is thread mode. Count executed retains, releases, retained loads, allocations, frees and arena
   operations in each.
5. Write the split down. Steps 4 to 7 are ordered by it; the order below assumes what the audit expects (thread
   mode first, then code quality, then counting).

### Step 4. Static thread escape (A2.5, A3.4, SPEC 4.5)

1. Per allocation site, decide at compile time whether its objects can be reached by a second thread: captured by a
   delegate handed to a thread starter, stored in a static, or stored into an object already shared. Whole-program,
   flow-insensitive, on the type-and-field graph the arena analysis already builds.
2. A type all of whose allocation sites are thread-local gets plain counts and plain loads; a shared type gets
   atomic ones. Where a value's static type covers both, the operation is the atomic form (correct for both). No
   run-time shared bit (owner's rule).
3. Thread-local objects go back to the paged heap; shared ones use a heap that joins and reuses blocks.
4. Replace the name list of thread starters (`Scheduling.cs:38-47`) with the analysis's own sources.
5. Remove the `ThreadsSeen` restart: thread use is known from reachability before code generation.
6. Target: the threaded self-hosted compiler on one thread within 5% of the no-threads build.

### Step 5. A mid-level IR with explicit ownership (A2.2, A6.3, A6.4, A4.10)

This is the one structural change; steps 6, 7 and 9 need it.

1. Spike first, a day at most: build Roslyn's `ControlFlowGraph` for every method of the conformance sources and
   record (a) which constructs it flattens (expected: `?:`, `??`, `?.`, `&&`/`||`, `using`, `lock`, `foreach`,
   loops, `switch`), (b) which it leaves as operations (expected: patterns, lambdas, `await`, `yield`), (c) where it
   fails or loses a fact the ownership analysis uses today (a `foreach` element's borrow, a local that is never
   reassigned), and (d) its cost in build time. If (c) or (d) rules it out, the IR is built from the operation tree
   directly and the rest of this step is unchanged.
2. Define the IR: typed, block-structured, values in SSA form; C#-level operations already lowered (no patterns, no
   `foreach`, no closures) but above WASM (field load/store by symbol, array access with its check as a separate
   operation, call with a resolved target or a slot, `retain`, `release`, `move`, `borrow` as instructions). Captured
   variables and state-machine locals stay variables in the IR until a late pass gives them their storage, so the
   ownership analysis sees them as locals. Every instruction keeps its Roslyn operation and location.
3. Split `FunctionCompiler` in two: operation tree (through the control-flow graph, if 5.1 holds) to IR, IR to stack
   code. Move one construct family at a time behind a switch that falls back to the old path, with the suite passing
   at each move; delete the old path at the end. Each refusal site of A5.1 that the control-flow graph flattens goes
   with its family, with a conformance program.
4. Move the analyses onto the IR as classes with an input and an immutable result: escape, field borrows, owned
   parameters, fresh stores, arena membership. The string-keyed dictionaries on `Driver` go with them.
5. Give `WasmFunction` a role enum and the runtime functions typed identities; remove every `Name.StartsWith`.
6. Keep the WASM passes that are about encoding (locals packing, merging, ordering, data packing); retire those the
   IR passes replace.
7. Unit tests per pass on small IR inputs.
8. Give the IR a stable serialized form (needed by step 12.4).

### Step 6. Optimizations on the IR (A2.2, A2.3, A2.4, A2.6)

In the order step 3's numbers give; expected order:

1. Class-hierarchy devirtualization; guarded direct calls for two or three implementations.
2. Inlining on the IR, including callees that can throw (the inline table already exists: `cs2wasm.inline`).
3. Bounds-check and null-check removal by dominance and induction ranges; hoisting of invariant loads.
4. Counting removal by dataflow: cancel retain/release pairs across blocks, sink releases, borrow across calls that
   provably do not write the source.
5. Ownership specialization as SPEC 4.1 states it: per function, for the ownership its callers need, virtual groups
   sharing one borrow-first form; returns owned, with a borrowed-return variant where every caller borrows.
6. Reuse: a release that frees immediately followed by an allocation of the same size class takes the block
   directly.
7. Report the removed-count percentage from the build on the step 3 programs, in `docs/measurements`.
8. Target: no program in the set slower than NativeAOT's. Faster is welcome, not required. Stop optimizing a
   program once it is at parity and go to the next slowest.

### Step 7. Cycles: groups (redesign decided 2026-11-18: "redesign first")

The owner chose to redesign before extending today's machinery to closures, tasks and structs (A1.8). This is the
design; 7.A to 7.E are its stages, each ending with the suite green.

**What is kept from the decisions so far:** no collector, no search for garbage at run time, no list of things to
free later, no annotation; cycles that can be proven away are (7.E); a part of a group that is let go while the
group lives may stay until the group goes, when its objects still refer to each other.

**The model.**

- The type graph covers every heap object that holds references: classes, arrays, boxes, closures' display
  objects, delegates, async and iterator state machines; and every reference slot in them, also inside a struct
  that is a field or an element. Its strongly connected components are the cycle-capable types, as today.
- An object of a cycle-capable type is counted like any other object: one count word, every reference to it
  counted, freed when the count is zero. So everything that is not on a cycle at run time goes at once, in a group
  or not (a node popped off a list, a child removed from its parent).
- **A group** is a set of such objects that refer to each other. It has one number of its own: the references to
  its members from outside it (locals, statics, objects that are not members). When that number is zero and
  members are left, they are a cycle nothing can reach: they are all destroyed (finalizers first, then each as
  any object, references among them not counted down).
- **The rule that needs no walk:** a reference in a slot of object C to object V is *inside* exactly when C and V
  are of the same component of the type graph; and whenever such a reference is stored, C's and V's groups become
  one (a union of two records: constant time). So two objects of one component that are in different groups never
  refer to each other, an object that is in no group yet has only outside references (its count is its group's
  number the moment it gets one), and joining needs no look at what the objects hold. Today's join walk, owner
  chains, up-references, slot bits and kill queue all go.
- A store into a slot is then: the new value's group joined with the container's when the two are of one
  component, and the reference it had counted as an outside one no longer is; the old value let go as an inside
  reference when it is of the container's component, as an outside one otherwise. Whether a reference is inside
  is a fact about the two objects' types, not about how it was stored: nothing has to be remembered per slot.

**The constraint that shapes the rest: the container must be known at every store.** A reference is stored into a
heap object through its field, but also through a `ref`, a span, a pointer, a struct's own method (`this` is the
struct inside some object), or a block copy. The code there has the slot's address and nothing else. Counting such a
store as an outside reference while another path later lets it go as an inside one loses a count; the reverse frees a
group that is still referred to. Today's runtime has this hole (A1.4 is one face of it). Two ways close it:

- a mark in the stored reference itself (its low bit): safe, but every read of a reference from a field of a
  cycle-capable type pays a mask;
- **the allocator can say which object an address is in** (chosen): small blocks already lie in pages of one size
  (the block is arithmetic on the address); blocks above 2 KiB get the same in larger pages, and blocks above
  16 KiB a table entry per 16 KiB. Reads cost nothing; a store whose container the code does not know asks the
  allocator. It also gives threads the paged heap they lack (A3.4) and is what a group needs to keep its members
  in pages of its own and release them by returning the pages (7.D).

**Stages.**

- **7.A The allocator answers "which object is this address in".** One paged heap for programs with and without
  threads: small pages (to 2 KiB), medium pages (to 16 KiB), large blocks with a table. `Rt.OwnerOf(address)`:
  the object, or none for the stack, the statics and memory that is no object. A checked build tests it on every
  store through an address. The rule threads need for reads that count stays: a block keeps its kind for good.
- **7.B The group runtime, under the interface the compiler has today** (`Link`, `Unlink`, the count word's
  sign for a member), for the sites it has today: union of two group records, the group's number, a group's
  destruction, a finalizer that brings a member back (the destruction is called off: the finalizers ran before
  anything was let go). A store through an address resolves its container. Tests 016, 017, 082 to 085, 090, 119,
  121 and the fixed-memory API checks are the measure; test 127's `tree`, `ring`, `cut`, `kept`, `self`, `array`
  lines must hold or improve.
- **7.C Every container.** Sites for references inside structs (a field, an element, a box), for display objects'
  captured variables, delegates' targets, state machines' fields; block copies that say where they copy to;
  `MemberwiseClone`. Test 127 passes whole and loses its `known-gap` line.
- **7.D Groups that keep their members together.** Where the compiler sees at an allocation which group the
  object is for (it is stored into a member before anything else can see it), the object comes from that group's
  pages. A group whose types have no finalizer and refer to nothing counted outside it is released by returning
  its pages. The longest single runtime call per program is measured against NativeAOT's longest pause (the
  bench's latency figure).
- **7.E Cycles proven away.** A reference back to the object that owns the holder (a child's parent) is a borrow
  where the compiler proves the holder cannot outlive it: the types leave their component, and such objects are
  never in a group. A structure built new and attached whole (the isolated-value rule) likewise. Each proof takes
  types out of the graph; nothing else changes.

**What a program pays.** An object of a cycle-capable type: a few words before its header (its group, its place
among the members). A store that may join two objects: a look at two types, and a union when their groups differ.
Counting a member from outside: its count and its group's number. Letting a group go: its members, one after the
other, at that moment (7.D removes the visit where it can). With threads the group's number is shared state: step 4
decides which groups more than one thread can touch.

**Refinements for speed, taken up when the owner asked whether anything would make the structure faster
(2026-11-18).** None changes the structure.

- A member keeps the number of inside references to it beside its count, and its group counts the members that
  have a reference from outside, not the references. Counting a member that outside code already holds then
  touches the member alone, as for any object; the group is looked at only when a member gets its first outside
  reference or loses its last.
- A `ref` to a slot of an object the method itself names (`ref var x = ref a[i]`) knows its container: only a
  store through a reference that came from elsewhere asks the allocator.
- "Written only while nothing else can reach the object" is asked of display objects, delegates and state machines
  as of classes, so most of them are in no component at all.
- A program with threads locks the group it changes, not the whole runtime.
- The block of an address in a page of small blocks is a multiplication by the size's reciprocal kept in the
  page, not a division.

**One thing found while starting 7.B, which changes one sentence of the model (2026-11-18).** "Joining needs no
look at what the objects hold" is true only if every store of one component's object into another links at once.
Today a store into an object that nothing else can reach yet is a plain counted store (it cannot close a cycle), and
that is what makes building a tree or a list of a cycle-capable type cost what building any objects costs. Giving
it up makes every such object a group member as it is built: by the instructions it takes, about half as much
again for an object that is allocated, linked and freed (not measured). Keeping it means that the first time such
an object is stored where a cycle can close, what it holds is linked then: a visit of the structure built so far,
each object once in its life, at the moment it is attached. 7.B builds the runtime so that both can be measured
(the compiler emits the plain store or the link; the runtime is the same), and the numbers decide: time on the
bench's `tree` and `linked_list`, and the longest single runtime call.

**What it does not do.** A cycle that is let go inside a group that lives on stays until the group goes (a build
notice names the types, as today). The component that `object` fields make (anything that can hold anything) is one
component: objects of it stored into each other share a group. Both are measured in 7.B and 7.C before 7.D and 7.E
are ordered.

**Correction, 2026-11-18, on reading the runtime whole before replacing it.** The audit's summary of it ("counted
raw pointers", "arenas only merge", walks) was mine and was wrong in what mattered. `Boundary/Runtime.cs` already
is the model above: an object of a cycle-capable type starts as a plain counted object; a store that can close a
cycle puts container and value in one group (a union of two records, constant time, whatever their sizes); a member
has its count of inside references and its count of outside ones, and its group counts the members that have
outside ones; a member nothing refers to goes at once, a group nothing outside refers to goes whole.

And it has one thing the model above gives up: **a reference from a member to one that holds it (a child to its
parent) is not counted at all, decided at run time along the chain of owners**, so a subtree with parent links that
is cut from a living tree is destroyed at the store that cuts it. The fixed-memory checks of the suite ("a
long-lived tree that keeps dropping subtrees stays in 16 MB") depend on it, and so does any game that removes nodes
from a scene. The model above would have kept such a subtree until the whole tree goes. The compile-time proof of
7.E cannot stand in for it: a child may outlive its parent in ways a compiler cannot rule out, and the run-time
chain handles those.

So nothing is replaced. What was wrong with the runtime is narrower, and it is what step 7 now is:

- **7.B (done 2026-11-18): a store that has only the slot's address is the store through the object.** Through a
  `ref`, a span, a struct's own method, `Interlocked`, a block copy of references. The allocator says which object
  the slot is in (7.A); the object's type says whether the slot is one a cycle can close through (`ArenaIsSlot`,
  generated). Before, a slot whose address the program took was left out of the graph for good (cycles through it
  were never freed: test 128 left four objects), and a store through a span into a slot that was not left out
  counted the old value as an outside reference whatever it was.
- **7.B2: what a slot holds is known for every slot.** A member keeps two bits for each of its first sixteen words
  (nothing, an inside reference, an uncounted one to its owner); for an array's elements and later fields
  `ArenaUnlink` goes by the counts (A1.4). Bits for every slot, in a block of their own for an object that has
  more than sixteen: no guess, and a child's link to its parent may then be an element or a late field too.
- **7.C: every container** (unchanged): references inside structs, boxes, display objects, delegates, state
  machines. Test 127.
- **7.D, 7.E** as above, after the measurements of 7.C. The store into an object nothing else can reach stays a
  plain store, and what such an object holds is linked when it is first stored where a cycle can close, as today:
  the paragraph above that proposed to measure the other way is moot.

### Step 8. Plugin fixes that do not need the IR (A4.1, A4.5, A4.11)

1. `AddSource` compiles with access checks off for the game's assemblies (Roslyn's `IgnoresAccessChecksTo` on the
   generated compilation), and the compiler honours it; support field `[UnsafeAccessor]` as well.
2. Make the write hook cover every form `Accesses` reports as a write (`ref`/`out`, deconstruction, initializers),
   or report those forms as "cannot instrument" so the plugin treats them as unsure.
3. Tests for every existing interface member, including the rejection paths.

### Step 9. The plugin interface on the IR (A4.2-A4.10, goal 3)

1. Interface version 4: a plugin receives the IR of every reachable method after the analyses and before the
   optimizer (SPEC 7.1's place), and may rewrite it: insert, replace or remove instructions; add functions built as
   IR or as C#; clone a function and edit the clone. This is the LLVM-pass model, and it is what makes "potential
   needs" answerable.
2. On top of that, the conveniences the Invisible API needs today, each written against the IR:
   - a hook at any single write, read, call or branch, receiving the receiver, the old and new value, and for a
     collection the key; and able to replace the value (branch outcomes, random streams, rounding, limits);
   - accesses with collection entries (indexer and mutating calls on a field's collection resolved to the field),
     through locals by the real alias analysis;
   - path conditions from the IR's control flow, across calls;
   - the ownership-based alias query SPEC 7.1 names;
   - the call graph with delegate targets where known and an explicit list of unsure sites (unknown delegate,
     reflection, `dynamic`);
   - clones that follow virtual calls and lambdas, callable from generated code, with suppression by import set.
3. Closed forms as a real solver over the IR: linear recurrences in several coupled variables (matrix form), known
   pure functions (`Lerp`, `Clamp`, `Min`, `Max`) expanded, guarded pieces with the tick at which each guard flips;
   results as IR or C# the plugin can emit, not strings.
4. Write the Invisible API plugin's first slice in the Bazoodle repository against version 4 (classification,
   "mark changed", one gate, one solved timer) as the acceptance test; each gap it finds is a compiler issue.
   Done 2026-11-20 as `samples/invisible-api` in this repository (the owner's decision), against version 7 (above).
5. Keep versions 1 to 3 running through an adapter over the IR.

### Step 10. Configuration and reentrancy (A6.1, A6.2, A5.10)

1. One `CompilerFlags` record on `CompileOptions`; the CLI parses the environment into it; no other
   `GetEnvironmentVariable` in the library. Diagnostics output goes to a writer the embedder supplies.
2. Delete each kill switch whose optimization has passed checked mode; the few that stay are options, recorded in
   the module's version section so "same inputs, same bytes" is checkable.
3. Statics become fields of a per-compile context; library caches become a `LibraryCache` object the embedder owns.
4. Fetching sources (git, Godot editor) moves out of the library into a tool; the library takes a directory.
5. A test that runs two compilations concurrently and compares each with its sequential result.

### Step 11. Spec conformance (A5)

1. A5.1: the refusal sites that are control-flow shapes go in step 5.3. Work through the rest by frequency in real
   code; each fixed site gets a conformance program. Add Roslyn's and dotnet/runtime's own test programs where they
   fit, to find the sites nobody has hit yet. The remaining count is reported by the build of the suite.
2. A5.2: a missing capability compiles to a throw of .NET's exception at run time; only platform invoke stays an
   error. A5.3 goes with it.
3. A5.4: two-pass filters decided per `try` from what its filters can observe, library included.
4. A5.7-A5.9: secure randomness from a host import that fills bytes; string hash seed fixed in the module; identity
   hash from a per-instance counter that scheduling cannot reorder, or marked scheduling-dependent.
5. A5.12: a notice when a program has `Main` and no export; the CLI roots `Main` for console programs.
6. A5.6: stack frames from the module's own tables for engines without V8's stack text.
7. A5.13-A5.14: assembly-level keep attribute; export signature check; a location on every diagnostic.
8. A5.5: share reference-type instantiations in the IR where the body does not depend on the type.

### Step 12. Self-hosting and the proofs (goal 1, A5.16, A5.17)

1. With steps 2, 4 and 7 done, re-run the self-hosted compiler on the step 3 set in checked mode; fix the trap, the
   memory growth and the multi-core hang it shows.
2. An automated check: the self-hosted compiler's output equals the .NET-hosted compiler's, byte for byte, for every
   conformance program.
3. Proof 4 as a script: time, size and peak memory of the self-hosted toolchain against NativeAOT's WebAssembly
   build over the fixed set; goal 1 is met when every row is better.
4. Compiler speed itself (A2.7): cache the library's IR and analysis summaries between builds, keyed by the library
   version and the compiler flags, so a build parses and binds only the program (SPEC 7.6 allows it); ship that cache
   in the toolchain download in place of the library sources. Then one static-initializer pass, hashed keys instead
   of display strings, indexed function lookup. Target: the browser build budget of a few seconds for a small game.
5. Proof 5 in the browser on stock Godot; a headless path without JS (Wasmtime needs the import surface in a second
   host).

### Step 13. Repository (A7, A5.15)

1. LICENSE and a third-party notice. README matching the tree, with prerequisites.
2. `docs/STATUS.md` restored as the generated measurement summary; stale comments removed (A6.7).
3. One shared import implementation for the Node and web hosts.
4. A `WellKnown` symbol table replacing name literals; each remaining by-name special case either becomes a general
   rule or is listed with its reason (A6.5).
5. Record each modified upstream file and why, so the boundary is auditable (A5.15).

### What can run in parallel

Steps 1, 2 and 3 first, in that order. After them: step 4, step 8, step 10 and step 13 are independent of the IR and
of each other. Step 5 gates steps 6, 7 and 9. Step 11 can proceed alongside throughout, except 11.8. Step 12 closes.

### Open decisions for the owner

None are open. All three were decided by the owner on 2026-11-18:

1. A large free is handled by a release that does not visit the objects (step 7.D): regions and groups released by
   returning their pages. The price is accepted: a member dropped early stays until its region or group goes. This
   supersedes the 2026-11-16 ruling against retention, for regions and groups only; the growth is measured and
   reported (steps 7.B, 7.C), never hidden.
2. The back-reference proof goes ahead (step 7.E), with compile-time scoping (the groups of step 7) for what stays cyclic. The
   run-time owner-chain counting built on 2026-11-16 (`Runtime.cs`, "Owners") is the fallback where the proof fails,
   and goes with step 7.B: the redesign of 2026-11-18 replaces it.
3. Decided by the owner, 2026-11-18: the bar is **the same speed as NativeAOT, lower memory, no frame-time spikes**.
   Faster is welcome, not required. Step 6's target is therefore "no program in the set slower than NativeAOT's";
   step 6.6 (reuse) is done only if step 3's numbers call for it.

What was agreed for decisions 1 and 2:

- Decision 1: the alternative to a large synchronous free that the owner's rules allow is a free that does not visit
  the objects. An arena whose member types have no finalizer and hold no counted reference to anything outside it
  can be released by returning its pages. The compiler knows both facts per type. The same applies to an owned tree
  with no cycle (a list of nodes, a parsed document): allocate it in a region of its root and release the region.
- Decision 2: compile-time scoping does not replace the back-reference proof; they do different jobs. The proof
  removes the commonest cycle (a child's link to its parent) so the child stays a plain counted object and is freed
  the moment it is dropped. Scoping makes what is still cyclic cheap. Without the proof, a long-lived parent with
  children that come and go (a world and its enemies) is one arena that only grows.

- **Per-group locks for arena operations (stream C, 2026-11-20).** Measured first: a PLINQ query making 3,000 trees of
  200 nodes (tools/bench-threads/plinq_trees.cs) took 0.65 s on one thread, 4.9 on four, 9.1 on 24; on four threads
  one thread did all the work while the others slept in the runtime lock's wait, every handover a wake-up (not cache
  lines: the lock's words on lines of their own changed nothing). Now (Boundary/GroupLocks.cs):
  - A lock is one of 256 stripes, each word on its own cache line. A group's stripe is in each member's lock word (the
    first of its member words, unused before) and in its record (+28); an object that is no member is a group of its
    own, of the stripe its address gives. Whoever changes an object's lock word, or its count between plain and
    member, holds the stripe it had and the one it gets (a join writes the word before the count; a member that
    leaves keeps it until the count says plain; a merge restripes the smaller arena's members), so a stripe this
    thread holds, read in an object's lock word, means the object's words are its to read (GroupHeld); every read of
    another object's words in the machinery checks that first (the visits, the unlink's old value, Handover).
  - An operation locks its stripes lowest first and verifies them (the object may have moved meanwhile). A join's
    objects (the container and the value when plain, what their slots hold, and so on: ArenaLinkJoined) are found
    under the locks before anything changes (LinkLock: each object found has its stripe tried; a try that fails gives
    all back and the next attempt takes it in order; eight attempts, then every stripe). Not walked: a link that
    joins nothing (a finalizer's stores into its dying structure walked all of it at each store: test 133, 98 s).
  - A store's region (StoreLockIf, now with the slot's address) locks at its link, which knows the value; its slot's
    place lock is tried after the stripes (given back and waited for without them when busy), and the exchange is an
    atomic one (Rt.RegionExchange). Order: runtime lock, places, stripes, heap. The allocator's lists have a lock of
    their own (HeapLock), since code holding stripes allocates and the runtime lock's holders (Monitor) count.
  - A release that needs a stripe below those the thread holds waits for its locks to go (QueueRelease); the
    destructions wait as under the runtime lock (rt.destroy checks Rt.LocksHeld, AfterLocks runs them); a store's
    give-back still waits for its unlink. The kill list is the thread's own: drained before its locks go.
  - The arena scratch (visit state, kill, link, cut and trial lists) is per thread with threads: statics marked
    [Intrinsic] are thread statics in thread mode, plain ones without. All of it is behind Rt.ThreadMode, which
    Folding.KnownBool folds when each method is compiled: Rt.SingleThreaded resolves only at the end, after what a
    branch calls is compiled (that alone grew hello world by 527 bytes, through Interlocked's thread path).
  - Found on the way: AfterLocks looped forever inside a destroy function (rt.lock_exit leaves pending destructions
    to the destruction's loop there); a give-back run at a link's unlock, before its store was written; ArenaRetain
    left kills in the thread's list for good (another thread's ops skip a queued member).
  - Measured interleaved holding a heavy.sh slot, main before against now. Trees: 0.60, 3.2, 6.5, 7.0 s on 1, 4, 12,
    24 threads against 0.79, 0.46, 0.45, 0.44. Self-hosted compiler (threaded) on hello world: 16.3, 23.6, 37.7, 39.4
    against 18.7, 17.0, 26.6, 26.7; its output byte-identical to the .NET-hosted compiler's. Its profile on 12
    threads: StripeWait and StripeUnlock (the wake-up) on one stripe, the lexer cache's, the syntax node cache's
    and BlendedNode copies' stores and counts: Roslyn's types are one group, which locks per group cannot split
    (stream A's value sources per site, the back-reference proof). A waiter spins 2,048 times before it sleeps
    (test 139 on 16 threads: 830 to 700 ms). One thread: 15% slower than the one lock (the stripes' bookkeeping).
    No-threads builds: hello world byte-identical; micro and bench the same code (function type order and line
    numbers differ). Suite 187/187 checked and as shipped (070_files failed once in a run beside another session's, its fixed directory shared; passes alone); test 139 200/200 under tools/stress.mjs, 8 at once.
- **Threads past four (branch threads-scale, 2026-11-21), on stream C's group locks.** Measured on the homelab (16
  hardware threads), `tools/thread-scale.sh`: the threaded self-hosted compiler built once per commit (named) and
  timed on hello world per thread count, interleaved with other builds (ALSO), with the PLINQ programs of
  tools/bench-threads and a profile (PROF=n: busy time per thread, callers of the stripes' waits).
  `tools/bench-threads/shared_group.cs` is the compiler's shape in small: one arena of 4,096 cached nodes that every
  thread reads, refers to from new nodes and stores into (1.2 s on one thread, 2.7 on 24: it does nothing but arena
  operations on one group, so it can only get slower; a check that the waiting does not collapse).
  - Where the 12-thread time went (main ec13733): the parse. Waits on the one stripe from the lexer's and the parser's
    stores (LinkLock: Lexer.QuickScanSyntaxToken, LexSyntaxTrivia, BlendedNode copies in SyntaxParser.Reset, AddToken,
    MoveToNextToken: about 45%) and from the counts of cached tokens, members with no outside reference whose first
    and last one take the group's lock (LexerCache.LookupToken, FetchCurrentToken, AddNewToken: rt.load_retain's
    LoadRetainSlow, rt.retain, rt.release, about 45%). Every waiter spun on the lock word before it slept.
  - Done: (1) at most two threads spin for a stripe, the others sleep, and an unlock wakes one only when none spins
    (GroupLocks.cs, StripeWait); (2) before the first thread starts no walk under LinkLock and every object counts as
    this thread's (StripeMine), as without threads; (3) a store into a pinned plain object (a static readonly field's
    array: Roslyn's node cache) takes no lock and links nothing, as it never could; (4) with threads, a member stored
    into a plain object stays a counted reference and the container plain (no join): no member refers to a plain
    object of a cycle-capable class (a store into a member joins it), so no cycle closes through it until a member
    comes to refer to it, and it joins then with its slots linked; LinkLock then locks only the container (which keeps
    it plain) and the store links nothing (t_linkOver: the value, unlocked, may have left its group meanwhile). Fresh
    and owned stores already left such counted references.
  - Measured, 3 interleaved rounds, main ec13733 against the branch (ed1e518): 15.7 / 13.2 / 16.8 / 17.7 / 18.1 s on
    1 / 4 / 12 / 16 / 24 threads against 15.5 / 12.9 / 15.1 / 15.9 / 18.2. Output byte-identical on every count.
    12 and 16 threads 10% faster, 24 (more threads than the machine has) unchanged; more threads than four are still
    slower than four (check 5 not met). The spinner limit gave most of it (16.2 on 12 threads alone); (4) kept the
    lexer and parser plain only in part: the profile after it still has the BlendedNode copies' and the lexer's stores
    waiting, so those containers become members some other way, and the token counts are as before.
  - Tried and dropped: leaving a just-freed stripe to the thread that gave it back for a while (cache locality of the
    group's words): shared_group 10-35% slower on 4 to 24 threads.
  - Left, by measurement the next pieces: the first and last outside reference of a member without the group's lock
    (a count exchange that the lock's holders must not misread: every reader of a member's count word under the lock,
    about twenty, the drop check, TopHeld and the holds, would have to see such an exchange or exclude it, a lock per
    member at least for the count word and the hold's words); and finding what still makes the parser's arrays
    members (CS2WASM_DROP_STATS's op counts on a threaded build).

### NetWasm's suite as il2wasm's main suite (SPEC 13), 2026-11-23, claude/netwasm-suite

- The harness: `tools/netwasm-suite.mjs` with `tools/netwasm-suite/Extract` (the case extractor), `Shim` (an il2wasm-owned
  stand-in for the part of TUnit's API that tests/libraries uses) and `Launcher` (starts each .NET run with Windows'
  fault dialogs off). All of it is MIT and il2wasm's own. No NetWasm file is in the repository. The pinned commit
  (d0cecda45cc8) is fetched into `%LOCALAPPDATA%/il2wasm/netwasm`, and the harness refuses a cache inside the repository.
- Extraction: a small evaluator over C# syntax walks tests/NetWasm.Compiler.Tests and recovers each `runner.Run(...)`
  case (theories through InlineData and MemberData, helpers, `with`, extension methods, embedded resources). It also
  takes the fixtures with a JSON manifest, eng/open-source-suts and tests/libraries. Each case becomes a net11.0 console
  project whose generated Main prints ASCII observation lines. NetWasm's stored expectations are never read.
  474 cases: 375 runner, 70 fixture, 6 open-source programs, 23 library suites (350 tests); 78,073 inputs; 169
  duplicates folded (the same program at NetWasm's optimisation levels). Not extracted: 31 test methods. 10 of those are
  IL emitted by the harness (no C# source), 1 is an xUnit project that compiles NetWasm's own library sources, and the
  rest are tests of NetWasm's harness itself.
- Oracle: .NET 11.0.0 win-x86 (IntPtr.Size 4), from a .NET root assembled out of the NuGet runtime pack (no machine-wide
  install), run three times per case. It runs only on the homelab (`--oracle-here`), because a library fixture calls
  Debugger.Launch/Break, which opened Visual Studio's JIT-debugger dialog on the desk. Homelab, d508471: 473 of 474 built
  (1 has top-level statements: a harness test) and 473 of 473 were stable over three runs. `sut:imagesharp`'s WebP lossy
  encoding differs with hardware intrinsics off (760 against 756 bytes); the expected output is with intrinsics on.
- il2wasm's step: a command template (`--compile`, `IL2WASM_COMPILE`; `{dll}` `{out}` `{mode}` `{checked}`
  `{project}`). The module runs under host/run.mjs and is compared byte for byte (stdout and exit code), checked and as
  shipped, `--repeat n` at once. Not yet run with il2wasm: the IL front end cannot compile these yet. The plumbing is
  proven with `--compile dotnet-standin`: 473 of 473 passed in both modes, 4 at once each.
- Classification (none skipped; the expected output is .NET's): accepted differences 7 (KnownNonBugCorpusPartitioner);
  PlatformNotSupportedException in the source 17 (3 throw it under .NET); type names 13; GC-dependent 144; needs GC
  semantics 114; collects at every allocation 88; pointer size 65; desktop carve-outs 6; desktop entry differs 10;
  NetWasm-only API 6; machine state 4; debugger 1. 39 library tests fail under .NET: they assert NetWasm's behaviour
  (compression bytes, crypto, file system, PlatformNotSupportedException expectations).
- Open for the owner: outputs that depend on the host OS. Compression bytes, the file system and crypto are .NET on
  Windows here. il2wasm on WASI must match which .NET?
### IL front end, steps 2 and 3 (claude/il-frontend, 2026-11-23)

- Type system: NativeAOT's (dotnet/runtime v11.0.0, deae2e32, ILCompiler.TypeSystem's 231 files, MIT) in
  `src/Il2Wasm/IL/TypeSystem`, one change marked `// il2wasm:` (a class with layout over an auto-layout base is laid
  out as auto, as CoreCLR does: the IL CoreLib's SafeHandle). `Types/IlContext.cs`: wasm32 (pointers of 4 bytes),
  System.Object laid out as the 12-byte header so NativeAOT's offsets are il2wasm's, canonical forms over __Canon.
  A long is 8-aligned in structs and classes (as NativeAOT's wasm32 and ARM32; x86 .NET aligns it to 4), kept for
  the WASM atomics.
- IR and importer (`src/Il2Wasm/IL/Ir`): SSA by Braun et al.'s construction over locals, arguments and stack slots at
  joins, ECMA's stack types checked, exception regions as a tree. Locals written in a protected region or a finally
  are variables (no SSA value names their value at a handler); address-taken locals and every struct local are in a
  frame on the shadow stack. Struct values are copies of their own in the frame.
- Ownership, naive (`Ir/Ownership.cs`): every reference value (and struct with references) owned from definition to
  last use, by SSA liveness with handlers as successors of their try's blocks; each value's local zero when it holds
  no count, which makes the releases at block entries, handler entries and exceptional exits path-independent.
- Structuring and lowering (`src/Il2Wasm/IL/Lower`): per region a graph of its blocks and of try compounds, Ramsey's
  "Beyond Relooper" on it, a dispatch loop when it is irreducible; try_table with catch_ref and type tests in order;
  finally and fault over catch_all_ref with a leave local. A branch into a try's start from outside (a loop headed
  by a try, a catch's leave back to its try) is an ordinary edge into the compound, so no rewrite is needed.
  The back end's passes need `br` after a try_table's end, not `unreachable` (with `unreachable`, a pass cut the
  function's body): the old front end's shape is kept.
- Results [run, desk, tests/Il2Wasm.IL.Tests against a hand-written stub CoreLib]: 67 of 67 checks, 8 programs equal
  to .NET (run in process as the oracle), each with 0 allocator blocks live at its end, back end optimization on and
  off. The old front end's 145_size_hello still passes (narrow suite, desk).
- The IL CoreLib (claude/il-corelib's artifacts/framework) with `Console.WriteLine(42)`, compiled leniently
  (IL2WASM_IL_LENIENT=1: what is missing traps, as a warning): 335 warnings, 28 missing intrinsics (TypeInfo.*,
  Il2WasmArrays.*, TextScan.Eq16*, Rt.Sqrt, Rt.SccOf, Rt.MarkConstructed, Rt.ArrayRankAt, Rt.ArrayElementSizeAt,
  Rt.ArrayMoveMode, Rt.MemStatsHash, Atomic.Add64), about 300 methods that need Type objects (typeof other than a
  comparison of two known types, which folds), 3 ldftn. Not run: the module traps where those are reached.
- Not done: Type objects and the rest of ldtoken (RuntimeHelpers.InitializeArray and CreateSpan over field data),
  delegates and function pointers (ldftn, ldvirtftn, calli), generic sharing (everything is instantiated), generic
  virtual methods, two-pass filters (one pass now), thread-safe static constructors, array-store covariance checks,
  floating-point remainder, finalizers and weak references, stack traces and the line table from the PDB.
### il2wasm step 1: the core library as IL (claude/il-corelib, 2026-11-23)

What was done:

- `tools/BuildCoreLib` (and `tools/build-corelib.mjs`) builds `System.Private.CoreLib.dll` and its PDB ahead of time
  with Roslyn. The inputs are the old `Library`'s: the manifest, the edits and il2wasm's own files. The manifest's
  156 substitutions are applied as source bodies.
- The framework directory holds that CoreLib and the 180 stock IL assemblies of
  Microsoft.NETCore.App.Runtime.Mono.browser-wasm 11.0.0, which is built from runtime deae2e32, our v11.0.0 tag.
- docs/CORELIB.md covers the build, the pack, the reference check, the native entry points and hello world's reach.
  docs/CORELIB-INTRINSICS.md lists the 220 intrinsics, the 39 intrinsic fields, the 40 upstream intrinsics and the
  host imports, with what the old compiler emitted for each.

Measured:

- **Build.** About 10 s. Byte-identical on rebuild (`node tools/build-corelib.mjs --check`: 183 files).
- **Reference check.** 0 of 12,172 TypeRefs and 0 of 29,032 MemberRefs from the stock libraries are missing or not
  public. The checker reports 37 types and 51 members missing against .NET 8's CoreLib.
- **Native entry points.** 28 in the stock libraries:
  - 19 in libSystem.Native, of which hello world reaches `SystemNative_Write` and 3 error helpers;
  - 9 in libSystem.IO.Compression.Native (zlib).

  Our CoreLib has no P/Invoke.
- **Hello world** reaches CoreLib and System.Console, nothing else.

Runtime change: one local in `Rt.AllocObjectSlow` is renamed (CS0136, which emit refuses and binding tolerated). The
old compiler's hello world module is byte-identical with and without it (checked and as shipped, 27,784 B).

Not done:

- the IL front end (claude/il-frontend);
- runtime bodies for the 28 native entry points;
- hybrid-globalization `GetLocaleInfo`;
- the full homelab suite and the self-host check. They were not run, because the only runtime change is that rename.

### il2wasm step 1b: the runtime side of the stock libraries' native calls (claude/il-corelib, 2026-11-23)

What was done:

- `natives.txt` is the binding table the IL front end reads: (library, entry point) and internal calls mapped to
  CoreLib methods (docs/CORELIB.md section 7). `BuildCoreLib bindings` shows all 56 of the stock libraries'
  P/Invokes and internal calls bound, with matching WASM signatures, and none unbound.
- libSystem.Native's 19 entry points behave as the browser runtime's System.Native over Emscripten 6.0.2.
- errno is Emscripten's (generated from the headers), and CoreLib's own boundary moves to it from PAL codes.
- The JavaScript interop's internal calls are host imports; today's hosts throw on them, since they do not implement
  Mono's protocol.
- GetLocaleInfo has a body under invariant globalization.
- libSystem.IO.Compression.Native: deflate is a C# port of zlib-ng 2.2.5; inflate is the managed inflater.

Measured:

- **Deflate.** tests/CoreLibNatives: 30,690 cases identical to .NET's shipped zlib-ng in output (611,069,290 bytes),
  return codes and stream positions, over every level, strategy and window, with random chunking and flushes. The
  only byte compared differently is the gzip header's OS byte, by design (Windows 10, Emscripten 3).
- **Inflate.** 3,000 streams with the same output and final result. The per-call positions differ in 737 of them.
- **Desk suite.** 145, 070, 088, 152, 220 and 193 pass both ways. Hello world is unchanged (27,784 B, the same hash).
- **CoreLib build.** Still byte-identical on rebuild (184 files).

Not done:

- a port of zlib-ng's inflate (positions and error texts);
- the hosts' side of Mono's JavaScript interop;
- [JSExport] binding;
- FS.mmap's EACCES check;
- the homelab suite and the self-host check (left to the coordinator).

### il2wasm step 1c: zlib-ng's inflate ported (claude/il-corelib, 2026-11-23)

`Natives/ZLibNgInflate.cs` is zlib-ng 2.2.5's inflate.c, inftrees.c and inffast_tpl.h in C#, with the licence notice.
It replaces the managed inflater.

Measured with tests/CoreLibNatives against .NET's shipped native zlib-ng: 72,240 inflate runs identical
(2,568,441,758 output bytes, 833,572 calls). Each call's return code, stream positions and error message matched.

- The runs cover every stream of the deflate corpus, read eight ways: DeflateStream's way, random chunks and
  flushes, auto-detect, truncated, with trailing bytes, and four bit-flip corruptions.
- 18 distinct zlib-ng error messages came up.
- Deflate is still identical on its 30,690 cases. CoreLib rebuilds byte-identical, and all 56 bindings hold.
- Desk: 145 and 070 pass both ways; hello world is unchanged.

Not run: the homelab suite and the self-host check on this commit.

### IL front end on the IL CoreLib, and the NetWasm suite's first number (claude/il-frontend-2, 2026-11-23)

- Console.WriteLine(42) against claude/il-corelib's framework (a copy of artifacts/framework built 16:59) compiles
  strictly and prints 42 under host/run.mjs [run, desk]; module 1,948,641 bytes (size not worked on). With
  IL2WASM_IL_COUNT_OBJECTS=1, 44 objects are live at exit with one call and with three: nothing leaks per call; that
  the 44 are exactly what statics hold is not shown.
- What it took: the natives table (P/Invoke and internal calls bound to CoreLib methods; EntryPointNotFound and
  DllNotFound as SPEC 8); the CORELIB-INTRINSICS methods hello world reaches (atomics, math, the runtime's switches
  baked and folded at import, groups' entry points in their no-group form, Generics.*, TextScan, arrays' helpers,
  TypeInfo tables by type id; reflection's own tables empty); upstream intrinsics by name; Type objects as CoreLib's
  RuntimeType by id; ldtoken, InitializeArray, CreateSpan, field data; delegates, ldftn, ldvirtftn, calli; constrained
  static virtuals and generic methods; string's Ctor methods; arrays' interfaces through SZArrayHelper; KnownBool's
  members folded at import (SIMD off, EventSource off, invariant globalization) and typeof(T).IsValueType.
- Bugs found by measurement: thread statics overlapped other static blocks; data fields read zeros; an unused
  reference argument was released before its entry count (found with IL2WASM_IL_WATCH: the console's encoding freed
  after its first line).
- The command line il2wasm-il (src/Il2Wasm.IL.Cli): a dotnet build output, its neighbours and the framework.
- NetWasm's suite on the desk with the oracle records pulled from the homelab, shipped mode only (the checked mode
  builds the same module today) [run, desk, 450 case-modes: 23 cases had stale oracle records]: **161 passed**, 217
  failed, 71 did not compile, 1 timed out. Failures by first difference: 112 a wrong value, 33 a trap
  (unreachable), 8 stack overflow, 3 out-of-bounds, 10 NotSupportedException from CoreLib's "il2wasm: not supported
  yet" paths, 10 NullReferenceException; did not compile: Delegate.TargetOf (19, added after the run, not re-run),
  generic virtual methods, multi-dimensional arrays, calli with struct arguments, Rt.DelegateMethodEntry and
  DelegateArity, others.
- tools/netwasm-suite.mjs ran modules with a relative host/run.mjs from the case's directory (every module failed to
  start); it now gives the runner's absolute path.
### IL front end: reachability to a fixed point, substitutions, assemblies' reflection records (claude/il-generics, 2026-11-23)

- Measured first (IL2WASM_IL_REACH, IL2WASM_SIZES_FILE, tools/il-reach.mjs: each method's first-reach chain and its
  subtree's bytes, the data by kind). Hello world on the IL CoreLib, 1,949,547 B, 7,930 methods: EventSource reached
  from SharedArrayPool.Rent (IsEnabled() not folded, about 670 KB of subtree); StreamWriter's ctor reaching
  ThrowIfNegativeOrZero -> SR.Format -> string.Format (about 1.2 MB); Stream.get_Position -> ResourceManager.
- What is in (src/Il2Wasm/IL/Reach, Lower/Assemblies.cs; each rule has a switch):
  - SCCP on the IR (Wegman and Zadeck; integers exact, nothing folded that would throw; references null, non-null,
    exact type, null-or-exact) and a second import with its facts: branches that go one way are not imported
    (Importer's ImportFacts hook) (IL2WASM_NO_SCCP).
  - ILLink substitutions of every assembly (CoreLib's XMLs embedded as ILLink.Substitutions.xml by BuildCoreLib, as
    the runtime's build does), applied under browser-wasm's feature defaults and the project's runtimeconfig.json
    (IL2WASM_NO_SUBSTITUTIONS).
  - Pure calls evaluated: provably no exception, no effect, no cycle, no static constructor, no intrinsic; never
    removed when an argument is counted (a call is its argument's last use: GC.KeepAlive) (IL2WASM_NO_PURE_CALLS).
  - Rounds: the program is reached again with the round before's facts until they stop changing (each round's facts
    hold for the next, since every fact only removes code): unwritten statics (IL2WASM_NO_UNWRITTEN_STATICS),
    constant arguments of methods reached only by direct calls (IL2WASM_NO_CONSTANT_ARGS), return values
    (IL2WASM_NO_RETURN_VALUES), class instance fields' value sets with their default (IL2WASM_NO_FIELD_VALUES), a
    virtual call's value over the constructed or exact receiver's implementations (IL2WASM_NO_DEVIRT_VALUES).
  - Reflection records: type names only for reflectable types (IL2WASM_NO_REFLECTABLE_TYPES); the assembly table
    (.NET's full names), assembly attributes kept when their type is a typeof/ldtoken target, with factories; manifest
    resources of reflectable assemblies, so stock libraries' exception messages come from their .resources as under
    .NET; arrays of types named straight into Array.CreateInstance/MakeArrayType (IL2WASM_NO_REFLECTED_ARRAYS).
- Results [run, desk]: hello world 1,949,547 -> 845,943 B before merging main; on merged main 2,145,227 B with every
  rule off, 970,075 B with them (it now carries System.Console's resources and the attribute factories). Compile
  14.4-15.0 s with the rounds against 12.8-13.8 s without (the desk loaded by a suite run). Stub and runtime tests 85
  of 85 (r50_reach_rules, r51_library_messages, r52_reflected_arrays new; r51 is il-codegen's probe:
  LinkedList's ICollection.CopyTo message).
- The 28,383 B bar is not reached and not near: what remains in hello world is the stock System.Console's real
  error paths (SafeHandle.IsClosed in Flush, Encoding(int)'s ThrowIfNegative of a codePage no constant reaches),
  which reach SR.Format, string.Format and the ResourceManager. Integer ranges and flow-sensitive field values are
  the next general rules; the old front end's 27,784 B came with its own Console boundary.
- NetWasm suite [run, desk, shipped, 4d29bf0: merged main, before the assembly records]: 224 passed, 214 failed, 11
  did not compile, 1 timed out (main: 186 passed).
- Not done: generic sharing (every instantiation is still its own code), Delegate.Method, DynamicInvoke,
  DelegateArity (method reflection records), member reflection records beyond assemblies.
### IL front end: the runtime's semantics (claude/il-runtime, 2026-11-23)

- Finalizers and weak references (SPEC 4.4), Lower/Finalizers.cs: the destroy path runs a class's finalizer (the
  most derived Finalize override: in IL a C# destructor already calls its base's) with a temporary count, an object
  its finalizer brings back lives on and is not finalized again (Rt.FinalizedBit) unless GC.ReRegisterForFinalize
  asks; ObjectMarks.OnFinalize clears the short weak handles before the finalizer and skips a suppressed one,
  OnDestroy clears the rest (WeakTrackResurrection, ConditionalWeakTable's dependents). Compiled in only once
  ObjectMarks.SetAt is reached. No groups in the IL front end yet: Rt.WeakDyingAll is not wired (nothing dies as a
  group). Test r01 [run, desk, against .NET in process].
- Static constructors (SPEC 3), Lower/Statics.cs: ECMA's precise triggers (static field, static method, any
  constructor including a base call, a value type's instance method); the old front end's state word (0, -1, -2)
  and the InitChecks shape; an exception becomes a TypeInitializationException, the same object at every later
  access (CoreCLR's). One thread only: the threaded protocol (owner id in the word, waits, cycle detection) is not
  ported, because the IL front end compiles no threads yet. Test r02.
- Activator (SPEC 6.6), Lower/Activation.cs: RuntimeType.CreateInstanceOfT, CreateInstanceMono and
  CallDefaultStructConstructor served by generated switches over the constructed and typeof'd types, with CoreCLR's
  messages and TargetInvocationException wrapping (ThrowHelpers.CannotCreate, InvocationFailed in Runtime.cs). Test r03.
- Two-pass filters (SPEC 3), Lower/Filters.cs and Ir/FilterSlots.cs: records [site, frame base] on a stack of their
  own while a try with catches runs; throw and rethrow walk them first (rt.first_pass), a filter running as its own
  function on the method's frame (what a filter uses is a frame variable); a try with a filter takes the exception
  only as decided. Without filters in the program the functions are empty and inlined away. A filter that reads a
  struct argument keeps its method in one pass. A stale decision (a later throw's first pass inside a finally) is
  re-decided from the site, which can run a filter twice (as the old front end). Test r04.
- Stack traces (SPEC 6.3), Lower/DebugInfo.cs: frame names in .NET's format from metadata, file and line from the
  portable PDB's sequence points, the il2wasm.frames/lines/inline sections, Rt.OnThrow at a throw and Rt.OnCatch at
  the handler. The frames section is about 83 KB of a 2.27 MB hello world. Test r05.
- Exports (SPEC 6.2), Lower/Exports.cs: [UnmanagedCallersOnly(EntryPoint)] methods exported, an escaping exception
  reported to the host with its trace and the export returning zero. Test r00.
- The checked build, Lower/Checks.cs: Rt.Checked is 1 (poisoned, never-reused freed memory), rt.release traps on a
  release too many, rt.retain on a freed object. It found, and this branch fixes: pinned locals not keeping their
  object (Ir/Pinning.cs; shipped too: fixed over a temporary array read freed memory), the runtime's int addresses
  of objects (Rt.ArrayData etc.) not keeping theirs (the console wrote freed memory), and a rethrow using an
  exception its handler had let go. Test r06.
- Counting bugs found on the way and fixed (il-codegen's area, told): a store of a value that lives on was counted
  twice (a leak); new IntPtr(v) wrote through address 0; Interlocked byte/ushort atomics recursed.
- NetWasm suite [run, desk, own cache copy of the work directory, oracle records from the shared cache]: at a04d832
  (with il-codegen through d2ce0aa) **257 passed**, 182 failed, 11 did not compile, 0 timed out, the same in the
  shipped and the checked build (no case differs between the two; 439 of 450 case-modes per mode with a stable
  oracle). At 29b8e62 (finalizers and static constructors on main 179edef; checked = shipped then): 182 passed, 218
  failed, 49 did not compile, 1 timed out.
- Homelab: at a04d832 the old suite 264 of 264 checked and 264 of 264 as shipped; at 8b92adb the self-hosted compiler
  byte for byte with the .NET-hosted one on 145, 220, 193 and 088, hello world in 7.0 to 7.2 s over 3 rounds (27,723
  bytes). IL tests 94 of 94 [desk]. Not run: stress (no new old-suite tests), frames.mjs and bench.mjs (the old front
  end and the back end are unchanged; Runtime.cs only gained two methods the old front end never reaches).
- Not done: thread-safe static constructors with real threads (no threads in the IL front end yet), weak references
  of groups (no groups yet), filters in a method whose filter reads a struct argument (one pass), Activator for
  types the program never constructs nor names with typeof.
### IL front end: importer and lowering correctness, driven by the NetWasm suite (claude/il-codegen, 2026-11-23)

- Method: the NetWasm suite on the desk (shipped mode, oracle records pulled from the homelab, a cache of its own
  under the session's scratch directory), failures grouped by first difference and stack, the smallest repro of
  each group written as a program of tests/Il2Wasm.IL.Tests/FrameworkPrograms (`fw`, f01 to f16: compiled against
  the real IL CoreLib, run under host/run.mjs, .NET in process as the oracle), then the fix at the cause. No
  NetWasm code is in the repository; every test is il2wasm's own.
- Suite, shipped mode, desk, each run whole with --force [run]: main 179edef 163 passed, 237 failed, 49 did not
  compile, 1 timed out; after the first two commits 186/252/11/1; f8a57e0 226/212/11/1; d2ce0aa 255/184/11/0;
  9e69375 (with claude/il-runtime a04d832) 262/178/9/1; f96d770 263/178/8/1; 162ff60 (with TypedReference) 265/177/7/1, nothing that passed before failing at any stage after its fix (two regressions of f96d770 fixed in 162ff60). da78abc (UnsafeAccessor through NativeAOT's generator) not run whole: its two cases checked alone.
- Features: generic virtual methods (a stub per instantiation called; each case the override of the method's
  definition on the receiver, instantiated with the call's method arguments: NativeAOT's resolution);
  multi-dimensional arrays in the layout the runtime's Array reads (lengths at 16 + 4 d, elements from
  (16 + 4 rank) rounded to 8; newobj with lengths or zero lower bounds; generated Get, Set, Address, bounds-checked
  and row-major; the data offset in destroy, retain_contents, copy, clear and InitializeArray); calli with struct
  arguments; floating-point remainder (Rt.FMod); [UnsafeAccessor] bodies (all five kinds, generic owners and
  methods; a missing member throws MissingMethodException or MissingFieldException); initblk and cpblk; array
  covariance store checks and ldelema's exact-type check (readonly. exempt); Nullable<T> box and unbox as ECMA-335
  says; delegates of static virtuals on type arguments (constrained. ldftn), of another delegate's Invoke, of
  extension methods (closed over the first argument), and the CLR's errors for a null target.
- Bugs found by measurement: a T[] had none of IEnumerable<T>, IList<T>, IReadOnlyList<T> and the rest (the IL
  CoreLib has no Array<T>, which NativeAOT's type system reads them from; now its SimpleArrayOfT algorithm);
  a boxed struct with references released them twice when the box was freed (heap corruption: every struct
  enumerator used through an interface, async Start's null test); string.Empty read as null (an [Intrinsic] field
  no initializer sets); a reference released while an address into its object was still used (unbox then ldfld,
  ref a[i], spans over new arrays); a returned address's base released after the ret (a leak per call:
  GCHandle.AddrOfPinnedObject); the 64-bit checked multiply trapped on MinValue * -1 and checked double
  conversions at 2^63 did not throw; open types (typeof(KeyValuePair<,>)) failed the type tables; TextScan's
  vector case change wrote past a non-ASCII char.
- Addresses and spans as roots (Ownership.cs): an address made from a reference (field and element addresses,
  unbox, address arithmetic and conversion, a call's returned byref), and a byref-like struct a call or newobj
  makes, carries its bases; a use of it is a use of them. A span or ref variable keeps everything ever stored into
  it, for every read of it (flow-insensitive: at worst a count kept longer); phis are not followed (an address
  carried around a loop in SSA does not keep its object; il-runtime's Pinning.cs covers fixed). At a return the
  bases are released before it when something else holds them (an argument, a load, a cast of one) or the result
  is a raw address; otherwise an object only the callee held stays under the returned span or byref (a leak, where
  a GC keeps it). A first version (6026574) released every base before the return and freed spans' arrays; a
  leaked count in GetArrayDataReference had hidden the one-block limit for variables (TextInfo, SearchValues).
- TypedReference (mkrefany, refanyval, refanytype).
- Not done: UnsafeAccessorTypeAttribute's type translation and
  the invalid-attribute errors; DynamicInterfaceCastable; DelegateArity, DynamicInvoke and Delegate.Method
  (reflection tables, claude/il-generics); Array.CreateInstance of array types the program never makes and SR
  resource lookups (claude/il-generics); the zero-length Array.Copy type check and Stopwatch.Frequency (CoreLib's
  code, not codegen). Unknown: whether the leaked base of a returned span over a callee-made object matters in
  practice.
- Not run: the homelab suite, stress and the self-host check (the old front end, the back end's passes and the
  runtime's C# are unchanged by this branch, apart from Boundary/CompilerHelpers.cs, which only the IL front end
  reaches; the IL front end is its own project); frames.mjs and bench.mjs (the IL
  front end does not compile those programs yet).

### Memory model step 1: facts, summary, solver, the phase split (claude/mem-foundation, 2026-11-24)

- What: docs/MEMORY-IR.md 11.3 step 1. `IL/Memory/Facts.cs` (section 2's lattice as side tables, each fact with its
  join and its emptiness test), `Summary.cs` (`MethodSummary`, `ImportedMethod`, `IReachHost`), `Solver.cs` (the
  reach phase: imports only, RTA dispatch; `ProgramSolution`: stable ids, dispatch targets, receiver types, Mods
  over call-graph SCCs on first use), `Pipeline.cs`; `IlCompiler.Reach` split into ReachPhase, SolvePhase and
  LowerPhase (`Lower/Phases.cs`); `ProgramFacts.Import` writes nothing and `Merge` records each method's facts where
  its import used to be; stubs with frozen signatures for the six S1 rows (MEMORY-IR.md 11.4). Lowering is still
  naive (`Ownership.Run`); `IL2WASM_MEM_PLAN=1` runs the stub planner pipeline.
- Byte identity [run, desk]: hello world on the IL CoreLib 1,377,218 B, the same bytes as main cee3cab; every module
  the IL tests compile (48) the same bytes as main's (IL2WASM_IL_TESTS_DUMP, a new test-harness hook).
- Tests [run, desk]: IL tests 109 of 109. NetWasm suite, shipped, desk, case by case against main's CLI on the same
  cache: 283 passed, 159 failed, 7 did not compile, 1 timed out with both, every one of the 450 case-modes the same status as main (no case lost or gained).
- Coverage [run]: methods only lowering reaches (the generated code's helpers, finalizers, activation, attribute
  factories) are late imports, outside the solution. The round before's late methods and late-constructed types
  seed the next reach phase: hello world's rounds 0 to 5 have 1,002, 3, 1, 0, 0, 0 late imports of about 4,350
  methods lowered, and the last round imports nothing lowering does not take.
- Cost [run, desk, hello world, interleaved runs]: compile time equal within the desk's noise (new 11.0-11.4 s,
  main 10.7-11.5 s). Live data held between the phases: 19 MB of bodies and 4 MB of summaries (measured by dropping
  them). Peak working set under the CLI's server GC (24 heaps): 625-673 MB against main's 497-549 MB (about +23%,
  inside 3.8's 25% budget, accepted by the coordinator for step 1; the headroom is S1's); under workstation GC
  (DOTNET_gcServer=0) 431-499 MB against 413-438 MB. IL2WASM_MEM_REIMPORT=1 (re-import at lowering) costs about
  0.8 s and did not lower the server-GC peak (575-631 MB). The CLI's GC mode is unchanged.
- Not done: variant functions have no queue entry in IlCompiler; receiver types come from static types only;
  intrinsic effects are Wild until Memory/IntrinsicSignatures.cs; ReleaseEffects is All when any finalizer exists;
  PureCalls' memo depends on import order (3.5) though no module changed; no Groups.cs/Threads.cs stubs. Not run:
  the homelab suite, stress and the self-host check (the old front end, the runtime and the back end are
  unchanged).

### Memory model S1 row f: lowering from MethodPlan, the variant queue (claude/s1-lowering, 2026-11-24)

- What: `Lower/FunctionLowering.cs` (with `FunctionLowering.Plan.cs`) lowers from the plan's tables, on the contract
  the coordinator accepted.
  - `Own` Guaranteed or Trivial: no count at the load, cast, catch value or phi copy, and no release on unwind.
  - `Counts` Elided: nothing. `Atomic`/`LoadRetain`: lowered plain only when the solution is single-threaded,
    otherwise an internal error until S3.
  - `Stores` Init (or `Counts` Elided at a store): the old value is neither read nor released.
  - `Args`, aligned with call.A, and call `Moves` bits: HandOver moves the caller's count in. KeepAlive is one retain
    at the push, kept by the callee. The 4.6 protection around a Lend call is explicit Retain and Release nodes.
  - `Callees`: direct calls and constructors call the chosen variant; any other site with a variant is an error.
  - Entry BorrowReturn: a return takes no count.
  - `Allocs` Virtual or FrameOwned (a class object, a non-Nullable box, or an array of constant length, at most
    256 B, not finalizable): a frame slot made by `Rt.InitFrameObject`, with an immortal header. The plan's Release
    of the object releases its children and empties the slot; re-making the slot and unwinding release what it still
    holds (SPEC 4.3: never later than the object's last use). Virtual is lowered as a frame slot for now.
  - `Lower/RcStats.cs`'s `Count` at every count operation, the implied ones included (load retain, store old and
    retain, phi, unwind, argument keep-alive, return).
- The variant queue, the one IlCompiler change of S1:
  - `IlCompiler.VariantFor(m, v)` makes `m + v.Suffix` with the entry's signature, queues it on the method queue,
    and refuses methods with no IL of their own; under `Pipeline.Verify` it also refuses a variant that
    `Signatures.VariantsOf` does not list.
  - `CompileMethodCore` lowers a variant from a fresh import, merging its facts again (idempotent).
  - `PlanMethod` takes the entry, so `PlanContext.Entry` is the variant.
- Test aid: `IL2WASM_LOWER_SELFTEST=1` rewrites naive plans into equivalent ones that use these paths:
  - an argument whose last use is a direct call is handed over, and the call takes `Owned(mask)`, whose plan drops
    those parameters' entry retains;
  - `=2` adds FrameOwned for local objects of a constructor that only calls Object's.
  `IL2WASM_LOWER_STATS=1` reports frame sites and variants. Both are off by default.
- Byte identity [run, desk]:
  - hello world (`Console.WriteLine("Hello, World!")`, IL CoreLib) 1,374,328 B, the same bytes as main;
  - every module the IL tests compile (48) the same as main's, by default and with `IL2WASM_MEM_PLAN=1` (stub
    planner).
- Tests [run, desk]: IL tests 109/109 by default, with MEM_PLAN=1, and with SELFTEST=1 and =2. With row c's
  planner (a local merge of claude/s1-plan 71355f3, MEM_PLAN=1, framework rebuilt): 109/109.
- Frame objects [run]: 0 on hello world, the IL tests and the bench kernels, because no planner sets `Allocs` yet
  (row c's first stage does not). Under SELFTEST=2: hello world and the bench have 0 sites (their objects all
  escape or have constructors with work); a scratch program has 2 sites (an object in a loop with two string
  fields, and one outside), with the same output and the same live count at exit (343) as on the heap.
- Variants [run]: none with the stub Signatures. Under SELFTEST=1 (a variant at every last-use site, no size cap:
  an upper bound, not 4.4's policy): hello world 1,027 Owned variants, 1,409,885 B (+35.6 KB, +2.6%); the bench
  kernels (tools/bench/Programs.cs as one program) 1,218 variants, 1,669,678 B against 1,631,081 B (+2.4%),
  checksums and live count (1,405) unchanged.
- With row c's planner [run]: hello world 1,297,010 B (-5.6%); the bench kernels 1,536,608 B (-5.8%), checksums
  and live count unchanged.
- NetWasm suite [run, desk, shipped, SELFTEST=2 so the new paths run]: 283 passed, 159 failed, 7 did not compile,
  1 timed out. All 450 case-modes have the same status as main's results on the same cache (none lost, none gained).
- Cost [run, desk, hello world, framework mode with its Roslyn step, interleaved]: base 20.8-22.5 s, peak 516-526
  MB; new 19.9-22.1 s, peak 486-513 MB. Equal within the noise: with the tables unset, lowering only adds
  emptiness tests.
- Not done:
  - a variant at a dispatch site (a group's shared entry needs per-group stubs in GenerateLate);
  - a frame object's finalizer (an internal error for now);
  - true scalar replacement for Virtual (it needs the IR shape after inlining, row d);
  - AccessKind, which lives in IlCompiler.TryIntrinsic's Interlocked lowering;
  - group store kinds (S2).
  No planner sets Allocs yet, so frame allocation is unmeasured on real plans. Not run: the homelab suite, bench.mjs
  and frames.mjs speed, stress, and the self-host check (the old front end, the runtime and the back end are
  unchanged).

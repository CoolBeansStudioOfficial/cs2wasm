# cs2wasm: audit findings and plan

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

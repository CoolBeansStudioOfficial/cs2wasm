# Finishing plan (2026-11-20)

The working list to 100%. Updated as items land: each line says its state and the evidence. Details and history
stay in docs/AUDIT.md; this file is only what is left and how each part is checked.

## What 100% means

Scope as the owner set it on 2026-11-20:

1. **Any C# in, one WASM module out** (not WasmGC), behaving as .NET does, stack traces included.
2. **No GC of any kind**: no collector, no run-time walk of memory; what .NET frees, cs2wasm frees.
3. **Plugins good enough for the Invisible API.**
4. **Every module cs2wasm emits beats its NativeAOT-LLVM WebAssembly counterpart**: faster, smaller, less memory, no
   frame-time spikes, in the same engine. The self-hosted compiler is one such module: cs2wasm's build of the
   compiler against NativeAOT-LLVM's build of it, both compiling the same program.
5. **Self-hosted**: the compiler compiles itself, and that build compiles programs (the game in the browser too).

Not required: the time or memory it takes to *build* the self-hosted compiler (18 to 20 minutes against 80 s is
known and accepted). The compiler only needs to be reasonably fast to compile with (rapid prototyping).

## The checks

Each requirement is done when its check passes. A check that does not exist yet is part of the work.

| # | Check | Now | Goal |
|---|---|---|---|
| 1 | Suite, checked and as shipped (`node tools/suite.mjs`) | 180/180 | all pass, no `known-gap` |
| 1 | Behaviour differences on record (AUDIT open items) | `StackFrame.GetMethod()` null; `FirstChanceException` maybe not raised | none |
| 2 | Leaks: test 139 under load, 127, 131 | 139: 2 nodes in ~1 run of 45 | none in 200 loaded runs |
| 3 | Closed-form solver (A4.6), its four examples against running the ticks | missing | pass |
| 3 | Invisible API slice (9.4) in the Bazoodle repository | not started | builds and gates one interaction |
| 4 | `tools/bench.mjs`, optimizing tier: time, memory | 0.96x, 16 MB vs 71 MB | < 1.0x, less memory |
| 4 | Frame-time spikes: worst frame of a long run against NativeAOT's (`node tools/frames.mjs`) | 4 of 5 scenes below; tree 31.9 ms vs 4.3 ms (AUDIT A3.5) | worst frame and p99.9 not above NativeAOT's on every scene |
| 4 | Hello world size (test 145) | 27,501 B | < 28,383 B |
| 4 | Self-hosted compiler on `Console.WriteLine(42)`, no threads: time | 12.45 s vs 4.6 s | < 4.6 s |
| 4 | Same: peak memory | 470 MB vs 239 MB | < 239 MB |
| 4 | Same: module size | 64 MB vs 129 MB | met; cut what is waste (breakdown first) |
| 5 | Threaded self-hosted compiler: more threads not slower | 23 s on 1, 64 s on 12 | faster with threads |
| 5 | Game built in the browser by the self-hosted compiler (proof 5 item 1) | not re-tried since 11-17 | plays, log equals the oracle's |
| - | 072_http's rare out-of-bounds | open | found or not seen in 500 runs |

## Where the self-hosted compiler's gap is (measured 2026-11-20)

Library ready 9.05 s against 3.89 s, compile 2.30 s against 0.46 s; memory 470 MB against 239 MB. Profile flat:
arena (cycle) machinery about a quarter, counting about a tenth, host file calls 6%, the rest slower code
everywhere. Drop stats for one run: 78 million top checks, 37 million of them marked; 30 million checks from
releases. So the two levers, in order:

1. **Fewer cycle-capable stores (7.E, the back-reference proof).** Roslyn's green and red nodes, syntax lists,
   symbols and pooled builders all refer to their own kinds, so every store into them links. A proof that a field
   only ever points up (a parent, set once at construction or from the object that owns it) takes that edge out of
   the cycle graph; most of Roslyn's types then stop being cycle-capable and their stores become plain stores.
   Measure first: which sites are hot (count stores by site in the named no-threads build), then prove those.
2. **Code quality in the baseline tier** (where the compiler runs): calls and instructions per operation. IR
   passes where the WASM passes cannot see (steps 5 and 6), driven by the hot functions of the profile.

Memory: the arena's member words (40 bytes per member) and the counts' words are per object; fewer cycle-capable
objects shrinks both. Then the allocator's waste by size class (measure).

## Work streams

Each stream is independent of the others' files where marked, so it can run in a session of its own.

| Stream | Items | Files | State |
|---|---|---|---|
| A. Self-hosted speed and memory | 7.E proof on the hot sites; baseline-tier code quality | Compiler/Arenas.cs, Driver, Wasm/* | here, in progress |
| B. Self-hosted size | breakdown of the 64 MB by namespace and kind; cut waste | tools (new), then what it finds | to start |
| C. Threads scale | per-group locks for arena operations, runtime scratch per thread | Boundary/Runtime.cs (arena locking), RuntimeGen | to start (touches Runtime.cs: after A's runtime edits or coordinated) |
| D. Proof 5 in the browser | game built by today's self-hosted compiler in the browser | host/web, tools/proof5.mjs, tests/Godot | to start |
| E. Behaviour gaps | `StackFrame.GetMethod()`, `FirstChanceException` | CoreLib Boundary, stack trace code | to start |
| F. Plugins | A4.6 closed-form solver with its tests | Compiler/Plugins (new solver file), tests | to start |
| G. Spikes | frame-time check against NativeAOT | tools/frames.mjs, tools/frames, tools/frames-naot-wasm | check landed; tree scene waits on 7.E (A) |
| H. Flakes | test 139 leak under load, 072_http | Runtime.cs (read first), tests | to start |

9.4 (the Invisible API slice) lives in the Bazoodle repository: started once F lands, from that repository.

## Log

- 2026-11-20: plan written. Self-hosted no threads 12.45 s / 470 MB / 64 MB against 4.6 s / 239 MB / 129 MB.
- 2026-11-20 (G): spikes check `tools/frames.mjs`, five frame loops against NativeAOT-LLVM. Worst frame cs2wasm vs
  NativeAOT: churn 8.6 / 23.1 ms, collections 0.31 / 2.56, strings 0.15 / 0.36, bigdrop (150k objects dropped in one
  frame) 2.8 / 17.5, tree 31.9 / 4.3; peak memory 16 MB against 59 to 71 MB (tree 32). tree's spike is its rebuild:
  a scene tree with children lists is cycle-capable by type, so building it is arena links (15 ms against ~1 ms);
  the fix is 7.E in the compiler (stream A). churn's median is 3.4x NativeAOT's (speed, open).

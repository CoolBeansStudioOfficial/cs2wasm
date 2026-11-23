# Hosts

A cs2wasm module imports its memory (`env.memory`, shared when it has threads) and the `cs2wasm.*` functions
(`engine.mjs` lists them). These are the hosts in this repository:

| Host | Files | What it gives a module |
|---|---|---|
| Node | `run.mjs` (entry), `engine.mjs`, `fs.mjs`, `http.mjs` + `io.mjs`, `spawner.mjs` + `worker.mjs` | console, time, files (a directory of the machine as `/`), HTTP, threads |
| Browser | `web/run-module.mjs` (page), `web/module-worker.mjs`, `web/cs2wasm-web.mjs`, `web/vfs.mjs`, `web/server.mjs` | console, time, files in memory (packs), threads from a fixed pool; no HTTP yet |
| Godot | `godot-bridge.mjs` with `godot-replay.mjs` (no engine) or `web/godot-web.mjs` (stock web export) | the `godot.*` imports (GodotSharp's `NativeFuncs`) |

## Threads: never start a thread from a busy worker

A thread of a module is a worker running a new instance of the module on the shared memory. A worker (Node's or a
browser's) is only started by an event loop that gets control back, and a thread of the module never gives its loop
back: it runs the module until it ends, and blocks in `memory.atomic.wait` when the module waits. So a worker that a
module's thread starts itself may never start, and the thread that waits for it (a lock, a join, PLINQ's
producers) waits forever. The self-hosted compiler on 12 threads hung that way.

Every embedder must therefore start the module's threads from a worker that runs none of the module:

- Node: `spawner.mjs`, a worker of the main thread whose loop only takes requests from a ring in shared memory and
  starts workers (`worker.mjs`), keeping one ready. Any thread asks it (`thread_spawn` in `engine.mjs`).
- Browser: `web/run-module.mjs` makes a fixed pool of workers (16 by default, `pool`) before the module runs; a
  `thread_spawn` takes a free one through the control block (`web/module-worker.mjs`). When all are busy the spawn
  fails with a message naming the pool's size: give the page a bigger `pool`, or tell the module fewer cores
  (`processors`: .NET's thread pool, PLINQ and Roslyn's concurrent build follow `Environment.ProcessorCount`).

The page's own thread must not block either: a module run on it, such as a Godot game in the web export without
threads, is told so (`mainCanBlock: false`, the import `main_can_block`), and `web/cs2wasm-web.mjs` without a pool
starts no threads for it (`thread_spawn` fails with `NotSupportedException`).

## A module's state kept between processes

`CS2WASM_SNAPSHOT=<file> node host/run.mjs module.wasm main -- ...` (`snapshot.mjs`): when the entry has returned 0
the module's memory and mutable globals are written to the file, and a later process puts them back in place of
`init` and calls the entry on them: the second call of the entry in one process, in a new one (SPEC sections 7.2 and
7.6). For the self-hosted compiler its libraries stay parsed and bound from one build to the next, as under `--watch`.
A snapshot is used only while what it was made from is unchanged: the module's bytes, the environment the module is
given, and every path the module asked the file system about while the state was made (size and time of change),
except the paths its command line names and those it wrote, which the entry reads anew on every call. None is taken
of a module that started a thread, has a file open, moved its working directory or used HTTP.
What is written is the memory as the entry left it, so after a large program the snapshot is large;
`CS2WASM_SNAPSHOT_KEEP=1` never writes one that holds again (one made with a small program stays small).
`CS2WASM_SNAPSHOT_WHY=1` says what was done; `tools/snapshot-check.sh` proves each rule; `tools/selfsnap.mjs` compares
the compiler's modules on a snapshot with the .NET-hosted compiler's and times them. `CS2WASM_REPEAT=n` calls the
entry n times in one instance (what an embedder that keeps the module pays for a later call).

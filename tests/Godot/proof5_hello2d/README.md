# Proof 5: hello2d in C#, built in the browser, played in the engine

SPEC section 10, proof 5. **Not met yet.** What is here, what was run, and what stops it (measured 2026-11-17, Windows
11, 24 cores, Node 24.19, Chrome 157 headless):

| Step | Command | Result |
|---|---|---|
| The game under stock Godot 4.7.2 .NET (the oracle) | `node tools/proof5.mjs oracle` | `oracle-calls.txt`, 1,667 lines; run again, the same bytes |
| Compiled by the .NET-hosted cs2wasmc, on a replayed engine | `node tools/proof5.mjs stub` (also in `dotnet run --project tests/Cs2Wasm.Tests`: `godot_proof5_hello2d`) | 3,740,445 bytes in 32 s; its 1,667 log lines equal the oracle's |
| The same module played in the engine in a browser | `node tools/proof5.mjs check --module tests/out/proof5/game-net.wasm` | 1,667 log lines equal the oracle's (**the engine was not a stock template**: below) |
| ... and by the keyboard | `... check --module tests/out/proof5/game-net.wasm --play shot.png` | right arrow for 1 s then Space: the HUD goes from `jumps: 0 position: (576, 548)` to `jumps: 1 position: (896, 548)` |
| A console program built in the browser by the self-hosted compiler | `node tools/proof5.mjs check --game hello` | 27,805 bytes in 23 to 30 s (five runs), 17,139,958 bytes downloaded; the same bytes as the self-hosted compiler under Node writes; run in the page it writes `42` |
| **The game built in the browser** | `node tools/proof5.mjs check` | **fails** 3 s in: the self-hosted compiler traps (`repro/SelfHostRefSafety.cs`) |
| The script throws (key P) | `node tools/proof5.mjs stub --panic` | **fails** at line 1575: `GD.PushError` throws in the module (`repro/pusherror`) |

`node tools/proof5.mjs web` makes what the page serves (`tests/out/proof5/site`) and must run before `check`.

## The game

`Character.cs` and `Hud.cs` are Bazoodle's `samples/hello2d` (`src/character.rs`, `src/hud.rs`) against Godot's own C#
API: a `CharacterBody2D` that runs and jumps by the project's input actions, and a `Label` that reads the other
script's instance through its node. The scene, the input map and the icon are the sample's. Left out: what is
Bazoodle's platform and not Godot's (`Players`, the tick clock, `script_mut`'s re-entry error), and the benchmark
scripts.

`driver.gd` (an autoload, GDScript, the same file on the desktop and in the web export) is the fixed input: it presses
the actions through Godot's `Input` at fixed physics ticks and quits at tick 170. Both runs use `--fixed-fps 60`, so
every frame is one physics tick then one `_process`, whatever the machine. With `--proof5-panic` it also presses the
key that makes `Character` throw (`oracle-calls-panic.txt`).

`Oracle/CallLog.cs` is proof 3's logger (it wraps stock GodotSharp's ptrcall, method-bind lookup and print), with
`NodePath` and with line ends inside a text written `\n`. A line of the log is one engine call with its arguments and
result, so the result of `move_and_slide`, the positions the physics gives and the HUD's text are all in it.

One line is compared loosely (`comparable()` in `host/godot-bridge.mjs`): `ProjectSettings.localize_path(...)`, whose
argument is the path stock GodotSharp's `ExceptionUtils.cs` had on Godot's build machine. It occurs only in the panic
log.

## The engine bridge (`host/godot-bridge.mjs`, `host/web/godot-web.mjs`, `host/web/godot/*.gd`)

The module's `godot.*` imports are stock GodotSharp's `NativeFuncs`. `host/godot-bridge.mjs` implements them for an
engine that is not in the module's address space: strings, string names and node paths live on the module's side;
objects (numbers the engine's side hands out), method calls, singletons and printing go to a backend. Two backends:

- `host/godot-replay.mjs`: no engine. Each call must be the oracle's next line and is answered with the oracle's
  result. It checks what the compiled scripts ask of the engine given the engine's answers; it checks no engine.
- `host/web/godot-web.mjs`: the real engine in the page. A ptrcall becomes a call of a GDScript callback
  (`JavaScriptBridge.create_callback`) in the autoload `cs2wasm_bridge.gd`, which does `Object.callv` and hands the
  result back through `cs2wasmEngine.ret(...)` (a callback's own result does not reach JavaScript). The other way,
  `cs2wasm_node.gd` stands on each node where a C# script stood (the C# script's path and the scene's values of its
  properties are in the node's metadata) and passes `_ready`, `_process` and `_physics_process` to the module.
  All of it is synchronous on the page's thread (the engine's build without threads).

What the bridge carries: `bool`, `int`, `float`, `String`, `StringName`, `NodePath`, objects, enums, `Vector2`,
`Vector2i`, `Color`, `Rect2`. Any other type, and every `godotsharp_*` function the game does not reach (variants,
arrays, dictionaries, callables, signals, the vararg `method_bind_call`), stops with an error naming it. Unmeasured: the
cost of a call through the bridge (SPEC 7.6 asks 3 to 5 ns; this one crosses JavaScript and GDScript and is certainly
far from it).

## The browser host (`host/web`)

`server.mjs` (static files, the cross-origin isolation headers, `x.br` for `x`, a count of bytes sent);
`cs2wasm-web.mjs` (the `cs2wasm.*` imports: console, time, command line, environment, stack traces);
`vfs.mjs` (files in memory: read-only packs in one `SharedArrayBuffer` for every thread, files written kept by the
thread that wrote them); `run-module.mjs` and `module-worker.mjs` (a module with a `main` run in a Web Worker, with a
pool of workers made beforehand for its threads: a blocked worker cannot start another). Not there: HTTP
(`cs2wasm.http_*` throws), a cache of the downloads between visits, writes from worker threads.

Measured for `repro/Hello.cs` (`check --game hello`): the browser downloads **17,139,958 bytes** to build
(`cs2wasm.wasm` 8,243,308 and `library.pack` 8,896,298, both Brotli quality 9; 59,964,726 and 85,215,870 bytes
unpacked; plus the program's sources). `library.pack` is the 4,896 files the self-hosted compiler opened when it built
a program that references GodotSharp: 72.2 MB of sources from the cache (the .NET library's, 3,633 files, 46.6 MB;
GodotSharp's, 1,086 files, 25.6 MB), 6.5 MB of reference assemblies, GodotSharp.dll (5.9 MB). The build itself took
22.8 to 29.4 s of the 23.2 to 29.9 s (five runs, one of them told four cores; the downloads are from this machine,
0.2 to 0.3 s; the browser reports 0.0 s to compile the compiler's module, which it compiles lazily). The compiler's
memory at the end: 395 MB (444 MB told four cores). The game's own sources are 28,897 bytes in 15 files.

**The game's build time in the browser is unmeasured: the build does not complete.** The nearest measurements, under
Node, of the self-hosted compiler on a program that uses GodotSharp (`repro/pusherror`, one script, no generated
code; and `repro/Hello.cs` with `-r GodotSharp.dll`): 355 s and 380 s on one core, 2.6 GiB of memory at the end
(41,536 pages). The .NET-hosted compiler builds the game in 32 s.

## What stops proof 5

1. **The self-hosted compiler traps on the code Godot's generators write for every script.** A call that passes a ref
   struct and has an `out` argument (`base.InvokeGodotClassMethod(method, args, out ret)`) ends the build with
   `RuntimeError: operation does not support unaligned accesses` in a worker thread, in
   `RefSafetyAnalysis.EscapeValue.Deconstruct` (called from `<CheckInvocationArgMixingWithUpdatedRules>g__inferDeclarationExpressionValEscape`,
   `CheckInvocationArgMixing`, `VisitArguments`, `VisitCall`). Under Node and in the browser alike.
   `repro/SelfHostRefSafety.cs` is 30 lines without Godot. No change of the game's code avoids it.
2. **The self-hosted compiler cannot run Godot's source generators** (`-a Godot.SourceGenerators.dll` needs an
   assembly loader; SPEC 7.1 has such code compiled into the toolchain module). Here the generators' output for the
   two scripts is written by the .NET-hosted compiler (`CS2WASM_GENERATED`) and served as sources, so an edit in the
   browser that changes a script's members would not be followed.
3. **The self-hosted compiler at `tests/out/self2` has 1 GiB of memory at most**, and a program with GodotSharp ends
   with `OutOfMemoryException` there. `check/raise-memory.mjs` writes a copy with the limit in its memory import
   raised to 4 GiB (the site serves that copy); `cs2wasmc --max-pages` at the toolchain's build is the proper way.
4. **With more than one core the self-hosted compiler did not end** on a program with GodotSharp under Node: told 4
   cores (limit raised), 163 s of processor time in 9.5 minutes and no output after "program diagnostics"; told 24,
   831 s of processor time in 6 minutes, no end. Told one core it ends. The page tells it one.
5. **No stock web export template on this machine, and the stock editor with .NET refuses a web export.** The
   `web_*.zip` files in `%APPDATA%/Godot/export_templates/4.7.2.stable.mono` are copies of the macOS, Android and iOS
   archives (same sizes; `web_nothreads_release.zip` starts with `macos_template.app/`). The stock editor says
   "Exporting to Web is currently not supported in Godot 4 when using C#/.NET" for any project. So `web` has the stock
   editor write the pack (`--export-pack`), and takes the engine's files from the archive
   `CS2WASM_GODOT_WEB_TEMPLATE` names. The runs above used
   `bazoodle/godot/bin/godot.web.template_release.wasm32.nothreads.zip`: a local build that reports
   `v4.7.2.stable.custom_build.ed1daf0bf` (the stock release's commit), from a tree whose local changes, if any, were
   not examined. **That is not the stock template the proof asks for.** With a stock `web_nothreads_release.zip` of
   4.7.2 in place nothing else changes.
6. **`GD.PushError` throws `NullReferenceException` in the module** (stock GodotSharp reads
   `new StackTrace(skip, true).GetFrame(0)`), so an exception that escapes a script callback is not reported and its
   `ProjectSettings.localize_path` call is missing from the log (`repro/pusherror`). A console program with
   `new StackTrace(0, true)` does not build at all: `error CW0101: type char[] is used at run time but has no number
   (in Cs2Wasm.Runtime.Rt.CaptureTrace)` (`repro/StackTraceFrames.cs`).

Also found: an export leaves out of a scene a property that neither the node's class nor the script standing there
declares (the scene's `Speed = 320.0` was gone from the pack while `metadata/...` stayed), hence the metadata.

## Files

- `Character.cs`, `Hud.cs`, `main.tscn`, `project.godot`, `driver.gd`, `Proof5.csproj`, `Oracle/`: the Godot project.
- `oracle-calls.txt`, `oracle-calls-panic.txt`: stock Godot's logs.
- `check/`: the steps of `tools/proof5.mjs` (`stub.mjs`, `web.mjs`, `browser.mjs`), the self-hosted compiler under
  Node from any shell (`self.mjs`, with `--trace-files`), `raise-memory.mjs`.
- `web/`: the page.
- `repro/`: the gaps' programs. `repro/Hello.cs` is the smallest program, for timing.

Environment: `CS2WASM_GODOT_EDITOR`, `CS2WASM_GODOT_WEB_TEMPLATE`, `CS2WASM_CACHE`, `CS2WASM_SELF`,
`CS2WASM_REFERENCE_DIR`, `CS2WASM_BROWSER`.

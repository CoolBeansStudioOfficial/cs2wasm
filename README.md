# cs2wasm

A compiler from C# (a Roslyn `Compilation`, C# 15 / .NET 11) to one core WebAssembly module, with no garbage
collector and no .NET runtime. The design is in [docs/SPEC.md](docs/SPEC.md); what is built is what the tests check
(`dotnet run --project tests/Cs2Wasm.Tests`, `node tools/bench.mjs`).

## Layout

| Path | What |
|---|---|
| `src/Cs2Wasm` | The compiler library. `Cs2WasmCompiler.Compile(compilation, options)` returns module bytes, diagnostics and a source map. |
| `src/Cs2Wasm/Compiler` | Front end (library mapping, reachability), code generation from the operation tree, reference counting, runtime generation. |
| `src/Cs2Wasm/Wasm` | WASM instructions, encoder, validator, and the passes over the emitted code (inlining, locals, checks, loops). |
| `src/Cs2Wasm/Upstream` | The .NET library every module is built from: per library, a `manifest.txt` listing the files of `dotnet/runtime` (tag `v11.0.0`) it is made of, and the code that replaces what assumes a collector, native code or another runtime (`Boundary`, `Runtime`, `Generated`). The upstream files themselves are fetched on first use. |
| `src/Cs2Wasm.Wasm` | The entry point of the self-hosted compiler (`tools/selfhost.mjs`). |
| `src/Cs2Wasm.Cli` | `cs2wasmc`, a command-line wrapper (for tests). |
| `host` | A reference engine in JavaScript: `engine.mjs` (the imports the library calls) and `run.mjs` (runs a module in Node). |
| `tests/Conformance` | C# programs whose output must equal .NET's. |
| `tests/Cs2Wasm.Tests` | The runner: each program runs under .NET (the oracle) and through cs2wasm + Node; plus API checks. Programs are built checked (freed memory poisoned, every function type-checked); `--unchecked` builds them as shipped. |
| `tests/Godot` | The Godot proofs: engine calls compared with stock Godot's. |
| `tools` | Measurements against NativeAOT compiled to WebAssembly (`bench.mjs`, `micro.mjs`), the self-hosting build, profile readers. Each file's header says how to run it. |
| `docs/AUDIT.md` | The audit of 2026-11-18: what is wrong, where it comes from, the plan and its progress. |

## Use

```bash
dotnet build
```

```bash
dotnet run --project tests/Cs2Wasm.Tests
```

```bash
src/Cs2Wasm.Cli/bin/Debug/net11.0/cs2wasmc game.cs -o game.wasm --map game.wasm.map
```

```bash
node host/run.mjs game.wasm main
```

While developing, `--watch` keeps the compiler running and rebuilds whenever a `.cs` file of the inputs changes (a
directory input picks up new files too). The library stays loaded, so only the first build pays for parsing it:

```bash
src/Cs2Wasm.Cli/bin/Debug/net11.0/cs2wasmc src -o game.wasm --watch
```

An export is a static method marked `[UnmanagedCallersOnly(EntryPoint = "name")]`. The engine creates the shared
memory (`env.memory`), supplies the `cs2wasm.*` imports (see `host/engine.mjs`) and the boundary-table imports, calls
`init` once, then calls exports. Engine methods are `extern` C# methods listed in a boundary table:

```json
{ "imports": [ { "method": "M:Game.Engine.Log(System.Int32)", "module": "godot", "name": "log_i32" } ] }
```

## Requirements

.NET SDK 11 (Roslyn 5.12), Node 24 (for the tests: WASM exception handling with `exnref`, shared memory).

The first build of a program fetches the library's sources with `git` (a sparse clone of `dotnet/runtime` at `v11.0.0`, into `%LOCALAPPDATA%/cs2wasm` or `CS2WASM_CACHE`; `CS2WASM_RUNTIME_SOURCES` names a checkout to use instead). The Godot proofs need a stock Godot 4.7.2 .NET editor (`CS2WASM_GODOT_EDITOR`) and are skipped without it. The measurements against NativeAOT need the Emscripten SDK 3.1.56 (`EMSDK`).

Licence: MIT (`LICENSE`); the projects whose sources are built in are listed in `THIRD-PARTY-NOTICES.md`.

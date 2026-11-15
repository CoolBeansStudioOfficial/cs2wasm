# cs2wasm

A compiler from C# (a Roslyn `Compilation`, C# 15 / .NET 11) to one core WebAssembly module, with no garbage
collector and no .NET runtime. The design is in [docs/SPEC.md](docs/SPEC.md); what is built so far, and what is not,
is in [docs/STATUS.md](docs/STATUS.md).

## Layout

| Path | What |
|---|---|
| `src/Cs2Wasm` | The compiler library. `Cs2WasmCompiler.Compile(compilation, options)` returns module bytes, diagnostics and a source map. |
| `src/Cs2Wasm/Compiler` | Front end (library mapping, reachability), code generation from the operation tree, reference counting, runtime generation. |
| `src/Cs2Wasm/Wasm` | WASM IR, encoder, final cleanup pass. |
| `src/Cs2Wasm/Lib` | cs2wasm's .NET library: C# compiled into every module (its own corlib: `System.Object`, `String`, numbers, collections, LINQ, math...). |
| `src/Cs2Wasm.Cli` | `cs2wasmc`, a command-line wrapper (for tests). |
| `host` | A reference engine in JavaScript: `engine.mjs` (the imports the library calls) and `run.mjs` (runs a module in Node). |
| `tests/Conformance` | C# programs whose output must equal .NET's. |
| `tests/Cs2Wasm.Tests` | The runner: each program runs under .NET (the oracle) and through cs2wasm + Node; plus API checks. |

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

Licence: MIT.

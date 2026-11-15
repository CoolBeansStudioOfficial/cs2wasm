# Proof 3: a Node2D script, stock Godot vs the compiled module

`Mover.cs` is the script (SPEC section 10, proof 3). The oracle is stock Godot 4.7.2 with .NET running this project;
`Oracle/CallLog.cs` (only in the oracle build) wraps entries of stock GodotSharp's engine-callback table and logs
each engine call, formatted by its `extension_api.json` types. `expected-calls.txt` is that log.

To regenerate it (the API description from the stock editor: `--headless --dump-extension-api`):

```bash
dotnet build -c Debug
CS2WASM_CALL_LOG=expected-calls.txt CS2WASM_GODOT_API=extension_api.json Godot_v4.7.2-stable_mono_win64_console.exe --headless --path .
```

The compiled side (`tests/Cs2Wasm.Tests`, check `godot_proof3_node2d`): `Mover.cs` against stock GodotSharp's
package, with Godot's source generators (`GodotProjectDir` this folder; `GodotPluginsInitializer` off: cs2wasm's
GodotPlugins starts the module), run by `host/godot-run.mjs` on the stub engine in `host/godot.mjs`, which plays the
engine's part (script registration, the instance on a native Node2D, `_ready`, `_process` until the tree quits,
shutdown) and writes the same log from the scene on. By hand:

```bash
cs2wasmc Mover.cs -r GodotSharp.dll -a Godot.SourceGenerators.dll --property GodotProjectDir=<this folder>/ --property GodotDisabledSourceGenerators=GodotPluginsInitializer -o p.wasm
node host/godot-run.mjs p.wasm extension_api.json res://Mover.cs Node2D calls.txt
```

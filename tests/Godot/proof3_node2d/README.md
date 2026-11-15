# Proof 3: a Node2D script, stock Godot vs the compiled module

`Mover.cs` is the script (SPEC section 10, proof 3). The oracle is stock Godot 4.7.2 with .NET running this project;
`Oracle/CallLog.cs` (only in the oracle build) wraps entries of stock GodotSharp's engine-callback table and logs
each engine call, formatted by its `extension_api.json` types. `expected-calls.txt` is that log.

To regenerate it (the API description from the stock editor: `--headless --dump-extension-api`):

```bash
dotnet build -c Debug
CS2WASM_CALL_LOG=expected-calls.txt CS2WASM_GODOT_API=extension_api.json Godot_v4.7.2-stable_mono_win64_console.exe --headless --path .
```

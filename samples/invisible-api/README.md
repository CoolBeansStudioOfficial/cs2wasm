# The Invisible API's first slice

A small multiplayer-style game in plain C# and an il2wasm plugin that turns it into a server's view of it without
changing the game (docs/FINISH.md 9.4, requirement 3).

- `Game/Arena.cs`: players keyed by id (position, velocity from input, health, a fire cooldown, all private), a coin
  inventory keyed by player, a grid of claimed cells, a wave timer. Exports `join`, `input`, `tick`, `dump`.
- `Plugin/InvisiblePlugin.cs` (interface version 7), everything from `RunBeforeCompile`, so the program is compiled
  once:
  - classifies each write (`Accesses` with entries; `PathConditions` with a taint of the input exports' parameters
    along each path: under input, input-gated, tick only, set up; `UnsureSites` and writes no hook can follow as
    unknown);
  - sees each input (`InterceptValue` on the input exports' parameter reads) and can replace the next action;
  - marks the interactions changed: `InstrumentWrite` at each write under input, `InstrumentEntryWrite` at each entry
    write under input with the player's id as the key (the tick's deterministic entry writes of the same dictionary
    are not marked);
  - generates one gate with `AddSource`: whether a player's fire is accepted now, built from the conditions
    `TryFire` itself checks, over its private fields; exported as `gate_fire`, with `gate_view`;
  - solves the wave timer (`Solve`, `GenerateAdvance`, exact): `server_predict(n)` on a copy, `server_advance(n)` on
    the live timer.
- `driver.mjs`: plays `script.txt` against the module as a server would.
- `expected-writes.txt`: the classification the check expects.

The check (`tests/Il2Wasm.Tests/InvisibleCheck.cs`, "invisible-api slice" in the suite, both modes) plays the script
with the driver and under .NET (the game without the plugin; the plugin's answers computed from the private state by
reflection and by running the ticks) and compares: the game's own output, each gate answer against whether the fire
happened, each view, each prediction and catch-up against running the ticks, every value changed by an input marked
(but for the unknown members) and no tick marking anything, the inputs seen. Run it alone:

```bash
dotnet run --project tests/Il2Wasm.Tests -- invisible
```

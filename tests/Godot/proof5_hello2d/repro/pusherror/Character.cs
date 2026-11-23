// Repro (proof 5): GD.PushError under il2wasm. Stock Godot .NET prints the error (godotsharp_err_print_error after
// ProjectSettings.localize_path of the caller's file); see ../README.md for what the compiled module does.
// Build as the game is built, with this file in place of Character.cs and Hud.cs, and run on the replay backend:
//   il2wasm PushError.cs -r GodotSharp.dll -a Godot.SourceGenerators.dll --property GodotProjectDir=<dir>/
//     --property GodotDisabledSourceGenerators=GodotPluginsInitializer -o r.wasm
//   node tools/proof5.mjs stub --module r.wasm
using System;
using Godot;

public partial class Character : CharacterBody2D
{
    public override void _Ready()
    {
        try { GD.PushError("x"); }
        catch (Exception e) { GD.Print("caught: " + e); }
    }
}

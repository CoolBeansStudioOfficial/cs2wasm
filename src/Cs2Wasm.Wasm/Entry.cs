// The self-hosted compiler (SPEC section 9, proof 4): cs2wasm compiled by itself to a module whose `main` export runs
// the command line (src/Cs2Wasm.Cli/Cli.cs) on the arguments its host gives. Built by tools/selfhost.mjs.
using System;
using System.Runtime.InteropServices;

static class Entry
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main() => Cs2Wasm.Cli.Run(Environment.GetCommandLineArgs()[1..]);
}

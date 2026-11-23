// The self-hosted compiler (SPEC section 9, proof 4): il2wasm compiled by itself to a module whose `main` export runs
// the command line (src/Il2Wasm.Cli/Cli.cs) on the arguments its host gives. Built by tools/selfhost.mjs.
using System;
using System.Runtime.InteropServices;

static partial class Entry
{
    /// <summary>The plugin passes compiled into this toolchain (SPEC section 7.1): tools/selfhost.mjs writes the other
    /// part of this class when it is given plugin sources; without it there are none.</summary>
    static partial void AddPlugins();

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        AddPlugins();
        return Il2Wasm.Cli.Run(Environment.GetCommandLineArgs()[1..]);
    }
}

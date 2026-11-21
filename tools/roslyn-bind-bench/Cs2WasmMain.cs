// cs2wasm's entry for tools/roslyn-bind-bench (host/run.mjs: the command line is the module's name, then the arguments)
using System;
using System.Linq;
using System.Runtime.InteropServices;

static class Cs2WasmMain
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => BindBench.Run(Environment.GetCommandLineArgs().Skip(1).ToArray());
}

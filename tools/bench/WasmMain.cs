using System.Runtime.InteropServices;

static class WasmMain
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Bench.RunAll();
}

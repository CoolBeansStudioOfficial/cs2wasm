// tools/snapshot-check.sh's module: state that lasts from one call of its entry to the next (a count of the calls, the
// files it has read and kept, a value made from the environment when the module started), so that what the host's
// snapshot keeps and when it lets go of it shows in the output.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

static class P
{
    static int s_runs;
    static readonly SortedDictionary<string, string> s_files = new(StringComparer.Ordinal);
    static readonly string s_mode = Environment.GetEnvironmentVariable("SNAP_MODE") ?? "none";

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        var args = Environment.GetCommandLineArgs()[1..];
        s_runs++;
        // a file the command line does not name: read once and kept, as the compiler keeps its libraries
        if (Environment.GetEnvironmentVariable("SNAP_FILE") is { } lib && !s_files.ContainsKey(lib))
        {
            s_files[lib] = File.ReadAllText(lib).Trim();
            Console.WriteLine("read " + lib);
        }
        foreach (var kv in s_files) Console.WriteLine("kept " + kv.Key + ": " + kv.Value);
        // the files the command line names: read on every call
        foreach (var a in args) Console.WriteLine("given " + a + ": " + File.ReadAllText(a).Trim());
        Console.WriteLine($"call {s_runs}, mode {s_mode}");
        return args.Length > 0 && args[0].EndsWith("fail.txt") ? 1 : 0;
    }
}

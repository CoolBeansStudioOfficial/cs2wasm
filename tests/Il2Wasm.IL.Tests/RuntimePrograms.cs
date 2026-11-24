using Il2Wasm.IL.Lower;
using Il2Wasm.IL.Types;

namespace Il2Wasm.IL.Tests;

/// <summary>Programs (RuntimePrograms/*.cs) compiled against the framework (the IL CoreLib and the stock libraries,
/// artifacts/framework: tools/build-corelib.mjs) and run under host/run.mjs; their output must equal the same
/// program's under .NET. These are the runtime's semantics that need the real CoreLib: finalizers, weak references,
/// exceptions' traces and filters, static constructors, Activator. A program's first line may say
/// "// checked" (compiled as the checked build too, and both must match).</summary>
static class RuntimePrograms
{
    /// <summary>The group's filter: all ("" or "rt"), or the programs whose name starts with it ("r01").</summary>
    public static bool Wants(string filter, string? name = null) =>
        filter is "" or "rt" || filter.Length > 1 && filter[0] == 'r' && char.IsDigit(filter[1]) && (name == null || name.StartsWith(filter));

    public static string? Dir()
    {
        var env = Environment.GetEnvironmentVariable("IL2WASM_FRAMEWORK");
        if (env != null && File.Exists(Path.Combine(env, "System.Private.CoreLib.dll"))) return env;
        var d = Path.Combine(EndToEnd.RepoRoot, "artifacts", "framework");
        return File.Exists(Path.Combine(d, "System.Private.CoreLib.dll")) ? d : null;
    }

    /// <summary>A program against the framework, to a module.</summary>
    public static string Assembly(string dir, string name, string source)
    {
        var refs = Directory.GetFiles(dir, "*.dll");
        return Build.Compile(name, [source], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
    }

    /// <summary>A program's assembly (Assembly) to a module.</summary>
    public static byte[] Compile(string dir, string name, string dll, bool isChecked, List<string>? warnings = null)
    {
        var refs = Directory.GetFiles(dir, "*.dll");
        var ctx = new IlContext();
        ctx.AddAssemblies(refs.Append(dll));
        ctx.SetCoreLib();
        var entry = ctx.Load(name).EntryPoint!;
        var comp = new IlCompiler(ctx) { Checked = isChecked };
        if (File.Exists(Path.Combine(dir, "il2wasm.natives.txt"))) comp.LoadNatives(Path.Combine(dir, "il2wasm.natives.txt"));
        var bytes = comp.Compile(entry);
        warnings?.AddRange(comp.Warnings);
        return EndToEnd.Dump("rt." + name + (isChecked ? ".checked" : ""), bytes);
    }

    const string ExportsProgram = """
        using System;
        using System.Runtime.InteropServices;
        static class Program
        {
            static void Main() { Console.WriteLine("main"); }
            [UnmanagedCallersOnly(EntryPoint = "add")]
            static int Add() { Console.WriteLine("add " + (2 + 3)); return 5; }
            [UnmanagedCallersOnly(EntryPoint = "fail")]
            static int Fail() { Console.WriteLine("failing"); throw new InvalidOperationException("boom from export"); }
        }
        """;

    /// <summary>SPEC 6.2: an [UnmanagedCallersOnly(EntryPoint)] method is an export; an exception that escapes it is
    /// reported to the host (type, message, trace) and the export returns.</summary>
    static void Exports(Checks c, string dir)
    {
        var dll = Assembly(dir, "r00_exports", ExportsProgram);
        var wasm = Compile(dir, "r00_exports", dll, false);
        var file = Path.Combine(Build.Dir, "r00_exports.wasm");
        File.WriteAllBytes(file, wasm);
        string RunExport(string export)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("node", [Path.Combine(EndToEnd.RepoRoot, "host", "run.mjs"), file, export]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var er = p.StandardError.ReadToEndAsync();
            p.WaitForExit(60_000);
            return (o.Result + er.Result).Replace("\r\n", "\n");
        }
        c.Eq("exports: add runs", RunExport("add"), "add 5\n");
        var failed = RunExport("fail");
        c.True("exports: an exception that escapes is reported with its type, message and trace (got: " + failed.Replace("\n", " | ") + ")",
            failed.StartsWith("failing\nUnhandled exception. System.InvalidOperationException: boom from export\n") && failed.Contains("at Program.Fail()"));
    }

    public static void Run(Checks c, string filter)
    {
        if (Dir() is { } d0 && (filter is "" or "rt" || "r00".StartsWith(filter))) Exports(c, d0);
        c.Section("runtime: programs on the IL CoreLib under host/run.mjs, against .NET");
        var dir = Dir();
        if (dir == null) { c.Info("no artifacts/framework (node tools/build-corelib.mjs): skipped"); return; }
        foreach (var file in Directory.GetFiles(Build.Here("RuntimePrograms"), "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!Wants(filter, name)) continue;
            var src = File.ReadAllText(file);
            string expected;
            try { expected = Build.Oracle(name, src); }
            catch (Exception e) { c.True($"{name}: the oracle ran ({e.Message})", false); continue; }
            string dll;
            try { dll = Assembly(dir, name, src); }
            catch (Exception e) { c.True($"{name}: compiles with Roslyn ({e.Message})", false); continue; }
            foreach (bool chk in new[] { false, true })
            {
                string actual;
                var mode = chk ? "checked" : "shipped";
                try
                {
                    var wasm = Compile(dir, name, dll, chk);
                    actual = EndToEnd.Run(wasm, name + "." + mode, live: false);
                    c.Info($"{name} ({mode}): {wasm.Length} bytes");
                }
                catch (Exception e) { actual = "(compile failed) " + e.Message; }
                if (actual != expected)
                {
                    c.True($"{name} ({mode})", false);
                    Console.WriteLine("     expected:\n" + EndToEnd.Indent(expected) + "     actual:\n" + EndToEnd.Indent(actual));
                }
                else c.True($"{name} ({mode})", true);
            }
        }
    }
}

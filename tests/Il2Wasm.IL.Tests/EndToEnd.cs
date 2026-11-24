using System.Diagnostics;
using Il2Wasm.IL.Lower;
using Il2Wasm.IL.Types;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Tests;

/// <summary>Small programs (Programs/*.cs) compiled from IL through the existing back end and run under host/run.mjs;
/// their output must equal the same program's under .NET.</summary>
static class EndToEnd
{
    public static string RepoRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "host", "run.mjs"))) return d.FullName;
            throw new Exception("the repository's host/run.mjs is not found above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Compiles a program (against the stub CoreLib) to a module; its bytes.</summary>
    public static byte[] Compile(string name, string source, List<string>? warnings = null)
    {
        var lib = Build.CoreLib();
        var prog = Build.Program(name, source);
        var ctx = new IlContext();
        ctx.AddAssemblies([lib, prog]);
        ctx.SetCoreLib();
        var m = ctx.Load(name);
        var entry = m.EntryPoint ?? throw new Exception(name + " has no entry point");
        var c = new IlCompiler(ctx);
        c.LiveCounter = ctx.SystemModule.GetType("Il2Wasm.Runtime"u8, "Rt"u8).GetMethods().First(x => x.GetName() == "get_LiveBlocks");
        var bytes = c.Compile(entry);
        warnings?.AddRange(c.Warnings);
        return Dump(name, bytes);
    }

    public static string Run(byte[] wasm, string name, bool live = true)
    {
        var file = Path.Combine(Build.Dir, name + ".wasm");
        File.WriteAllBytes(file, wasm);
        var psi = new ProcessStartInfo("node", [Path.Combine(RepoRoot, "host", "run.mjs"), file]) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (live) psi.Environment["IL2WASM_PRINT_LIVE"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { p.Kill(); return "(timed out)"; }
        var err = stderr.Result.Replace("\r\n", "\n");
        // (an unhandled exception: the host's report, as .NET prints its first line)
        return stdout.Result.Replace("\r\n", "\n") + err;
    }

    public static void Run(Checks c)
    {
        c.Section("end to end: programs from IL under host/run.mjs, against .NET");
        var dir = Build.Here("Programs");
        foreach (var file in Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!c.WantsProgram(name)) continue;
            var src = File.ReadAllText(file);
            string expected, actual;
            try { expected = Build.Oracle(name, src); }
            catch (Exception e) { c.True($"{name}: the oracle ran ({e.Message})", false); continue; }
            // the leak check: what the program's statics (or an exception that escaped) keep, as its "// live: n" says
            int expectLive = src.Split('\n').FirstOrDefault(l => l.StartsWith("// live: ")) is { } ll ? int.Parse(ll[9..].Trim()) : 0;
            int live = -1;
            try
            {
                var wasm = Compile(name, src);
                actual = Run(wasm, name);
                int at = actual.LastIndexOf("live: ", StringComparison.Ordinal);
                if (at >= 0) { live = int.Parse(actual[(at + 6)..].Trim()); actual = actual[..at]; }
                c.Info($"{name}: {wasm.Length} bytes, {live} blocks live at the end");
            }
            catch (Exception e) { actual = "(compile failed) " + e.Message; }
            if (live >= 0) c.Eq($"{name}: blocks live at the end (no leak, nothing freed twice)", live, expectLive);
            if (actual != expected)
            {
                c.True(name, false);
                Console.WriteLine("     expected:\n" + Indent(expected) + "     actual:\n" + Indent(actual));
            }
            else c.True(name, true);
        }
    }

    /// <summary>The framework directory (the IL CoreLib and the stock libraries): IL2WASM_FRAMEWORK, else the repository's
    /// artifacts/framework (node tools/build-corelib.mjs); null when there is none.</summary>
    public static string? FrameworkDir()
    {
        var d = Environment.GetEnvironmentVariable("IL2WASM_FRAMEWORK") ?? Path.Combine(RepoRoot, "artifacts", "framework");
        return File.Exists(Path.Combine(d, "System.Private.CoreLib.dll")) ? d : null;
    }

    /// <summary>A program against the framework (the real CoreLib): its module's bytes.</summary>
    public static byte[] CompileOnFramework(string dir, string name, string source, bool countObjects = false)
    {
        var refs = Directory.GetFiles(dir, "*.dll");
        var dll = Build.Compile(name, [source], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
        var ctx = new IlContext();
        ctx.AddAssemblies(refs.Append(dll));
        ctx.SetCoreLib();
        var entry = ctx.Load(name).EntryPoint!;
        var comp = new IlCompiler(ctx);
        if (countObjects) comp.CountObjects = true;
        if (File.Exists(Path.Combine(dir, "il2wasm.natives.txt"))) comp.LoadNatives(Path.Combine(dir, "il2wasm.natives.txt"));
        return Dump("fw." + name, comp.Compile(entry));
    }

    /// <summary>IL2WASM_IL_TESTS_DUMP=dir: every module the tests compile is also written there by name (to compare two
    /// compilers' output byte for byte).</summary>
    public static byte[] Dump(string name, byte[] wasm)
    {
        if (Environment.GetEnvironmentVariable("IL2WASM_IL_TESTS_DUMP") is { Length: > 0 } d) { Directory.CreateDirectory(d); File.WriteAllBytes(Path.Combine(d, name + ".wasm"), wasm); }
        return wasm;
    }

    /// <summary>The programs of FrameworkPrograms/*.cs (the IL features the NetWasm suite's failures came down to, each
    /// its smallest repro), compiled against the real CoreLib and run under host/run.mjs; .NET is the oracle.</summary>
    public static void RunFramework(Checks c)
    {
        c.Section("end to end on the IL CoreLib: FrameworkPrograms against .NET");
        var fw = FrameworkDir();
        if (fw == null) { c.Info("no framework (node tools/build-corelib.mjs, or IL2WASM_FRAMEWORK): skipped"); return; }
        foreach (var file in Directory.GetFiles(Build.Here("FrameworkPrograms"), "*.cs").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!c.WantsProgram(name)) continue;
            var src = File.ReadAllText(file);
            // ("// plan: only": a program whose output depends on the memory model's plan, IL2WASM_MEM_PLAN=1; the naive
            // plan is the baseline that leaks there. "// live: n": with the plan, the objects live at exit, counted)
            var lines = src.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Contains("// plan: only") && !Il2Wasm.IL.Memory.Pipeline.Planned) { c.Info($"{name}: with IL2WASM_MEM_PLAN=1 only"); continue; }
            int expectLive = lines.FirstOrDefault(l => l.StartsWith("// live: ")) is { } ll ? int.Parse(ll[9..].Trim()) : -1;
            string expected, actual;
            try { expected = Build.Oracle(name, src); }
            catch (Exception e) { c.True($"{name}: the oracle ran ({e.Message})", false); continue; }
            int live = -1;
            try
            {
                var wasm = CompileOnFramework(fw, name, src, countObjects: expectLive >= 0);
                actual = Run(wasm, name);
                int at = actual.LastIndexOf("live: ", StringComparison.Ordinal);
                if (at >= 0) { if (expectLive >= 0) live = int.Parse(actual[(at + 6)..].Trim()); actual = actual[..at]; }
                c.Info($"{name}: {wasm.Length} bytes" + (live >= 0 ? $", {live} objects live at the end" : ""));
            }
            catch (Exception e) { actual = "(compile failed) " + e.Message; }
            if (expectLive >= 0) c.Eq($"{name}: objects live at the end (no leak)", live, expectLive);
            if (actual != expected)
            {
                c.True(name, false);
                Console.WriteLine("     expected:\n" + Indent(expected) + "     actual:\n" + Indent(actual));
            }
            else c.True(name, true);
        }
    }

    internal static string Indent(string s) => string.Join("", s.Split('\n').Take(40).Select(l => "       | " + l + "\n"));
}

using System.Diagnostics;
using System.Text;
using Cs2Wasm;
using Microsoft.CodeAnalysis;

/// <summary>Checks of the compiler's API and module shape that have no .NET oracle (sections 6, 7.3, 8).</summary>
static class ApiChecks
{
    public static List<(string name, string? failure)> Run(string root, string outDir)
    {
        var results = new List<(string, string?)>();
        void Check(string name, Func<string?> f)
        {
            try { results.Add((name, f())); }
            catch (Exception ex) { results.Add((name, ex.ToString())); }
        }

        const string Header = "using System;\nusing System.Runtime.InteropServices;\n";

        Check("boundary table: extern methods become typed imports", () =>
        {
            var src = Header + """
                namespace Game {
                static class Engine {
                    public static extern void Log(int value);
                    public static extern float Speed(int node, float scale);
                }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() { Engine.Log(42); Console.WriteLine(Engine.Speed(7, 1.5f)); }
                }
                }
                """;
            var table = BoundaryTable.FromJson("""
                { "imports": [
                    { "method": "M:Game.Engine.Log(System.Int32)", "module": "godot", "name": "log_i32" },
                    { "method": "M:Game.Engine.Speed(System.Int32,System.Single)", "module": "godot", "name": "node_speed" } ] }
                """);
            var r = Compile(src, new CompileOptions { Boundary = table });
            if (!r.Success) return Errors(r);
            var mod = new Wasm(r.Module!);
            if (!mod.Imports.Contains("godot.log_i32:(i32)->()")) return "missing import godot.log_i32, got: " + string.Join(", ", mod.Imports);
            if (!mod.Imports.Contains("godot.node_speed:(i32,f32)->(f32)")) return "missing import godot.node_speed, got: " + string.Join(", ", mod.Imports);
            var path = Path.Combine(outDir, "api_boundary.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, code) = Node(root, path);
            var want = "[engine] godot.log_i32(42)\n[engine] godot.node_speed(7, 1.5)\n0\n";
            return o == want ? null : $"engine log differs:\n{o}\nwanted:\n{want}\n{e}";
        });

        Check("missing boundary entry is a build error naming the method", () =>
        {
            var src = Header + "static class E { public static extern void Nope(); [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() => Nope(); }";
            var r = Compile(src, new CompileOptions());
            return !r.Success && r.Diagnostics.Any(d => d.Id == "CW0002" && d.GetMessage().Contains("M:E.Nope")) ? null : "expected CW0002, got " + Errors(r);
        });

        Check("files are not in the sandbox: build error naming the capability", () =>
        {
            var src = Header + "static class E { [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() => Console.WriteLine(System.IO.File.ReadAllText(\"x\")); }";
            var r = Compile(src, new CompileOptions());
            return !r.Success && r.Diagnostics.Any(d => d.Id == "CW0003" && d.GetMessage().Contains("file system")) ? null : "expected CW0003, got " + Errors(r);
        });

        Check("platform invoke is a build error", () =>
        {
            var src = Header + "static class E { [DllImport(\"user32\")] static extern int MessageBoxW(int a); [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() => MessageBoxW(0); }";
            var r = Compile(src, new CompileOptions());
            return !r.Success && r.Diagnostics.Any(d => d.Id == "CW0004") ? null : "expected CW0004, got " + Errors(r);
        });

        Check("custom sections from the embedder and the version section are written", () =>
        {
            var src = Header + "static class E { [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() { } }";
            var payload = Encoding.UTF8.GetBytes("{\"scripts\":[\"Player\"]}");
            var r = Compile(src, new CompileOptions { CustomSections = [("platform.scripts", payload)], AbiVersion = 3 });
            if (!r.Success) return Errors(r);
            var mod = new Wasm(r.Module!);
            if (!mod.Custom.TryGetValue("platform.scripts", out var got) || !got.SequenceEqual(payload)) return "platform.scripts missing or changed";
            if (!mod.Custom.TryGetValue("cs2wasm.version", out var ver)) return "no version section";
            var vs = Encoding.UTF8.GetString(ver, 1, ver[0]);
            return vs == Cs2WasmCompiler.CompilerVersion && ver[1 + ver[0]] == 3 ? null : "bad version section";
        });

        Check("module shape: shared memory imported, init and exports, no WASI", () =>
        {
            var src = Header + "static class E { [UnmanagedCallersOnly(EntryPoint = \"tick\")] static int T(int a, float b) => a + (int)b; }";
            var r = Compile(src, new CompileOptions { MemoryInitialPages = 512 });
            if (!r.Success) return Errors(r);
            var mod = new Wasm(r.Module!);
            if (!mod.SharedMemoryImport) return "memory is not an imported shared memory";
            if (mod.MemoryMin != 512) return "initial pages " + mod.MemoryMin;
            if (!mod.Exports.Contains("init") || !mod.Exports.Contains("tick")) return "exports: " + string.Join(",", mod.Exports);
            if (mod.Imports.Any(i => i.StartsWith("wasi"))) return "WASI import found";
            return mod.Custom.ContainsKey("name") && mod.Custom.ContainsKey("cs2wasm.lines") ? null : "missing name or line sections";
        });

        Check("reproducible output: the same inputs give a byte-identical module", () =>
        {
            var src = File.ReadAllText(Path.Combine(root, "tests", "Conformance", "008_linq_lambdas.cs"));
            var a = Compile(src, new CompileOptions(), "a.cs");
            var b = Compile(src, new CompileOptions(), "a.cs");
            if (!a.Success || !b.Success) return Errors(a.Success ? b : a);
            return a.Module!.AsSpan().SequenceEqual(b.Module) ? null : "modules differ";
        });

        Check("an exception escaping an export is reported with type, message and stack; the export returns its failure value", () =>
        {
            var src = Header + "static class E { static int Div(int a, int b) => a / b; [UnmanagedCallersOnly(EntryPoint = \"main\")] static int M() { Console.WriteLine(\"before\"); return Div(1, 0) + 5; } }";
            var r = Compile(src, new CompileOptions(), "esc.cs");
            if (!r.Success) return Errors(r);
            var path = Path.Combine(outDir, "api_escape.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, code) = Node(root, path, printResult: true);
            if (!o.StartsWith("before\n")) return "stdout: " + o;
            if (!o.Contains("=> 0")) return "export did not return 0: " + o;
            return e.Contains("System.DivideByZeroException: Attempted to divide by zero.") && e.Contains("at E.Div(Int32 a, Int32 b) in esc.cs:line 3") && e.Contains("at E.M()") ? null : "report: " + e;
        });

        Check("plugin passes see reachable methods and the call graph, and add sections and diagnostics", () =>
        {
            var src = Header + "static class E { static int Helper(int x) => x * 2; [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() => Console.WriteLine(Helper(21)); }";
            var plugin = new CountingPlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin] });
            if (!r.Success) return Errors(r);
            if (!plugin.SawHelper) return "plugin did not see E.Helper reachable from E.M";
            var mod = new Wasm(r.Module!);
            if (!mod.Custom.TryGetValue("test.plugin", out var data) || Encoding.UTF8.GetString(data) != "ok") return "plugin section missing";
            return r.Diagnostics.Any(d => d.Id == "TP0001") ? null : "plugin diagnostic missing";
        });

        Check("source map maps code offsets to C# lines", () =>
        {
            var src = Header + "static class E {\n [UnmanagedCallersOnly(EntryPoint = \"main\")]\n static void M() {\n Console.WriteLine(1);\n }\n}";
            var r = Compile(src, new CompileOptions(), "map.cs");
            if (!r.Success) return Errors(r);
            return r.SourceMap != null && r.SourceMap.Contains("\"sources\":[") && r.SourceMap.Contains("map.cs") && r.SourceMap.Contains("\"mappings\":\"") ? null : "bad map: " + r.SourceMap?[..Math.Min(200, r.SourceMap.Length)];
        });

        return results;
    }

    sealed class CountingPlugin : ICompilerPlugin
    {
        public bool SawHelper;
        public int InterfaceVersion => 1;
        public string Name => "counting";
        public void Run(PluginContext context)
        {
            var main = context.ReachableMethods.FirstOrDefault(m => m.Name == "M");
            SawHelper = main != null && context.CallGraph.TryGetValue(main, out var callees) && callees.Any(c => c.Name == "Helper");
            context.AddCustomSection("test.plugin", Encoding.UTF8.GetBytes("ok"));
            context.Report(Diagnostic.Create(new DiagnosticDescriptor("TP0001", "t", "plugin notice", "test", DiagnosticSeverity.Info, true), Location.None));
        }
    }

    static CompileResult Compile(string src, CompileOptions o, string path = "test.cs") =>
        Cs2WasmCompiler.Compile(Frontend.CreateCompilation("t", [(path, src)]), o);

    static string Errors(CompileResult r) => string.Join("; ", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id + " " + d.GetMessage()));

    static (string, string, int) Node(string root, string wasm, bool printResult = false)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(root, "host", "run.mjs"), wasm, "main"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (printResult) psi.Environment["CS2WASM_PRINT_RESULT"] = "1";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (o, e, p.ExitCode);
    }

    /// <summary>A tiny reader for the parts of a module the checks look at.</summary>
    sealed class Wasm
    {
        public readonly List<string> Imports = [], Exports = [];
        public readonly Dictionary<string, byte[]> Custom = [];
        public bool SharedMemoryImport; public int MemoryMin;
        readonly List<string> _types = [];

        public Wasm(byte[] b)
        {
            int p = 8;
            while (p < b.Length)
            {
                byte id = b[p++]; int size = U(b, ref p); int end = p + size;
                switch (id)
                {
                    case 0:
                        { var n = Name(b, ref p); Custom[n] = b[p..end]; break; }
                    case 1:
                        {
                            int n = U(b, ref p);
                            for (int i = 0; i < n; i++)
                            {
                                p++; var ps = Vec(b, ref p); var rs = Vec(b, ref p);
                                _types.Add("(" + string.Join(",", ps) + ")->(" + string.Join(",", rs) + ")");
                            }
                            break;
                        }
                    case 2:
                        {
                            int n = U(b, ref p);
                            for (int i = 0; i < n; i++)
                            {
                                var m = Name(b, ref p); var f = Name(b, ref p); byte kind = b[p++];
                                if (kind == 0) Imports.Add(m + "." + f + ":" + _types[U(b, ref p)]);
                                else if (kind == 2) { byte flags = b[p++]; SharedMemoryImport = (flags & 2) != 0; MemoryMin = U(b, ref p); if ((flags & 1) != 0) U(b, ref p); }
                                else throw new Exception("unexpected import kind");
                            }
                            break;
                        }
                    case 7:
                        {
                            int n = U(b, ref p);
                            for (int i = 0; i < n; i++) { Exports.Add(Name(b, ref p)); p++; U(b, ref p); }
                            break;
                        }
                }
                p = end;
            }
        }

        static List<string> Vec(byte[] b, ref int p)
        {
            int n = U(b, ref p); var l = new List<string>();
            for (int i = 0; i < n; i++) l.Add(b[p++] switch { 0x7F => "i32", 0x7E => "i64", 0x7D => "f32", 0x7C => "f64", _ => "?" });
            return l;
        }
        static int U(byte[] b, ref int p) { int r = 0, s = 0; byte x; do { x = b[p++]; r |= (x & 0x7F) << s; s += 7; } while ((x & 0x80) != 0); return r; }
        static string Name(byte[] b, ref int p) { int n = U(b, ref p); var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
    }
}

using System.Diagnostics;
using System.Text;
using Cs2Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

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

        Check("plugin v2: accesses with conditions, update rules, def-use, aliasing, generated hooks, instrumented writes, clones without imports", () =>
        {
            var src = Header + """
                namespace Game {
                static class Engine { public static extern void Draw(int v); }
                class Player {
                    public float X; public float V = 2;
                    public int Health { get; set; } = 10;
                    public void Tick(float dt) { X += V * dt; }
                    public void Hit(int d) { if (d > 0) Health -= d; }
                }
                class Enemy { }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        var p = new Player();
                        p.Tick(0.5f); p.Hit(3); p.Hit(0);
                        Engine.Draw((int)(p.X * 10));
                        Console.WriteLine(p.Health + " " + p.X);
                        int s = 0;
                        for (int i = 0; i < 4; i++) s += 3;
                        Console.WriteLine(s + " " + new Enemy().GetHashCode());
                    }
                    [UnmanagedCallersOnly(EntryPoint = "frame")]
                    static void Frame() { Engine.Draw(1); Console.WriteLine("frame"); }
                }
                }
                """;
            var table = BoundaryTable.FromJson("""{ "imports": [ { "method": "M:Game.Engine.Draw(System.Int32)", "module": "game", "name": "draw" } ] }""");
            var plugin = new NetPlugin();
            var r = Compile(src, new CompileOptions { Boundary = table, Plugins = [plugin] });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin2.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            var want = "changed 7\n[engine] game.draw(10)\n7 1\n12 ";
            if (!o.StartsWith(want)) return $"main output:\n{o}\nwanted to start with:\n{want}\n{e}";
            var psi = new ProcessStartInfo("node", [Path.Combine(root, "host", "run.mjs"), path, "frame_quiet"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var pr = Process.Start(psi)!;
            var qo = pr.StandardOutput.ReadToEnd(); var qe = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            return qo == "frame\n" ? null : "clone output: " + qo + qe;
        });

        Check("plugin v3: values that depend on thread scheduling are marked; order-independent parallel work is not (section 7.3)", () =>
        {
            var src = Header + """
                using System.Threading;
                using System.Threading.Tasks;
                using System.Collections.Concurrent;
                namespace Sim {
                static class World {
                    public static double Energy;
                    public static int Count, Hits, Last, Score, Plain;
                    public static int[] Cells = new int[8];
                    public static string Winner = "";
                    public static readonly ConcurrentQueue<int> Queue = new();
                    static readonly object Gate = new();
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        Parallel.For(0, 8, i => {
                            lock (Gate) { Energy += i * 0.1; Hits++; }
                            Interlocked.Increment(ref Count);
                            Last = i;
                            Cells[i] = i * i;
                            Queue.Enqueue(i);
                        });
                        var t1 = Task.Run(() => 1); var t2 = Task.Run(() => 2);
                        var first = Task.WhenAny(t1, t2).Result;
                        Winner = first == t1 ? "a" : "b";
                        Score = Last * 2;
                        Plain = 5;
                        Console.WriteLine(Count + " " + Hits + " " + Cells[3] + " " + Plain);
                    }
                }
                }
                """;
            var plugin = new SchedulingPlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin] });
            if (!r.Success) return Errors(r);
            return plugin.Problem;
        });

        Check("threads park at safe points on the engine's request and resume (section 7.2); the engine pump is exported", () =>
        {
            var src = Header + """
                using System.Threading;
                static class P {
                    static int s_count, s_stop;
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        for (int i = 0; i < 2; i++)
                            new Thread(() => { while (Volatile.Read(ref s_stop) == 0) { Interlocked.Increment(ref s_count); Thread.Sleep(1); } }) { IsBackground = true }.Start();
                    }
                    [UnmanagedCallersOnly(EntryPoint = "count")] static int Count() => Volatile.Read(ref s_count);
                    [UnmanagedCallersOnly(EntryPoint = "stop")] static void Stop() => Volatile.Write(ref s_stop, 1);
                }
                """;
            var r = Compile(src, new CompileOptions());
            if (!r.Success) return Errors(r);
            var mod = new Wasm(r.Module!);
            foreach (var x in new[] { "cs2wasm_park", "cs2wasm_resume", "cs2wasm_parked_count", "cs2wasm_live_workers" })
                if (!mod.Exports.Contains(x)) return "missing export " + x + ": " + string.Join(",", mod.Exports);
            var path = Path.Combine(outDir, "api_park.wasm");
            File.WriteAllBytes(path, r.Module!);
            var js = Path.Combine(outDir, "api_park.mjs");
            var engine = new Uri(Path.Combine(root, "host", "engine.mjs")).AbsoluteUri;
            File.WriteAllText(js, $$"""
                import { readFileSync } from 'node:fs';
                import { cs2wasmImports, threadImports } from '{{engine}}';
                const module = new WebAssembly.Module(readFileSync(process.argv[2]));
                const memory = new WebAssembly.Memory({ initial: 256, maximum: 16384, shared: true });
                const out = { write: (s) => process.stdout.write(s), error: (t, m, s) => { console.error(t, m, s); process.exit(3); } };
                const inst = new WebAssembly.Instance(module, { env: { memory }, cs2wasm: { ...cs2wasmImports(module, () => memory, out), ...threadImports(module, memory, out) } });
                const x = inst.exports;
                const sleep = (ms) => new Promise(r => setTimeout(r, ms));
                x.init(); x.main();
                await sleep(100);
                x.cs2wasm_park();
                let t = 0;
                while (x.cs2wasm_parked_count() !== x.cs2wasm_live_workers() && t++ < 400) await sleep(5);
                const c1 = x.count(); await sleep(60); const c2 = x.count();
                x.cs2wasm_resume(); await sleep(60); const c3 = x.count();
                x.stop();
                console.log(x.cs2wasm_live_workers() === 2 && c1 === c2 && c3 > c2 && c1 > 0 ? 'ok' : `bad ${c1} ${c2} ${c3} parked ${x.cs2wasm_parked_count()}`);
                process.exit(0);
                """);
            var psi = new ProcessStartInfo("node", [js, path]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var pr = Process.Start(psi)!;
            var o = pr.StandardOutput.ReadToEnd(); var e = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            return o.Trim() == "ok" ? null : o + e;
        });

        Check("a blocking wait the engine thread reaches is a build notice; one in work handed to the pool is not", () =>
        {
            var src = Header + "using System.Threading.Tasks;\nstatic class E {\n static void Helper() => Task.Delay(1).Wait();\n [UnmanagedCallersOnly(EntryPoint = \"main\")]\n static void M() { Helper(); var t = Task.Run(() => Task.Delay(1).Wait()); Console.WriteLine(t.IsCompleted); }\n}";
            var r = Compile(src, new CompileOptions(), "block.cs");
            if (!r.Success) return Errors(r);
            var notes = r.Diagnostics.Where(d => d.Id == "CW0200").ToList();
            if (notes.Count != 1) return "notices: " + string.Join("; ", notes.Select(n => n.GetMessage() + " @" + n.Location.GetLineSpan().StartLinePosition));
            return notes[0].Location.GetLineSpan().StartLinePosition.Line == 4 && notes[0].GetMessage().Contains("Task.Wait") ? null : "notice: " + notes[0];
        });

        Check("source map maps code offsets to C# lines", () =>
        {
            var src = Header + "static class E {\n [UnmanagedCallersOnly(EntryPoint = \"main\")]\n static void M() {\n Console.WriteLine(1);\n }\n}";
            var r = Compile(src, new CompileOptions(), "map.cs");
            if (!r.Success) return Errors(r);
            return r.SourceMap != null && r.SourceMap.Contains("\"sources\":[") && r.SourceMap.Contains("map.cs") && r.SourceMap.Contains("\"mappings\":\"") ? null : "bad map: " + r.SourceMap?[..Math.Min(200, r.SourceMap.Length)];
        });

        // Memory checks (section 4.3): the module runs in a fixed 16 MB memory (256 pages, no growth allowed), so
        // anything that leaks runs out of memory and fails with OutOfMemoryException.
        string? Bounded(string name, string body, string want)
        {
            var src = Header + "using System.Collections.Generic;\n" + body;
            var r = Compile(src, new CompileOptions());
            if (!r.Success) return Errors(r);
            var path = Path.Combine(outDir, name + ".wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, code) = Node(root, path, extra: ["--pages", "256", "--max-pages", "256"]);
            return o == want && code == 0 ? null : $"exit {code}, output «{o}»{(e.Length > 0 ? "\n" + e : "")}";
        }

        Check("cycles: 1 000 000 two-object cycles run in a fixed 16 MB memory", () => Bounded("api_cycles_pairs", """
            class N { public N O; }
            static class P {
                [UnmanagedCallersOnly(EntryPoint = "main")]
                static void M() {
                    int s = 0;
                    for (int i = 0; i < 1000000; i++) { var a = new N(); var b = new N(); a.O = b; b.O = a; if (a.O.O == a) s++; }
                    Console.WriteLine(s);
                }
            }
            """, "1000000\n"));

        Check("cycles: a long-lived tree that keeps dropping subtrees stays in a fixed 16 MB memory (arena sweep)", () => Bounded("api_cycles_tree", """
            class T {
                public T Parent; public List<T> Kids = new(); public object Tag;
                public T Add(T c) { c.Parent = this; Kids.Add(c); return c; }
            }
            static class P {
                [UnmanagedCallersOnly(EntryPoint = "main")]
                static void M() {
                    var root = new T();
                    for (int i = 0; i < 100000; i++) {
                        var sub = root.Add(new T());
                        for (int j = 0; j < 4; j++) sub.Add(new T()).Tag = sub;
                        if (root.Kids.Count > 8) root.Kids.RemoveAt(0);
                    }
                    Console.WriteLine(root.Kids.Count + " " + root.Kids[0].Kids.Count);
                }
            }
            """, "8 4\n"));

        Check("threads: 200 threads one after another run in a fixed 16 MB memory (stacks and thread-local blocks reused)", () => Bounded("api_thread_reuse", """
            using System.Threading;
            static class P {
                [ThreadStatic] static List<int>? s_mine;
                [UnmanagedCallersOnly(EntryPoint = "main")]
                static void M() {
                    long total = 0;
                    for (int i = 0; i < 200; i++) {
                        int k = i;
                        var t = new Thread(() => { s_mine = new List<int> { k, k }; Interlocked.Add(ref total, s_mine[0] + s_mine[1]); });
                        t.Start(); t.Join();
                    }
                    Console.WriteLine(total);
                }
            }
            """, "39800\n"));

        Check("cycles: types that cannot form cycles never pay for arenas", () =>
        {
            var src = Header + """
                class A { public object X; public B Child; }
                class B { public object Y; public string S; }
                static class P {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void M() { var a = new A { X = "s", Child = new B { S = "t" } }; a.Child.Y = 1; a.X = a.Child.S; Console.WriteLine(a.X); }
                }
                """;
            var r = Compile(src, new CompileOptions());
            if (!r.Success) return Errors(r);
            // the program's types never pay (the library's own may be cycle-capable: upstream's encoders are)
            if (r.CycleCapableTypes.FirstOrDefault(t => t is "A" or "B") is { } bad) return bad + " is cycle-capable";
            return r.CycleCapableTypes.Count == 0 && Encoding.UTF8.GetString(r.Module!).Contains("ArenaLink") ? "arena runtime present without cycle-capable types" : null;
        });

        return results;
    }

    sealed class NetPlugin : ICompilerPlugin
    {
        public string? Problem;
        public int InterfaceVersion => 2;
        public string Name => "net";
        void Fail(string m) { Problem ??= m; }
        public void Run(PluginContext c)
        {
            var player = c.Compilation.GetTypeByMetadataName("Game.Player")!;
            var health = player.GetMembers("Health").OfType<IPropertySymbol>().Single();
            var writes = c.AccessesOf(health).Where(a => a.Kind != AccessKind.Read).ToList();
            if (writes.Count != 1) { Fail("Health writes: " + writes.Count); return; }
            if (writes[0].Kind != AccessKind.ReadWrite || writes[0].Conditions.Count != 1 || writes[0].Conditions[0].Test.Syntax.ToString() != "d > 0" || !writes[0].Conditions[0].WhenTrue)
            { Fail("Health write condition: " + string.Join(",", writes[0].Conditions.Select(x => x.Test.Syntax + "=" + x.WhenTrue))); return; }
            var tick = player.GetMembers("Tick").OfType<IMethodSymbol>().Single();
            var rules = c.GetUpdateRules(tick);
            if (rules.Count != 1 || rules[0].Target.Name != "X" || rules[0].Kind != UpdateKind.Add || rules[0].Loop != null || rules[0].Step!.Syntax.ToString() != "V * dt")
            { Fail("Tick rules: " + string.Join("; ", rules.Select(x => x.Formula))); return; }
            var script = c.Compilation.GetTypeByMetadataName("Game.Script")!;
            var main = script.GetMembers("Main").OfType<IMethodSymbol>().Single();
            var mrules = c.GetUpdateRules(main);
            if (!mrules.Any(x => x.Target.Name == "s" && x.Kind == UpdateKind.Add && x.Loop != null && x.Formula == "s(n) = s(0) + n * (3)"))
            { Fail("Main rules: " + string.Join("; ", mrules.Select(x => x.Formula))); return; }
            if (!mrules.Any(x => x.Target.Name == "i" && x.StepSign == 1)) { Fail("loop counter rule missing"); return; }
            var du = c.GetDefUse(main);
            if (du == null || !du.Definitions.Keys.Any(k => k.Name == "s") || du.ReachingDefinitions.Count == 0) { Fail("def-use"); return; }
            var x = player.GetMembers("X").OfType<IFieldSymbol>().Single();
            if (c.PointsTo(x).Count != 0) { Fail("float field points to objects"); return; }
            var creations = c.Accesses.Select(a => a.Operation).ToList();
            // generated code: a hook that counts writes, and an export
            var gen = c.AddSource("""
                using System;
                using System.Runtime.InteropServices;
                public static class NetHooks {
                    public static int Changes;
                    public static void Changed(object o, int tag) { Changes++; Console.WriteLine("changed " + tag); }
                    [UnmanagedCallersOnly(EntryPoint = "changes")] static int Count() => Changes;
                }
                """);
            var hook = gen.GetTypeByMetadataName("NetHooks")!.GetMembers("Changed").OfType<IMethodSymbol>().Single();
            c.InstrumentWrites(health, hook, tag: 7);
            var frame = script.GetMembers("Frame").OfType<IMethodSymbol>().Single();
            var draw = c.Compilation.GetTypeByMetadataName("Game.Engine")!.GetMembers("Draw").OfType<IMethodSymbol>().Single();
            c.CloneWithSuppressedImports(frame, [draw], "frame_quiet");
        }
    }

    sealed class SchedulingPlugin : ICompilerPlugin
    {
        public string? Problem;
        public int InterfaceVersion => 3;
        public string Name => "scheduling";
        public void Run(PluginContext c)
        {
            var world = c.Compilation.GetTypeByMetadataName("Sim.World")!;
            var info = c.Scheduling;
            var marked = info.Members.Select(m => m.Name).ToHashSet();
            var want = new[] { "Energy", "Last", "Queue", "Winner", "Score" };
            var not = new[] { "Count", "Hits", "Cells", "Plain" };
            var missing = want.Where(w => !marked.Contains(w)).ToList();
            var extra = not.Where(n => marked.Contains(n)).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                Problem = "marked: " + string.Join(",", marked) + "; missing " + string.Join(",", missing) + "; extra " + string.Join(",", extra)
                    + " | " + string.Join(" | ", info.Marks.Select(m => (m.Symbol?.Name ?? "-") + ": " + m.Reason + " at " + m.Operation.Syntax));
                return;
            }
            if (!info.Marks.Any(m => m.Symbol == null && m.Reason.Contains("WhenAny"))) { Problem = "WhenAny not a race result"; return; }
            if (!info.Marks.Any(m => m.Symbol?.Name == "Energy" && m.Reason.Contains("floating-point"))) { Problem = "Energy not a float accumulation"; return; }
            var main = world.GetMembers("Main").OfType<IMethodSymbol>().Single();
            var winnerWrite = c.AccessesOf(world.GetMembers("Winner").Single()).First(a => a.Kind == AccessKind.Write);
            if (winnerWrite.Operation.Parent is not IAssignmentOperation wa || !info.IsSchedulingDependent(wa.Value)) { Problem = "Winner's value not dependent"; return; }
            var plainWrite = c.AccessesOf(world.GetMembers("Plain").Single()).First(a => a.Kind == AccessKind.Write);
            if (plainWrite.Operation.Parent is IAssignmentOperation pa && info.IsSchedulingDependent(pa.Value)) Problem = "Plain's value dependent";
        }
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

    static (string, string, int) Node(string root, string wasm, bool printResult = false, string[]? extra = null)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(root, "host", "run.mjs"), wasm, "main", .. extra ?? []]) { RedirectStandardOutput = true, RedirectStandardError = true };
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

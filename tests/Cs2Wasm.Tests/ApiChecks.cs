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

        Check("files are the host's: every module gets them through the cs2wasm.fs_* imports (section 8)", () =>
        {
            var src = Header + "static class E { [UnmanagedCallersOnly(EntryPoint = \"main\")] static void M() => Console.WriteLine(System.IO.File.ReadAllText(\"x\")); }";
            var r = Compile(src, new CompileOptions());
            if (!r.Success) return Errors(r);
            var imports = new Wasm(r.Module!).Imports;
            return new[] { "fs_open", "fs_read", "fs_close" }.All(n => imports.Any(i => i.StartsWith("cs2wasm." + n + ":"))) ? null : "missing file imports: " + string.Join(", ", imports);
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

        Check("plugin: generated code reads and writes private fields through field accessors (a gate, a solver), hooked at a private field's writes", () =>
        {
            var src = Header + """
                namespace Game {
                class Round {
                    float _timer = 3f; int _phase; static Round s_current = new Round();
                    string _label = "round";
                    public void Tick(float dt) { _timer -= dt; if (_timer <= 0) { _timer = 3f; _phase++; } }
                    public static Round Current => s_current;
                    static void Zero(ref float f) { if (f < -100) f = 0; }
                    public void Odd() { Zero(ref _timer); int unused; if (_timer < -100) (_timer, unused) = (0f, 1); }
                    public string Show() => _label + " " + _phase + " " + _timer;
                }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        for (int i = 0; i < 7; i++) Round.Current.Tick(1f);
                        Round.Current.Odd();
                        Console.WriteLine(Round.Current.Show());
                    }
                }
                }
                """;
            var plugin = new PrivatePlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin_private.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            // each phase change: the gate's answer and the timer the solver set; then the program's own line
            var want = "phase 1 open False timer 3\nphase 2 open True timer 3\nsolved round 2 1.5\n";
            return o == want ? null : $"output:\n{o}\nwanted:\n{want}\n{e}";
        });

        Check("plugin: hooks before and after a write, at a read and before a statement; the control-flow graph and aliasing; a plugin that fails or asks for a later interface fails the build", () =>
        {
            var src = Header + """
                namespace Game {
                class Counter { public int N; public Counter? Other; public void Inc() { N++; } }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        var c = new Counter(); var d = new Counter();
                        c.Other = d;
                        c.Inc(); c.N = 5;
                        int r = c.N;
                        if (r > 1) Console.WriteLine("big");
                        Console.WriteLine(r + " " + (c.Other == d));
                    }
                }
                }
                """;
            var plugin = new HooksPlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin_hooks.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            var want = "before 0\nafter 1\nbefore 1\nafter 5\nread 5\nstatement 4\nbig\n5 True\n";
            if (o != want) return $"output:\n{o}\nwanted:\n{want}\n{e}";
            var failing = Compile(src, new CompileOptions { Plugins = [new ThrowingPlugin()] });
            if (failing.Success || !failing.Diagnostics.Any(d => d.Id == "CW0300" && d.GetMessage().Contains("went wrong"))) return "a plugin that throws did not fail the build";
            var later = Compile(src, new CompileOptions { Plugins = [new LaterPlugin()] });
            if (later.Success || !later.Diagnostics.Any(d => d.Id == "CW0300" && d.GetMessage().Contains("interface version"))) return "a plugin for a later interface did not fail the build";
            var erring = Compile(src, new CompileOptions { Plugins = [new ErrorPlugin()] });
            return erring.Success ? "a plugin's error diagnostic did not fail the build" : null;
        });

        Check("plugin v4: the value of one expression goes through a hook that can replace it (a branch's outcome, a random draw, a stored struct, a string, an awaited answer); a hook at one write of several", () =>
        {
            var src = Header + """
                using System.Threading.Tasks;
                namespace Game {
                struct Vec { public float X, Y; public Vec(float x, float y) { X = x; Y = y; } public override string ToString() => X + "," + Y; }
                static class Rng { static uint s_state = 7; public static int Next(int n) { s_state = s_state * 1103515245 + 12345; return (int)((s_state >> 16) % (uint)n); } }
                class Enemy {
                    bool _seesPlayer; float _x; Vec _velocity; int _score; string _name = "enemy";
                    public void Look(bool sees) { _seesPlayer = sees; }
                    public void Tick() {
                        if (_seesPlayer) _x += 10; else _x -= 1;
                        _velocity = new Vec(_x * 0.123456f, 2.5f);
                    }
                    public void Roll() { _score = Rng.Next(6); }
                    public void Reset() { _score = 0; }
                    public string Show() => _name + " " + _x + " " + _velocity + " " + _score;
                    public string Name() => _name + "?";
                    static async Task<int> AnswerAsync() { await Task.Yield(); return 5; }
                    public async Task<int> AskAsync() { int a = await AnswerAsync(); return a * 2; }
                }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        var e = new Enemy();
                        e.Tick(); e.Look(true); e.Tick(); e.Tick();
                        e.Roll(); e.Reset(); e.Roll();
                        Console.WriteLine(e.Show());
                        Console.WriteLine(e.Name());
                        Console.WriteLine(e.AskAsync().GetAwaiter().GetResult());
                    }
                }
                }
                """;
            var plugin = new ValuePlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin_values.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            // the branch: seen false, then true, then true but told false; the draws replaced by 4; one write marked;
            // the velocity rounded as it is stored; the name through two hooks in order; the awaited answer plus 100
            var want = "branch False\nbranch True\nbranch True\ndraw\nmarked 4\ndraw\nmarked 4\nenemy 8 1,2.5 4\nenemy?.!\n210\n";
            if (o != want) return $"output:\n{o}\nwanted:\n{want}\n{e}";
            var refused = Compile(src, new CompileOptions { Plugins = [new ValuePlugin { HookTarget = true }] });
            return !refused.Success && refused.Diagnostics.Any(d => d.Id == "CW0300" && d.GetMessage().Contains("was not applied")) ? null : "a value hook on an assignment's target was not refused";
        });

        Check("plugin v5: collection entries are accesses (an element stored, a compound store, Remove, TryGetValue, a foreach, an array element, through a local) with their keys, and hooks after them get the receiver and the key; plugins of version 4 do not see them; a hook whose key type differs is refused", () =>
        {
            var src = Header + """
                using System.Collections.Generic;
                namespace Game {
                class Player { public string Name; public Player(string n) { Name = n; } public override string ToString() => Name; }
                class Match {
                    public Dictionary<Player, int> Scores = new();
                    public List<int> Log = new();
                    public int[] Cells = new int[4];
                    public void Score(Player p, int n) { Scores[p] = n; }
                    public void Bump(Player p) { Scores[p] += 1; }
                    public void Drop(Player p) { Scores.Remove(p); }
                    public int Get(Player p) => Scores.TryGetValue(p, out var v) ? v : -1;
                    public void Note(int x) { var log = Log; log.Add(x); }
                    public void Cell(int i) { Cells[i] = i * 10; }
                    public int Total() { int t = 0; foreach (var kv in Scores) t += kv.Value; return t; }
                    public static Player Pick(Player a, Player b, bool first) => first ? a : b;
                }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        var m = new Match(); var a = new Player("a"); var b = new Player("b");
                        m.Score(a, 3); m.Score(b, 4); m.Bump(a); m.Drop(b); m.Note(7); m.Cell(2);
                        m.Scores[Match.Pick(a, b, false)] = 9;
                        Console.WriteLine(m.Get(a) + " " + m.Total() + " " + m.Log.Count + " " + m.Cells[2]);
                    }
                }
                }
                """;
            var plugin = new EntryPlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin_entries.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            var want = "write a 3 True\nwrite b 4 True\nwrite a 4 True\nwrite b -1 True\nlog True 5\ncell 2 20 True\nwrite b 9 True\nread\nread\n4 13 1 20\n";
            if (o != want) return $"output:\n{o}\nwanted:\n{want}\n{e}";
            var old = new EntryPlugin { Version = 4 };
            var r4 = Compile(src, new CompileOptions { Plugins = [old] });
            if (!r4.Success) return Errors(r4);
            if (old.Problem != null) return "version 4: " + old.Problem;
            var refused = Compile(src, new CompileOptions { Plugins = [new EntryPlugin { WrongKey = true }] });
            return !refused.Success && refused.Diagnostics.Any(d => d.Id == "CW0300" && d.GetMessage().Contains("the key here is")) ? null : "a hook whose key type differs was not refused: " + Errors(refused);
        });

        Check("plugin v5: a hook after a statement runs when it completes normally (an if, a loop left by break, a declaration with an await) and not when it returns or throws; a hook after a jump is refused", () =>
        {
            var src = Header + """
                using System.Threading.Tasks;
                namespace Game {
                static class Script {
                    static int s_n;
                    static void Step(int i) {
                        if (i == 2) return;
                        s_n += i;
                    }
                    static int Find(int[] xs) {
                        foreach (var x in xs) { if (x > 2) break; }
                        try { Throw(); } catch (Exception) { }
                        return 0;
                    }
                    static void Throw() => throw new InvalidOperationException();
                    static async Task<int> Answer() { await Task.Yield(); return 4; }
                    static async Task<int> Later() {
                        int a = await Answer();
                        return a;
                    }
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        for (int i = 0; i < 4; i++) Step(i);
                        Console.WriteLine("n " + s_n);
                        Find(new[] { 1, 5 }); Find(new[] { 1 });
                        Console.WriteLine(Later().GetAwaiter().GetResult());
                    }
                }
                }
                """;
            var r = Compile(src, new CompileOptions { Plugins = [new AfterPlugin()], Checked = true });
            if (!r.Success) return Errors(r);
            var path = Path.Combine(outDir, "api_plugin_after.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            var want = "after 1\nafter 1\nafter 1\nn 4\nafter 3\nafter 3\nafter 5\n4\n";
            if (o != want) return $"output:\n{o}\nwanted:\n{want}\n{e}";
            var refused = Compile(src, new CompileOptions { Plugins = [new AfterPlugin { OnJump = true }] });
            return !refused.Success && refused.Diagnostics.Any(d => d.Id == "CW0300" && d.GetMessage().Contains("never completes normally")) ? null : "a hook after a return was not refused: " + Errors(refused);
        });

        Check("plugin v5: conditions with early returns and across calls (paths from the export, a virtual call's callers); aliasing by where references come from and which new objects a method owns alone", () =>
        {
            var src = Header + """
                namespace Game {
                class Unit { public int Hp; }
                class World {
                    public bool Paused; public int Tick;
                    Unit _boss = new Unit();
                    public void Step(bool input) {
                        if (Paused) return;
                        Tick++;
                        if (input) Fire(_boss);
                    }
                    void Fire(Unit u) {
                        if (u.Hp <= 0) return;
                        u.Hp -= 1;
                    }
                    public virtual void Hit(Unit u) { u.Hp -= 2; }
                    static Unit? s_kept;
                    static void Keep(Unit u) { s_kept = u; }
                    public static int Make() {
                        var a = new Unit(); var b = new Unit(); var c = a;
                        c.Hp = 3;
                        var kept = new Unit(); Keep(kept);
                        Unit d = b.Hp > 0 ? a : b;
                        var o = new Unit(); o.Hp = 1;
                        var k = s_kept;
                        int h = o.Hp + (k == null ? 0 : 1);
                        return a.Hp + b.Hp + d.Hp + kept.Hp + h - 2;
                    }
                    public int Self(Unit p) { var n = new Unit(); return n == p ? 1 : 0; }
                }
                class Arena : World { public override void Hit(Unit u) { u.Hp -= 3; } }
                static class Script {
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() {
                        var w = new World(); w.Step(true); w.Step(false);
                        World x = new Arena(); x.Hit(new Unit());
                        Console.WriteLine(World.Make() + " " + w.Tick + " " + w.Self(new Unit()));
                    }
                }
                }
                """;
            var plugin = new PathPlugin();
            var r = Compile(src, new CompileOptions { Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            if (plugin.Problem != null) return plugin.Problem;
            var path = Path.Combine(outDir, "api_plugin_paths.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            return o == "3 2 0\n" ? null : $"output:\n{o}\n{e}";
        });

        Check("plugin v4: suppressible imports do nothing while their bit is set, whatever calls them (a virtual method, a delegate, a lambda, an interface); other imports and the program's state go on", () =>
        {
            var src = Header + """
                namespace Game {
                static class Engine { public static extern void Play(int sound); public static extern int Spawn(int kind); public static extern void Move(int x); }
                abstract class Thing { public abstract void Act(); }
                class Loud : Thing { public override void Act() { Engine.Play(1); } }
                interface IFx { int Go(); }
                class Spawner : IFx { public int Go() => Engine.Spawn(7); }
                static class Script {
                    static int s_ticks;
                    static readonly System.Action s_later = () => Engine.Play(2);
                    public static void Tick() {
                        s_ticks++;
                        Thing t = new Loud(); t.Act();
                        IFx fx = new Spawner(); int id = fx.Go();
                        s_later();
                        System.Func<int, int> f = k => Engine.Spawn(k + id);
                        Engine.Move(s_ticks * 10 + f(1));
                    }
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void Main() { Tick(); Console.WriteLine("ticks " + s_ticks); }
                }
                }
                """;
            var table = BoundaryTable.FromJson("""{ "imports": [ { "method": "M:Game.Engine.Play(System.Int32)", "module": "game", "name": "play" }, { "method": "M:Game.Engine.Spawn(System.Int32)", "module": "game", "name": "spawn" }, { "method": "M:Game.Engine.Move(System.Int32)", "module": "game", "name": "move" } ] }""");
            var plugin = new QuietPlugin();
            var r = Compile(src, new CompileOptions { Boundary = table, Plugins = [plugin], Checked = true });
            if (!r.Success) return Errors(r);
            var path = Path.Combine(outDir, "api_plugin_quiet.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            // (the test host's imports return 0)
            var loud = "[engine] game.play(1)\n[engine] game.spawn(7)\n[engine] game.play(2)\n[engine] game.spawn(1)\n[engine] game.move(10)\nticks 1\n";
            if (o != loud) return $"main output:\n{o}\nwanted:\n{loud}\n{e}";
            var psi = new ProcessStartInfo("node", [Path.Combine(root, "host", "run.mjs"), path, "quiet_tick"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var pr = Process.Start(psi)!;
            var qo = pr.StandardOutput.ReadToEnd().Replace("\r\n", "\n"); var qe = pr.StandardError.ReadToEnd(); pr.WaitForExit();
            // sounds and spawns left out, the move made, the state advanced, and loud again afterwards
            var quiet = "[engine] game.move(10)\nquiet 1 0\n[engine] game.play(1)\n[engine] game.spawn(7)\n[engine] game.play(2)\n[engine] game.spawn(1)\n[engine] game.move(20)\n";
            return qo == quiet ? null : $"quiet output:\n{qo}\nwanted:\n{quiet}\n{qe}";
        });

        Check("what the sandbox lacks builds with a warning and throws PlatformNotSupportedException where the program reaches it (section 8); the rest of the program runs", () =>
        {
            var src = Header + """
                static class E {
                    static void Try(string what, Action a) { try { a(); Console.WriteLine(what + ": ran"); } catch (PlatformNotSupportedException) { Console.WriteLine(what + ": PlatformNotSupportedException"); } }
                    [UnmanagedCallersOnly(EntryPoint = "main")]
                    static void M() {
                        Console.WriteLine("before");
                        Try("process", () => System.Diagnostics.Process.Start("notepad"));
                        Try("socket", () => new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp).Dispose());
                        Try("code page", () => System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes("x"));
                        Console.WriteLine("after");
                    }
                }
                """;
            var r = Compile(src, new CompileOptions(), "sandbox.cs");
            if (!r.Success) return Errors(r);
            if (!r.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning && d.Id is "CW0003" or "CW0100")) return "no warning names what the sandbox lacks: " + string.Join("; ", r.Diagnostics.Select(d => d.Id));
            var path = Path.Combine(outDir, "api_sandbox.wasm");
            File.WriteAllBytes(path, r.Module!);
            var (o, e, _) = Node(root, path);
            var want = "before\nprocess: PlatformNotSupportedException\nsocket: PlatformNotSupportedException\ncode page: PlatformNotSupportedException\nafter\n";
            return o == want ? null : $"output:\n{o}\nwanted:\n{want}\n{e}";
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

        // (section 4.3: objects that refer to each other in a cycle are freed together, when nothing outside the group
        // refers to any of them; no part of a group is looked for or freed on its own while the group lives)
        Check("cycles: trees whose nodes link back to their parents, built and dropped, stay in a fixed 16 MB memory (each is freed whole with its last outside reference)", () => Bounded("api_cycle_trees", """
            class T {
                public T Parent; public List<T> Kids = new(); public object Tag;
                public T Add(T c) { c.Parent = this; Kids.Add(c); return c; }
            }
            static class P {
                [UnmanagedCallersOnly(EntryPoint = "main")]
                static void M() {
                    int kids = 0;
                    for (int i = 0; i < 100000; i++) {
                        var root = new T();
                        var sub = root.Add(new T());
                        for (int j = 0; j < 4; j++) sub.Add(new T()).Tag = sub;
                        kids += sub.Kids.Count + root.Kids.Count;
                    }
                    Console.WriteLine(kids);
                }
            }
            """, "500000\n"));

        // (a reference from an object to what holds it is not counted, so the part cut off has no reference left)
        Check("cycles: a long-lived tree that keeps dropping subtrees stays in a fixed 16 MB memory (the part cut off is destroyed at the store that cuts it, by its counts alone)", () => Bounded("api_cycle_split", """
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

    /// <summary>What a networking plugin's generated functions need: a gate that reads a private field, a solver that
    /// writes one, both reached from a hook at the writes of another private field.</summary>
    sealed class PrivatePlugin : ICompilerPlugin
    {
        public string? Problem;
        public int InterfaceVersion => 3;
        public string Name => "private";
        public void Run(PluginContext c)
        {
            var round = c.Compilation.GetTypeByMetadataName("Game.Round")!;
            var phase = round.GetMembers("_phase").OfType<IFieldSymbol>().Single();
            var writes = c.AccessesOf(phase).Where(a => a.Kind != AccessKind.Read).ToList();
            if (writes.Count != 1 || writes[0].Conditions.Count != 1 || writes[0].Conditions[0].Test.Syntax.ToString() != "_timer <= 0" || !writes[0].Instrumentable)
            { Problem = "_phase writes: " + writes.Count; return; }
            // a write through `ref`, and a deconstruction's target: seen as writes, and marked as ones no hook runs at
            var timer = round.GetMembers("_timer").OfType<IFieldSymbol>().Single();
            var tw = c.AccessesOf(timer).Where(a => a.Kind != AccessKind.Read).ToList();
            if (tw.Count(a => !a.Instrumentable) != 2 || tw.Count(a => a.Instrumentable) != 2)
            { Problem = "_timer writes: " + string.Join(", ", tw.Select(a => a.Operation.Parent!.Kind + "=" + a.Instrumentable)); return; }
            var gen = c.AddSource("""
                using System;
                using System.Runtime.CompilerServices;
                using Game;
                public static class Generated {
                    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_phase")] static extern ref int Phase(Round r);
                    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_timer")] static extern ref float Timer(Round r);
                    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_label")] static extern ref string Label(Round r);
                    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_current")] static extern ref Round Current(Round? r);
                    // a gate: whether the others may read the scores yet
                    static bool Gate(Round r) => r._phase >= 2 && Round.s_current != null;
                    // a solver: the timer after n ticks of dt
                    static void Solve(Round r, int n, float dt) { Timer(r) -= n * dt; }
                    public static void PhaseChanged(object o, int tag)
                    {
                        var r = (Round)o;
                        Console.WriteLine("phase " + Phase(r) + " open " + Gate(r) + " timer " + Timer(r));
                        if (Phase(r) == 2) { Solve(Current(null), 1, 0.5f); Label(r) = "solved " + Label(r); }
                    }
                }
                """);
            var hook = gen.GetTypeByMetadataName("Generated")!.GetMembers("PhaseChanged").OfType<IMethodSymbol>().Single();
            c.InstrumentWrites(phase, hook, tag: 1);
        }
    }

    sealed class HooksPlugin : ICompilerPlugin
    {
        public string? Problem;
        public int InterfaceVersion => 3;
        public string Name => "hooks";
        public void Run(PluginContext c)
        {
            var counter = c.Compilation.GetTypeByMetadataName("Game.Counter")!;
            var n = counter.GetMembers("N").OfType<IFieldSymbol>().Single();
            var main = c.Compilation.GetTypeByMetadataName("Game.Script")!.GetMembers("Main").OfType<IMethodSymbol>().Single();
            var graph = c.GetControlFlowGraph(main);
            if (graph == null || graph.Blocks.Length < 4) { Problem = "control-flow graph: " + graph?.Blocks.Length; return; }
            var tree = c.Compilation.SyntaxTrees.Single();
            var model = c.Compilation.GetSemanticModel(tree);
            var news = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>().Select(x => model.GetOperation(x)!).ToList();
            if (c.MayAlias(news[0], news[1])) { Problem = "two new objects alias"; return; }
            var others = c.AccessesOf(counter.GetMembers("Other").Single()).Select(a => ((IFieldReferenceOperation)a.Operation).Instance!).ToList();
            if (!c.MayAlias(others[0], others[1])) { Problem = "the same local does not alias itself"; return; }
            if (c.PointsTo((IFieldSymbol)counter.GetMembers("Other").Single()).All(t => t.Name != "Counter")) { Problem = "Other points to no Counter"; return; }
            var gen = c.AddSource("""
                using System;
                using Game;
                public static class Hooks {
                    public static void Before(object o, int tag) => Console.WriteLine("before " + ((Counter)o).N);
                    public static void After(object o) => Console.WriteLine("after " + ((Counter)o).N);
                    public static void Read(object o, int tag) => Console.WriteLine("read " + ((Counter)o).N);
                    public static void Statement(int tag) => Console.WriteLine("statement " + tag);
                }
                """);
            IMethodSymbol H(string name) => gen.GetTypeByMetadataName("Hooks")!.GetMembers(name).OfType<IMethodSymbol>().Single();
            c.InstrumentWrites(n, H("Before"), 1, InstrumentPoint.Before);
            c.InstrumentWrites(n, H("After"), 2, InstrumentPoint.After);
            // (reads in the program's Main only show: the hooks' own reads of N are not instrumented again)
            c.InstrumentReads(n, H("Read"), 3);
            var big = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax>().Single(s => s.ToString().Contains("\"big\""));
            c.InstrumentBefore(model.GetOperation(big)!, H("Statement"), 4);
        }
    }

    /// <summary>The hooks a networking plugin needs at single expressions: a branch's outcome seen and replaced, a draw
    /// redirected, a stored value rounded, an awaited answer changed, and one write of several marked.</summary>
    sealed class ValuePlugin : ICompilerPlugin
    {
        public string? Problem;
        public bool HookTarget;
        public int InterfaceVersion => 4;
        public string Name => "values";
        public void Run(PluginContext c)
        {
            var tree = c.Compilation.SyntaxTrees.Single();
            var model = c.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            IOperation Op<T>(Func<T, bool> pick) where T : SyntaxNode => model.GetOperation(root.DescendantNodes().OfType<T>().Single(pick))!;
            var gen = c.AddSource("""
                using System;
                using Game;
                static class Net {
                    static int s_branches;
                    public static bool Branch(bool outcome) { Console.WriteLine("branch " + outcome); return ++s_branches == 3 ? false : outcome; }
                    public static int Draw(int value, int tag) { Console.WriteLine("draw"); return tag; }
                    public static Vec Round(Vec v) => new Vec(MathF.Round(v.X), v.Y);
                    public static string Dot(string s) => s + ".";
                    public static string Bang(string s) => s + "!";
                    public static int Answer(int a) => a + 100;
                    public static void Marked(object o, int tag) => Console.WriteLine("marked " + ((Enemy)o)._score);
                }
                """);
            IMethodSymbol H(string name) => gen.GetTypeByMetadataName("Net")!.GetMembers(name).OfType<IMethodSymbol>().Single();
            var ifs = (Microsoft.CodeAnalysis.Operations.IConditionalOperation)Op<Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax>(_ => true);
            c.InterceptValue(ifs.Condition, H("Branch"));
            var draw = Op<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(i => i.ToString() == "Rng.Next(6)");
            c.InterceptValue(draw, H("Draw"), tag: 4);
            var store = (Microsoft.CodeAnalysis.Operations.ISimpleAssignmentOperation)Op<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(a => a.Left.ToString() == "_velocity");
            c.InterceptValue(store.Value, H("Round"));
            var name = Op<Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax>(b => b.ToString() == "_name + \"?\"");
            c.InterceptValue(name, H("Dot")); c.InterceptValue(name, H("Bang"));
            var awaited = Op<Microsoft.CodeAnalysis.CSharp.Syntax.AwaitExpressionSyntax>(a => a.ToString() == "await AnswerAsync()");
            c.InterceptValue(awaited, H("Answer"));
            // the draw's write is an interaction; the reset is not: only the first is marked
            var roll = Op<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(a => a.ToString() == "_score = Rng.Next(6)");
            c.InstrumentWrite(roll, H("Marked"), tag: 1);
            if (HookTarget)
            {
                var reset = (Microsoft.CodeAnalysis.Operations.ISimpleAssignmentOperation)Op<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>(a => a.ToString() == "_score = 0");
                c.InterceptValue(reset.Target, H("Answer"));
            }
        }
    }

    /// <summary>Per-player state in a dictionary, a list reached through a local, an array: the entries as accesses,
    /// and hooks at them with the key.</summary>
    sealed class EntryPlugin : ICompilerPlugin
    {
        public string? Problem;
        public int Version = 5;
        public bool WrongKey;
        public int InterfaceVersion => Version;
        public string Name => "entries";
        public void Run(PluginContext c)
        {
            var match = c.Compilation.GetTypeByMetadataName("Game.Match")!;
            ISymbol F(string n) => match.GetMembers(n).Single();
            var entries = c.Accesses.Where(a => a.IsEntry).ToList();
            if (Version < 5) { if (entries.Count > 0 || c.AccessesOf(F("Scores")).Any(a => a.IsEntry)) Problem = "entries seen: " + entries.Count; return; }
            string Show(MemberAccess a) => a.Method.Name + ":" + a.Kind + ":" + (a.Key?.Syntax.ToString().Replace(" ", "") ?? "-") + (a.Instrumentable ? "" : ":no");
            string Of(string member) => string.Join(" ", c.AccessesOf(F(member)).Where(a => a.IsEntry).Select(Show).Order());
            var want = new Dictionary<string, string>
            {
                ["Scores"] = "Score:Write:p Bump:ReadWrite:p Drop:Write:p Get:Read:p Total:Read:- Main:Write:Match.Pick(a,b,false)",
                ["Log"] = "Note:Write:- Main:Read:-",
                ["Cells"] = "Cell:Write:i Main:Read:2",
            };
            foreach (var (member, w) in want)
                if (Of(member) != string.Join(" ", w.Split(" ").Order())) { Problem = $"entries of {member}: {Of(member)}; wanted {w}"; return; }
            var gen = c.AddSource("""
                using System;
                using Game;
                static class Entries {
                    public static void Score(object o, Player key, bool keyed, int tag) => Console.WriteLine("write " + key + " " + (((Match)o).Scores.TryGetValue(key, out var v) ? v : -1) + " " + keyed);
                    public static void Log(object o, int tag) => Console.WriteLine("log " + (o == null) + " " + tag);
                    public static void Cell(object o, int key, bool keyed, int tag) => Console.WriteLine("cell " + key + " " + ((Match)o).Cells[key] + " " + keyed);
                    public static void Read(object o) => Console.WriteLine("read");
                    public static void Wrong(object o, int key, bool keyed, int tag) { }
                }
                """);
            IMethodSymbol H(string name) => gen.GetTypeByMetadataName("Entries")!.GetMembers(name).OfType<IMethodSymbol>().Single();
            c.InstrumentEntryWrites(F("Scores"), H(WrongKey ? "Wrong" : "Score"));
            c.InstrumentEntryWrites(F("Log"), H("Log"), tag: 5);
            c.InstrumentEntryWrites(F("Cells"), H("Cell"));
            c.InstrumentEntryReads(F("Scores"), H("Read"));
        }
    }

    sealed class PathPlugin : ICompilerPlugin
    {
        public string? Problem;
        public int InterfaceVersion => 5;
        public string Name => "paths";
        public void Run(PluginContext c)
        {
            Problem = Check(c);
        }
        static string? Check(PluginContext c)
        {
            var world = c.Compilation.GetTypeByMetadataName("Game.World")!;
            var unit = c.Compilation.GetTypeByMetadataName("Game.Unit")!;
            static string Conds(IEnumerable<BranchCondition> cs) => string.Join(" ", cs.Select(x => x.Test.Syntax + ":" + x.WhenTrue));
            // an early return is a condition
            var tick = c.AccessesOf(world.GetMembers("Tick").Single()).Single(a => a.Method.Name == "Step");
            if (Conds(tick.Conditions) != "Paused:False") return "conditions of Tick++: " + Conds(tick.Conditions);
            // across calls: from the export through Step to Fire
            var hit = c.AccessesOf(unit.GetMembers("Hp").Single()).Single(a => a.Method.Name == "Fire" && a.Kind == AccessKind.ReadWrite);
            var paths = c.PathConditions(hit.Operation);
            // (Main calls Step twice: two paths)
            if (paths.Count != 2 || paths.Select(x => x.Calls[0].Call.Syntax.ToString()).Distinct().Count() != 2) return "paths to Fire's write: " + paths.Count;
            foreach (var p in paths) {
            if (p.Start != PathStart.Entry || p.Root.Name != "Main" || string.Join(">", p.Calls.Select(s => s.Callee.Name)) != "Step>Fire")
                return $"path: {p.Start} {p.Root.Name} {string.Join(">", p.Calls.Select(s => s.Callee.Name))}";
            if (Conds(p.Conditions) != "Paused:False input:True u.Hp <= 0:False") return "path conditions: " + Conds(p.Conditions);
            }
            // a virtual call is a call of the override
            var arenaHit = (IMethodSymbol)c.Compilation.GetTypeByMetadataName("Game.Arena")!.GetMembers("Hit").Single();
            if (!c.CallersOf(arenaHit).Any(s => s.Caller.Name == "Main" && s.Callee.Name == "Hit")) return "Arena.Hit has no caller";
            // aliasing
            var tree = c.Compilation.SyntaxTrees.Single();
            var model = c.Compilation.GetSemanticModel(tree);
            var make = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(m => m.Identifier.Text == "Make");
            var ret = make.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax>().Single();
            var hst = make.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalDeclarationStatementSyntax>().Single(s => s.ToString().StartsWith("int h"));
            IOperation L(SyntaxNode within, string name) => model.GetOperation(within.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>().First(i => i.Identifier.Text == name))!;
            IOperation New(string local) => model.GetOperation(make.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>().Single(v => v.Identifier.Text == local).Initializer!.Value)!;
            var (a, b, d, kept) = (L(ret, "a"), L(ret, "b"), L(ret, "d"), L(ret, "kept"));
            var (o, k) = (L(hst, "o"), L(hst, "k"));
            if (c.MayAlias(a, b)) return "a and b (two news) alias";
            if (!c.MayAlias(a, L(make.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax>().First(s => s.ToString().StartsWith("c.Hp")), "c"))) return "c = a does not alias a";
            if (!c.MayAlias(d, a) || !c.MayAlias(d, b)) return "d (a or b) does not alias both";
            if (c.AllocationSites(d)?.Count != 2) return "allocation sites of d: " + c.AllocationSites(d)?.Count;
            if (c.AllocationSites(k) != null) return "a static's value has allocation sites";
            if (!c.IsOwned(New("o"))) return "o is not owned";
            if (c.IsOwned(New("kept"))) return "kept (handed to Keep, which stores it) is owned";
            if (c.MayAlias(o, k)) return "an owned new and a static's value alias";
            if (!c.MayAlias(kept, k)) return "kept and the static it was stored in do not alias";
            var self = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax>().Single(x => x.ToString() == "n == p");
            if (c.MayAlias(model.GetOperation(self.Left)!, model.GetOperation(self.Right)!)) return "a new and a parameter's incoming value alias";
            return null;
        }
    }

    sealed class AfterPlugin : ICompilerPlugin
    {
        public bool OnJump;
        public int InterfaceVersion => 5;
        public string Name => "after";
        public void Run(PluginContext c)
        {
            var tree = c.Compilation.SyntaxTrees.Single();
            var model = c.Compilation.GetSemanticModel(tree);
            var statements = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>().ToList();
            IOperation S(string text) => model.GetOperation(statements.First(s => s.ToString().StartsWith(text)))!;
            var gen = c.AddSource("""
                using System;
                static class After { public static void Hook(int tag) => Console.WriteLine("after " + tag); }
                """);
            var hook = gen.GetTypeByMetadataName("After")!.GetMembers("Hook").OfType<IMethodSymbol>().Single();
            c.InstrumentAfter(S("if (i == 2)"), hook, 1);
            c.InstrumentAfter(S("foreach"), hook, 3);
            c.InstrumentAfter(S("Throw();"), hook, 4);
            c.InstrumentAfter(S("int a = await"), hook, 5);
            if (OnJump) c.InstrumentAfter(S("return 0;"), hook, 6);
        }
    }

    sealed class QuietPlugin : ICompilerPlugin
    {
        public int InterfaceVersion => 4;
        public string Name => "quiet";
        public void Run(PluginContext c)
        {
            var engine = c.Compilation.GetTypeByMetadataName("Game.Engine")!;
            IMethodSymbol E(string n) => engine.GetMembers(n).OfType<IMethodSymbol>().Single();
            int effects = c.SuppressibleImports([E("Play"), E("Spawn")]);
            c.AddSource($$"""
                using System;
                using System.Runtime.InteropServices;
                static class Quiet {
                    [UnmanagedCallersOnly(EntryPoint = "quiet_tick")]
                    static void Tick()
                    {
                        int before = Cs2Wasm.Effects.Suppressed;
                        Cs2Wasm.Effects.Suppressed = before | {{effects}};
                        try { Game.Script.Tick(); }
                        finally { Cs2Wasm.Effects.Suppressed = before; }
                        Console.WriteLine("quiet " + Game.Script.s_ticks + " " + Cs2Wasm.Effects.Suppressed);
                        Game.Script.Tick();
                    }
                }
                """);
        }
    }

    sealed class ThrowingPlugin : ICompilerPlugin
    {
        public int InterfaceVersion => 3;
        public string Name => "throws";
        public void Run(PluginContext c) => throw new InvalidOperationException("went wrong");
    }

    sealed class LaterPlugin : ICompilerPlugin
    {
        public int InterfaceVersion => PluginContext.InterfaceVersion + 1;
        public string Name => "later";
        public void Run(PluginContext c) { }
    }

    sealed class ErrorPlugin : ICompilerPlugin
    {
        public int InterfaceVersion => 3;
        public string Name => "error";
        public void Run(PluginContext c) => c.Report(Diagnostic.Create(new DiagnosticDescriptor("TP0002", "test", "refused", "test", DiagnosticSeverity.Error, true), Location.None));
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

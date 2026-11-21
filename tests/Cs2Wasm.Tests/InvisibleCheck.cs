using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Cs2Wasm;
using InvisibleApi;
using Microsoft.CodeAnalysis;

/// <summary>The Invisible API's first slice (docs/FINISH.md 9.4, samples/invisible-api): the game compiled by cs2wasm
/// with the plugin, played from the script by the driver, against .NET playing the same game without the plugin.
///  - the plugin's classification of every write (samples/invisible-api/expected-writes.txt), everything registered
///    before the compile and nothing compiled twice;
///  - the game's own output (its dumps) equal to .NET's: the plugin changes nothing the game does (the server's
///    override aside, which .NET is given as the input it replaced);
///  - each gate answer equal to whether the fire happened under .NET; each view equal to the private fields .NET has;
///  - each prediction and catch-up of the closed-form timer equal to running its ticks under .NET;
///  - after each input, every member or entry whose value changed under .NET marked (but for the members the plugin
///    calls unknown), and no tick marking anything;
///  - the inputs the plugin saw equal to the script's.</summary>
static class InvisibleCheck
{
    public static string? Run(string root, string outDir, bool checkedBuild)
    {
        var dir = Path.Combine(root, "samples", "invisible-api");
        var game = Path.Combine(dir, "Game", "Arena.cs");
        var comp = Frontend.CreateCompilation("arena", [(game, File.ReadAllText(game))]);
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errs.Count > 0) return "C# errors:\n" + string.Join("\n", errs);
        var plugin = new InvisiblePlugin();
        var r = Cs2WasmCompiler.Compile(comp, new CompileOptions { Plugins = [plugin], Checked = checkedBuild });
        if (plugin.Problem != null) return "plugin: " + plugin.Problem;
        if (!r.Success) return "cs2wasm failed:\n" + string.Join("\n", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        if (plugin.RunsBefore != 1 || plugin.RunsAfter != 1) return $"the plugin ran {plugin.RunsBefore} times before the compile and {plugin.RunsAfter} after";
        if (r.RecompiledFunctions != 0) return $"{r.RecompiledFunctions} functions compiled twice";

        // the classification
        var report = new StringBuilder();
        foreach (var w in plugin.Writes.OrderBy(w => w.Line).ThenBy(w => w.Member, StringComparer.Ordinal)) report.Append(w).Append('\n');
        foreach (var u in plugin.UnknownMembers.Distinct().Order(StringComparer.Ordinal)) report.Append("unknown: ").Append(u).Append('\n');
        report.Append("gate: ").Append(plugin.Gate).Append('\n');
        report.Append("input reads: ").Append(string.Join(", ", plugin.InputReads)).Append('\n');
        report.Append("timer: ").Append(plugin.Timer!.Pieces.Count).Append(" pieces, exact ").Append(plugin.Timer.Exact).Append('\n');
        var expectedFile = Path.Combine(dir, "expected-writes.txt");
        var expected = File.Exists(expectedFile) ? File.ReadAllText(expectedFile).Replace("\r\n", "\n") : "";
        if (report.ToString() != expected)
        {
            File.WriteAllText(Path.Combine(outDir, "invisible-writes.txt"), report.ToString());
            return "the classification differs from expected-writes.txt (written to tests/out/invisible-writes.txt):\n" + Diff(expected, report.ToString());
        }

        // the play
        var wasm = Path.Combine(outDir, "invisible_arena.wasm");
        File.WriteAllBytes(wasm, r.Module!);
        var script = Path.Combine(dir, "script.txt");
        var psi = new ProcessStartInfo("node", [Path.Combine(dir, "driver.mjs"), wasm, script]) { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        string got, err;
        using (var p = Process.Start(psi)!)
        {
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { p.Kill(); return "the driver timed out"; }
            got = o.Result.Replace("\r\n", "\n"); err = e.Result;
            if (p.ExitCode != 0) return $"the driver failed (exit {p.ExitCode}):\n{err}\n{got}";
        }
        File.WriteAllText(wasm + ".play.txt", got);
        var unknown = plugin.Writes.Where(w => w.Class == WriteClass.Unknown).Select(w => w.Member).ToHashSet();
        var want = Oracle.Play(comp, File.ReadAllLines(script));
        File.WriteAllText(wasm + ".oracle.txt", string.Join("\n", want) + "\n");
        var lines = got.TrimEnd('\n').Split('\n');
        for (int i = 0; i < Math.Max(lines.Length, want.Count); i++)
        {
            var a = i < lines.Length ? lines[i] : "<missing>";
            var b = i < want.Count ? want[i] : "<missing>";
            if (b.StartsWith("changes: ") && a.StartsWith("changes: "))
            {
                // what changed under .NET (but for the unknown members) is marked
                static HashSet<string> Set(string l) => l[9..].Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var marked = Set(a);
                var missing = Set(b).Where(c => !marked.Contains(c) && !unknown.Contains(c.Split(' ', '[')[0])).ToList();
                if (missing.Count > 0) return $"line {i + 1}: changed under .NET and not marked: {string.Join(", ", missing)}\n  marked: {a}\n  {Context(lines, i)}";
                continue;
            }
            if (a != b) return $"line {i + 1}: expected «{b}»\n  actual «{a}»\n  {Context(lines, i)}";
        }
        return null;
    }

    static string Context(string[] lines, int i) => string.Join("\n  ", lines.Skip(Math.Max(0, i - 6)).Take(7));

    static string Diff(string expected, string actual)
    {
        var a = expected.Split('\n'); var b = actual.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0, shown = 0; i < Math.Max(a.Length, b.Length) && shown < 30; i++)
        {
            var x = i < a.Length ? a[i] : "<missing>"; var y = i < b.Length ? b[i] : "<missing>";
            if (x != y) { sb.AppendLine($"line {i + 1}: expected «{x}»\n           actual   «{y}»"); shown++; }
        }
        return sb.ToString();
    }

    /// <summary>.NET plays the script on the game compiled without the plugin, and writes what the driver should
    /// print: the game's own output as it is; what the plugin's server code prints computed from the game's private
    /// state by reflection, by running the ticks (the predictions) and by comparing the state before and after each
    /// input (the changes: every member and entry whose value differs).</summary>
    static class Oracle
    {
        public static unsafe List<string> Play(Compilation comp, string[] script)
        {
            using var ms = new MemoryStream();
            var emit = comp.Emit(ms);
            if (!emit.Success) throw new Exception(string.Join("\n", emit.Diagnostics));
            ms.Position = 0;
            var alc = new AssemblyLoadContext("invisible-oracle", isCollectible: true);
            var oldOut = Console.Out;
            var outText = new StringWriter { NewLine = "\n" };
            try
            {
                var asm = alc.LoadFromStream(ms);
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var gameT = asm.GetType("Arena.Game")!; var worldT = asm.GetType("Arena.World")!; var playerT = asm.GetType("Arena.Player")!; var spawnerT = asm.GetType("Arena.Spawner")!;
                IntPtr Export(string name) => gameT.GetMethods(All).Single(m => m.GetCustomAttributesData().Any(a => a.NamedArguments.Any(n => (string?)n.TypedValue.Value == name))).MethodHandle.GetFunctionPointer();
                var join = (delegate* unmanaged<int, void>)Export("join");
                var input = (delegate* unmanaged<int, int, void>)Export("input");
                var tick = (delegate* unmanaged<void>)Export("tick");
                var dump = (delegate* unmanaged<void>)Export("dump");
                var world = gameT.GetField("s_world", All)!.GetValue(null)!;
                object F(object o, string f) => o.GetType().GetField(f, All)!.GetValue(o)!;
                var players = (System.Collections.IDictionary)F(world, "_players");
                var coins = (System.Collections.IDictionary)F(world, "_coins");
                var owner = (int[])F(world, "_owner");
                var spawner = F(world, "_spawner");
                var tickMs = (int)worldT.GetField("TickMs")!.GetValue(null)!;
                var spawnerTick = spawnerT.GetMethod("Tick")!;
                var describe = spawnerT.GetMethod("Describe")!;
                var playerFields = playerT.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                // every value the server may have to send, by the names the plugin's marks use
                Dictionary<string, string> Snapshot()
                {
                    var s = new Dictionary<string, string>();
                    foreach (System.Collections.DictionaryEntry e in players)
                    {
                        s["World._players[" + e.Key + "]"] = "present";
                        foreach (var f in playerFields) s[$"Player.{f.Name} p{((int)e.Key)}"] = f.GetValue(e.Value)!.ToString()!;
                    }
                    foreach (System.Collections.DictionaryEntry e in coins) s["World._coins[" + e.Key + "]"] = e.Value!.ToString()!;
                    for (int i = 0; i < owner.Length; i++) s["World._owner[" + i + "]"] = owner[i].ToString();
                    foreach (var f in spawnerT.GetFields(All)) s["Spawner." + f.Name] = f.GetValue(spawner)!.ToString()!;
                    s["World._tick"] = F(world, "_tick").ToString()!;
                    return s;
                }
                List<string> Changes(Dictionary<string, string> before, Dictionary<string, string> after)
                {
                    var newPlayers = after.Keys.Where(k => k.StartsWith("World._players[") && !before.ContainsKey(k)).Select(k => " p" + k[15..^1]).ToList();
                    return after.Where(kv => !(before.TryGetValue(kv.Key, out var v) && v == kv.Value))
                        // (a new player's fields are part of the new object: its entry is the change)
                        .Where(kv => !(kv.Key.StartsWith("Player.") && newPlayers.Any(np => kv.Key.EndsWith(np))))
                        .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                }

                Console.SetOut(outText);
                var lines = new List<string>();
                void Flush() { var t = outText.ToString(); outText.GetStringBuilder().Clear(); if (t.Length > 0) lines.AddRange(t.TrimEnd('\n').Split('\n')); }
                int overrideAction = -1;
                foreach (var raw in script)
                {
                    var line = (raw.Contains('#') ? raw[..raw.IndexOf('#')] : raw).Trim();
                    if (line.Length == 0) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var n = parts.Skip(1).Select(int.Parse).ToArray();
                    lines.Add("> " + line);
                    switch (parts[0])
                    {
                        case "join":
                        {
                            var before = Snapshot();
                            join(n[0]); Flush();
                            lines.Add("changes: " + string.Join(", ", Changes(before, Snapshot())));
                            lines.Add("inputs: join.id=" + n[0]);
                            break;
                        }
                        case "input":
                        {
                            int action = overrideAction >= 0 ? overrideAction : n[1];
                            overrideAction = -1;
                            var before = Snapshot();
                            string shotsKey = $"Player._shots p{n[0]}";
                            int at = lines.Count;
                            input(n[0], action); Flush();
                            var after = Snapshot();
                            // the gate's answer is whether the fire happened
                            if ((n[1] & 16) != 0) lines.Insert(at, "gate " + (before.GetValueOrDefault(shotsKey) != after.GetValueOrDefault(shotsKey) ? 1 : 0));
                            lines.Add("changes: " + string.Join(", ", Changes(before, after)));
                            lines.Add($"inputs: input.id={n[0]}, input.action={action}");
                            break;
                        }
                        case "tick":
                            for (int k = 0; k < (n.Length > 0 ? n[0] : 1); k++) { tick(); Flush(); }
                            break;
                        case "dump": dump(); Flush(); break;
                        case "view":
                        {
                            if (!players.Contains(n[0])) { lines.Add($"view p{n[0]}: none"); break; }
                            var p = players[n[0]]!;
                            lines.Add($"view p{n[0]}: {F(p, "_x")},{F(p, "_y")} v {F(p, "_vx")},{F(p, "_vy")} hp {F(p, "_hp")} cd {F(p, "_cooldown")} shots {F(p, "_shots")} coins {coins[n[0]]}");
                            break;
                        }
                        case "predict":
                        {
                            var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(spawner, null)!;
                            for (int k = 0; k < n[0]; k++) spawnerTick.Invoke(copy, [tickMs]);
                            lines.Add($"predict {n[0]}: {describe.Invoke(copy, null)}");
                            break;
                        }
                        case "advance":
                            for (int k = 0; k < n[0]; k++) spawnerTick.Invoke(spawner, [tickMs]);
                            break;
                        case "override": overrideAction = n[0]; break;
                        default: throw new Exception("unknown script line: " + line);
                    }
                }
                return lines;
            }
            finally { Console.SetOut(oldOut); alc.Unload(); }
        }
    }
}

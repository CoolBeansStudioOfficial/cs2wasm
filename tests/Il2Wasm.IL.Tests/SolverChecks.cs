using Il2Wasm.IL.Lower;
using Il2Wasm.IL.Memory;
using Il2Wasm.IL.Types;

namespace Il2Wasm.IL.Tests;

/// <summary>Memory/Solver.cs's release effects on the IL CoreLib (needs the framework; skipped without it):
/// <list type="bullet">
/// <item>ObjectMarks' [ReleasesDependents] contract: dependents' releases count only once a dependent handle can be
/// made. A program without one leaves them out from round 1 on. A program with a ConditionalWeakTable keeps them
/// (object-typed), and so does round 0 of any program.</item>
/// <item>The check behind the release intrinsics' effects (ProgramSolution.ReleaseViolations) holds on every body
/// either program reaches.</item>
/// </list></summary>
static class SolverChecks
{
    const string Plain = """
        class P { static void Main() { System.Console.WriteLine(42); } }
        """;
    const string Dependents = """
        using System.Runtime.CompilerServices;
        class Key { }
        class P
        {
            static readonly ConditionalWeakTable<Key, object> s_t = new();
            static void Main() { var k = new Key(); s_t.Add(k, new object()); System.Console.WriteLine(s_t.TryGetValue(k, out var v) && v != null); }
        }
        """;

    public static void Run(Checks c)
    {
        var dir = EndToEnd.FrameworkDir();
        c.Section("solver: release effects on the IL CoreLib");
        if (dir == null) { c.Info("no artifacts/framework (node tools/build-corelib.mjs): skipped"); return; }
        foreach (var (name, source, hasDependents) in new[] { ("solver_plain", Plain, false), ("solver_dependents", Dependents, true) })
        {
            var refs = Directory.GetFiles(dir, "*.dll");
            var dll = Build.Compile(name, [source], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
            var ctx = new IlContext();
            ctx.AddAssemblies(refs.Append(dll));
            ctx.SetCoreLib();
            var entry = ctx.Load(name).EntryPoint!;
            var comp = new IlCompiler(ctx);
            // (two solves of one program: round 0, then a round that can rely on what round 0 reached)
            ProgramSolution? first = null, second = null;
            for (int round = 0; round < 2; round++)
            {
                var solver = new Solver(comp);
                solver.Root(entry);
                solver.Reach();
                var sol = solver.Solve();
                if (round == 0) first = sol; else second = sol;
                if (round == 1)
                {
                    int bodies = 0, bad = 0;
                    string? example = null;
                    foreach (var m in sol.Methods.Items)
                        if (sol.BodyOf(m) is { } b)
                        {
                            bodies++;
                            var v = ProgramSolution.ReleaseViolations(b, !sol.DependentsReleased);
                            if (v.Count > 0) { bad++; example ??= m + ": " + v[0]; }
                        }
                    c.True($"{name}: every Rt.Release / ReleaseElements operand is a released type ({bodies} bodies){(example != null ? " first: " + example : "")}", bad == 0 && bodies > 100);
                }
            }
            if (!hasDependents)
            {
                // a dependent handle's setter imported late (lowering reached it, the reach phase did not) after a solve
                // that left the dependents' releases out: no error, another round, whose solve counts them; a hard
                // error only if that round's module were the one encoded
                var solver = new Solver(comp);
                solver.Root(entry);
                solver.Reach();
                var sol = solver.Solve();
                c.True($"{name}: a further round still leaves them out", !sol.DependentsReleased);
                var ws = (Internal.TypeSystem.MetadataType)ctx.SystemModule.GetType("Il2Wasm.Runtime"u8, "WeakSlots"u8);
                var link = ws.GetMethods().Single(m => m.GetName() == "LinkDependent");
                bool threw = false;
                try { solver.Late(new ImportedMethod(link)); } catch (InvalidOperationException) { threw = true; }
                c.True($"{name}: the late setter does not fail the compile; the round asks for another", !threw && solver.NeedsAnotherRound);
                bool encodable = true;
                try { solver.CheckEncodable(); } catch (InvalidOperationException) { encodable = false; }
                c.True($"{name}: that round's module may not be encoded (a hard error if it were the last)", !encodable);
                var again = new Solver(comp);
                again.Root(entry);
                again.Reach();
                c.True($"{name}: the next round counts the dependents' releases", again.Solve().DependentsReleased);
            }
            c.True($"{name}: round 0 counts the dependents' releases", first!.DependentsReleased);
            c.Eq($"{name}: round 1 counts them {(hasDependents ? "(a dependent handle can be made)" : "not (none can)")}", second!.DependentsReleased, hasDependents);
        }
    }
}

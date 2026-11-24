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

    const string Comparers = """
        class P { static void Main() { System.Console.WriteLine(System.Collections.Generic.Comparer<int>.Default.Compare(1, 2)); } }
        """;

    // (an element store, read back through a span: the read's address is not one the solve knows, a Slot key)
    const string SpanRead = """
        using System;
        using System.Runtime.CompilerServices;
        abstract class A { public abstract int F(); }
        sealed class B : A { public override int F() => 2; }
        sealed class C : A { public override int F() => 3; }
        static class P
        {
            [MethodImpl(MethodImplOptions.NoInlining)] static int Read(Span<A> s) => s[0].F();
            static void Main() { var a = new A[1]; a[0] = new C(); Console.WriteLine(Read(a) + new B().F()); }
        }
        """;

    // (a store of a reference through an address made from a pointer: Put without a contract, PutElement with
    // [RawStores("elements")])
    const string RawWrites = """
        using System;
        using System.Runtime.CompilerServices;
        sealed class Marker { public override string ToString() => "m"; }
        sealed class Other { public override string ToString() => "o"; }
        static unsafe class P
        {
            [MethodImpl(MethodImplOptions.NoInlining)] static void Put(ref Marker slot, Marker v) => Unsafe.AsRef<Marker>(Unsafe.AsPointer(ref slot)) = v;
            [Il2Wasm.Runtime.RawStores("elements")]
            [MethodImpl(MethodImplOptions.NoInlining)] static void PutElement(ref Other slot, Other v) => Unsafe.AsRef<Other>(Unsafe.AsPointer(ref slot)) = v;
            static void Main()
            {
                var a = new Marker[1];
                var b = new Other[1];
                Put(ref a[0], new Marker());
                PutElement(ref b[0], new Other());
                Console.WriteLine(a[0] + "" + b[0]);
            }
        }
        """;

    // (G's address escapes into a span; F's does not: a store through the span is a Slot(object) store)
    const string FieldEscape = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        sealed class Holder { public object F; public object G; }
        sealed class Marker { public override string ToString() => "m"; }
        sealed class Other { public override string ToString() => "o"; }
        static class P
        {
            [MethodImpl(MethodImplOptions.NoInlining)] static object ReadF(Holder h) => h.F;
            [MethodImpl(MethodImplOptions.NoInlining)] static object ReadG(Holder h) => h.G;
            static void Main()
            {
                var h = new Holder { F = new Other(), G = new Other() };
                Span<object> s = MemoryMarshal.CreateSpan(ref h.G, 1);
                s[0] = new Marker();
                Console.WriteLine(ReadF(h) + "" + ReadG(h));
            }
        }
        """;

    // (EventSource.IsEnabled(), an ILLink stub: ArrayPool's Rent and Return test it)
    const string Stubs = """
        sealed class Log : System.Diagnostics.Tracing.EventSource { public static readonly Log Shared = new(); }
        class P { static void Main() { System.Console.WriteLine(Log.Shared.IsEnabled()); } }
        """;

    static void MetadataTypeOf(ProgramSolution sol, string name, out Internal.TypeSystem.TypeDesc? t) =>
        t = sol.Methods.Items.Select(m => m.OwningType).FirstOrDefault(x => x is Internal.TypeSystem.MetadataType mt && mt.GetName() == name)
            ?? sol.Constructed.FirstOrDefault(x => x is Internal.TypeSystem.MetadataType mt && mt.GetName() == name);

    /// <summary>The program's solution from its second solve (the first with type sets, Solver.Solve).</summary>
    static ProgramSolution SecondSolve(string dir, string name, string source)
    {
        var refs = Directory.GetFiles(dir, "*.dll");
        var dll = Build.Compile(name, [source], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
        var ctx = new IlContext();
        ctx.AddAssemblies(refs.Append(dll));
        ctx.SetCoreLib();
        var entry = ctx.Load(name).EntryPoint!;
        var comp = new IlCompiler(ctx);
        ProgramSolution? sol = null;
        for (int round = 0; round < 2; round++)
        {
            var solver = new Solver(comp);
            solver.Root(entry);
            solver.Reach();
            sol = solver.Solve();
        }
        return sol!;
    }

    public static void Run(Checks c)
    {
        var dir = EndToEnd.FrameworkDir();
        c.Section("solver: release effects on the IL CoreLib");
        if (dir == null) { c.Info("no artifacts/framework (node tools/build-corelib.mjs): skipped"); return; }
        {
            // Generics.Implements/IsNullable/IsEnum fold in the constant propagation as lowering decides them
            // (PureCalls.GenericsTest): Comparer<int>.CreateComparer's branches for enums, nullables and other types go,
            // and with them the comparers only they construct (IL2WASM_NO_GENERICS_FOLD=1 brings them back)
            var refs = Directory.GetFiles(dir, "*.dll");
            var dll = Build.Compile("solver_comparers", [Comparers], refs.Where(r => !Path.GetFileName(r).StartsWith("System.Private.CoreLib")).Append(Path.Combine(dir, "System.Private.CoreLib.dll")).ToArray());
            var ctx = new IlContext();
            ctx.AddAssemblies(refs.Append(dll));
            ctx.SetCoreLib();
            var solver = new Solver(new IlCompiler(ctx));
            solver.Root(ctx.Load("solver_comparers").EntryPoint!);
            solver.Reach();
            var made = solver.Solve().Constructed.Select(t => t.ToString()!).ToList();
            c.True("Comparer<int>.Default: GenericComparer<int> constructed", made.Any(t => t.Contains("GenericComparer`1<int32>")));
            c.True("Comparer<int>.Default: the dead branches' ObjectComparer<int> and EnumComparer<int> are not", !made.Any(t => t.Contains("ObjectComparer`1<int32>") || t.Contains("EnumComparer`1<int32>")));
        }
        {
            // TypeSets (MEMORY-IR 2.6): a store into an array's element reaches a read through a reference the solve
            // cannot place (a span's element, Slot(A)): the virtual call on what it read targets C.F
            var sol = SecondSolve(dir, "typesets_span", SpanRead);
            var read = sol.Methods.Items.First(m => m.GetName() == "Read" && m.OwningType.ToString()!.EndsWith("P"));
            var call = sol.BodyOf(read)?.Blocks.SelectMany(b => b.Code).FirstOrDefault(n => n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is Internal.TypeSystem.MethodDesc f && f.GetName() == "F");
            var targets = call == null ? null : sol.CallTargetsOf(read, call.Id);
            c.True("type sets: an element store reaches a read through a span (the call's targets: " + (targets == null ? "unsolved" : string.Join(", ", targets)) + ")",
                sol.TypeSetsSolved && call != null && (targets == null || targets.Any(t => t.OwningType.ToString()!.EndsWith("C"))));
        }
        {
            // the raw-write contracts (TypeSets.RawStoresOf, the trust boundary in docs/AUDIT.md): a raw store of a
            // reference without one still lands in Slot(its type), the slots every reference the solve cannot place
            // reads (for object: Slot(object), which every field read of a reference type joins); with "elements" it
            // lands in the array elements' key instead (the keys' own sets: KeyTypes)
            var sol = SecondSolve(dir, "typesets_raw", RawWrites);
            MetadataTypeOf(sol, "Marker", out var marker); MetadataTypeOf(sol, "Other", out var other);
            List<string>? Of(MemKey k) => sol.KeyTypes(k)?.Select(t => t.ToString()!).ToList();
            string Show(List<string>? l) => l == null ? "unknown" : "{" + string.Join(", ", l) + "}";
            var slotMarker = Of(MemKey.Slot(marker!)); var slotOther = Of(MemKey.Slot(other!)); var elemOther = Of(MemKey.Elem(other!));
            c.True("type sets: an unannotated raw store of a reference lands in Slot(its type): Slot(Marker) " + Show(slotMarker),
                sol.TypeSetsSolved && slotMarker != null && slotMarker.Any(t => t.EndsWith("Marker")));
            c.True("type sets: under [RawStores(\"elements\")] it lands in Elem(its type): Elem(Other) " + Show(elemOther) + ", Slot(Other) " + Show(slotOther),
                sol.TypeSetsSolved && elemOther != null && elemOther.Any(t => t.EndsWith("Other")) && slotOther != null && !slotOther.Any(t => t.EndsWith("Other")));
        }
        {
            // a field's read sees the Slot keys' stores only once its address escaped (TypeSets.EscapeKeys; before, every
            // reference field's read joined Slot(object), which in every program on the CoreLib is unknown)
            var sol = SecondSolve(dir, "typesets_escape", FieldEscape);
            List<string>? ReadOf(string method, string field)
            {
                var m = sol.Methods.Items.First(x => x.GetName() == method && x.OwningType.ToString()!.EndsWith("P"));
                var ld = sol.BodyOf(m)?.Blocks.SelectMany(b => b.Code).FirstOrDefault(n => n.Op == Il2Wasm.IL.Ir.Opc.LdFld && n.X is Internal.TypeSystem.FieldDesc f && f.GetName() == field);
                return ld == null ? ["(no load)"] : sol.ExactTypes(m, ld)?.Select(t => t.ToString()!).ToList();
            }
            string Show(List<string>? l) => l == null ? "unknown" : "{" + string.Join(", ", l) + "}";
            var f = ReadOf("ReadF", "F"); var g = ReadOf("ReadG", "G");
            c.True("type sets: a field whose address does not escape is read as what is stored in it: h.F " + Show(f),
                sol.TypeSetsSolved && f != null && f.Count == 1 && f[0].EndsWith("Other"));
            c.True("type sets: one whose address escaped into a span sees the span's store: h.G " + Show(g),
                sol.TypeSetsSolved && (g == null || g.Any(t => t.EndsWith("Marker"))));
        }
        {
            // an ILLink substitution's stub is in the solution with no effects (IL2WASM_NO_STUB_SUMMARIES=1: out of it, a
            // call of it is one the solve cannot see, Wild)
            var sol = SecondSolve(dir, "solver_stubs", Stubs);
            var ie = sol.Methods.Items.FirstOrDefault(m => m.GetName() == "IsEnabled" && m.Signature.Length == 0 && m.OwningType is Internal.TypeSystem.MetadataType et && et.GetName() == "EventSource");
            c.True("solver: EventSource.IsEnabled(), an ILLink stub, is in the solution with no effects (" + (ie == null ? "not reached: " + string.Join(", ", sol.Methods.Items.Where(m => m.ToString()!.Contains("EventSource") || m.ToString()!.Contains("SharedArrayPool")).Select(m => m.ToString()).Take(6)) : "covered " + sol.Covers(ie) + ", Mods " + sol.Facts(ie).Mods) + ")", ie != null && sol.Covers(ie) && !sol.Facts(ie).Mods.Wild && sol.Facts(ie).Mods.IsEmpty);
        }
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
                    if (hasDependents)
                    {
                        // (a mark can be set: every destroy calls ObjectMarks.OnDestroy, generated code no IR edge shows;
                        // it is rooted, so a destroy's effects are its facts, not Wild for a method outside the solution.
                        // IL2WASM_NO_MARKS_ROOTS=1: not rooted)
                        var str = ctx.SystemModule.Context.GetWellKnownType(Internal.TypeSystem.WellKnownType.String);
                        c.True($"{name}: ObjectMarks.OnDestroy is in the solution once a mark can be set", sol.Methods.Items.Any(m => m.GetName() == "OnDestroy" && m.OwningType is Internal.TypeSystem.MetadataType om && om.GetName() == "ObjectMarks"));
                        var od = sol.Methods.Items.FirstOrDefault(m => m.GetName() == "OnDestroy" && m.OwningType is Internal.TypeSystem.MetadataType om2 && om2.GetName() == "ObjectMarks");
                        var odf = od == null ? null : sol.Facts(od);
                        c.True($"{name}: a string's destroy has OnDestroy's effects (Wild only if they are)", odf != null && sol.DestroyEffects(str).Wild == (odf.Mods.Wild || odf.ReleaseEffects.Wild));
                    }
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

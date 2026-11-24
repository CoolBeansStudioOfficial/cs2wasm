using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Lower;
using Il2Wasm.IL.Memory;
using Il2Wasm.IL.Types;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Tests;

/// <summary>Memory/Borrows.cs (docs/MEMORY-IR.md 4.6): which loads stay Guaranteed and which flip, on a program
/// reached and solved by the real reach phase (Solver over IlCompiler), each body analysed twice: with the naive
/// plan's Retain and Release nodes in it, and with them taken out again (the planner's view: releases implied by last
/// uses). Each rule's check fails when the rule is broken on purpose: IL2WASM_SPEC_BORROWS_BREAK (proof 1 ignores
/// the body's stores), IL2WASM_INIT_ONLY_BREAK, IL2WASM_SELF_STORES_BREAK; and when it is switched off:
/// IL2WASM_NO_INIT_ONLY, IL2WASM_NO_SELF_STORES, IL2WASM_NO_LOCAL_BORROWS, IL2WASM_NO_STATIC_BORROWS...</summary>
static class BorrowChecks
{
    const string Source = """
        using System;
        public delegate void Act();
        public class Node
        {
            public Node Next; public object Val; public readonly object Ro;
            public Node(object r) { Ro = r; }
        }
        public static class B
        {
            static object s_o;
            static int s_n;
            static void Use(object o) { s_n++; }
            static void Clobber(Node n) { n.Val = null; }
            static Node Make() => new Node(null);
            public static object Field(Node n) { var v = n.Val; Use(v); return v; }
            public static object AcrossCall(Node n) { var v = n.Val; Clobber(n); Use(v); return v; }
            public static object AcrossStore(Node n, object w) { var v = n.Val; n.Val = w; Use(v); return null; }
            public static object Nested(Node n) { var v = n.Next.Next.Val; Use(v); return v; }
            public static object TooLong(Node n) { var v = n.Next.Next.Next.Val; Use(v); return v; }
            public static int Loop(Node h) { int c = 0; for (var n = h; n != null; n = n.Next) { Use(n.Val); c++; } return c; }
            public static int LoopFromField(Node h) { int c = 0; for (var n = h.Next; n != null; n = n.Next) { Use(n.Val); c++; } return c; }
            public static int LoopWrite(Node h) { int c = 0; for (var n = h; n != null; n = n.Next) { n.Val = null; Use(n); c++; } return c; }
            public static object ReadOnly(Node n, Act a) { var v = n.Ro; a(); Use(v); return v; }
            public static object Wild(Node n, Act a) { var v = n.Val; a(); Use(v); return v; }
            public static object Static() { var v = s_o; Use(v); return v; }
            public static object StaticStore(object w) { var v = s_o; s_o = w; Use(v); return v; }
            public static object SelfStore(Node n, Node m) { var v = n.Val; m.Val = v; Use(v); return v; }
            public static object Elem(object[] a) { var v = a[0]; Use(v); return v; }
            public static object ElemCovariant(object[] a, string[] s, string w) { var v = a[0]; s[1] = w; Use(v); return v; }
            public static object RootDies() { var v = Make().Val; Use(v); return v; }
            public static object RootLives() { var m = Make(); var v = m.Val; Use(v); Use(m); return m; }
            public static object Cast(Node n) { var v = (string)n.Val; Use(v); return v; }
        }
        public class Program
        {
            public static void Main()
            {
                var n = new Node("r"); n.Next = n; var a = new object[2]; Act act = () => { };
                B.Field(n); B.AcrossCall(n); B.AcrossStore(n, "w"); B.Nested(n); B.TooLong(n); B.Loop(n); B.LoopFromField(n); B.LoopWrite(n);
                B.ReadOnly(n, act); B.Wild(n, act); B.Static(); B.StaticStore("x"); B.SelfStore(n, n); B.Elem(a); B.ElemCovariant(a, new string[2], "s");
                B.RootDies(); B.RootLives(); B.Cast(n);
            }
        }
        """;

    public static void Run(Checks c)
    {
        var lib = Build.CoreLib();
        var prog = Build.Program("borrows", Source);
        var ctx = new IlContext();
        ctx.AddAssemblies([lib, prog]);
        ctx.SetCoreLib();
        var mod = ctx.Load("borrows");
        var comp = new IlCompiler(ctx);
        var solver = new Solver(comp);
        solver.Root(mod.EntryPoint!);
        solver.Reach();
        var sol = solver.Solve();
        var type = (MetadataType)mod.GetType(""u8, "B"u8);

        foreach (bool planned in new[] { true, false })
        {
            c.Section("borrows (" + (planned ? "naive plan's counts in the body" : "no counts in the body: implied releases") + ")");
            // the loads of a method (field, element, static, variable reads of references), with their facts
            var analysed = new Dictionary<string, (Body, BorrowFacts)>();
            List<(Node Load, OwnFact Own, bool Flipped)> Loads(string name, Opc? phi = null)
            {
                if (!analysed.TryGetValue(name, out var bf))
                {
                    var m = type.GetMethods().Single(x => x.GetName() == name);
                    var u = solver.Take(m) ?? throw new Exception(name + " not reached");
                    var b0 = u.Body!;
                    var info = Ownership.Run(b0);
                    if (!planned)
                        foreach (var b in b0.Blocks) { b.Code.RemoveAll(n => n.Op is Opc.Retain or Opc.Release); foreach (var n in b.Code) n.Moves = 0; }
                    analysed[name] = bf = (b0, Borrows.Analyze(new PlanContext(m, b0, sol, Variant.Entry), info));
                }
                var (body, f) = bf;
                var ops = phi is { } p ? new[] { p } : new[] { Opc.LdFld, Opc.LdElem, Opc.LdSFld, Opc.VarLd };
                return body.Blocks.SelectMany(b => b.Phis.Concat(b.Code)).Where(n => n.Kind == VK.Ref && ops.Contains(n.Op)).Select(n => (n, f.Own[n], f.Flipped[n])).ToList();
            }
            bool AllBorrowed(string name, Opc? op = null) { var l = Loads(name, op); return l.Count > 0 && l.All(x => x.Own.Kind == OwnKind.Guaranteed); }
            bool AnyFlipped(string name) => Loads(name).Any(x => x.Flipped);
            // (each method's body is taken once from the solver: re-reach for the second pass)
            if (!planned) { solver = new Solver(comp); solver.Root(mod.EntryPoint!); solver.Reach(); sol = solver.Solve(); }

            c.True("a field read used by a call that writes nothing: borrowed", AllBorrowed("Field"));
            c.True("a call that writes the field: the borrow flips", AnyFlipped("AcrossCall"));
            c.True("the body's own store to the field: flips (IL2WASM_SPEC_BORROWS_BREAK breaks this)", AnyFlipped("AcrossStore"));
            c.True("n.Next.Next.Val (3 steps): borrowed", AllBorrowed("Nested"));
            c.True("four steps: generic, Owned without a path", Loads("TooLong").Any(x => x.Own.Kind == OwnKind.Owned && !x.Flipped));
            c.True("a loop over n = n.Next: the phi is borrowed (IL2WASM_NO_LOCAL_BORROWS turns it off)", AllBorrowed("Loop", Opc.Phi));
            c.True("the loop's reads of n.Next and n.Val: borrowed", AllBorrowed("Loop"));
            c.True("a loop from a field (h.Next): the phi is borrowed", AllBorrowed("LoopFromField", Opc.Phi));
            c.True("a loop that stores n.Val: n.Next's loop still holds, n.Val's read is gone", AllBorrowed("LoopWrite", Opc.Phi));
            c.True("a readonly field across an unknown call: borrowed (init-only; IL2WASM_NO_INIT_ONLY flips it)", AllBorrowed("ReadOnly"));
            c.True("a plain field across an unknown call: flips (IL2WASM_INIT_ONLY_BREAK breaks this)", AnyFlipped("Wild"));
            c.True("a static read: borrowed", AllBorrowed("Static"));
            c.True("a static stored while borrowed: flips", AnyFlipped("StaticStore"));
            c.True("a store of the borrowed value itself: still borrowed (IL2WASM_NO_SELF_STORES flips it)", AllBorrowed("SelfStore"));
            c.True("an element read: borrowed", AllBorrowed("Elem"));
            c.True("a store into a string[] while an object[] element is borrowed: flips (array covariance)", AnyFlipped("ElemCovariant"));
            c.True("a field of a call's result that dies at once: flips (the root dies)", AnyFlipped("RootDies"));
            c.True("a field of a value live over the borrow: borrowed", AllBorrowed("RootLives"));
            c.True("a cast of a borrowed field: borrowed with it", AllBorrowed("Cast"));
        }
    }
}

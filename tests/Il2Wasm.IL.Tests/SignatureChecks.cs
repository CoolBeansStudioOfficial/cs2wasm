using Il2Wasm.IL.Memory;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Tests;

/// <summary>Memory/Signatures.cs (docs/MEMORY-IR.md 4.1-4.4) on a small program over the stub CoreLib: a stored
/// parameter and one passed on to a store are HandOver (Stores); one dead before a loop is HandOver (KeepsRunning);
/// one used after the further call, or only before a throw helper, is Lend; getters and `return this` are borrowed
/// returns; the owned variants follow the size caps. The call rule: a tree walk's root, an Owned value at its last
/// use, goes to the owned variant; the walk's own calls on its fields' loads (borrows, not the caller's counts) go to
/// the Entry, in the Entry and in the owned variant alike. Runs only with IL2WASM_MEM_PLAN=1 (Compute runs only then).</summary>
static class SignatureChecks
{
    const string Source = """
        using System;
        public sealed class Node
        {
            public Node Next; public int X;
            public Node Get() => Next;
            public Node Self() => this;
        }
        public sealed class T { public T L, R; public int V; }
        public static class S
        {
            public static long W(T n) => n == null ? 0 : n.V + W(n.L) + W(n.R);
            static Node s_held;
            public static void Keep(Node n) { s_held = n; }
            public static void Pass(Node n) { Keep(n); }
            public static int Peek(Node n) => n.X;
            public static int Spin(Node n) { int v = n.X; int s = 0; for (int i = 0; i < v; i++) s += Work(i); return s; }
            static int Work(int k) { int s = 0; for (int i = 0; i < k; i++) s += i; return s; }
            public static int Late(Node n) { int a = Work(3); return a + n.X; }
            public static int Guard(Node n) { if (n.X < 0) Fail(); return n.X; }
            static void Fail() => throw new Exception();
            public static void Main()
            {
                var n = new Node { X = 3 };
                n.Next = new Node();
                Pass(n);
                Console.WriteLine(Peek(n) + Spin(n) + Late(n) + Guard(n));
                Console.WriteLine(n.Get() != null);
                Console.WriteLine(n.Self().X);
                var t = new T { V = 1 };
                t.L = new T { V = 2 };
                Console.WriteLine(W(t));
            }
        }
        """;

    public static void Run(Checks c)
    {
        c.Section("memory: ownership signatures (Signatures.cs)");
        if (!Pipeline.Planned) { c.Info("needs IL2WASM_MEM_PLAN=1: skipped"); return; }
        ProgramSolution? sol = null;
        Signatures.Observed = s => sol = s;
        // (the last plan of Main and of each of W's entries: the variants their calls of W take)
        var callees = new Dictionary<string, string>();
        Plan.Built = (ctx, plan) =>
        {
            if (ctx.Method.OwningType is not MetadataType ot || ot.GetName() != "S" || ctx.Method.GetName() is not ("Main" or "W")) return;
            var calls = ctx.Body.Blocks.SelectMany(b => b.Code).Where(n => n.Op == Il2Wasm.IL.Ir.Opc.Call && n.X is MethodDesc cm && cm.GetName() == "W");
            lock (callees) callees[ctx.Method.GetName() + ctx.Entry.Suffix] = string.Join(" ", calls.Select(n => plan.Callees[n].Suffix is { Length: > 0 } s ? s : "entry"));
        };
        try { EndToEnd.Compile("sigs", Source); }
        finally { Signatures.Observed = null; Plan.Built = null; }
        if (sol == null) { c.True("Compute ran", false); return; }
        MethodDesc M(string type, string name) => sol.Methods.Items.First(m => m.OwningType is MetadataType t && t.GetName() == type && m.GetName() == name);
        string Sig(string type, string name) { var d = Signatures.Demand(sol, M(type, name)); return string.Join(",", d.Params.Select((p, i) => p == ParamConv.HandOver ? "H:" + d.Reasons[i] : "L")); }
        c.Eq("a stored parameter: HandOver by store", Sig("S", "Keep"), "H:Stores");
        c.Eq("passed on at its last use to a stored parameter: HandOver by store", Sig("S", "Pass"), "H:Stores");
        c.Eq("only read: Lend", Sig("S", "Peek"), "L");
        c.Eq("dead before a loop: HandOver, keeps running", Sig("S", "Spin"), "H:KeepsRunning");
        c.Eq("used after the further call: Lend", Sig("S", "Late"), "L");
        c.Eq("a throw helper is no further call: Lend", Sig("S", "Guard"), "L");
        var get = Signatures.Demand(sol, M("Node", "Get"));
        c.True("a getter returns a borrow of this.Next", get.ReturnBorrow is { Root: RootKind.Arg, Index: 0, Steps.Length: 1 });
        c.True("return this: a borrow of this", Signatures.Demand(sol, M("Node", "Self")).ReturnBorrow is { Root: RootKind.Arg, Index: 0, Steps.Length: 0 });
        c.Eq("the Entry lends everything", string.Join(",", Signatures.Of(sol, M("S", "Keep")).Params), "Lend");
        c.Eq("a small method's owned variant", string.Join(" ", Signatures.VariantsOf(sol, M("S", "Keep")).Select(v => v.Suffix)), "|own1");
        c.Eq("a getter's borrowed-return variant", string.Join(" ", Signatures.VariantsOf(sol, M("Node", "Get")).Select(v => v.Suffix)), "|retborrow");
        c.Eq("an owned variant's convention", string.Join(",", Signatures.ConventionOf(sol, M("S", "Keep"), new Variant(VariantKind.Owned, 1)).Params), "HandOver");
        c.True("Mods: a method that only reads is not Wild", !sol.Facts(M("S", "Peek")).Mods.Wild);
        // the call rule (4.3) and the variant choice (4.4), on the plans of Main and of W's two entries
        c.Eq("W's root at its last use: the owned variant", callees.GetValueOrDefault("Main"), "|own1");
        c.Eq("W's calls on its fields' loads, in the Entry: the Entry", callees.GetValueOrDefault("W"), "entry entry");
        c.Eq("W's calls on its fields' loads, in its owned variant: the Entry", callees.GetValueOrDefault("W|own1"), "entry entry");
    }
}

using Il2Wasm.IL.Memory;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Tests;

/// <summary>Memory/Signatures.cs (docs/MEMORY-IR.md 4.1-4.4) on a small program over the stub CoreLib: a stored
/// parameter and one passed on to a store are HandOver (Stores); one dead before a loop is HandOver (KeepsRunning);
/// one used after the further call, or only before a throw helper, is Lend; getters and `return this` are borrowed
/// returns; the owned variants follow the size caps. Runs only with IL2WASM_MEM_PLAN=1 (Compute runs only then).</summary>
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
        public static class S
        {
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
            }
        }
        """;

    public static void Run(Checks c)
    {
        c.Section("memory: ownership signatures (Signatures.cs)");
        if (!Pipeline.Planned) { c.Info("needs IL2WASM_MEM_PLAN=1: skipped"); return; }
        ProgramSolution? sol = null;
        Signatures.Observed = s => sol = s;
        try { EndToEnd.Compile("sigs", Source); }
        finally { Signatures.Observed = null; }
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
    }
}

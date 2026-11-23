using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Tests;

/// <summary>The importer's IR for the IL shapes Roslyn makes: SSA phis at loop heads and at joins with values on the
/// stack, exception regions and their variables, address-taken locals in the frame, the typed stack, and the naive
/// counts Ownership inserts.</summary>
static class ImporterChecks
{
    const string Source = """
        using System;
        public static class M
        {
            public static int Loop(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }
            public static int Pick(bool b, int x) => Twice(b ? x : 2);
            static int Twice(int v) => v * 2;
            public static int Guarded(int x)
            {
                int r = 1;
                try { r = 10 / x; }
                catch (DivideByZeroException) { r = -1; }
                finally { r += 100; }
                return r;
            }
            public static string Addr(int v) => v.ToString();
            public static string Concat(string a) => a + a;
            public static object Field(Box b) => b.Value;
            public static void Store(Box b, object o) { b.Value = o; }
            public static long Wide(int a, long b) => a + b;
        }
        public class Box { public object Value; }
        public class Program { public static void Main() { } }
        """;

    public static void Run(Checks c)
    {
        var lib = Build.CoreLib();
        var prog = Build.Program("shapes", Source);
        var ctx = new IlContext();
        ctx.AddAssemblies([lib, prog]);
        ctx.SetCoreLib();
        var m = ctx.Load("shapes");
        var type = (MetadataType)m.GetType(""u8, "M"u8);
        Body Import(string name)
        {
            var md = (EcmaMethod)type.GetMethods().Single(x => x.GetName() == name);
            return Importer.Import(md, EcmaMethodIL.Create(md));
        }
        IEnumerable<Node> All(Body b) => b.Blocks.SelectMany(x => x.Phis.Concat(x.Code));

        c.Section("importer: SSA");
        var loop = Import("Loop");
        var phis = loop.Blocks.SelectMany(b => b.Phis).ToList();
        c.Eq("a counting loop: phis for s and i at its head", phis.Count, 2);
        c.True("the phis are in one block (the loop head) with two predecessors", phis.Select(p => p.Block).Distinct().Count() == 1 && phis[0].Block.Preds.Count == 2);
        c.True("no variables besides SSA values", loop.Vars.Count == 0);

        var pick = Import("Pick");
        var stackPhi = pick.Blocks.SelectMany(b => b.Phis).ToList();
        c.Eq("a value on the stack across a join (b ? x : 2): one phi", stackPhi.Count, 1);
        c.True("the phi feeds the call", All(pick).Any(n => n.Op == Opc.Call && n.A.Contains(stackPhi[0])));

        c.Section("importer: exception regions");
        var g = Import("Guarded");
        c.True("regions: a try in a try (catch inside finally)", g.Regions.Count(r => r.Kind == RegionKind.Try) == 2);
        c.True("a catch with its type and a finally", g.Regions.Any(r => r.Kind == RegionKind.Catch && r.CatchType?.ToString() == "[S.P.CoreLib]System.DivideByZeroException") && g.Regions.Any(r => r.Kind == RegionKind.Finally));
        c.True("the catch's entry takes the exception (CatchVal)", All(g).Any(n => n.Op == Opc.CatchVal));
        c.Eq("r, written in the try, is a variable of its own", g.Vars.Count, 1);
        c.True("leaves out of the try", All(g).Count(n => n.Op == Opc.Leave) >= 2);
        c.True("an endfinally", All(g).Any(n => n.Op == Opc.EndFinally));

        c.Section("importer: addresses, the typed stack");
        var addr = Import("Addr");
        c.True("ldarga: the argument is in the frame", addr.Vars.Count == 1 && addr.Vars[0].InFrame && All(addr).Any(n => n.Op == Opc.LocA));
        var wide = Import("Wide");
        c.True("int32 + int64 is widened first (conv.i8), the add is 64-bit", All(wide).Any(n => n.Op == Opc.Conv && n.Kind == VK.I64) && All(wide).Any(n => n.Op == Opc.Bin && n.Kind == VK.I64));

        c.Section("ownership: naive counting");
        var cat = Import("Concat");
        Ownership.Run(cat);
        c.True("an argument is counted on entry and released after its last use", All(cat).Count(n => n.Op == Opc.Retain) == 1 && All(cat).Count(n => n.Op == Opc.Release && n.A[0].Op == Opc.Arg) == 1);
        c.True("the call's result is returned with its count (a move)", All(cat).Any(n => n.Op == Opc.Ret && (n.Moves & 1) != 0));
        var fld = Import("Field");
        Ownership.Run(fld);
        c.True("a field's value returned: the load counts it, the return takes the count", All(fld).Any(n => n.Op == Opc.Ret && (n.Moves & 1) != 0));
        var st = Import("Store");
        Ownership.Run(st);
        c.True("a stored argument: its entry count moves into the field", All(st).Any(n => n.Op == Opc.StFld && (n.Moves & 2) != 0));
    }
}

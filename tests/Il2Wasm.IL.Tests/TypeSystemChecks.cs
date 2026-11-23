using Il2Wasm.IL.Types;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

namespace Il2Wasm.IL.Tests;

/// <summary>NativeAOT's type system over a test assembly and the stub CoreLib, for wasm32: layouts, statics, generic
/// instantiation and canonical forms, virtual slots and their resolution (vtables), interface and default methods.</summary>
static class TypeSystemChecks
{
    const string Source = """
        using System;
        public class A { public byte d; public object c; public long b; public int a; }
        public class B : A { public int e; public string f; }
        public struct S { public byte x; public long y; public object z; }
        public struct Small { public short p; public byte q; }
        public class Gen<T> { public T value; public int n; public static T s_last; }
        public class Stat { public static int i; public static long l; public static object o; public static string t; }
        public class Base { public virtual int M() => 1; public virtual int N() => 2; public virtual string Name => "base"; }
        public class Mid : Base { public override int M() => 3; public new virtual int N() => 4; }
        public sealed class Leaf : Mid { public override int N() => 5; public override string Name => "leaf"; }
        public interface IShape { int Sides(); int Twice() => Sides() * 2; }
        public interface IGen<T> { T Get(); }
        public class Square : IShape, IGen<string> { public int Sides() => 4; public string Get() => "sq"; }
        public class Tri : IShape { public int Sides() => 3; int IShape.Twice() => 7; }
        public class Program { public static void Main() { } }
        """;

    public static void Run(Checks c)
    {
        var lib = Build.CoreLib();
        var prog = Build.Program("types", Source);
        var ctx = new IlContext();
        ctx.AddAssemblies([lib, prog]);
        ctx.SetCoreLib();
        var m = ctx.Load("types");
        MetadataType T(string name) => (MetadataType)m.GetType(""u8, System.Text.Encoding.UTF8.GetBytes(name));
        FieldDesc F(TypeDesc t, string name) => t.GetFields().Single(f => f.GetName() == name);
        int Off(TypeDesc t, string f) => IlContext.FieldOffset(F(t, f));

        c.Section("type system: wasm32 target");
        c.Eq("pointer size", ctx.Target.PointerSize, 4);
        c.Eq("IntPtr size", ((DefType)ctx.GetWellKnownType(WellKnownType.IntPtr)).InstanceFieldSize.AsInt, 4);

        c.Section("type system: instance layout (header 12 bytes, NativeAOT's auto layout)");
        var a = T("A");
        var offs = new[] { "d", "c", "b", "a" }.Select(f => (f, Off(a, f))).ToArray();
        c.Info("A: " + string.Join(", ", offs.Select(x => $"{x.f}@{x.Item2}")) + $", size {IlContext.InstanceSize(a)}");
        c.True("A's fields after the header", offs.All(x => x.Item2 >= IlContext.ObjectHeader));
        c.True("A's long 8-aligned", Off(a, "b") % 8 == 0);
        c.True("A's reference 4-aligned", Off(a, "c") % 4 == 0);
        c.True("A's fields do not overlap", NoOverlap([(Off(a, "d"), 1), (Off(a, "c"), 4), (Off(a, "b"), 8), (Off(a, "a"), 4)]));
        c.True("A's size covers its fields", offs.Max(x => x.Item2) < IlContext.InstanceSize(a));
        var b = T("B");
        c.Info($"B: e@{Off(b, "e")}, f@{Off(b, "f")}, size {IlContext.InstanceSize(b)}");
        c.True("B's fields after A's", Off(b, "e") >= IlContext.InstanceSize(a) - 4 && Off(b, "f") >= IlContext.InstanceSize(a) - 4);
        var s = T("S");
        c.Info($"S: x@{Off(s, "x")}, y@{Off(s, "y")}, z@{Off(s, "z")}, size {s.InstanceFieldSize.AsInt}, align {s.InstanceFieldAlignment.AsInt}");
        c.Eq("S (struct with a long and a reference) size (wasm32 aligns a long to 8, as ARM32)", s.InstanceFieldSize.AsInt, 24);
        c.Eq("Small size", T("Small").InstanceFieldSize.AsInt, 4);
        c.True("S contains GC pointers", s.ContainsGCPointers);
        c.True("Small contains no GC pointers", !T("Small").ContainsGCPointers);

        c.Section("type system: statics");
        var st = T("Stat");
        foreach (var f in st.GetFields()) c.Info($"Stat.{f.GetName()}: {(f.HasGCStaticBase ? "gc" : "plain")} block +{f.Offset.AsInt}");
        c.Eq("Stat plain statics block", st.NonGCStaticFieldSize.AsInt, 16);
        c.Eq("Stat reference statics block", st.GCStaticFieldSize.AsInt, 8);
        c.True("Stat.o and Stat.t in the reference block", F(st, "o").HasGCStaticBase && F(st, "t").HasGCStaticBase && !F(st, "i").HasGCStaticBase);

        c.Section("type system: generic instantiation and canonical forms");
        var gen = T("Gen`1");
        var gInt = gen.MakeInstantiatedType(ctx.GetWellKnownType(WellKnownType.Int32));
        var gLong = gen.MakeInstantiatedType(ctx.GetWellKnownType(WellKnownType.Int64));
        var gStr = gen.MakeInstantiatedType(ctx.GetWellKnownType(WellKnownType.String));
        c.Info($"Gen<int>: value@{Off(gInt, "value")} size {IlContext.InstanceSize(gInt)}; Gen<long>: value@{Off(gLong, "value")} size {IlContext.InstanceSize(gLong)}");
        c.True("Gen<long>'s value 8-aligned", Off(gLong, "value") % 8 == 0);
        c.Eq("Gen<string> canonical form", gStr.ConvertToCanonForm(CanonicalFormKind.Specific).ToString(), "[types]Gen`1<System.__Canon>");
        c.Eq("Gen<int> canonical form (value types are not shared)", gInt.ConvertToCanonForm(CanonicalFormKind.Specific).ToString(), "[types]Gen`1<int32>");
        c.Eq("Gen<int> static s_last size", gInt.NonGCStaticFieldSize.AsInt, 4);
        c.Eq("Gen<string> static s_last in the reference block", gStr.GCStaticFieldSize.AsInt, 4);

        c.Section("type system: virtual slots (vtables) and resolution");
        var leaf = T("Leaf");
        var slots = leaf.EnumAllVirtualSlots().ToList();
        string Res(MethodDesc slot) => leaf.FindVirtualFunctionTargetMethodOnObjectType(slot) is { } r ? $"{((MetadataType)r.OwningType).GetName()}.{r.GetName()}" : "?";
        c.Info("Leaf's slots: " + string.Join("; ", slots.Select(sl => $"{((MetadataType)sl.OwningType).GetName()}.{sl.GetName()} -> {Res(sl)}")));
        var baseT = T("Base");
        MethodDesc Mt(TypeDesc t, string n) => t.GetMethods().Single(x => x.GetName() == n);
        c.Eq("Base.M on a Leaf", Res(Mt(baseT, "M")), "Mid.M");
        c.Eq("Base.N on a Leaf (Mid.N is a new slot)", Res(Mt(baseT, "N")), "Base.N");
        c.Eq("Mid.N on a Leaf", Res(Mt(T("Mid"), "N")), "Leaf.N");
        c.Eq("Base.get_Name on a Leaf", Res(Mt(baseT, "get_Name")), "Leaf.get_Name");
        c.Eq("Leaf's slot count (Object's 4 and Base's 3 and Mid's new N)", slots.Count, 8);

        c.Section("type system: interfaces and default methods");
        var ishape = T("IShape");
        var sq = T("Square");
        var tri = T("Tri");
        c.Eq("IShape.Sides on Square", sq.ResolveInterfaceMethodToVirtualMethodOnType(Mt(ishape, "Sides"))?.ToString(), "[types]Square.Sides()");
        c.Eq("IShape.Twice on Square (not implemented: the default)", sq.ResolveInterfaceMethodToVirtualMethodOnType(Mt(ishape, "Twice")), null);
        var dr = sq.Context.GetVirtualMethodAlgorithmForType(sq).ResolveInterfaceMethodToDefaultImplementationOnType(Mt(ishape, "Twice"), sq, out var impl);
        c.Eq("IShape.Twice on Square's default", $"{dr} {impl}", "DefaultImplementation [types]IShape.Twice()");
        c.Eq("IShape.Twice on Tri (explicit)", tri.ResolveInterfaceMethodToVirtualMethodOnType(Mt(ishape, "Twice"))?.ToString(), "[types]Tri.IShape.Twice()");
        var igenStr = T("IGen`1").MakeInstantiatedType(ctx.GetWellKnownType(WellKnownType.String));
        var getOnIGen = ctx.GetMethodForInstantiatedType(Mt(T("IGen`1"), "Get").GetTypicalMethodDefinition(), igenStr);
        c.Eq("IGen<string>.Get on Square", sq.ResolveInterfaceMethodToVirtualMethodOnType(getOnIGen)?.ToString(), "[types]Square.Get()");
        c.True("Square casts to IGen<string>", sq.CanCastTo(igenStr));
        c.True("Leaf casts to Base", leaf.CanCastTo(baseT));
        c.True("string[] casts to object[] (array covariance)", ctx.GetWellKnownType(WellKnownType.String).MakeArrayType().CanCastTo(ctx.GetWellKnownType(WellKnownType.Object).MakeArrayType()));
        c.True("int[] does not cast to object[]", !ctx.GetWellKnownType(WellKnownType.Int32).MakeArrayType().CanCastTo(ctx.GetWellKnownType(WellKnownType.Object).MakeArrayType()));
    }

    static bool NoOverlap((int off, int size)[] f)
    {
        var o = f.OrderBy(x => x.off).ToArray();
        for (int i = 1; i < o.Length; i++) if (o[i - 1].off + o[i - 1].size > o[i].off) return false;
        return true;
    }
}

// Cycles closed only by stores through an address (a span's element, MemoryMarshal's reference, Unsafe.Add, a ref
// reinterpreted by Unsafe.As, Interlocked through a span's element, a readonly field written through Unsafe.AsRef):
// each holder's array (or field) is written no other way, so the type graph knows of these stores only if stores by
// address count. Let go, each cycle must be freed (counted by finalizers, as .NET's collector leaves none).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Tracked
{
    public static readonly List<string> Kinds = new();
    public static readonly int[] Live = new int[16];
    readonly int _kind;
    public Tracked(string kind)
    {
        int k = Kinds.IndexOf(kind);
        if (k < 0) { k = Kinds.Count; Kinds.Add(kind); }
        _kind = k;
        System.Threading.Interlocked.Increment(ref Live[k]);
    }
    ~Tracked() { System.Threading.Interlocked.Decrement(ref Live[_kind]); }
}

class SItem : Tracked { public SHolder? Back; public SItem() : base("span") { } }
class SHolder : Tracked { public readonly SItem[] Items = new SItem[1]; public SHolder() : base("span holder") { } }

class MItem : Tracked { public MHolder? Back; public MItem() : base("data reference") { } }
class MHolder : Tracked { public readonly MItem[] Items = new MItem[1]; public MHolder() : base("data reference holder") { } }

class AItem : Tracked { public AHolder? Back; public AItem() : base("unsafe add") { } }
class AHolder : Tracked { public readonly AItem[] Items = new AItem[2]; public AHolder() : base("unsafe add holder") { } }

class RItem : Tracked { public RHolder? Back; public RItem() : base("reinterpreted") { } }
class RHolder : Tracked { public readonly RItem[] Items = new RItem[1]; public RHolder() : base("reinterpreted holder") { } }

class OItem : Tracked { public readonly OHolder? Back; public OItem(OHolder? back) : base("readonly through a ref") { Back = back; } }
class OHolder : Tracked { public readonly OItem? Item; public OHolder() : base("readonly through a ref holder") { } }

class XItem : Tracked { public XHolder? Back; public XItem() : base("exchange") { } }
class XHolder : Tracked { public readonly XItem[] Items = new XItem[1]; public XHolder() : base("exchange holder") { } }

static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Build(int round)
    {
        var s = new SHolder();
        s.Items.AsSpan()[0] = new SItem { Back = s };

        var m = new MHolder();
        MemoryMarshal.GetArrayDataReference(m.Items) = new MItem { Back = m };

        var a = new AHolder();
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a.Items), 1) = new AItem { Back = a };

        var r = new RHolder();
        var objects = MemoryMarshal.CreateSpan(ref Unsafe.As<RItem, object>(ref MemoryMarshal.GetArrayDataReference(r.Items)), 1);
        objects[0] = new RItem { Back = r };

        // (readonly fields, filled at construction but for this store through a ref after it)
        var o = new OHolder();
        Unsafe.AsRef(in o.Item) = new OItem(o);

        var x = new XHolder();
        System.Threading.Interlocked.Exchange(ref x.Items.AsSpan()[0], new XItem { Back = x });

        return s.Items.Length + m.Items.Length + a.Items.Length + r.Items.Length + x.Items.Length + (a.Items[1].Back == a ? round : 0) + (o.Item!.Back == o ? 1 : 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long total = 0;
        for (int round = 0; round < 50; round++) total += Build(round);
        Console.WriteLine(total);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        foreach (var kind in Tracked.Kinds.OrderBy(k => k, StringComparer.Ordinal)) Console.WriteLine(kind + " " + Tracked.Live[Tracked.Kinds.IndexOf(kind)]);
    }
}

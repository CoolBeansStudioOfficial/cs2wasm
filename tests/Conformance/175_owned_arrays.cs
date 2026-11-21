// Owned arrays (section 4.3, docs/FINISH.md stream J): a field whose arrays are filled while only their maker knows
// them and never written once handed over takes them out of the cycle graph (Roslyn's green nodes and their child
// arrays, against the builders' arrays of the same type). The nodes below are built that way and are no group's; each
// near miss writes its array some other way after it is handed over, builds the cycle that makes possible, and must
// stay cycle-capable. Behaviour (and freeing: the checked build) as .NET's.
// cycles: AHolder, AItem, BHolder, BItem, CHolder, CItem, DHolder, DItem, EHolder, EItem, FHolder, FItem, GHolder, GItem, HHolder, HItem, IHolder, IItem, JHolder, JItem, KHolder, KItem
using System;
using System.Runtime.InteropServices;

// ---- the shape the rule is for: Roslyn's SyntaxList (green nodes), SyntaxListBuilder, ArrayElement<GreenNode> ----

struct Slot { public GNode Value; }

abstract class GNode
{
    public readonly string Text;
    protected GNode(string text) { Text = text; }
    public abstract int Count { get; }
    public abstract GNode Get(int i);
    public abstract void CopyTo(Slot[] array, int offset);
    public override string ToString()
    {
        if (Count == 0) return Text;
        var s = Text + "(";
        for (int i = 0; i < Count; i++) s += (i > 0 ? " " : "") + Get(i);
        return s + ")";
    }
}

sealed class Leaf : GNode
{
    public Leaf(string text) : base(text) { }
    public override int Count => 0;
    public override GNode Get(int i) => throw new IndexOutOfRangeException();
    public override void CopyTo(Slot[] array, int offset) => array[offset].Value = this;
}

sealed class Pair : GNode
{
    readonly GNode _a, _b;
    public Pair(GNode a, GNode b) : base("pair") { _a = a; _b = b; }
    public override int Count => 2;
    public override GNode Get(int i) => i == 0 ? _a : _b;
    public override void CopyTo(Slot[] array, int offset) { array[offset].Value = _a; array[offset + 1].Value = _b; }
}

class ListNode : GNode
{
    readonly Slot[] children;
    readonly int[] offsets;
    public ListNode(Slot[] children) : this("list", children) { }
    protected ListNode(string text, Slot[] children) : base(text) { this.children = children; offsets = Offsets(children); }
    static int[] Offsets(Slot[] c)
    {
        var r = new int[c.Length];
        int n = 0;
        for (int i = 0; i < c.Length; i++) { r[i] = n; n += c[i].Value.Text.Length; }
        return r;
    }
    public override int Count => children.Length;
    public override GNode Get(int i) => children[i].Value;
    public int Offset(int i) => offsets[i];
    public override void CopyTo(Slot[] array, int offset) => Array.Copy(children, 0, array, offset, children.Length);
    public ListNode Renamed(string text) => new LongList(text, children);
}

sealed class LongList : ListNode
{
    public LongList(string text, Slot[] children) : base(text, children) { }
}

static class Lists
{
    public static GNode List(Slot[] children) => children.Length < 3 ? new ListNode(children) : new LongList("long", children);

    public static GNode Concat(GNode left, GNode right)
    {
        var tmp = new Slot[left.Count + right.Count];
        left.CopyTo(tmp, 0);
        right.CopyTo(tmp, left.Count);
        return List(tmp);
    }

    public static Slot[] Make(GNode[] items)
    {
        var array = new Slot[items.Length];
        for (int i = 0; i < items.Length; i++) array[i].Value = items[i];
        return array;
    }

    public static GNode Of(params GNode[] items) => List(Make(items));
}

sealed class Builder
{
    Slot[] _nodes = new Slot[2];
    int _count;
    public void Add(GNode n)
    {
        if (_count == _nodes.Length) { var bigger = new Slot[_count * 2]; Array.Copy(_nodes, bigger, _count); _nodes = bigger; }
        _nodes[_count++].Value = n;
    }
    public void Clear() { Array.Clear(_nodes, 0, _count); _count = 0; }
    public GNode ToList()
    {
        var tmp = new Slot[_count];
        Array.Copy(_nodes, tmp, _count);
        return Lists.List(tmp);
    }
}

// ---- near misses: each writes its array after the hand-off (or lets it out before) and closes a cycle ----

// A: the maker writes after handing it over
class AItem { public AHolder Back; }
class AHolder
{
    readonly AItem[] items;
    public AHolder(AItem[] items) { this.items = items; }
    public int Count => items.Length;
    public static AHolder Make()
    {
        var t = new AItem[1];
        var h = new AHolder(t);
        t[0] = new AItem { Back = h };
        return h;
    }
}

// B: written through the field later
class BItem { public BHolder Back; }
class BHolder
{
    readonly BItem[] items;
    public BHolder(BItem[] items) { this.items = items; }
    public void Set(int i, BItem v) => items[i] = v;
    public int Count => items.Length;
}

// C: let out before it is handed over (and written through what was let out)
class CItem { public CHolder Back; }
class CHolder
{
    static CItem[]? s_keep;
    readonly CItem[] items;
    public CHolder(CItem[] items) { this.items = items; }
    public int Count => items.Length;
    public static CHolder Make()
    {
        var t = new CItem[1];
        s_keep = t;
        var h = new CHolder(t);
        s_keep[0] = new CItem { Back = h };
        s_keep = null;
        return h;
    }
}

// D: written through a span of the field's array (its maker stores elements too: a store through a span alone
// records no value at the array's site, a gap of the type graph's own, docs/AUDIT.md)
class DItem { public DHolder? Back; }
class DHolder
{
    readonly DItem[] items;
    public DHolder(DItem[] items) { this.items = items; }
    public void Set(DItem v) => items.AsSpan()[0] = v;
    public int Count => items.Length;
    public static DHolder Make()
    {
        var t = new DItem[1];
        t[0] = new DItem();
        return new DHolder(t);
    }
}

// E: a writing callee keeps it in a lambda that writes later
class EItem { public EHolder Back; }
class EHolder
{
    static Action? s_later;
    static EItem? s_value;
    readonly EItem[] items;
    public EHolder(EItem[] items) { this.items = items; }
    public int Count => items.Length;
    static void Fill(EItem[] a) { s_later = () => a[0] = s_value!; }
    public static EHolder Make()
    {
        var t = new EItem[1];
        Fill(t);
        var h = new EHolder(t);
        s_value = new EItem { Back = h };
        s_later!();
        s_later = null; s_value = null;
        return h;
    }
}

// F: handed over in a loop that the array is declared outside of
class FItem { public FHolder? Back; }
class FHolder
{
    readonly FItem[] items;
    public FHolder(FItem[] items) { this.items = items; }
    public int Count => items.Length;
    public static FHolder Make()
    {
        var t = new FItem[2];
        FHolder? h = null;
        for (int i = 0; i < 2; i++)
        {
            t[i] = new FItem { Back = h };
            h = new FHolder(t);
        }
        return h!;
    }
}

// G: Array.Copy into the field's array
class GItem { public GHolder Back; }
class GHolder
{
    readonly GItem[] items;
    public GHolder(GItem[] items) { this.items = items; }
    public void Fill(GItem[] from) => Array.Copy(from, items, from.Length);
    public int Count => items.Length;
}

// H: a reference to an element, written after the hand-off
class HItem { public HHolder Back; }
class HHolder
{
    readonly HItem[] items;
    public HHolder(HItem[] items) { this.items = items; }
    public int Count => items.Length;
    public static HHolder Make()
    {
        var t = new HItem[1];
        ref var r = ref t[0];
        var h = new HHolder(t);
        r = new HItem { Back = h };
        return h;
    }
}

// I: one override of the virtual callee the maker fills through keeps the array
abstract class IFiller { public abstract void Fill(IItem[] a); }
sealed class IKeeper : IFiller
{
    public static IItem[]? Kept;
    public override void Fill(IItem[] a) { Kept = a; }
}
sealed class IPlain : IFiller
{
    public override void Fill(IItem[] a) { a[0] = new IItem(); }
}
class IItem { public IHolder? Back; }
class IHolder
{
    readonly IItem[] items;
    public IHolder(IItem[] items) { this.items = items; }
    public int Count => items.Length;
    public static IHolder Make(IFiller f)
    {
        var t = new IItem[1];
        f.Fill(t);
        return new IHolder(t);
    }
}

// J: a caller hands over a builder's array, which the builder writes again
class JItem { public JHolder? Back; }
class JHolder
{
    readonly JItem[] items;
    public JHolder(JItem[] items) { this.items = items; }
    public int Count => items.Length;
}
sealed class JBuilder
{
    readonly JItem[] _nodes = new JItem[1];
    public JHolder Build() => new JHolder(_nodes);
    public void Set(JItem v) => _nodes[0] = v;
}

// K: written through a local copy of the field
class KItem { public KHolder Back; }
class KHolder
{
    readonly KItem[] items;
    public KHolder(KItem[] items) { this.items = items; }
    public void Set(KItem v) { var a = items; a[0] = v; }
    public int Count => items.Length;
}

static class P
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var a = new Leaf("a");
        var b = new Leaf("b");
        var l1 = Lists.Of(a, b);
        var l2 = Lists.Concat(l1, new Pair(new Leaf("c"), new Leaf("d")));
        var builder = new Builder();
        for (int i = 0; i < 5; i++) builder.Add(new Leaf("x" + i));
        var l3 = builder.ToList();
        builder.Clear();
        builder.Add(l2);
        builder.Add(l3);
        var l4 = builder.ToList();
        Console.WriteLine(l1);
        Console.WriteLine(l2);
        Console.WriteLine(l4);
        Console.WriteLine(((ListNode)l3).Offset(4) + " " + ((ListNode)l3).Renamed("renamed"));
        // many lists, built and let go (the checked build reports a use after free, the runs' outputs any difference)
        long total = 0;
        for (int round = 0; round < 2000; round++)
        {
            var x = Lists.Of(new Leaf("p" + round), new Leaf("q"));
            var y = Lists.Concat(x, x);
            total += y.Count + y.Get(3).Text.Length;
        }
        Console.WriteLine(total);

        var ah = AHolder.Make();
        var bh = new BHolder(new BItem[1]); bh.Set(0, new BItem { Back = bh });
        var ch = CHolder.Make();
        var dh = DHolder.Make(); dh.Set(new DItem { Back = dh });
        var eh = EHolder.Make();
        var fh = FHolder.Make();
        var gh = new GHolder(new GItem[1]); gh.Fill([new GItem { Back = gh }]);
        var hh = HHolder.Make();
        var ih = IHolder.Make(new IPlain());
        var ik = IHolder.Make(new IKeeper());
        IKeeper.Kept![0] = new IItem { Back = ik };
        IKeeper.Kept = null;
        var jb = new JBuilder();
        var jh = jb.Build();
        jb.Set(new JItem { Back = jh });
        var kh = new KHolder(new KItem[1]); kh.Set(new KItem { Back = kh });
        Console.WriteLine($"{ah.Count} {bh.Count} {ch.Count} {dh.Count} {eh.Count} {fh.Count} {gh.Count} {hh.Count} {ih.Count} {ik.Count} {jh.Count} {kh.Count}");
        // the cycles let go: each group is freed whole
        for (int round = 0; round < 500; round++)
        {
            var x = AHolder.Make();
            var y = FHolder.Make();
            var z = new BHolder(new BItem[1]); z.Set(0, new BItem { Back = z });
            total += x.Count + y.Count + z.Count;
        }
        Console.WriteLine(total);
    }
}

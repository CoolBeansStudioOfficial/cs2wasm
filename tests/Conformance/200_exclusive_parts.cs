// Exclusive parts (Compiler/ExclusiveParts.cs, Rt.ArenaPartStore; docs/AUDIT.md A3.5, "The build, round 8"): a field
// whose object is its holder's alone (a node's list of children, the list's array) is no member of its own in the
// holder's group. Off by default; the line below makes the suite compile this program once more with them on, and it
// must print the same, which is what .NET prints. First the shape the proof is for (class A: a tree built in the
// scene's order, lists that grow, subtrees stored over, children moved and taken out, a node of a replaced part held
// meanwhile, nodes that die with their lists in use). Then one class for each condition of the proof, broken in the
// program: with the condition not asked (CS2WASM_EXP_PARTS_SKIP=1, 2, 4, 5, 6) the class's field is taken for
// exclusive and the lines of its section change (objects that live too long or not long enough, a read of freed
// memory in the checked build). Condition 3 (the holder let go while a call on its part runs) has no section here:
// tools/parts-holder-goes.cs is that program, and with exclusive parts on its checked build reads freed memory with
// the condition asked too (not fixed: one reason they are off by default). What lives is counted through finalizers of objects that are in no group (Tag), against .NET.
// exclusive-parts
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; P.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref P.Live[_k]); } }

// the shape: every store of Kids a new list, every read a receiver of a call that keeps nothing
sealed class A
{
    public A Up; public List<A> Kids = new(); public Tag Tag = new Tag(0); public int Id;
    public int Count() { int n = 1; foreach (var c in Kids) n += c.Count(); return n; }
    public bool Sound() { foreach (var c in Kids) if (c.Up != this || !c.Sound()) return false; return true; }
}

// condition 1: a store of a list that is not new (another node's)
sealed class B { public B Up; public List<B> Kids = new(); public Tag Tag = new Tag(1); }

// condition 1 (the local): a new object in a local that is used after the store
sealed class C { public CBox Box; public Tag Tag = new Tag(2); }
sealed class CBox
{
    public C[] Items = new C[2]; public Tag Tag = new Tag(3);
    public void Put(int i, C c) { Items[i] = c; }
    public int Count() { int n = 0; foreach (var c in Items) if (c != null) n++; return n; }
    public bool Sound(int i) => Items[i] != null && Items[i].Box == this && Items[i].Tag != null;
}

// condition 2: a read whose value is kept (a static, a result)
sealed class D { public D Up; public List<D> Kids = new(); public Tag Tag = new Tag(4); public List<D> Hand() => Kids; }

// condition 4: the field's address taken
sealed class F { public F Up; public List<F> Kids = new(); public Tag Tag = new Tag(6); }

// condition 5: the holder copied
sealed class G
{
    public G Up; public List<G> Kids = new(); public Tag Tag = new Tag(7);
    public G Copy() => (G)MemberwiseClone();
}

static class P
{
    public static readonly int[] Live = new int[8];
    static int s_ids;
    static A s_a, s_keepA;
    static List<D> s_listD;
    static D s_d;
    static F s_f;
    static G s_g, s_g2;
    static B s_b, s_b2;

    static void Report(string when)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(" ", Live));
    }

    static A Tree(A up, int depth, int fan)
    {
        var e = new A { Up = up, Id = s_ids++ };
        if (depth > 0) for (int i = 0; i < fan; i++) e.Kids.Add(Tree(e, depth - 1, fan));
        return e;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Shape()
    {
        s_a = Tree(null, 4, 5);
        Console.WriteLine("built " + s_a.Count() + " sound " + s_a.Sound());
        Report("built");
        // subtrees stored over, one held meanwhile
        var x = s_a.Kids[1].Kids[2];
        s_keepA = x.Kids[3];
        s_a.Kids[1].Kids[2] = Tree(s_a.Kids[1], 2, 5);
        x = null;
        Console.WriteLine("stored over " + s_a.Count() + " sound " + s_a.Sound() + " held up " + (s_keepA.Up != null) + " held " + s_keepA.Count());
        Report("stored over");
        s_keepA = null;
        Report("held let go");
        // children moved, taken out, lists that grow to 8, 16, 32 and are cleared
        for (int r = 0; r < 40; r++)
        {
            var from = s_a.Kids[r % 5].Kids[(r / 5) % 5];
            if (from.Kids.Count == 0) continue;
            var moved = from.Kids[from.Kids.Count - 1];
            from.Kids.RemoveAt(from.Kids.Count - 1);
            var to = s_a.Kids[(r + 2) % 5];
            moved.Up = to;
            to.Kids.Add(moved);
        }
        Console.WriteLine("moved " + s_a.Count() + " sound " + s_a.Sound() + " widest " + Math.Max(s_a.Kids[0].Kids.Count, s_a.Kids[2].Kids.Count));
        Report("moved");
        s_a.Kids[3].Kids.Clear();
        s_a.Kids[4].Kids.RemoveRange(0, 2);
        s_a.Kids[0].Kids.Insert(0, Tree(s_a.Kids[0], 1, 3));
        Console.WriteLine("cut " + s_a.Count() + " sound " + s_a.Sound());
        Report("cut");
        // a node's list replaced by a new one, the node's children gone with it
        var n0 = s_a.Kids[0];
        n0.Kids = new List<A>();
        n0.Kids.Add(new A { Up = n0, Id = s_ids++ });
        Console.WriteLine("list replaced " + s_a.Count() + " sound " + s_a.Sound());
        Report("list replaced");
        // nodes that never join a tree, with children
        for (int i = 0; i < 3; i++) { var lone = Tree(null, 2, 3); if (lone.Count() != 13) Console.WriteLine("lone?"); }
        Report("lone");
        s_a = null;
        Report("gone");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NotNew()
    {
        s_b = new B(); s_b2 = new B();
        for (int i = 0; i < 5; i++) s_b.Kids.Add(new B { Up = s_b });
        // (two holders of one list)
        s_b2.Kids = s_b.Kids;
        s_b2.Kids.Add(new B { Up = s_b2 });
        s_b = null;
        Report("1: one of two holders gone");
        Console.WriteLine("1: " + s_b2.Kids.Count + " " + (s_b2.Kids[0].Up == null) + " " + (s_b2.Kids[5].Up == s_b2));
        for (int i = 0; i < 9; i++) s_b2.Kids.Add(new B { Up = s_b2 });
        Report("1: grown");
        s_b2 = null;
        Report("1: gone");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int LocalUsedAfter1()
    {
        var box = new CBox();
        var c = new C { Box = box };
        // (the local used after the store: the box refers to its holder, and code to the box alone)
        box.Put(0, c);
        c = null;
        Report("1b: the holder referred to by its box only");
        Console.WriteLine("1b: " + box.Sound(0));
        box.Put(1, new C());
        box.Put(0, null);
        Report("1b: an item stored over");
        return box.Count();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LocalUsedAfter()
    {
        Console.WriteLine("1b: " + LocalUsedAfter1());
        Report("1b: gone");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Kept()
    {
        s_d = new D();
        for (int i = 0; i < 6; i++) s_d.Kids.Add(new D { Up = s_d });
        s_listD = s_d.Hand();
        s_d = null;
        Report("2: the holder let go, its list kept");
        Console.WriteLine("2: " + s_listD.Count + " " + (s_listD[3].Up != null) + " " + s_listD[3].Up.Kids.Count);
        s_listD.RemoveAt(0);
        for (int i = 0; i < 20; i++) s_listD.Add(new D());
        Report("2: grown");
        s_listD = null;
        Report("2: gone");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Swap(ref List<F> a, ref List<F> b) { var t = a; a = b; b = t; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ByAddress()
    {
        s_f = new F();
        var other = new F();
        for (int i = 0; i < 5; i++) s_f.Kids.Add(new F { Up = s_f });
        for (int i = 0; i < 3; i++) other.Kids.Add(new F { Up = other });
        Swap(ref s_f.Kids, ref other.Kids);
        Console.WriteLine("4: " + s_f.Kids.Count + " " + other.Kids.Count + " " + (s_f.Kids[0].Up == other));
        other = null;
        Report("4: the other let go");
        for (int i = 0; i < 9; i++) s_f.Kids.Add(new F { Up = s_f });
        Console.WriteLine("4: " + s_f.Kids.Count + " " + (s_f.Kids[0].Up != null) + " " + s_f.Kids[0].Up.Kids.Count);
        s_f = null;
        Report("4: gone");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Copied()
    {
        s_g = new G();
        for (int i = 0; i < 5; i++) s_g.Kids.Add(new G { Up = s_g });
        s_g2 = s_g.Copy();
        s_g2.Kids.Add(new G { Up = s_g2 });
        s_g = null;
        Report("5: the first of two copies gone");
        Console.WriteLine("5: " + s_g2.Kids.Count + " " + (s_g2.Kids[0].Up == null) + " " + (s_g2.Kids[5].Up == s_g2));
        for (int i = 0; i < 9; i++) s_g2.Kids.Add(new G { Up = s_g2 });
        Report("5: grown");
        s_g2 = null;
        Report("5: gone");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Shape();
        NotNew();
        LocalUsedAfter();
        Kept();
        ByAddress();
        Copied();
    }
}

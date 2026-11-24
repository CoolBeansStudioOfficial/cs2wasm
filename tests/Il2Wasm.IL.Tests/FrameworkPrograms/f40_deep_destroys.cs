// Destructions that nest deep (src/Il2Wasm/IL/Lower/DestroyDepth.cs): letting go of a long acyclic chain nested
// rt.release -> rt.destroy as deep as the chain and ran out of stack (RangeError: Maximum call stack size exceeded).
// - a sealed list node: the sealed tail, a loop over its next;
// - an unsealed one, and a tree of depth 100,000 down the field that is not the loop's: the depth queue;
// - a chain whose items have a finalizer (no sealed tail): the queue, every finalizer run, and a weak reference to an
//   item cleared, within the statement that let go of the head.
using System;
using System.Runtime.CompilerServices;

sealed class Item { public Item Next; public long V; }
class Open { public Open Next; public int V; }
// (a derived item among them: what the sealed tail relies on, that the field holds exactly its own class, does not
// hold for Open, and a loop that took it for one would skip this finalizer)
class OpenFin : Open { public static int s_finalized; ~OpenFin() { s_finalized++; } }
sealed class Tree { public Tree Left; public Tree Right; public int V; }
class Fin
{
    public static int s_finalized;
    public Fin Next;
    public int V;
    ~Fin() { s_finalized++; }
}

static class P
{
    static Item s_items;
    static Open s_open;
    static Tree s_tree;
    static Fin s_fins;
    static WeakReference s_weak;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MakeFins(int n)
    {
        for (int i = 0; i < n; i++) s_fins = new Fin { Next = s_fins, V = i };
        Fin mid = s_fins;
        for (int i = 0; i < n / 2; i++) mid = mid.Next;
        s_weak = new WeakReference(mid);
    }

    static void Main()
    {
        for (int i = 0; i < 200000; i++) s_items = new Item { Next = s_items, V = i };
        Console.WriteLine(s_items.V);
        s_items = null;
        Console.WriteLine("items dropped");

        for (int i = 0; i < 200000; i++) s_open = i % 1000 == 7 ? new OpenFin { Next = s_open, V = i } : new Open { Next = s_open, V = i };
        Console.WriteLine(s_open.V);
        s_open = null;
        Console.WriteLine("open dropped");

        // depth 100,000 down Left, a leaf on each Right; then a spine down Right with a leaf on each Left
        for (int i = 0; i < 100000; i++) s_tree = new Tree { Left = s_tree, Right = new Tree { V = -i }, V = i };
        Console.WriteLine(s_tree.V + " " + s_tree.Left.Right.V);
        s_tree = null;
        Console.WriteLine("left tree dropped");
        for (int i = 0; i < 100000; i++) s_tree = new Tree { Right = s_tree, Left = new Tree { V = -i }, V = i };
        Console.WriteLine(s_tree.V + " " + s_tree.Right.Left.V);
        s_tree = null;
        Console.WriteLine("right tree dropped");

        MakeFins(100000);
        Console.WriteLine(s_fins.V + " " + s_weak.IsAlive);
        s_fins = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine(Fin.s_finalized + " " + s_weak.IsAlive + " " + OpenFin.s_finalized);
        Console.WriteLine("fins dropped");
    }
}

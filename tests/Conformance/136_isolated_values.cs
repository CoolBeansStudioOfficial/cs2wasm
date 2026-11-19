// A structure a call builds of new objects that refer only to each other, stored into a long-lived one (a subtree
// replaced): such a store closes no cycle (FreshStores.cs, isolated values). And the near misses, which may: a
// structure that refers to an old object, one that a static holds while it is built, one whose field the method hands
// out, one stored where parent links are. A class of its own for each, so that each is judged alone. Every kind must
// be gone after it is let go, as .NET's collector leaves none.
using System;
using System.Runtime.InteropServices;
using System.Threading;

class Tracked
{
    public static readonly int[] Live = new int[8];
    readonly int _kind;
    public Tracked(int kind) { _kind = kind; Interlocked.Increment(ref Live[kind]); }
    ~Tracked() { Interlocked.Decrement(ref Live[_kind]); }
}

sealed class T1 : Tracked { public T1? Left, Right, Next; public int Value; public T1() : base(1) { } }
sealed class T2 : Tracked { public T2? Next; public T2() : base(2) { } }
sealed class T3 : Tracked { public T3? Next; public T3() : base(3) { } }
sealed class T4 : Tracked { public T4? Next; public T4() : base(4) { } }
sealed class T5 : Tracked { public T5? Left, Right, Parent; public T5() : base(5) { } }
sealed class T6 : Tracked { public T6? Next; public T6? Old = Program.s_t6; public T6() : base(6) { } }

static class Program
{
    static T1? s_root;
    static T2? s_hub;
    static T3? s_a, s_side;
    static T4? s_kept;
    static T5? s_p;
    internal static T6? s_t6;

    // isolated: new objects, each given only new ones
    static T1 Build(int depth)
    {
        var n = new T1 { Value = depth };
        if (depth > 0) { n.Left = Build(depth - 1); n.Right = Build(depth - 1); }
        return n;
    }
    static T1 Alias() { var n = new T1(); var m = n; m.Next = new T1 { Value = 7 }; return n; }
    static T5 Build5(int depth)
    {
        var n = new T5();
        if (depth > 0) { n.Left = Build5(depth - 1); n.Right = Build5(depth - 1); }
        return n;
    }

    // not isolated: refers to an old object
    static T2 Hook() { var n = new T2(); n.Next = s_hub; return n; }
    // not isolated: a static holds it while it is built
    static T3 Leak() { var n = new T3(); s_side = n; return n; }
    // not isolated: its field is handed out
    static T4 Hand() { var n = new T4(); n.Next = new T4(); Keep(n.Next); return n; }
    static void Keep(T4 x) { s_kept = x; }
    // not isolated: a field initializer refers to an old object
    static T6 Make6() => new T6();

    static void Run()
    {
        // replaced subtrees go at once, the tree stays
        s_root = Build(4);
        for (int i = 0; i < 50; i++) s_root.Left = Build(3);
        for (int i = 0; i < 20; i++) s_root.Right!.Next = Alias();

        // a structure that refers back: a cycle hub -> n -> hub
        s_hub = new T2();
        s_hub.Next = Hook();
        s_hub = null;

        // one a static held while it was built: a -> n -> a
        s_a = new T3();
        s_a.Next = Leak();
        s_side!.Next = s_a;
        s_a = null; s_side = null;

        // one whose field was handed out: kept -> holder (through Next) -> kept
        var holder = Hand();
        s_kept!.Next = holder;
        holder = null;
        s_kept = null;

        // parent links (stores that can close a cycle) beside an isolated store
        s_p = new T5();
        for (int i = 0; i < 10; i++)
        {
            var c = Build5(2);
            c.Parent = s_p;
            s_p.Left = c;
        }
        s_p = null;

        // one whose field initializer refers back: t6 -> n -> t6
        s_t6 = new T6();
        s_t6.Next = Make6();
        s_t6 = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // (kind 1: the tree that stays, 31 nodes, and the last Alias pair under it)
        for (int k = 1; k <= 6; k++) Console.WriteLine("kind " + k + " " + Tracked.Live[k]);
        Console.WriteLine(s_root!.Left!.Value + " " + s_root.Right!.Next!.Next!.Value);
    }
}

// An object initializer's store into a new object: where the constructors left the field null it has no old value;
// where a constructor, a base constructor or a field initializer set it, the old value must be let go; a `with`
// copies a record, whose fields are set. Counted by finalizers: everything made is gone at the end, as under .NET.
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

sealed class V : Tracked { public int N; public V(int kind, int n) : base(kind) { N = n; } }

class Plain { public V? A; public V? B; }
class SetByCtor { public V? A; public SetByCtor() { A = new V(2, -1); } }
class SetByInit { public V? A = new V(3, -2); }
class Base4 { public V? A; public Base4() { A = new V(4, -3); } }
sealed class Derived4 : Base4 { public V? B; }
sealed class Node { public Node? Next; public V? Item; }
record R(V? A) { public V? B { get; init; } }

static class Program
{
    static object? s_keep;

    static void Run()
    {
        long sum = 0;
        for (int i = 0; i < 100; i++)
        {
            var p = new Plain { A = new V(1, i), B = new V(1, i + 1) };
            var c = new SetByCtor { A = new V(2, i) };
            var d = new SetByInit { A = new V(3, i) };
            var e = new Derived4 { A = new V(4, i), B = new V(4, i) };
            sum += p.A!.N + p.B!.N + c.A!.N + d.A!.N + e.A!.N + e.B!.N;
        }
        // a list built by initializers, a node of each holding the next
        Node? head = null;
        for (int i = 0; i < 50; i++) head = new Node { Next = head, Item = new V(5, i) };
        for (var n = head; n != null; n = n.Next) sum += n.Item!.N;
        head = null;
        // a record copied with `with`: the copy's fields were set, the store replaces them
        var r = new R(new V(6, 1)) { B = new V(6, 2) };
        for (int i = 0; i < 20; i++) r = r with { A = new V(6, i), B = new V(6, -i) };
        sum += r.A!.N + r.B!.N;
        s_keep = r;
        Console.WriteLine(sum);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        s_keep = null;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        for (int k = 1; k <= 6; k++) Console.WriteLine("kind " + k + " " + Tracked.Live[k]);
    }
}

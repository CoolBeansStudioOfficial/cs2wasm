// A loop over a list's Next links whose entry input is the list's head, an Owned value of the same method: the loop's
// phi is a borrow of the head (Plan.PhiRootInput), so the walk counts nothing; the head holds its count while the walk
// runs and is let go after its last use. Walked again after a reversal, which writes the links (the borrow flips
// there), and after the head is dropped and rebuilt. With the plan off this is the naive baseline.
// plan: only
// live: 35
using System;
using System.Runtime.CompilerServices;

sealed class Node { public Node? Next; public string Name = ""; public int Value; }

static class Program
{
    static readonly string[] s_names = ["a", "bb", "ccc", "dddd"];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Walk(int count)
    {
        Node? head = null;
        for (int i = 0; i < count; i++) head = new Node { Next = head, Name = s_names[i % 4], Value = i };
        long sum = 0;
        for (int round = 0; round < 3; round++)
            for (var n = head; n != null; n = n.Next) { sum += n.Value; if (n.Name.Length == 3) sum++; }
        Node? prev = null;
        for (var n = head; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; }
        for (var n = prev; n != null; n = n.Next) sum ^= n.Value;
        return sum;
    }

    static WeakReference? s_weak;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int WalkAndDrop()
    {
        var head = new Node { Value = 1, Next = new Node { Value = 2, Next = new Node { Value = 3 } } };
        s_weak = new WeakReference(head.Next.Next);
        int s = 0;
        for (var n = head; n != null; n = n.Next) s += n.Value;
        return s;
    }

    static void Main()
    {
        Console.WriteLine(Walk(1000));
        Console.WriteLine(WalkAndDrop());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine(s_weak!.IsAlive);
        s_weak = null;
    }
}

// An ordered PLINQ query whose items each build a structure of cycle-capable objects with finalizers and let it go
// (as the compiler's parse of its library on many threads: every group made, joined and destroyed takes the runtime
// lock, as every Monitor operation does). Every query ends, every node is freed.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Node
{
    public static int Live;
    public Node? Next, Prev;
    public int Id;
    public Node(int id) { Id = id; Interlocked.Increment(ref Live); }
    ~Node() { Interlocked.Decrement(ref Live); }
}

static class Program
{
    static int Work(int i)
    {
        Node? head = null, last = null;
        int n = 10 + i % 40;
        for (int k = 0; k < n; k++)
        {
            var x = new Node(k) { Prev = last };
            if (last != null) last.Next = x; else head = x;
            last = x;
        }
        int sum = 0;
        for (var p = head; p != null; p = p.Next) sum += p.Id & 1;
        if (i % 11 == 0) Thread.Sleep(1);
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long total = 0;
        for (int round = 0; round < 30; round++)
        {
            int count = round % 3 == 0 ? 400 : round % 3 == 1 ? 25 : 3;
            var outs = Enumerable.Range(round, count).AsParallel().AsOrdered().Select(Work).ToList();
            total += outs.Sum();
        }
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(total + " " + Node.Live);
    }
}

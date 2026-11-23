// Threads (section 4.5): each worker builds a world of its own (a list of structs that hold references, grown and
// cut, so the list's storage moves them: Buffer.Memmove; a tree with parent links; a slot only ever stored null)
// and hands the main thread one result object through an array its lambda captures. The worlds are each thread's
// own; the results are every thread's. Whatever the interleaving, the sums are the same.
// CS2WASM_SHARING_NONE=1 takes no object for one another thread can reach: with CS2WASM_SHARE_CHECK=1 a result made
// by a worker stops the program when the main thread counts it (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Unit
{
    public int V;
    public Unit? Parent;
    public List<Unit> Children = new();
    public object? Never;
    public Unit(int v) { V = v; }
}

struct Slot { public Unit? Who; public int At; }

// (no finalizer in this program: one that writes memory, as a count of the living does, can run at any release, and
// then no element is borrowed across a call that may release: the walk over a worker's own tree borrows each child)
sealed class Result
{
    public long Sum;
    public string Text = "";
}

static class Program
{
    static Unit Build(Unit? parent, int depth, ref int ids)
    {
        var u = new Unit(ids++) { Parent = parent };
        u.Never = null;
        if (depth > 0) for (int i = 0; i < 3; i++) u.Children.Add(Build(u, depth - 1, ref ids));
        return u;
    }

    static long Walk(Unit u) { long s = u.V + (u.Parent != null ? 1 : 0) + (u.Never == null ? 0 : 1000000); foreach (var c in u.Children) s += Walk(c); return s; }

    static Result Work(int t)
    {
        var slots = new List<Slot>();
        long sum = 0;
        int ids = t * 100000;
        Unit? root = null;
        for (int i = 0; i < 3000; i++)
        {
            if (i % 500 == 0) root = Build(null, 4, ref ids);
            var u = new Unit(i);
            slots.Insert(slots.Count / 2, new Slot { Who = u, At = i });
            if (slots.Count > 48) slots.RemoveRange(8, 24);
            sum += slots[slots.Count / 3].Who!.V + slots[0].At;
            var kid = root!.Children[i % 3];
            kid.Children[i % 3] = Build(kid, 1, ref ids);
            sum += Walk(kid) & 1023;
        }
        return new Result { Sum = sum, Text = "t" + t + ":" + slots.Count };
    }

    static void Run()
    {
        var results = new Result[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => results[k] = Work(k));
            threads[t].Start();
        }
        var own = Work(9);
        foreach (var th in threads) th.Join();
        long total = own.Sum;
        string text = own.Text;
        for (int round = 0; round < 1000; round++)
            for (int i = 0; i < results.Length; i++) { var x = results[i]; total += x.Sum & 3; }
        for (int i = 0; i < results.Length; i++) { total += results[i].Sum; text += " " + results[i].Text; }
        Console.WriteLine(total + " " + text);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        Console.WriteLine("done");
    }
}

// A program that hangs in about one run of six, with this branch's compiler and with its base's (d430594: 2 of 12
// runs of the checked build, each stopped after 40 s): four workers store small trees (a root with three children
// and their parent links: a group) over the elements of a list a static holds, through the list's span
// (CollectionsMarshal.AsSpan: no change of the list's version), while the main thread's foreach reads each element
// with a count. Found while writing test 279; not a conformance test (it would hang the suite). The groups' locks
// are the other session's (claude/load-flakes-1).
//   cs2wasmc tools/thread-board-hang.cs --checked -o hang.wasm; timeout 40 node host/run.mjs hang.wasm main
// Root cause (claude/thread-hangs-1, 2026-11-23): not the groups' locks. The hung runs' stacks (CS2WASM_HANG_DUMP=15,
// host/hangdump.mjs) showed a worker making the OutOfMemoryException GrowFor keeps, under the heap's lock, and the
// others waiting for that lock: the first-growth deadlock that b859008 (main 4cf95fe, test 217) fixed. 1b16778 4 of
// 100 hung, 3f73a93 15 of 150, 3f73a93 with b859008 alone 0 of 300; main 0 of 1,200 on the homelab (24 at once).
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
// then no element is borrowed across one)
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

    static readonly List<Unit> s_board = new();
    static int s_done;

    static void Run()
    {
        int seed = 1 << 20;
        for (int i = 0; i < 8; i++) s_board.Add(Build(null, 1, ref seed));
        var results = new Result[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() =>
            {
                results[k] = Work(k);
                // (racy stores over the board's elements: each a small tree, a group that goes at once when its last
                // reference from outside does)
                int ids = (k + 2) << 20;
                // (through the list's span: a store by the indexer changes the version the foreach checks)
                for (int i = 0; i < 30000; i++) CollectionsMarshal.AsSpan(s_board)[(i + k) & 7] = Build(null, 1, ref ids);
                Interlocked.Increment(ref s_done);
            });
            threads[t].Start();
        }
        var own = Work(9);
        long low = 0;
        // (until every worker is through with the board)
        while (Volatile.Read(ref s_done) < threads.Length)
            foreach (var u in s_board)
            {
                // (a while with the element in hand and nothing counted: no call, no safe point)
                int wait = 0;
                for (int j = 0; j < 40; j++) wait += j & 1;
                low += u.V >= 0 ? 0 : wait;
            }
        foreach (var th in threads) th.Join();
        long total = own.Sum + low;
        s_board.Clear();
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
        Console.WriteLine(s_board.Count);
    }
}

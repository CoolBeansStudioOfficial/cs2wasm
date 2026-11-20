// Threads each adding an item to a static array by copy and compare-exchange, as the thread pool registers its
// threads' queues (WorkStealingQueueList.Add): every item must be there at the end, whatever the interleaving (the
// old array a thread compares against must not be freed, and its memory given to another, while it holds it).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Item { public int Id; public Item(int id) { Id = id; } }

static class Program
{
    static Item[] s_items = new Item[0];

    static void Add(Item item)
    {
        while (true)
        {
            Item[] old = s_items;
            var next = new Item[old.Length + 1];
            Array.Copy(old, next, old.Length);
            next[^1] = item;
            if (Interlocked.CompareExchange(ref s_items, next, old) == old) break;
        }
    }

    static void Remove(Item item)
    {
        while (true)
        {
            Item[] old = s_items;
            int pos = Array.IndexOf(old, item);
            if (pos < 0) return;
            var next = new Item[old.Length - 1];
            Array.Copy(old, 0, next, 0, pos);
            Array.Copy(old, pos + 1, next, pos, next.Length - pos);
            if (Interlocked.CompareExchange(ref s_items, next, old) == old) break;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        const int Threads = 16, Each = 300;
        var threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int k = t;
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < Each; i++)
                {
                    var a = new Item(k * Each + i);
                    Add(a);
                    // (half go again: arrays freed all the time, their memory given to the next)
                    if ((i & 1) == 0) Remove(a);
                }
            });
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
        var seen = new bool[Threads * Each];
        foreach (var it in s_items) seen[it.Id] = true;
        int missing = 0, extra = 0;
        for (int i = 0; i < seen.Length; i++) { bool want = (i % Each & 1) == 1; if (want && !seen[i]) missing++; if (!want && seen[i]) extra++; }
        Console.WriteLine(s_items.Length + " " + missing + " " + extra);
    }
}

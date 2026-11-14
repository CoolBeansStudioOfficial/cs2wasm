using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var q = new ConcurrentQueue<int>();
        Parallel.For(0, 500, i => q.Enqueue(i));
        int sum = 0;
        while (q.TryDequeue(out var v)) sum += v;
        Console.WriteLine(sum + " " + q.IsEmpty);

        var counts = new ConcurrentDictionary<int, int>();
        Parallel.For(0, 1000, i => counts.AddOrUpdate(i % 10, 1, (_, c) => c + 1));
        Console.WriteLine(string.Join(",", counts.OrderBy(kv => kv.Key).Select(kv => kv.Value)));
        Console.WriteLine(counts.GetOrAdd(42, k => k * 2) + " " + counts.GetOrAdd(42, 0) + " " + counts.TryAdd(42, 1));
        Console.WriteLine(counts.TryRemove(42, out var removed) + " " + removed + " " + counts.ContainsKey(42) + " " + counts.Count);
        Console.WriteLine(counts.TryUpdate(3, 7, 100) + " " + counts.TryUpdate(3, 7, 99) + " " + counts.TryUpdate(3, 7, 100) + " " + counts[3]);

        var bag = new ConcurrentBag<string>();
        Parallel.ForEach(new[] { "a", "b", "c", "d" }, s => bag.Add(s));
        Console.WriteLine(string.Concat(bag.OrderBy(s => s)) + " " + bag.Count);
        var single = new ConcurrentBag<int> { };
        single.Add(1); single.Add(2); single.Add(3);
        single.TryTake(out var top);
        Console.WriteLine(top);

        var st = new ConcurrentStack<int>();
        st.Push(1); st.Push(2);
        st.TryPop(out var p);
        st.TryPeek(out var pk);
        Console.WriteLine(p + " " + pk + " " + st.Count);
        try { Console.WriteLine(counts[-1]); } catch (System.Collections.Generic.KeyNotFoundException e) { Console.WriteLine(e.Message); }
    }
}

// tools/corelibs-check.mjs: a program that uses System.Linq (methods and a query) and no Console.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

static class Program
{
    record Item(string Name, int Size);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        var items = Enumerable.Range(0, 50).Select(i => new Item("n" + (i % 7), i * 3 % 11)).ToList();
        var q = from it in items
                where it.Size > 2
                group it by it.Name into g
                orderby g.Key descending
                select new { g.Key, Total = g.Sum(x => x.Size), Max = g.Max(x => x.Size) };
        int sum = 0;
        foreach (var r in q) sum += r.Total * 3 + r.Max + r.Key.Length;
        sum += items.OrderBy(x => x.Size).ThenBy(x => x.Name).Skip(5).Take(10).Aggregate(0, (a, x) => a + x.Size);
        sum += items.ToDictionary(x => x.Name + x.Size + items.IndexOf(x)).Count + items.Zip(items.AsEnumerable().Reverse(), (a, b) => a.Size - b.Size).Count(d => d > 0);
        return sum;
    }
}

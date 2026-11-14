using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async IAsyncEnumerable<int> Numbers(int n)
    {
        for (int i = 0; i < n; i++)
        {
            await Task.Delay(1);
            yield return i * i;
        }
    }

    static async IAsyncEnumerable<string> Words()
    {
        try
        {
            yield return "a";
            await Task.Yield();
            yield return "b";
        }
        finally { Console.WriteLine("words finally"); }
    }

    static async IAsyncEnumerable<int> Failing()
    {
        yield return 1;
        await Task.Delay(1);
        throw new InvalidOperationException("stream broke");
    }

    static async IAsyncEnumerable<int> Nested()
    {
        try
        {
            yield return 1;
            try { await Task.Delay(1); yield return 2; yield return 3; }
            finally { await Task.Delay(1); Console.WriteLine("inner async finally"); }
        }
        finally { Console.WriteLine("outer async finally"); }
    }

    static IEnumerable<int> SyncNested()
    {
        try
        {
            yield return 10;
            try { yield return 20; }
            finally { Console.WriteLine("sync inner finally"); }
        }
        finally { Console.WriteLine("sync outer finally"); }
    }

    sealed class Resource : IAsyncDisposable
    {
        public string Name;
        public Resource(string n) { Name = n; }
        public async ValueTask DisposeAsync() { await Task.Delay(1); Console.WriteLine("disposed " + Name); }
    }

    static async Task<int> Run()
    {
        int sum = 0;
        await foreach (var x in Numbers(5)) sum += x;
        Console.WriteLine("sum " + sum);
        var all = new List<string>();
        await foreach (var w in Words()) all.Add(w);
        Console.WriteLine(string.Join("+", all));
        try { await foreach (var f in Failing()) Console.WriteLine("got " + f); }
        catch (InvalidOperationException e) { Console.WriteLine("caught " + e.Message); }
        await using (var r = new Resource("one")) Console.WriteLine("using " + r.Name);
        await using var r2 = new Resource("two");
        await foreach (var x in Numbers(3).WithCancellation(CancellationToken.None)) Console.Write(x + " ");
        Console.WriteLine();
        var e2 = Numbers(2).GetAsyncEnumerator();
        while (await e2.MoveNextAsync()) Console.Write("[" + e2.Current + "]");
        Console.WriteLine(await e2.MoveNextAsync());
        await e2.DisposeAsync();
        // leaving early runs the iterator's finally blocks (DisposeAsync / Dispose)
        await foreach (var w in Words()) { Console.WriteLine("first " + w); break; }
        await foreach (var w in Nested()) { Console.WriteLine("nested " + w); if (w == 2) break; }
        foreach (var v in SyncNested()) { Console.WriteLine("sync " + v); break; }
        Console.WriteLine(System.Linq.Enumerable.First(SyncNested()));
        using (var it = SyncNested().GetEnumerator()) { it.MoveNext(); it.MoveNext(); Console.WriteLine("at " + it.Current); }
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Run().Result);
    }
}

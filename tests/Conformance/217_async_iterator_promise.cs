// An async iterator whose every step gives up its thread (await Task.Yield()), read by `await foreach` on pool
// threads: the thread that completes a step's promise and the consumer that reads the completed promise and asks for
// the next step run at once. The iterator's field held the only reference to the promise, the consumer's next
// MoveNextAsync let it go, and the completing thread was still inside the promise's TrySetResult (test 087's rare
// trap under load, `operation does not support unaligned accesses` in Interlocked.Exchange, made frequent: many
// steps, several iterators at once). Also an iterator that ends by throwing.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class P
{
    static async IAsyncEnumerable<int> Count(int n, bool fail)
    {
        for (int i = 0; i < n; i++) { await Task.Yield(); yield return i; }
        if (fail) { await Task.Yield(); throw new InvalidOperationException("end " + n); }
    }

    static async Task<long> Sum(int n, bool fail)
    {
        long s = 0;
        try { await foreach (var v in Count(n, fail)) s += v; }
        catch (InvalidOperationException e) { s = -s - e.Message.Length; }
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var tasks = new Task<long>[8];
        for (int round = 0; round < 3; round++)
        {
            for (int t = 0; t < tasks.Length; t++) { int n = 4000 + t, k = t; tasks[t] = Task.Run(() => Sum(n, k % 3 == 2)); }
            long total = 0;
            foreach (var task in tasks) total += task.Result;
            Console.WriteLine(total);
        }
        // (and short ones, where the first step's promise is complete before the consumer awaits it)
        long shorts = 0;
        for (int i = 0; i < 300; i++) shorts += Sum(i % 7, i % 5 == 0).Result;
        Console.WriteLine(shorts);
    }
}

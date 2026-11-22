// Threads that use, at once, what another thread has just made in pages the memory has grown by: a thread's instance
// learns of a growth later than the thread that grew (Wasm/MemSync.cs: before an atomic access, a copy or a fill, the
// instance is brought up to date when the address is past the size it knows; without it such an access trapped,
// `memory access out of bounds`, in code the engine had not optimized yet, rarely: test 139, one loaded run in tens
// of thousands). One thread makes the memory grow several times (arrays of 16 MB, kept) and after each growth hands
// the others the new array, new small objects and new arrays of them; the others, started before the first growth
// and running through all of them, count the objects they read (a read that counts is an atomic operation on the
// object), add to the new array's last element by Interlocked, lock the objects, copy from the new arrays and clear
// them. What is printed does not depend on which thread ran when.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Box
{
    public long V; public Box Next; public long[] Tail;
    public Box(long v) { V = v; }
}

static class P
{
    const int Readers = 6, Growths = 6, Rounds = 30000;
    static long[] s_big;
    static readonly Box[] s_boxes = new Box[16];
    static Box[] s_fresh;
    static readonly long[] s_totals = new long[Readers];
    static int s_grown, s_started;
    static long s_locked;

    static void Reader(object arg)
    {
        int id = (int)arg;
        Interlocked.Increment(ref s_started);
        long sum = 0;
        var scratch = new long[32];
        for (int i = 0; i < Rounds || Volatile.Read(ref s_grown) < Growths; i++)
        {
            var big = Volatile.Read(ref s_big);
            if (big != null)
            {
                // (the last elements: in the pages the memory grew by)
                Interlocked.Add(ref big[big.Length - 1], 1);
                Interlocked.Increment(ref big[big.Length - 2 - id]);
                Array.Copy(big, big.Length - 64, scratch, 0, 32);
                sum += scratch[31 - 1 - id] >= 0 ? 0 : 1;
            }
            var b = s_boxes[(i + id) & 15];
            if (b != null)
            {
                sum += b.V & 0;
                var n = b.Next;
                if (n != null) sum += n.V & 0;
                var t = b.Tail;
                if (t != null) { Array.Clear(scratch, 0, 8); Array.Copy(t, 0, scratch, 8, t.Length < 8 ? t.Length : 8); sum += scratch[8] & 0; }
                if ((i & 63) == 0) lock (b) { s_locked++; }
            }
            var fresh = Volatile.Read(ref s_fresh);
            if (fresh != null)
            {
                var f = fresh[(i + id) % fresh.Length];
                if (f != null) sum += Interlocked.Read(ref f.V) & 0;
            }
            if (i < Rounds) sum++;
        }
        s_totals[id] = sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var threads = new Thread[Readers];
        for (int t = 0; t < Readers; t++) { threads[t] = new Thread(Reader); threads[t].Start(t); }
        while (Volatile.Read(ref s_started) < Readers) Thread.Yield();
        var kept = new List<long[]>();
        long made = 0;
        for (int g = 0; g < Growths; g++)
        {
            // (16 MB: more than is free, so the memory grows; what follows is made in the new pages or after them)
            var big = new long[2_000_000];
            kept.Add(big);
            var fresh = new Box[64];
            for (int k = 0; k < fresh.Length; k++) { fresh[k] = new Box(g * 100 + k) { Tail = new long[4 + (k & 7)] }; made++; }
            for (int k = 0; k < s_boxes.Length; k++) s_boxes[k] = new Box(k) { Next = fresh[k], Tail = new long[9000] };
            Volatile.Write(ref s_fresh, fresh);
            Volatile.Write(ref s_big, big);
            for (int spin = 0; spin < 200; spin++) Thread.Yield();
            Interlocked.Increment(ref s_grown);
        }
        foreach (var t in threads) t.Join();
        long total = 0, counted = 0;
        foreach (var x in s_totals) total += x;
        // (every increment of a big array's elements is there: none lost, none written elsewhere)
        foreach (var big in kept)
        {
            long last = big[big.Length - 1], each = 0;
            for (int id = 0; id < Readers; id++) each += big[big.Length - 2 - id];
            counted += last == each ? 0 : 1;
            for (int k = 0; k < big.Length - 2 - Readers; k += 4096) counted += big[k];
        }
        Console.WriteLine("readers " + Readers + " total " + total + " growths " + kept.Count + " made " + made + " miscounted " + counted + " locked " + (s_locked > 0));
    }
}

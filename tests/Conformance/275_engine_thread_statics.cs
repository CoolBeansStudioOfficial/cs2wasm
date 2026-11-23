// Threads (section 4.5): a static that only the engine thread's code names is that thread's own (its objects are
// counted as in a program without threads), and a static that a worker's code names is every thread's: the pallets
// below live in statics only Main's code touches, the crates in statics the workers store and read too: a crate made
// by one thread is counted and let go by another. Whatever the interleaving, every object is freed exactly once.
// CS2WASM_EXP_ALL_ENGINE_STATICS=1 takes the crates' statics for the engine thread's too: with
// CS2WASM_SHARE_CHECK=1 the first crate a worker counts then stops the program (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Pallet
{
    public static int Live;
    public int V;
    public Pallet? Under;
    public Pallet(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Pallet() { Interlocked.Decrement(ref Live); }
}

sealed class Crate
{
    public static int Live;
    public int V;
    public Crate? Inner;
    public Crate(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Crate() { Interlocked.Decrement(ref Live); }
}

static class Program
{
    // the engine thread's own
    static List<Pallet> s_mine = new();
    static Pallet? s_last;
    static Pallet[] s_row = new Pallet[4];
    // every thread's
    static Crate?[] s_shared = new Crate?[8];
    static Crate? s_hand;

    static long Stack(int rounds)
    {
        long sum = 0;
        for (int r = 0; r < rounds; r++)
        {
            var p = new Pallet(r) { Under = s_last };
            s_mine.Add(p);
            s_last = s_mine[r / 2 % s_mine.Count];
            s_row[r & 3] = p;
            sum += s_last.V + (s_row[(r + 1) & 3]?.V ?? 0);
            if (p.Under != null && p.Under.Under != null) p.Under.Under = null;
            if (s_mine.Count > 64) s_mine.RemoveRange(0, 32);
        }
        return sum;
    }

    static long Race(int t, int rounds)
    {
        long n = 0;
        for (int i = 0; i < rounds; i++)
        {
            // (under a lock: an unsynchronized read racing a store of an object with a finalizer hangs about one
            // loaded run in three hundred, with the base's compiler too: tools/thread-notes-hang.cs. Test 139 has
            // the racing stores)
            lock (s_shared)
            {
                var seen = s_shared[(i + t) & 7];
                var c = new Crate(i) { Inner = seen };
                s_shared[(i * 3 + t) & 7] = c;
                s_hand = seen;
                var h = s_hand;
                if (h != null && h.V >= 0) n++;
                if (seen != null && seen.Inner != null) seen.Inner = null;
                n += c.V >= 0 ? 1 : 0;
            }
        }
        return n >= rounds ? rounds : -1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var results = new long[3];
        var threads = new Thread[3];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => results[k] = Race(k + 1, 20000));
            threads[t].Start();
        }
        long mine = Stack(30000);
        long raced = Race(0, 20000);
        foreach (var th in threads) th.Join();
        Console.WriteLine(mine + " " + raced + " " + results[0] + " " + results[1] + " " + results[2]);
        Console.WriteLine(s_mine.Count + " " + (s_last != null) + " " + s_row[0].V);
        s_mine.Clear(); s_last = null;
        for (int i = 0; i < s_row.Length; i++) s_row[i] = null!;
        for (int i = 0; i < s_shared.Length; i++) s_shared[i] = null;
        s_hand = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(Pallet.Live + " " + Crate.Live);
    }
}

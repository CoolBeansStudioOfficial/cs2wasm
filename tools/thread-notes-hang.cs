// A program that hangs in about one loaded run in a hundred (node tools/stress.mjs, 8 at once: 5 of 400 runs print
// nothing and are stopped; the base's compiler d430594 the same, 5 of 300 of the whole test): four threads read and
// replace the elements of an array a static holds, unsynchronized, and the elements' class has a finalizer. Found
// by test 277's first form on the homelab (a timeout in the suite). What takes it away, each 0 of 400: no
// finalizer; the read and the store under a lock; the store without the read. One run in 600 ended with a note
// left over instead ("0 1": never finalized). With IL2WASM_DEADLOCK_DUMP=1 the hung runs say nothing (not a wait
// for the runtime lock). Not looked into further: the read
// that counts, the last release and the finalizer's hold are the runtime's (Limbo, rt.load_retain, rt.store_ref).
//   il2wasm tools/thread-notes-hang.cs -o hang.wasm; node tools/stress.mjs hang.wasm --runs 400 --par 8 --timeout 20000
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Leaf
{
    public static int Live;
    public int V;
    public Leaf(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Leaf() { Interlocked.Decrement(ref Live); }
}

sealed class Note
{
    public static int Live;
    public int V;
    public Note(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Note() { Interlocked.Decrement(ref Live); }
}

static class Program
{
    static readonly Leaf[] s_noLeaves = new Leaf[0];
    static readonly Note[] s_notes = new Note[8];

    static long Work(int t)
    {
        // a list of this thread's own, and an array that starts as the shared empty one
        var mine = new List<Leaf>();
        long sum = 0;
        for (int i = 0; i < 20000; i++)
        {
            if (i < 20) mine.Add(new Leaf(i & 7));
            // the shared notes
            var n = s_notes[(i + t) & 7];
            if (n != null && n.V >= 0) sum += 0;
            s_notes[(i * 5 + t) & 7] = new Note(i);
        }
        return sum >= 0 ? mine.Count : -1;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var results = new long[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => results[k] = Work(k));
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
        Console.WriteLine(results[0] + " " + results[1] + " " + results[2] + " " + results[3] + " " + s_noLeaves.Length);
        for (int i = 0; i < s_notes.Length; i++) s_notes[i] = null!;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(Leaf.Live + " " + Note.Live);
    }
}

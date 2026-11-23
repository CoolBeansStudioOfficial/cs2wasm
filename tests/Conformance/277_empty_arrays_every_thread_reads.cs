// Threads (section 4.5): an array made with no element can be reached by every thread (List<T>'s empty array is a
// static each list starts with) and nothing can be reached through it: each worker below fills a list of its own
// with objects of its own, which stay that thread's. An array with elements that a static holds hands its elements
// to every thread: the workers also read and replace the notes of one, each made by one thread and let go by another.
// IL2WASM_EXP_EVERY_ARRAY_EMPTY=1 takes every array made for one with no element: with IL2WASM_SHARE_CHECK=1 a note
// read from the shared array stops the program (tools/thread-escape-breaks.sh).
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
        Leaf[] row = s_noLeaves;
        long sum = row.Length;
        for (int i = 0; i < 20000; i++)
        {
            mine.Add(new Leaf(i & 7));
            if (mine.Count > 40) { sum += mine[3].V; mine.RemoveRange(0, 30); }
            if ((i & 1023) == 0) { row = new Leaf[4]; row[i & 3] = mine[mine.Count - 1]; }
            if (row.Length > 0 && row[i & 3] != null) sum += row[i & 3].V >= 0 ? 1 : 0;
            // the shared notes (under a lock: an unsynchronized read racing a store of an object with a finalizer
            // hangs about one loaded run in a hundred, with the base's compiler too: tools/thread-notes-hang.cs)
            lock (s_notes)
            {
                var n = s_notes[(i + t) & 7];
                if (n != null && n.V >= 0) sum += 0;
                s_notes[(i * 5 + t) & 7] = new Note(i);
            }
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

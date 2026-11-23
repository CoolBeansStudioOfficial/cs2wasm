// Threads (section 4.5): four threads read and replace, with no lock, the elements of an array a static holds, and
// the elements' class has a finalizer. A data race, which C# allows: every object is still finalized exactly once
// and freed. A read that counts takes no lock (rt.load_retain): it may hold a reference read just before another
// thread's store let the object go, and count it while its finalizer runs under a temporary count; the count is then
// given back, and the object goes at that release, not finalized again. Before the temporary count was taken back
// by an exchange, a plain store of 0 lost such a count, whose release then fell on the next object made in the
// freed block: a place held an object that was gone, and the read spun on it for good (tools/thread-notes-hang.cs,
// about one loaded run in 16), or a note was never finalized ("0 1"), or the program trapped.
// CS2WASM_NO_FIN_EXCHANGE=1 puts the plain store back (the finalizer below is slow, so its window is wide).
using System;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Note
{
    public static int Live, Finalized, Made;
    public int V;
    public Note(int v) { V = v; Interlocked.Increment(ref Live); Interlocked.Increment(ref Made); }
    ~Note()
    {
        // (a while: another thread's read that counts meets the object in it)
        int w = 0;
        for (int i = 0; i < 64; i++) w += (i ^ V) & 1;
        if (w < 0) Console.WriteLine("never");
        Interlocked.Decrement(ref Live);
        Interlocked.Increment(ref Finalized);
    }
}

static class Program
{
    static readonly Note[] s_notes = new Note[4];

    static long Work(int t)
    {
        long sum = 0;
        for (int i = 0; i < 40000; i++)
        {
            var n = s_notes[(i + t) & 3];
            if (n != null) sum += n.V & 1;
            s_notes[(i * 5 + t) & 3] = new Note(i);
        }
        return sum >= 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int round = 0; round < 24; round++)
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
            Console.WriteLine("round " + round + ": " + (results[0] + results[1] + results[2] + results[3]));
        }
        for (int i = 0; i < s_notes.Length; i++) s_notes[i] = null!;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("made " + Note.Made + ", finalized " + Note.Finalized + ", living " + Note.Live);
    }
}

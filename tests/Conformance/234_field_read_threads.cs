// A program with threads: a field another thread stores is read again on every turn of a loop (Wasm/FieldLoads.cs
// reads no field once there, but a readonly one and an array's length: the loop below that waits, under a lock, for
// a field another thread sets would never end if its read were made once before it). The waits are under locks or
// through Volatile and Interlocked, so that .NET promises the read sees the store; what is printed does not depend
// on which thread ran when. Readonly fields and array lengths read in loops while other threads run, and a readonly
// field read in a loop of the constructor that stores it. An async method's loop across its awaits, and a loop
// around a method that awaits (a task's continuation stores the field): an async method's locals are fields of its
// machine.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Shared
{
    public int Flag, Counter, Stage, Async, Await;
    public bool Done;
    public Shared? Link;
    public readonly int Ro;
    public readonly int[] Data = new int[8];
    public readonly object Gate = new();
    public Shared(int n)
    {
        for (int i = 0; i < n; i++) { Ro += i; Counter += Ro; }
    }
}

static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReadFlag(Shared s) { lock (s.Gate) return s.Flag; }

    // waits for a field the other thread sets, the read under the lock that the store is under
    static int WaitLocked(Shared s)
    {
        int seen;
        var t = new Thread(() => { for (int i = 0; i < 2000; i++) Thread.SpinWait(20); lock (s.Gate) s.Flag = 7; });
        t.Start();
        while (true)
        {
            lock (s.Gate) { if (s.Flag != 0) { seen = s.Flag; break; } }
            Thread.Yield();
        }
        t.Join();
        return seen;
    }
    static int WaitLockedParam(Shared s)
    {
        var t = new Thread(Setter);
        t.Start(s);
        int seen = Spin(s);
        t.Join();
        return seen;
    }
    static void Setter(object? o)
    {
        var s = (Shared)o!;
        for (int i = 0; i < 2000; i++) Thread.SpinWait(20);
        lock (s.Gate) { s.Stage = 3; s.Done = true; s.Link = new Shared(2); }
    }
    // (s is a parameter no lambda captures: a plain local of the loop)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Spin(Shared s)
    {
        int turns = 0;
        for (; ; turns++)
        {
            Monitor.Enter(s.Gate);
            bool done = s.Done;
            int stage = s.Stage;
            var link = s.Link;
            Monitor.Exit(s.Gate);
            if (done) return stage + (link != null ? link.Ro * 10 : -1);
        }
    }
    // two threads add under the lock; the reader's loop sees the count grow
    static int Counting(Shared s)
    {
        const int Each = 20000;
        var ws = new Thread[2];
        for (int k = 0; k < ws.Length; k++) { ws[k] = new Thread(o => { var x = (Shared)o!; for (int i = 0; i < Each; i++) lock (x.Gate) x.Counter++; }); ws[k].Start(s); }
        int start = ReadCounter(s), last = start, grew = 0;
        while (last < start + 2 * Each) { int now = ReadCounter(s); if (now < last) return -1; if (now > last) grew = 1; last = now; }
        foreach (var w in ws) w.Join();
        return last - start + grew;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ReadCounter(Shared s) { lock (s.Gate) return s.Counter; }
    // readonly fields and lengths, read in loops while another thread writes the elements and the other fields
    static long Readonlys(Shared s)
    {
        var stop = new int[1];
        var t = new Thread(() => { while (Volatile.Read(ref stop[0]) == 0) { lock (s.Gate) { s.Stage++; } Interlocked.Increment(ref s.Data[3]); } });
        t.Start();
        long sum = 0;
        for (int i = 0; i < 50000; i++) sum += s.Ro + s.Data.Length;
        for (int i = 0; i < s.Data.Length; i++) sum += s.Ro * i;
        Volatile.Write(ref stop[0], 1);
        t.Join();
        return sum;
    }

    static async Task<int> Awaits(Shared b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Await; await Task.Yield(); b.Await += 5; s += b.Await; }
        for (int i = 0; i < 4; i++) { s += b.Await; await Bump(b); s += b.Await; }
        return s;
    }
    static async Task Bump(Shared b) { await Task.CompletedTask; b.Await += 3; }
    static int Asyncs(Shared b)
    {
        int s = 0;
        for (int i = 0; i < 3; i++) { s += b.Async; s += Later(b).GetAwaiter().GetResult(); s += b.Async; }
        return s + Awaits(b).GetAwaiter().GetResult();
    }
    static async Task<int> Later(Shared b) { b.Async += 2; await Task.Yield(); b.Async += 4; return b.Async; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var s = new Shared(5);
        Console.WriteLine("constructed " + s.Ro + " " + s.Counter);
        Console.WriteLine("locked " + WaitLocked(s) + " " + ReadFlag(s));
        Console.WriteLine("parameter " + WaitLockedParam(s));
        Console.WriteLine("counting " + Counting(s));
        Console.WriteLine("readonly " + Readonlys(s));
        Console.WriteLine("async " + Asyncs(s));
    }
}

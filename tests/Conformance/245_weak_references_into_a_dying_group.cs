// Weak references to the members of a group that is going. What .NET guarantees and this test holds: a short weak
// reference (WeakReference<T>, WeakReference, a Weak GC handle) to an object is cleared before any finalizer of
// the objects that became unreachable with it runs; so a finalizer of one member sees the weak references to
// every member of its dying group dead, its own included, whatever order the finalizers run in. (A weak reference
// that tracks resurrection stays until the object is gone: not asked here, its moment is the collector's.)
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Log { public int Alive, Dead, Runs; }

sealed class Member
{
    public Member? Next; public List<Member> All = new();
    public WeakReference<Member>[] Weak = Array.Empty<WeakReference<Member>>();
    public WeakReference? Old; public GCHandle Handle; public Log Log; public int Id;
    public Member(int id, Log log) { Id = id; Log = log; }
    ~Member()
    {
        Log.Runs++;
        foreach (var w in Weak) { if (w.TryGetTarget(out _)) Log.Alive++; else Log.Dead++; }
        if (Old != null) { if (Old.IsAlive || Old.Target != null) Log.Alive++; else Log.Dead++; }
        if (Handle.IsAllocated) { if (Handle.Target != null) Log.Alive++; else Log.Dead++; Handle.Free(); }
    }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<Member>[] Ring(int n, Log log, Member? under)
    {
        var members = new Member[n];
        for (int i = 0; i < n; i++) members[i] = new Member(i, log);
        var weak = new WeakReference<Member>[n];
        for (int i = 0; i < n; i++) weak[i] = new WeakReference<Member>(members[i]);
        for (int i = 0; i < n; i++)
        {
            members[i].Next = members[(i + 1) % n];
            members[i].Weak = weak;
            members[i].Old = new WeakReference(members[(i + 2) % n]);
            members[i].Handle = GCHandle.Alloc(members[(i + 3) % n], GCHandleType.Weak);
            members[0].All.Add(members[i]);
        }
        under?.All.Add(members[0]);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int AliveOf(WeakReference<Member>[] weak) { int n = 0; foreach (var w in weak) if (w.TryGetTarget(out _)) n++; return n; }

    static Member? s_top;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Cut() { s_top!.All.Clear(); }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        foreach (int n in new[] { 1, 2, 5, 40 })
        {
            var log = new Log();
            var weak = Ring(n, log, null);
            Collect();
            Console.WriteLine($"ring of {n}: finalizers {log.Runs}, saw alive {log.Alive}, dead {log.Dead}; alive after {AliveOf(weak)}");
        }
        // (the ring hangs under a member that lives on, and is cut from it)
        var keepLog = new Log();
        s_top = new Member(-1, keepLog);
        foreach (int n in new[] { 1, 3, 20 })
        {
            var log = new Log();
            var weak = Ring(n, log, s_top);
            Collect();
            Console.WriteLine($"under a living one, {n}: before the cut alive {AliveOf(weak)}, finalizers {log.Runs}");
            Cut();
            Collect();
            Console.WriteLine($"cut: finalizers {log.Runs}, saw alive {log.Alive}, dead {log.Dead}; alive after {AliveOf(weak)}");
        }
        Console.WriteLine($"the living one: finalizers {keepLog.Runs}");
    }
}

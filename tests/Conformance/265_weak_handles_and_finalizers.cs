// Weak handles and finalizers, as .NET orders them: a short weak reference (WeakReference<T>, WeakReference, a
// Weak GC handle) reads dead from the object's finalizer on; one that tracks resurrection (WeakTrackResurrection,
// WeakReference(o, true)) and a ConditionalWeakTable's entry (a dependent handle) stay until the object is freed,
// so a finalizer finds its own entry, and an object its finalizer brought back keeps its entry and its long weak
// references (until 2026-11-23 all of them were cleared before the finalizer). A table's value that is its own key
// goes with the key (it kept itself).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Plain
{
    public static int Live;
    public int Id; public object? Ref;
    public Plain(int id) { Id = id; Live++; }
    ~Plain() { Live--; }
}

sealed class Tag { public static int Live; public int N; public Tag(int n) { N = n; Live++; } ~Tag() { Live--; } }

sealed class Thing
{
    public static int Live, Finalized;
    public static Thing? Saved;
    public static List<string> Log = new();
    public static ConditionalWeakTable<Thing, Tag> Table = new();
    public static WeakReference<Thing>? Short; public static WeakReference? Long, Short2;
    public static GCHandle HShort, HLong;
    public int Id, Mode, Runs;
    public Thing? Peer;
    public Thing(int id, int mode) { Id = id; Mode = mode; Live++; }
    ~Thing()
    {
        Finalized++; Runs++;
        bool s1 = Short != null && Short.TryGetTarget(out _);
        bool s2 = Short2 != null && Short2.IsAlive;
        bool l = Long != null && Long.IsAlive;
        bool hs = HShort.IsAllocated && HShort.Target != null, hl = HLong.IsAllocated && HLong.Target != null;
        bool entry = Table.TryGetValue(this, out var tag);
        if (Mode != 9) Log.Add($"  finalizer of {Id}, run {Runs}: short {s1} {s2} long {l} handles {hs} {hl} entry {(entry ? tag!.N.ToString() : "none")}");
        if (Mode == 1 && Runs == 1) Saved = this;
        if (Mode == 2 && Runs <= 2) { Saved = this; GC.ReRegisterForFinalize(this); }
        Live--;
    }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 6; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static void Show(string what)
    {
        Collect();
        bool s1 = Thing.Short!.TryGetTarget(out _), l = Thing.Long!.IsAlive;
        // (the tags are not counted while a thing is saved: under .NET the tag of a thing that was finalized and came
        // back has been finalized too, having been unreachable with it, though the table still gives it)
        string saved = Thing.Saved == null ? "none" : Thing.Saved.Id + (Thing.Table.TryGetValue(Thing.Saved, out var t) ? " entry " + t.N : " no entry");
        Console.WriteLine($"{what}: things {Thing.Live} finalized {Thing.Finalized} tags {(Thing.Saved == null ? Tag.Live.ToString() : "-")} short {s1} long {l} handles {Thing.HShort.Target != null} {Thing.HLong.Target != null} saved {saved}");
        foreach (var line in Thing.Log) Console.WriteLine(line);
        Thing.Log.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Make(int id, int mode, bool pair)
    {
        var x = new Thing(id, mode);
        if (pair) { var y = new Thing(id + 1, 9) { Peer = x }; x.Peer = y; }
        Thing.Table.Add(x, new Tag(id * 10));
        Thing.Short = new WeakReference<Thing>(x); Thing.Short2 = new WeakReference(x); Thing.Long = new WeakReference(x, true);
        if (Thing.HShort.IsAllocated) { Thing.HShort.Free(); Thing.HLong.Free(); }
        Thing.HShort = GCHandle.Alloc(x, GCHandleType.Weak); Thing.HLong = GCHandle.Alloc(x, GCHandleType.WeakTrackResurrection);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Drop() { Thing.Saved = null; }

    // a table whose values are their own keys, or hold them
    static ConditionalWeakTable<Plain, object> s_self = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Selves(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var k = new Plain(i);
            s_self.Add(k, k);
            var k2 = new Plain(100 + i);
            s_self.Add(k2, "text " + i);
            var k3 = new Plain(200 + i);
            s_self.AddOrUpdate(k3, "first"); s_self.AddOrUpdate(k3, k3); s_self.AddOrUpdate(k3, "third"); s_self.AddOrUpdate(k3, k3);
            var k4 = new Plain(300 + i);
            s_self.Add(k4, k4); s_self.Remove(k4);
            var k5 = new Plain(400 + i);
            s_self.Add(k5, k5); s_self.Remove(k5); s_self.Add(k5, k5);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Handles(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var k = new Plain(500 + i);
            var h = new System.Runtime.DependentHandle(k, k);
            var (t, d) = h.TargetAndDependent;
            if (!ReferenceEquals(t, d) || !ReferenceEquals(h.Dependent, k)) Console.WriteLine("handle differs");
            if ((i & 1) == 0) h.Dispose();
            var k2 = new Plain(600 + i);
            var h2 = new System.Runtime.DependentHandle(k2, new Plain(700 + i));
            h2.Dependent = k2;
            if ((i & 1) == 1) h2.Dispose();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        for (int pair = 0; pair <= 1; pair++)
            for (int mode = 0; mode <= 2; mode++)
            {
                string what = (pair == 1 ? "a pair" : "one") + ", mode " + mode;
                Make(10 * mode + 100 * pair, mode, pair == 1);
                Show(what);
                Drop(); Show(what + ", let go");
                Drop(); Show(what + ", let go again");
                Drop(); Show(what + ", at the end");
            }
        Selves(20);
        Collect();
        Console.WriteLine($"values that are their own keys: {Plain.Live}");
        Handles(10);
        Collect();
        Console.WriteLine($"dependent handles: {Plain.Live}");
    }
}

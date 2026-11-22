// A field of its class's own type whose stores all go down the structure (the value read out of the container, or
// a new object filled from below it) closes no cycle, and its objects are plain counted ones: StringBuilder's chunks
// (m_ChunkPrevious), driven through every path that stores the field (growth, Insert in the middle, Remove across
// chunks, Length cut, Replace, Clear, EnsureCapacity, a builder handed around), its text and its chunks compared; and
// a chain class of the program's with the same stores (a new chunk made from `this`, a cut to what a walk found, a
// walk in a local, two cursors and `end != cur`, a new chunk wired in below, the cursor an `out` parameter).
// Then the stores that look the same and do close a cycle, each of which must still be freed when dropped: a ring
// (the last's link set to the first), a chunk linked to itself, the two cursors without the test when they are the
// same, a constructor that keeps the object it is given, a value from above through a local and through a field, a
// cursor that is a `ref` to a field another call rewires, a local a lambda rewires (a link set by reflection: test
// 214). Every chunk has a finalizer; after each part the chunks left are counted (.NET's collector frees them all).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

// (one class of chunks for each part, by a marker type: a store that closes a cycle in one part must not be what
// makes the others' field cyclic)
struct Grow { } struct Cut { } struct Ins { } struct Rem { } struct Loop { } struct Ring { } struct Self { } struct Same { } struct Pair { }
struct Above { } struct Held { } struct Ref { } struct RefLocal { } struct Lambda { }

sealed class Chunk<M> where M : struct
{
    public Chunk<M>? Prev;
    public int Id;
    public static int Ids, Live;

    public Chunk() { Id = ++Ids; Live++; }
    ~Chunk() { System.Threading.Interlocked.Decrement(ref Live); }
    // (as StringBuilder(StringBuilder from): what the new chunk holds is read out of `from`)
    Chunk(Chunk<M> from) { Id = ++Ids; Live++; Prev = from.Prev; }
    // (as StringBuilder(int, int, StringBuilder previousBlock))
    Chunk(int add, Chunk<M>? previous) { Id = ++Ids + add; Live++; Prev = previous; }
    // (keeps the object it is given: `x.Prev = new Chunk(x, true)` is a cycle of two)
    public Chunk(Chunk<M> keep, bool self) { Id = ++Ids; Live++; Prev = keep; }

    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return typeof(M).Name + " " + Live; }
    public string Show() { var sb = new StringBuilder(); int n = 0; for (var c = this; c != null && n < 12; c = c.Prev, n++) sb.Append(c.Id).Append(c.Prev != null ? ">" : ""); return sb.ToString(); }
    public static Chunk<M> Build(int n) { var head = new Chunk<M>(); for (int i = 1; i < n; i++) head.Expand(); return head; }

    public void Expand() { Prev = new Chunk<M>(this); }
    Chunk<M> Find(int steps) { Chunk<M> result = this; while (steps-- > 0 && result.Prev != null) result = result.Prev; return result; }
    public void CutTo(int steps) { Chunk<M> c = Find(steps); if (c != this) Prev = c.Prev; }
    public void CutWalk(int steps) { var c = this; while (steps-- > 0 && c.Prev != null) c = c.Prev; Prev = c.Prev; }
    public void Insert(int steps, out Chunk<M> at)
    {
        at = this;
        while (steps-- > 0 && at.Prev != null) at = at.Prev;
        Chunk<M> made = new Chunk<M>(100, at.Prev);
        made.Id += 100;
        at.Prev = made;
    }
    // (as StringBuilder.Remove: two cursors down the chain, the chunks between them dropped)
    public void Remove(int from, int to, out Chunk<M> cur)
    {
        cur = this;
        Chunk<M>? end = null;
        int i = 0;
        while (true)
        {
            if (i >= from)
            {
                if (end == null) end = cur;
                if (i >= to || cur.Prev == null) break;
            }
            if (cur.Prev == null) break;
            cur = cur.Prev;
            i++;
        }
        if (end != null && end != cur)
        {
            end.Prev = cur;
            if (to > 100) end.Prev = cur.Prev;
        }
    }
    // (the same without the test: `end` and `cur` the same chunk is that chunk linked to itself)
    public void RemoveUntested(int from, out Chunk<M> cur)
    {
        cur = this;
        Chunk<M>? end = null;
        for (int i = 0; ; i++)
        {
            if (i >= from) { if (end == null) end = cur; break; }
            if (cur.Prev == null) break;
            cur = cur.Prev;
        }
        if (end != null) end.Prev = cur;
    }
    public Chunk<M> Last() { var c = this; while (c.Prev != null) c = c.Prev; return c; }
    public void TieLastTo(Chunk<M> first) { var last = Last(); last.Prev = first; }
    public void TieSelf() { var me = this; Prev = me; }
    // (the cursor a `ref`: what it names may be a field that the call in the middle rewires)
    public static void Splice(Chunk<M> x, ref Chunk<M>? y, Action rewire)
    {
        y = x.Prev;
        rewire();
        if (x != y) x.Prev = y;
    }
    // (a local a lambda rewires between the read and the store)
    public static void SpliceLambda(Chunk<M> x, Chunk<M> top)
    {
        Chunk<M>? c = x.Prev;
        Action up = () => c = top;
        up();
        if (x != c) x.Prev = c;
    }
}
sealed class Holder<M> where M : struct { public Chunk<M>? Slot; }

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Down()
    {
        var a = Chunk<Grow>.Build(6);
        Console.WriteLine("grown " + a.Show());
        var k = Chunk<Cut>.Build(6);
        k.CutTo(2); Console.WriteLine("cut to " + k.Show());
        k.CutWalk(1); Console.WriteLine("cut walk " + k.Show());
        var b = Chunk<Ins>.Build(4);
        b.Insert(1, out var at); Console.WriteLine("insert " + b.Show() + " at " + at.Id);
        b.Insert(9, out at); Console.WriteLine("insert last " + b.Show() + " at " + at.Id);
        var c = Chunk<Rem>.Build(8);
        c.Remove(1, 4, out var cur); Console.WriteLine("remove " + c.Show() + " cur " + cur.Id);
        c.Remove(0, 200, out cur); Console.WriteLine("remove all " + c.Show() + " cur " + cur.Id);
        c.Remove(0, 0, out cur); Console.WriteLine("remove none " + c.Show() + " cur " + cur.Id);
        var d = Chunk<Loop>.Build(3);
        for (int i = 0; i < 50; i++) { d.Expand(); if (i % 3 == 0) d.CutTo(1); if (i % 7 == 0) d.Insert(2, out _); if (i % 5 == 0) d.Remove(1, 3, out _); }
        Console.WriteLine("loop " + d.Show());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rings()
    {
        // the last's link set to the first
        var ring = Chunk<Ring>.Build(5);
        ring.TieLastTo(ring);
        Console.WriteLine("ring " + ring.Show());
        // a chunk linked to itself, and the two cursors on one chunk
        var self = Chunk<Self>.Build(2); self.TieSelf();
        Console.WriteLine("self " + self.Show());
        var same = Chunk<Same>.Build(3);
        same.RemoveUntested(1, out var cur);
        Console.WriteLine("same " + same.Show() + " cur " + cur.Id);
        // a constructor that keeps what it is given
        var pair = Chunk<Pair>.Build(2);
        pair.Prev = new Chunk<Pair>(pair, true);
        Console.WriteLine("pair " + pair.Show());
        // a value from above, through a local; through a field
        var top = Chunk<Above>.Build(4);
        var low = top.Last();
        Chunk<Above> viaLocal = top;
        low.Prev = viaLocal;
        Console.WriteLine("above " + top.Show());
        var h = new Holder<Held> { Slot = Chunk<Held>.Build(3) };
        h.Slot!.Last().Prev = h.Slot;
        Console.WriteLine("held " + h.Slot.Show());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Aliases()
    {
        // the cursor names a field; the call in the middle puts the top of the chain there
        var top = Chunk<Ref>.Build(4);
        var x = top.Prev!.Prev!;
        var h = new Holder<Ref>();
        Chunk<Ref>.Splice(x, ref h.Slot, () => h.Slot = top);
        Console.WriteLine("ref " + top.Show());
        // the same with a local of the caller's: nothing rewires it, the store goes down
        var top2 = Chunk<RefLocal>.Build(4);
        Chunk<RefLocal>? y = null;
        Chunk<RefLocal>.Splice(top2, ref y, () => { });
        Console.WriteLine("ref local " + top2.Show() + " y " + y!.Id);
        var top3 = Chunk<Lambda>.Build(4);
        Chunk<Lambda>.SpliceLambda(top3.Prev!.Prev!, top3);
        Console.WriteLine("lambda " + top3.Show());
    }

    static void Report(string when) =>
        Console.WriteLine(when + ": " + string.Join(", ", Chunk<Grow>.Left(), Chunk<Cut>.Left(), Chunk<Ins>.Left(), Chunk<Rem>.Left(), Chunk<Loop>.Left(), Chunk<Ring>.Left(), Chunk<Self>.Left(), Chunk<Same>.Left(),
            Chunk<Pair>.Left(), Chunk<Above>.Left(), Chunk<Held>.Left(), Chunk<Ref>.Left(), Chunk<RefLocal>.Left(), Chunk<Lambda>.Left()));

    // (not GetChunks: its enumerator passes a builder by reference from a field, and the chunks then count as any cyclic type: test 214)
    static string Chunks(StringBuilder sb) => sb.Capacity + "/" + sb.MaxCapacity;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Builders()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append("stone x").Append(i).Append(": ").Append(i * 37).Append('|');
        Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.ToString(0, 30));
        sb.Insert(0, "<<").Insert(sb.Length / 2, new string('m', 70)).Insert(17, 'q').Insert(sb.Length, ">>");
        Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.ToString(0, 30));
        sb.Remove(5, 200).Remove(0, 1).Remove(sb.Length - 3, 3);
        Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.ToString(0, 30));
        sb.Replace("stone", "st").Replace('x', 'X').Replace("|", "||", 10, 60);
        Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.ToString(0, 30));
        sb.Length = 150; Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.ToString(120, 30));
        sb.Length = 7; Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb);
        sb.Length = 40; sb.EnsureCapacity(500); sb.Append('z', 300);
        Console.WriteLine(sb.Length + " " + Chunks(sb) + " " + sb.Capacity + " " + sb.ToString(36, 8).Replace('\0', '0'));
        sb.Clear(); Console.WriteLine(sb.Length + " " + Chunks(sb) + " [" + sb + "]");
        var parts = new List<StringBuilder>();
        for (int i = 0; i < 6; i++) { var p = new StringBuilder(4); for (int k = 0; k <= i * 9; k++) p.Append((char)('a' + k % 26)); parts.Add(p); }
        var all = new StringBuilder();
        foreach (var p in parts) { all.Append(p).Append('/'); p.Insert(1, all.ToString(), 1).Remove(2, Math.Min(5, p.Length - 2)); }
        Console.WriteLine(all.Length + " " + Chunks(all) + " " + all.ToString(0, 40) + " " + string.Join(",", parts.Select(p => p.Length)) + " " + all.Equals(new StringBuilder(all.ToString())));
        long sum = 0;
        for (int r = 0; r < 300; r++) { var b = new StringBuilder(); for (int i = 0; i < 40; i++) b.Append("ore x").Append(i).Append('|'); b.Remove(r % 50, 20).Insert(r % 30, "abc"); sum += b.Length + b[r % b.Length]; }
        Console.WriteLine(sum);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Down(); Report("down");
        Rings(); Report("rings");
        Aliases(); Report("aliases");
        Builders();
    }
}

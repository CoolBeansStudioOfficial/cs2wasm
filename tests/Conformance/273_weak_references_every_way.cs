// Weak references and GC handles without finalizers in play: targets set again and set to null, several references
// to one object, a reference let go before its target, targets that are arrays, boxes, delegates, members of a
// cycle and parts cut from a tree that lives on; Normal and Pinned handles keep their target, Weak ones do not;
// handles through IntPtr and back.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node
{
    public Node? Next, Parent; public List<Node> Kids = new(); public int Id; public int[]? Data;
    public Node(int id) { Id = id; }
}

static class Program
{
    static readonly List<WeakReference<Node>> s_weak = new();
    static readonly List<WeakReference> s_plain = new();
    static readonly List<GCHandle> s_handles = new();
    static Node s_root = new Node(0);
    static object? s_keep;
    static void Collect() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }

    static string State()
    {
        Collect();
        var parts = new List<string>();
        foreach (var w in s_weak) parts.Add(w.TryGetTarget(out var n) ? n.Id.ToString() : "-");
        foreach (var w in s_plain) parts.Add(w.IsAlive ? (w.Target is Node n ? "n" + n.Id : w.Target is Array a ? "a" + a.Length : w.Target is Delegate ? "d" : w.Target!.ToString()!) : "-");
        foreach (var h in s_handles) parts.Add(!h.IsAllocated ? "free" : h.Target is Node n ? "h" + n.Id : h.Target == null ? "h-" : "h" + h.Target);
        return string.Join(" ", parts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Make()
    {
        var a = new Node(1); var b = new Node(2);
        s_weak.Add(new WeakReference<Node>(a)); s_weak.Add(new WeakReference<Node>(a)); s_weak.Add(new WeakReference<Node>(b));
        // a cycle of two, and a ring of five
        var c = new Node(3); var d = new Node(4); c.Next = d; d.Next = c;
        s_weak.Add(new WeakReference<Node>(c)); s_weak.Add(new WeakReference<Node>(d));
        var first = new Node(10); var prev = first;
        for (int i = 11; i < 15; i++) { var x = new Node(i); prev.Next = x; prev = x; s_weak.Add(new WeakReference<Node>(x)); }
        prev.Next = first;
        // a part of the living tree, and one that will be cut from it
        var kept = new Node(20) { Parent = s_root }; s_root.Kids.Add(kept);
        var cut = new Node(21) { Parent = s_root }; s_root.Kids.Add(cut);
        var under = new Node(22) { Parent = cut }; cut.Kids.Add(under); under.Kids.Add(new Node(23) { Parent = under, Next = cut });
        s_weak.Add(new WeakReference<Node>(kept)); s_weak.Add(new WeakReference<Node>(cut)); s_weak.Add(new WeakReference<Node>(under)); s_weak.Add(new WeakReference<Node>(under.Kids[0]));
        // other kinds of target
        s_plain.Add(new WeakReference(new int[7])); s_plain.Add(new WeakReference(new Node[2, 3])); s_plain.Add(new WeakReference((object)12345));
        Func<int> f = () => a.Id; s_plain.Add(new WeakReference(f)); s_plain.Add(new WeakReference("a literal")); s_plain.Add(new WeakReference(null));
        s_plain.Add(new WeakReference(s_root)); s_plain.Add(new WeakReference(b, false));
        // handles
        s_handles.Add(GCHandle.Alloc(new Node(30), GCHandleType.Weak));
        s_handles.Add(GCHandle.Alloc(new Node(31), GCHandleType.Normal));
        s_handles.Add(GCHandle.Alloc(new Node(32) { Data = new int[3] }, GCHandleType.WeakTrackResurrection));
        s_handles.Add(GCHandle.Alloc(b, GCHandleType.Weak));
        s_handles.Add(GCHandle.FromIntPtr(GCHandle.ToIntPtr(GCHandle.Alloc(new Node(33)))));
        s_keep = b;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Change()
    {
        s_weak[0].SetTarget(new Node(40));
        var n = new Node(41); s_weak[1].SetTarget(n); s_keep = n;
        s_weak[2].SetTarget(null!);
        s_plain[0].Target = s_root; s_plain[6].Target = null; s_plain[5].Target = new Node(42);
        var h = s_handles[0]; h.Target = s_root; s_handles[0] = h;
        var h1 = s_handles[1]; h1.Target = new Node(43); s_handles[1] = h1;
        var h3 = s_handles[3]; h3.Target = null; s_handles[3] = h3;
        s_handles[4].Free(); s_handles[4] = default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static void Cut() { s_root.Kids.RemoveAt(1); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Pinned()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var weak = new WeakReference(bytes);
        unsafe { byte* p = (byte*)h.AddrOfPinnedObject(); p[1] = 9; Console.WriteLine("pinned: " + p[0] + p[1] + bytes[1] + " " + (h.Target == bytes)); }
        s_pin = h; s_pinWeak = weak;
    }
    static GCHandle s_pin; static WeakReference? s_pinWeak;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpin() { s_pin.Free(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Many()
    {
        // references that go before their targets
        var target = new Node(50);
        for (int i = 0; i < 100; i++) { var w = new WeakReference<Node>(target); var w2 = new WeakReference(target, i % 2 == 0); var gh = GCHandle.Alloc(target, GCHandleType.Weak); gh.Free(); }
        Collect();
        var last = new WeakReference<Node>(target);
        Console.WriteLine("after a hundred references: " + last.TryGetTarget(out var t) + " " + t!.Id);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Make();
        Console.WriteLine("returned: " + State());
        Change();
        Console.WriteLine("changed: " + State());
        Cut();
        Console.WriteLine("cut: " + State());
        s_keep = null;
        Console.WriteLine("let go: " + State());
        foreach (var h in s_handles) if (h.IsAllocated) h.Free();
        s_handles.Clear();
        Console.WriteLine("handles freed: " + State());
        Pinned();
        Collect();
        Console.WriteLine("pinned, let go by the code: " + s_pinWeak!.IsAlive);
        Unpin();
        Collect();
        Console.WriteLine("handle freed: " + s_pinWeak.IsAlive + " " + s_pin.IsAllocated);
        Many();
        s_root = new Node(-1);
        Console.WriteLine("root replaced: " + State());
    }
}

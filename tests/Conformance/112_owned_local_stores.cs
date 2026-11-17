// Stores through local variables that own their objects (section 4.3: a store closes a cycle only if something
// refers to the object stored into), lists and trees let go link by link, and what is freed when: finalizers count.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node
{
    public static int Freed;
    public Node? Next, Other;
    public string Name = "";
    public int Value;
    ~Node() { Freed++; }
}

sealed class Leaf { public static int Freed; public int V; ~Leaf() { Freed++; } }
sealed class Tree { public static int Freed; public Tree? Left, Right; public int V; ~Tree() { Freed++; } }
sealed class Plain { public Plain? Next; public int V; }

static class Program
{
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Reverse(int n)
    {
        Node? head = null;
        for (int i = 0; i < n; i++) head = new Node { Next = head, Name = "n" + (i % 7), Value = i };
        Node? prev = null;
        for (var x = head; x != null;) { var next = x.Next; x.Next = prev; prev = x; x = next; }
        long sum = 0; int k = 0;
        for (var x = prev; x != null; x = x.Next) { sum += x.Value * (long)(k++ % 5) + x.Name.Length; }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Rings(int n)
    {
        // a ring made through owned locals: the second store closes it
        int seen = 0;
        for (int i = 0; i < n; i++)
        {
            var a = new Node { Value = i };
            var b = new Node { Value = i + 1 };
            a.Next = b;
            b.Next = a;
            var c = new Node { Value = i + 2 };
            c.Other = a;
            a.Other = c;
            seen += a.Next.Next!.Value == i && c.Other.Other == c ? 1 : 0;
        }
        return seen;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SelfAndSwap()
    {
        var a = new Node { Value = 1 };
        a.Next = a;                       // refers to itself
        var b = new Node { Value = 2 };
        var c = new Node { Value = 3 };
        b.Next = c;
        var t = b; b = c; c = t;          // the variables change hands
        b.Next = c;                       // c (the old b) refers to b (the old c) already: a ring of two
        Node? walk = b; int steps = 0;
        while (walk != null && steps < 5) { walk = walk.Next; steps++; }
        return a.Next.Value * 100 + steps;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Rotate(int n)
    {
        // variables rotated in a loop: a store through one whose object another variable held a moment ago
        Node p = new Node { Value = -1 }, q = new Node { Value = -2 };
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            var r = new Node { Value = i };
            r.Next = p;
            p.Other = (i & 3) == 0 ? r : null;   // every fourth closes a ring between r and p
            p = q; q = r;
            sum += q.Next!.Value + (q.Next.Other == q ? 1000 : 0);
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long ArrayOwned(int n)
    {
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            var arr = new Node[3];
            var x = new Node { Value = i };
            arr[0] = x; arr[1] = x; arr[2] = new Node { Value = i * 2, Next = x };
            x.Other = arr[2];               // x is in the array: a ring through the array's element and back
            sum += arr[2]!.Next!.Other!.Value;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long LongChain(int n)
    {
        Plain? head = null;
        for (int i = 0; i < n; i++) head = new Plain { Next = head, V = i };
        long s = 0;
        for (var x = head; x != null; x = x.Next) s += x.V;
        return s;   // the chain is let go at its head here
    }

    static Tree? Build(int depth, ref int c)
    {
        if (depth == 0) return null;
        var t = new Tree { V = c++ };
        t.Left = Build(depth - 1, ref c);
        t.Right = Build(depth - 1, ref c);
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Trees()
    {
        int c = 0;
        var root = Build(12, ref c)!;
        long s = 0;
        var stack = new Stack<Tree>();
        stack.Push(root);
        while (stack.Count > 0) { var t = stack.Pop(); s += t.V; if (t.Left != null) stack.Push(t.Left); if (t.Right != null) stack.Push(t.Right); }
        root.Left = null;                  // half of it goes here
        // a right spine a few thousand long, let go at its top
        var spine = new Tree { V = 1 };
        var tip = spine;
        for (int i = 0; i < 5000; i++) { var nx = new Tree { V = i }; tip.Right = nx; tip = nx; }
        return s + tip.V;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Leaves(int n)
    {
        int s = 0;
        for (int i = 0; i < n; i++) { var l = new Leaf { V = i }; var arr = new int[4]; arr[i & 3] = l.V; s += arr[i & 3]; string t = i.ToString(); s += t.Length; }
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Reverse(1000));
        Collect();
        Console.WriteLine("nodes freed after reverse: " + Node.Freed);
        Console.WriteLine(Rings(500));
        Collect();
        Console.WriteLine("nodes freed after rings: " + Node.Freed);
        Console.WriteLine(SelfAndSwap());
        Collect();
        Console.WriteLine("nodes freed after self and swap: " + Node.Freed);
        Console.WriteLine(Rotate(400));
        Collect();
        Console.WriteLine("nodes freed after rotate: " + Node.Freed);
        Console.WriteLine(ArrayOwned(300));
        Collect();
        Console.WriteLine("nodes freed after arrays: " + Node.Freed);
        Console.WriteLine(LongChain(300000));
        Console.WriteLine(Trees());
        Collect();
        Console.WriteLine("trees freed: " + Tree.Freed);
        Console.WriteLine(Leaves(2000));
        Collect();
        Console.WriteLine("leaves freed: " + Leaf.Freed);
        Console.WriteLine(Reverse(50000));
        Collect();
        Console.WriteLine("nodes freed at the end: " + Node.Freed);
    }
}

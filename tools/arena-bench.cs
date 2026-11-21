// Arena operations as Roslyn's red nodes meet them (docs/FINISH.md stream N): a tree of cycle-capable nodes, each made
// lazily by its parent into one of the parent's child slots (a red node's child cache) and holding its parent in a
// readonly field set at construction, walked again and again by an iterator whose state holds the current node, by a
// stack of structs that hold nodes (SyntaxNodeOrToken's shape), and by a visitor that keeps nodes in locals. Every
// store of the walks is a store into an arena member's slot; every local a count of a member. Parts of the tree are let
// go and made again. Best of five printed.
//   cs2wasmc tools/arena-bench.cs -o ab.wasm   (CS2WASM_NO_THREADS=1, as NativeAOT-LLVM)
//   node --liftoff-only host/run.mjs ab.wasm main
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

// the tree's shape, immutable (a green node)
sealed class Shape
{
    public readonly Shape[] Kids;
    public readonly int Width;
    public Shape(Shape[] kids, int width) { Kids = kids; Width = width; }
}

// a red node: its parent and position, its children made when first asked for
sealed class Node
{
    public readonly Node? Parent;
    public readonly Shape Green;
    public readonly int Position;
    Node?[]? _kids;
    public Node(Node? parent, Shape green, int position) { Parent = parent; Green = green; Position = position; }

    public int Count => Green.Kids.Length;
    public Node Child(int i)
    {
        var kids = _kids ??= new Node?[Green.Kids.Length];
        var k = kids[i];
        if (k == null) { k = new Node(this, Green.Kids[i], Position + i); kids[i] = k; }
        return k;
    }
    public void Forget() => _kids = null;

    public IEnumerable<Node> Descendants()
    {
        var stack = new Stack<Node>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Count - 1; i >= 0; i--) stack.Push(n.Child(i));
        }
    }
}

struct NodeOrValue
{
    public Node? Node;
    public int Index;
    public NodeOrValue(Node? n, int i) { Node = n; Index = i; }
}

sealed class Walker
{
    public NodeOrValue Current;
    public NodeOrValue[] Stack = new NodeOrValue[64];
    public int Depth;
    public Node? Last;

    public long Walk(Node root)
    {
        long sum = 0;
        Depth = 0;
        Stack[Depth++] = new NodeOrValue(root, 0);
        while (Depth > 0)
        {
            Current = Stack[--Depth];
            var n = Current.Node!;
            sum += n.Position;
            Last = n;
            for (int i = 0; i < n.Count; i++)
            {
                if (Depth == Stack.Length) Array.Resize(ref Stack, Depth * 2);
                Stack[Depth++] = new NodeOrValue(n.Child(i), i);
            }
        }
        Current = default;
        Last = null;
        return sum;
    }
}

static class P
{
    static Shape Build(int depth, int width) => new(depth == 0 ? [] : BuildKids(depth, width), width);
    static Shape[] BuildKids(int depth, int width) { var k = new Shape[width]; for (int i = 0; i < width; i++) k[i] = Build(depth - 1, width); return k; }

    static long Visit(Node n)
    {
        long s = n.Position;
        var parent = n.Parent;
        if (parent != null) s += parent.Position & 1;
        for (int i = 0; i < n.Count; i++) s += Visit(n.Child(i));
        return s;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var green = Build(5, 6); // 9,331 nodes
        var root = new Node(null, green, 0);
        var walker = new Walker();
        long check = 0;
        int best = int.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < 8; k++)
            {
                foreach (var n in root.Descendants()) check += n.Position;
                check += walker.Walk(root);
                check += Visit(root);
                // (a part of the tree let go: made again on the next walk)
                root.Child(k % root.Count).Forget();
            }
            best = Math.Min(best, (int)sw.ElapsedMilliseconds);
        }
        Console.WriteLine($"arena bench {best} ms (check {check})");
    }
}

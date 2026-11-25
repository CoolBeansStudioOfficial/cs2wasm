// Ported from tests/Conformance/082_parent_links.cs (the old suite): the same program, for the cycle groups (docs/MEMORY-IR.md S2).
// live: 0
// Trees whose nodes refer back to their parents (section 4.3). A reference from a node to what holds it is not
// counted, so a subtree taken out is destroyed at that store; but a node held from elsewhere keeps its parent, and the
// parent's parent, exactly as in .NET. The finalizers say when each object went; .NET runs them at a collection, so the
// program only prints what does not depend on when: what is still reachable, and the totals at the end.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Node
{
    public static int Made, Live;
    public readonly string Name;
    public Node? Parent;
    public object? Tag;
    public Node? Root;
    public readonly List<Node> Kids = new();
    public Node(string name) { Name = name; Made++; Live++; }
    public Node Add(Node c) { c.Parent = this; Kids.Add(c); return c; }
    public Node AddAfter(Node c) { Kids.Add(c); c.Parent = this; return c; }
    public string Path() => Parent == null ? Name : Parent.Path() + "/" + Name;
    public int Count() { int n = 1; foreach (var k in Kids) n += k.Count(); return n; }
}

sealed class Ring
{
    public Ring? Next, Prev;
    public int V;
}

static class Program
{
    static Node Build(string name, int width, int depth)
    {
        var n = new Node(name);
        if (depth > 0)
            for (int i = 0; i < width; i++)
            {
                var c = Build(name + i, width, depth - 1);
                if ((i & 1) == 0) n.Add(c); else n.AddAfter(c);
                c.Tag = n;
            }
        return n;
    }

    static void SetRoot(Node n, Node root) { n.Root = root; foreach (var k in n.Kids) SetRoot(k, root); }

    static Node HeldLeaf()
    {
        // the tree is let go while one leaf is kept: the leaf's parents must stay, up to the root
        var root = Build("r", 3, 3);
        SetRoot(root, root);
        return root.Kids[1].Kids[2].Kids[0];
    }

    static void Main()
    {
        var leaf = HeldLeaf();
        Console.WriteLine(leaf.Path());
        Console.WriteLine(leaf.Root!.Count());
        Console.WriteLine(((Node)leaf.Tag!).Name + " " + leaf.Parent!.Kids.Count);

        // a subtree taken out and kept: it keeps what it refers to above it, and no more once it lets go of that
        var tree = Build("t", 2, 3);
        var sub = tree.Kids[0];
        tree.Kids.RemoveAt(0);
        Console.WriteLine(sub.Path() + " " + tree.Count() + " " + sub.Count());
        sub.Parent = null; sub.Tag = null;
        Console.WriteLine(sub.Path() + " " + sub.Kids[1].Path());

        // moved to another parent, in both orders of the two stores
        var a = Build("a", 2, 1);
        var b = Build("b", 2, 1);
        var m = a.Kids[0];
        a.Kids.Remove(m); b.Add(m); m.Tag = b;
        var m2 = a.Kids[0];
        m2.Parent = b; a.Kids.Remove(m2); b.Kids.Add(m2);
        Console.WriteLine(m.Path() + " " + m2.Path() + " " + a.Count() + " " + b.Count());

        // a long-lived tree that keeps taking subtrees out
        var keep = new Node("k");
        long seen = 0;
        for (int i = 0; i < 20000; i++)
        {
            var s = keep.Add(new Node("s"));
            for (int j = 0; j < 4; j++) s.Add(new Node("g")).Tag = s;
            if (keep.Kids.Count > 8) { seen += keep.Kids[0].Kids.Count; keep.Kids.RemoveAt(0); }
        }
        Console.WriteLine(keep.Count() + " " + seen);

        // a ring, and a list linked both ways
        Ring first = new Ring { V = 0 }, last = first;
        for (int i = 1; i < 1000; i++) { var r = new Ring { V = i, Prev = last }; last.Next = r; last = r; }
        last.Next = first; first.Prev = last;
        int sum = 0;
        for (var r = first.Prev!; r != first; r = r.Prev!) sum += r.V;
        Console.WriteLine(sum);

        Console.WriteLine(leaf.Root!.Kids[0].Kids[0].Path());
        Console.WriteLine(Node.Made);
    }
}

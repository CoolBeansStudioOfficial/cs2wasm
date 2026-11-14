// Reference cycles (section 4.3): objects that point at each other live in arenas. Behaviour must not change.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Pair { public Pair Other; public int Id; public Pair(int id) { Id = id; } }

class TreeNode
{
    public string Name;
    public TreeNode Parent;
    public List<TreeNode> Children = new();
    public TreeNode(string name) { Name = name; }
    public TreeNode Add(TreeNode c) { c.Parent?.Children.Remove(c); c.Parent = this; Children.Add(c); return c; }
    public string Path() => Parent == null ? Name : Parent.Path() + "/" + Name;
    public int Count() { int n = 1; foreach (var c in Children) n += c.Count(); return n; }
}

interface IVisitor { string Visit(Graph g); }
class Graph : IVisitor
{
    public object Tag;            // object field: holds a Graph only in this program
    public IVisitor Visitor;      // interface field closing a cycle back to Graph
    public Graph[] Edges = new Graph[0];
    public int V;
    public string Visit(Graph g) => "visit " + g.V;
}

class Ring
{
    public Ring Next;
    public int Value;
    public static Ring Make(int n)
    {
        var first = new Ring { Value = 0 };
        var cur = first;
        for (int i = 1; i < n; i++) { cur.Next = new Ring { Value = i }; cur = cur.Next; }
        cur.Next = first;
        return first;
    }
}

static class Program
{
    static TreeNode s_root;

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // two-object cycles in a loop
        int ok = 0;
        for (int i = 0; i < 1000; i++)
        {
            var a = new Pair(i); var b = new Pair(-i);
            a.Other = b; b.Other = a;
            if (a.Other.Other == a && b.Other.Id == i) ok++;
        }
        Console.WriteLine("pairs " + ok);

        // self reference, then broken
        var self = new Pair(7);
        self.Other = self;
        Console.WriteLine("self " + (self.Other.Other.Other == self) + " " + self.Other.Id);
        self.Other = null;
        Console.WriteLine("self cut " + (self.Other == null));

        // hash codes stay the same when an object joins an arena
        var h1 = new Pair(1); var h2 = new Pair(2);
        int before1 = h1.GetHashCode(), before2 = h2.GetHashCode();
        h1.Other = h2; h2.Other = h1;
        Console.WriteLine("hash stable " + (h1.GetHashCode() == before1 && h2.GetHashCode() == before2) + " " + (before1 != before2));

        // a tree with parent pointers and child lists
        var root = new TreeNode("root");
        var x = root.Add(new TreeNode("x"));
        var y = root.Add(new TreeNode("y"));
        for (int i = 0; i < 5; i++) x.Add(new TreeNode("x" + i));
        var deep = y.Add(new TreeNode("deep")).Add(new TreeNode("deeper"));
        Console.WriteLine(deep.Path() + " count " + root.Count());
        y.Add(x.Children[2]);                     // re-parent (List.Remove shifts the array)
        Console.WriteLine(x.Children.Count + " " + y.Children.Count + " " + y.Children[1].Path());
        x.Children.RemoveAt(0);                   // dropped subtree
        x.Children.Clear();                       // dropped list contents
        Console.WriteLine("after clear " + root.Count());
        root.Children.Remove(y);                  // y keeps a parent pointer to root, root no longer reaches y
        Console.WriteLine("y parent " + y.Parent.Name + " root count " + root.Count() + " y count " + y.Count());
        var other = new TreeNode("other");
        other.Add(y);                             // y's tree joins other's
        Console.WriteLine(deep.Path());
        s_root = other;                           // a static holds the graph
        Console.WriteLine("static " + s_root.Children[0].Children[0].Children[0].Path());
        s_root = null;

        // many nodes: build a long-lived tree, then keep dropping subtrees from it
        var big = new TreeNode("big");
        for (int round = 0; round < 50; round++)
        {
            var sub = big.Add(new TreeNode("s" + round));
            for (int i = 0; i < 20; i++) sub.Add(new TreeNode("leaf"));
            if (round % 2 == 0) big.Children.Remove(sub);
        }
        Console.WriteLine("big " + big.Count() + " children " + big.Children.Count);

        // object and interface fields, arrays of members
        var g1 = new Graph { V = 1 }; var g2 = new Graph { V = 2 };
        g1.Tag = g2; g2.Tag = g1;
        g1.Visitor = g2; g2.Visitor = g1;
        g1.Edges = new[] { g1, g2 };
        g2.Edges = (Graph[])g1.Edges.Clone();
        Console.WriteLine(((Graph)g1.Tag).V + " " + g2.Visitor.Visit(g2) + " " + g2.Edges[0].Edges[1].V);
        Array.Copy(g2.Edges, 1, g2.Edges, 0, 1);
        Array.Clear(g1.Edges, 0, 1);
        Console.WriteLine((g1.Edges[0] == null) + " " + g2.Edges[0].V + " " + g2.Edges[1].V);

        // rings of different sizes, broken and re-closed
        int sum = 0;
        for (int n = 1; n < 40; n++)
        {
            var r = Ring.Make(n);
            var cur = r;
            for (int i = 0; i < 2 * n; i++) { sum += cur.Value; cur = cur.Next; }
            if (n % 3 == 0) r.Next = r;           // cut out everything but the first
            if (n % 5 == 0) r.Next = Ring.Make(3); // splice in another ring
        }
        Console.WriteLine("rings " + sum);

        // a node kept only by a local after its graph was dropped
        TreeNode keep;
        {
            var t = new TreeNode("t");
            keep = t.Add(new TreeNode("kept"));
            t.Add(new TreeNode("sibling"));
        }
        Console.WriteLine(keep.Path() + " " + keep.Parent.Children.Count);
    }
}

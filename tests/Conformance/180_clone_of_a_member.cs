// MemberwiseClone of an object of a cycle-capable type that refers to members of an arena (Roslyn's
// BoundExpression.WithSuppression clones a bound node that holds its symbol, its type and its children). The copy
// counts each reference it holds, then links those in slots that can close a cycle (Rt.ArenaLinkContents); the copy
// joins the arena at the first of them. It was then linked a second time as a member that joined with references in
// its slots, each store's count given back twice: the objects it referred to were freed while the original, the copy
// and the program still held them (proof 5: the self-hosted compiler on any program with -r GodotSharp.dll).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Sym
{
    public string Name = "";
    public Node? Decl;          // (a symbol refers to its declaration: Sym and Node can be on a cycle)
}

class Node
{
    public Node? Parent;
    public Sym? Symbol;
    public Sym? Type;
    public bool Suppressed;
    public List<Node> Kids = new();
    public Node WithSuppression(bool s)
    {
        if (Suppressed == s) return this;
        var r = (Node)MemberwiseClone();
        r.Suppressed = s;
        return r;
    }
}

sealed class Local : Node { public int Slot; }

static class Program
{
    static int Run(int count, bool twoFields)
    {
        var root = new Node();
        var sym = new Sym { Name = "x" };
        sym.Decl = root;
        var keep = new List<Node>();
        for (int i = 0; i < count; i++)
        {
            var n = new Local { Parent = root, Symbol = sym, Slot = i };
            if (twoFields) n.Type = sym;
            root.Kids.Add(n);
            var c = n.WithSuppression(true);
            var c2 = c.WithSuppression(false);
            if (i % 3 == 0) keep.Add(c);
            if (i % 5 == 0) keep.Add(c2);
        }
        int total = 0;
        foreach (var k in keep) total += ((Local)k).Slot + (k.Symbol == sym ? 1 : 0) + (k.Parent == root ? 1 : 0) + k.Kids.Count;
        foreach (var k in root.Kids) total += k.Symbol!.Name.Length + (k.Suppressed ? 100 : 0);
        keep.Clear();
        root.Kids.Clear();
        return total + (sym.Decl == root ? 1000 : 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Run(1, true));
        Console.WriteLine(Run(50, true));
        Console.WriteLine(Run(50, false));
        // a clone of a clone, the original let go first
        var a = new Local { Slot = 7, Symbol = new Sym { Name = "a" } };
        a.Symbol.Decl = a;
        var b = (Local)a.WithSuppression(true);
        a = null;
        var c = (Local)b.WithSuppression(false);
        b = null;
        Console.WriteLine(c.Slot + " " + c.Symbol!.Name + " " + (c.Symbol.Decl is Local l ? l.Slot : -1) + " " + c.Suppressed);
    }
}

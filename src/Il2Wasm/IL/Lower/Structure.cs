#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;

namespace Il2Wasm.IL.Lower;

/// <summary>A node of one level's graph when a region's code is structured: a block of the region, or a try region
/// nested in it with its handlers (a compound, whose successors are the targets its leaves go to).</summary>
abstract class SNode
{
    public readonly List<SNode> Succs = [];
    public readonly List<SNode> Preds = [];
    public int Rpo = -1;
    public SNode? IDom;
    public readonly List<SNode> DomChildren = [];
    public bool LoopHeader, Merge;
}

sealed class BNode(Block b) : SNode { public readonly Block B = b; public override string ToString() => B.ToString(); }

sealed class TNode(Region t) : SNode
{
    public readonly Region Try = t;
    /// <summary>The blocks outside the try and its handlers that they leave to, in a fixed order.</summary>
    public readonly List<Block> Exits = [];
    public override string ToString() => "try@" + Try.Start.ToString("x");
}

/// <summary>A branch out of the level being structured (a leave of a try body or a handler).</summary>
sealed class XNode(Block target) : SNode { public readonly Block Target = target; public override string ToString() => "exit " + Target; }

/// <summary>One region's code as a graph: its blocks and the compounds of the try regions directly inside it.</summary>
sealed class Level
{
    public readonly Region Region;
    public readonly SNode Entry;
    public readonly List<SNode> Nodes = [];
    readonly Dictionary<Block, BNode> _blocks = new();
    readonly Dictionary<Region, TNode> _tries = new();
    readonly Dictionary<Block, XNode> _exits = new();
    public bool Irreducible;

    /// <summary>The level of a region: its own blocks; for a try, its blocks and not its handlers (the compound holds them).</summary>
    public Level(Region region, Block entry)
    {
        Region = region;
        Entry = Map(entry);
        var work = new Stack<SNode>();
        var seen = new HashSet<SNode> { Entry };
        work.Push(Entry);
        while (work.Count > 0)
        {
            var n = work.Pop();
            if (n is XNode) continue;
            Nodes.Add(n);
            IEnumerable<Block> targets = n switch { BNode b => b.B.Succs, TNode t => t.Exits, _ => [] };
            foreach (var tb in targets.Distinct())
            {
                var s = Map(tb);
                if (!n.Succs.Contains(s)) { n.Succs.Add(s); s.Preds.Add(n); }
                if (seen.Add(s)) work.Push(s);
            }
        }
        Order();
    }

    /// <summary>The level's node a branch to this block goes to.</summary>
    public SNode Map(Block t)
    {
        if (t.Region == Region) return _blocks.TryGetValue(t, out var bn) ? bn : _blocks[t] = new BNode(t);
        // the region directly inside this one that holds the target
        Region? c = t.Region;
        while (c != null && c.Parent != Region) c = c.Parent;
        if (c == null || !Region.Contains(t.Region)) return _exits.TryGetValue(t, out var x) ? x : _exits[t] = new XNode(t);
        if (c.Kind == RegionKind.Try)
        {
            if (_tries.TryGetValue(c, out var tn)) return tn;
            tn = new TNode(c);
            _tries[c] = tn;
            // each part's leaves: the try body's, and each handler's (a catch may leave back to its own try's start)
            var parts = new List<Region> { c };
            foreach (var h in c.Handlers) { if (h.Filter != null) parts.Add(h.Filter); parts.Add(h); }
            foreach (var part in parts)
            {
                var inside = Ownership.BlocksWithin(part).ToHashSet();
                foreach (var b in inside.OrderBy(b => b.Id))
                    foreach (var s in b.Succs)
                        if (!inside.Contains(s) && !tn.Exits.Contains(s)) tn.Exits.Add(s);
            }
            return tn;
        }
        // (a handler's code is reached only through its try)
        throw new InvalidProgramException($"a branch into the handler at IL_{c.Start:x4}");
    }

    void Order()
    {
        // reverse postorder
        var post = new List<SNode>();
        var seen = new HashSet<SNode>();
        void Dfs(SNode n)
        {
            seen.Add(n);
            foreach (var s in n.Succs) if (s is not XNode && !seen.Contains(s)) Dfs(s);
            post.Add(n);
        }
        Dfs(Entry);
        post.Reverse();
        Nodes.Clear();
        Nodes.AddRange(post);
        for (int i = 0; i < Nodes.Count; i++) Nodes[i].Rpo = i;
        // dominators (Cooper, Harvey and Kennedy)
        Entry.IDom = Entry;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var n in Nodes.Skip(1))
            {
                SNode? d = null;
                foreach (var p in n.Preds)
                {
                    if (p.IDom == null) continue;
                    d = d == null ? p : Intersect(p, d);
                }
                if (d != n.IDom) { n.IDom = d; changed = true; }
            }
        }
        foreach (var n in Nodes.Skip(1)) n.IDom!.DomChildren.Add(n);
        foreach (var n in Nodes)
        {
            int forward = 0;
            foreach (var p in n.Preds)
            {
                if (p.Rpo >= n.Rpo) { n.LoopHeader = true; if (!Dominates(n, p)) Irreducible = true; }
                else forward++;
            }
            n.Merge = forward >= 2;
        }
        foreach (var n in Nodes) n.DomChildren.Sort((a, b) => b.Rpo.CompareTo(a.Rpo));
    }

    static SNode Intersect(SNode a, SNode b)
    {
        while (a != b)
        {
            while (a.Rpo > b.Rpo) a = a.IDom!;
            while (b.Rpo > a.Rpo) b = b.IDom!;
        }
        return a;
    }

    public static bool Dominates(SNode a, SNode b)
    {
        for (var x = b; ; x = x.IDom!)
        {
            if (x == a) return true;
            if (x.IDom == x || x.IDom == null) return false;
        }
    }
}

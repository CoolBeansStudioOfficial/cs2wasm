#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Il2Wasm.IL.Ir;

/// <summary>Naive reference counting on the IR (the first correct form; the borrow rules come back later as passes,
/// each with its switch). Every reference value is owned: it holds one count from its definition (a load counts
/// what it read; an argument is counted on entry) to its last use. A use that stores the value (a field, an element,
/// a static, a variable, a return, a throw, a phi) takes that count when it is the last use, and a new one otherwise;
/// any other last use is followed by a release. Liveness is SSA liveness, with the handlers of the enclosing try
/// regions as successors of every block in them, and a finally's end followed by its try's leave targets.
///
/// Lowering keeps each reference value in a local of its own that is zero whenever it holds no count (a release or
/// a move clears it), so a release of a value that may already be dead costs nothing: that is what lets a handler's
/// entry, a block's entry and an exception leaving the function release what may be live without knowing the path.</summary>
public static class Ownership
{
    public sealed class Info
    {
        public readonly Dictionary<Block, HashSet<Node>> LiveIn = new(), LiveOut = new();
        /// <summary>Phi operands (phi, index) whose count moves into the phi; the others are counted again.</summary>
        public readonly HashSet<(Node, int)> PhiMoves = [];
        /// <summary>All reference values of the body (each one's local is released when an exception leaves).</summary>
        public readonly List<Node> RefValues = [];
        /// <summary>An address into an object (a field's, an element's, a box's data) and the references it points
        /// into: each of those lives as long as the address is used (a GC's interior pointer: the object must not be
        /// freed under it).</summary>
        public readonly Dictionary<Node, List<Node>> Bases = new();
    }

    /// <summary>The operations whose address result points into one of their operands (the operand's object, or the
    /// object an operand address points into): field and element addresses, a box's data, address arithmetic and
    /// conversion, and a call's returned byref (a span's, an array's data reference, an accessor's). A pointer loaded
    /// from memory points into nothing known here.</summary>
    internal static bool Derives(Node n) => n.Kind == VK.Ptr && n.Op is Opc.LdFldA or Opc.LdElemA or Opc.Unbox or Opc.Bin or Opc.Conv or Opc.Call or Opc.CallI
        || ByRefLike(n) && n.Op is Opc.Call or Opc.CallI or Opc.NewObj or Opc.LdObj or Opc.MkRefAny
        || n.Kind == VK.Ptr && n.Op == Opc.RefAnyVal
        || n.Kind == VK.I32 && n.Op == Opc.Call && IsAddressOf(n);

    /// <summary>A value of a byref-like struct (a Span: it holds an address into an object, so it keeps it as one).</summary>
    internal static bool ByRefLike(Node n) => n.Kind == VK.Struct && n.Type is Internal.TypeSystem.DefType { IsByRefLike: true };

    /// <summary>il-runtime: the runtime's own addresses of an object as an int (Rt.AddressOf, ArrayData, StringData):
    /// the runtime's C# was written for the operation-tree front end, which kept a local to the end of its scope, so
    /// `Engine.Write(Rt.ArrayData(text), n)` relied on text living on; an int derived from them keeps the object as an
    /// address does (the int itself: arithmetic on it is not followed).</summary>
    static bool IsAddressOf(Node n) => n.X is Internal.TypeSystem.MethodDesc m && m.OwningType is Internal.TypeSystem.MetadataType t
        && t.GetName() == "Rt" && t.GetNamespace() == "Il2Wasm.Runtime" && m.GetName() is "AddressOf" or "ArrayData" or "StringData";

    /// <summary>il-runtime: the reference an address points into, as Derives follows it (a pinned local keeps it:
    /// Importer's Pinning.cs), or null.</summary>
    public static Node? RootOf(Node n)
    {
        if (n.Kind == VK.Ref) return n.Op is Opc.LdNull or Opc.LdStr ? null : n;
        if (!Derives(n)) return null;
        foreach (var a in n.A) if (RootOf(a) is { } r) return r;
        return null;
    }

    /// <summary>The bases of each derived address, in definition order (an address of an address has its bases).
    /// Phis are not followed: an address carried around a loop does not keep its object.</summary>
    internal static void FindBases(Body body, Info info)
    {
        // (a variable of an address or a span (a local the IL stores a Span in, a ref local in the frame): everything
        // ever stored into it, for every read of it, wherever it is: a span local read in a loop keeps the array it
        // was made over. A read on a path before the store makes the base live there too: at worst a count kept
        // longer, never one let go early. Repeated until nothing changes: a read's bases feed what is made from it)
        var varBases = new Dictionary<int, HashSet<Node>>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var b in body.Blocks)
                foreach (var n in b.Code)
                {
                    List<Node>? l = null;
                    if (n.Op == Opc.VarSt && n.A.Length > 0 && info.Bases.TryGetValue(n.A[0], out var sb))
                    {
                        if (!varBases.TryGetValue((int)n.I, out var set)) varBases[(int)n.I] = set = [];
                        foreach (var x in sb) changed |= set.Add(x);
                        continue;
                    }
                    if (n.Op is Opc.LocA or Opc.VarLd) { if (varBases.TryGetValue((int)n.I, out var vb)) l = vb.ToList(); }
                    else if (Derives(n))
                        foreach (var a in n.A)
                        {
                            if (a.Kind == VK.Ref && a.Op is not (Opc.LdNull or Opc.LdStr)) (l ??= []).Add(a);
                            else if (info.Bases.TryGetValue(a, out var ab)) (l ??= []).AddRange(ab);
                        }
                    if (l == null) continue;
                    var d = l.Distinct().ToList();
                    if (!info.Bases.TryGetValue(n, out var old) || old.Count != d.Count) { info.Bases[n] = d; changed = true; }
                }
        }
    }

    /// <summary>A reference that something besides this function holds: an argument (the caller's), one loaded from
    /// a field, element, static, variable or address, or a cast of one (castclass, isinst, Unsafe.As).</summary>
    internal static bool HeldElsewhere(Node v) => v.Op is Opc.Arg or Opc.LdFld or Opc.LdElem or Opc.LdSFld or Opc.VarLd or Opc.LdInd
        || v.Op is Opc.CastClass or Opc.IsInst && HeldElsewhere(v.A[0])
        || v.Op == Opc.Call && v.A.Length == 1 && v.A[0].Kind == VK.Ref && v.X is Internal.TypeSystem.MethodDesc m && m.GetName() == "As"
            && m.OwningType is Internal.TypeSystem.MetadataType { Name: var _ } ut && ut.GetName() == "Unsafe" && HeldElsewhere(v.A[0]);

    /// <summary>The counted values a use of operand a keeps alive: a itself, or the bases of an address.</summary>
    static IEnumerable<Node> Kept(Node a, Info info) => IsRef(a) ? [a] : info.Bases.TryGetValue(a, out var l) ? l : [];

    /// <summary>A value that holds counts: a reference, or a struct with references in it.</summary>
    internal static bool IsRef(Node n) => n.Kind == VK.Ref || n.Kind == VK.Struct && n.Type != null && Kinds.Counted(n.Type);

    /// <summary>Operand i of n takes a count (a store of it).</summary>
    public static bool Consumes(Node n, int i) => n.Op switch
    {
        Opc.StFld => i == 1, Opc.StSFld => i == 0, Opc.StElem => i == 2, Opc.StInd => i == 1, Opc.VarSt => i == 0,
        Opc.Ret => i == 0, Opc.Throw => i == 0, Opc.Rethrow => i == 0, Opc.Box => i == 0,
        _ => false,
    };

    /// <summary>The analysis without the insertion (Memory/Plan.cs builds on it): the edges split, the reference
    /// values, the bases and the naive liveness.</summary>
    public static Info Analyze(Body body)
    {
        SplitEdges(body);
        var info = new Info();
        foreach (var b in body.Blocks) { foreach (var n in b.Phis) if (IsRef(n)) info.RefValues.Add(n); foreach (var n in b.Code) if (IsRef(n)) info.RefValues.Add(n); }
        FindBases(body, info);
        Liveness(body, info);
        return info;
    }

    public static Info Run(Body body)
    {
        var info = Analyze(body);
        foreach (var b in body.Blocks) Insert(body, b, info);
        // the arguments are counted on entry
        // (each right after its argument: before the release of an argument nothing uses, which follows it)
        var entry = body.Blocks[0];
        foreach (var a in body.Args)
            if (IsRef(a)) entry.Code.Insert(entry.Code.IndexOf(a) + 1, new Node { Op = Opc.Retain, Kind = VK.Void, A = [a], Block = entry, Id = body.NextId() });
        return info;
    }

    /// <summary>An edge from a block with several successors into a block with phis gets a block of its own, where the
    /// phis' copies go.</summary>
    internal static void SplitEdges(Body body)
    {
        foreach (var p in body.Blocks.ToList())
        {
            if (p.Succs.Distinct().Count() < 2) continue;
            foreach (var s in p.Succs.Distinct().ToList())
            {
                if (s.Phis.Count == 0) continue;
                var e = new Block { Id = body.Blocks.Count, ILStart = s.ILStart, Region = s.Region.Contains(p.Region) ? s.Region : p.Region };
                e.Region.Blocks.Add(e);
                e.Code.Add(new Node { Op = Opc.Br, Kind = VK.Void, Block = e, Id = body.NextId() });
                body.Blocks.Add(e);
                for (int i = 0; i < p.Succs.Count; i++) if (p.Succs[i] == s) p.Succs[i] = e;
                for (int i = 0; i < s.Preds.Count; i++) if (s.Preds[i] == p) s.Preds[i] = e;
                e.Preds.Add(p); e.Succs.Add(s);
            }
        }
    }

    /// <summary>The handler entries an exception in this block can reach (every handler of every enclosing try).</summary>
    public static IEnumerable<Block> ExceptionSuccs(Block b)
    {
        for (var r = b.Region; r != null; r = r.Parent)
        {
            if (r.Kind != RegionKind.Try) continue;
            foreach (var h in r.Handlers) { if (h.Filter != null) yield return h.Filter.Entry; yield return h.Entry; }
        }
    }

    /// <summary>The blocks of a region and of every region inside it.</summary>
    public static IEnumerable<Block> BlocksWithin(Region r) => r.Blocks.Concat(r.Children.SelectMany(BlocksWithin));

    /// <summary>A finally's end continues to its try's leave targets (the leaves run the finally first).</summary>
    internal static Dictionary<Block, List<Block>> FinallyEnds(Body body)
    {
        var d = new Dictionary<Block, List<Block>>();
        foreach (var r in body.Regions)
        {
            if (r.Kind is not (RegionKind.Finally or RegionKind.Fault)) continue;
            var inside = BlocksWithin(r.Try!).ToHashSet();
            var targets = inside.SelectMany(b => b.Succs).Where(t => !inside.Contains(t)).Distinct().ToList();
            foreach (var b in BlocksWithin(r)) if (b.Term?.Op == Opc.EndFinally) d[b] = targets;
        }
        return d;
    }

    internal static void Liveness(Body body, Info info)
    {
        var ends = FinallyEnds(body);
        foreach (var b in body.Blocks) { info.LiveIn[b] = []; info.LiveOut[b] = []; }
        var exc = body.Blocks.ToDictionary(b => b, b => ExceptionSuccs(b).ToList());
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int bi = body.Blocks.Count - 1; bi >= 0; bi--)
            {
                var b = body.Blocks[bi];
                var outSet = new HashSet<Node>();
                foreach (var s in b.Succs.Concat(ends.GetValueOrDefault(b) ?? []).Concat(exc[b]))
                {
                    foreach (var v in info.LiveIn[s]) outSet.Add(v);
                    if (b.Succs.Contains(s))
                    {
                        int pi = s.Preds.IndexOf(b);
                        if (pi >= 0) foreach (var phi in s.Phis) foreach (var k in Kept(phi.A[pi], info)) outSet.Add(k);
                    }
                }
                var live = new HashSet<Node>(outSet);
                for (int i = b.Code.Count - 1; i >= 0; i--)
                {
                    var n = b.Code[i];
                    live.Remove(n);
                    foreach (var a in n.A) foreach (var k in Kept(a, info)) live.Add(k);
                }
                foreach (var phi in b.Phis) live.Remove(phi);
                if (!outSet.SetEquals(info.LiveOut[b])) { info.LiveOut[b] = outSet; changed = true; }
                if (!live.SetEquals(info.LiveIn[b])) { info.LiveIn[b] = live; changed = true; }
            }
        }
    }

    static Node Count(Opc op, Node v, Block b, Body body) => new() { Op = op, Kind = VK.Void, A = [v], Block = b, Id = body.NextId(), ILOffset = -1 };

    static void Insert(Body body, Block b, Info info)
    {
        var live = new HashSet<Node>(info.LiveOut[b]);
        // the phi copies at the block's end (one successor, SplitEdges): a count moves unless the value lives on
        if (b.Succs.Distinct().Count() == 1 && b.Succs[0].Phis.Count > 0)
        {
            var s = b.Succs[0];
            int pi = s.Preds.IndexOf(b);
            var liveOn = new HashSet<Node>(info.LiveIn[s]);
            foreach (var phi in s.Phis)
            {
                var v = phi.A[pi];
                if (!IsRef(v)) continue;
                if (!liveOn.Contains(v)) { info.PhiMoves.Add((phi, pi)); liveOn.Add(v); }
            }
            // what moves into a phi is not live after the block
            foreach (var phi in s.Phis) if (info.PhiMoves.Contains((phi, pi))) live.Remove(phi.A[pi]);
            foreach (var phi in s.Phis) if (IsRef(phi.A[pi]) && !info.PhiMoves.Contains((phi, pi))) live.Add(phi.A[pi]);
            // (values counted again for a phi are live through the copy: they are released at the successor's
            // entry if nothing else needs them there)
            foreach (var phi in s.Phis) if (IsRef(phi.A[pi])) live.Add(phi.A[pi]);
            // (an address copied into a phi: its objects live through the copy)
            foreach (var phi in s.Phis) if (!IsRef(phi.A[pi]) && info.Bases.TryGetValue(phi.A[pi], out var pb)) live.UnionWith(pb);
        }
        var code = b.Code;
        var output = new List<Node>(code.Count * 2);
        // walking backwards, inserted nodes are collected in reverse
        for (int i = code.Count - 1; i >= 0; i--)
        {
            var n = code[i];
            var after = new List<Node>();
            var before = new List<Node>();
            if (IsRef(n) && !live.Contains(n) && !n.IsTerminator) after.Add(Count(Opc.Release, n, b, body));
            live.Remove(n);
            if (n.Op is not (Opc.Retain or Opc.Release))
            {
                var seen = new HashSet<Node>();
                for (int k = 0; k < n.A.Length; k++)
                {
                    var v = n.A[k];
                    if (!IsRef(v) || !seen.Add(v)) continue;
                    bool lastUse = !live.Contains(v);
                    bool moved = false;
                    for (int j = k; j < n.A.Length; j++)
                    {
                        if (n.A[j] != v || !Consumes(n, j)) continue;
                        // (otherwise the store counts the value itself: lowering retains an operand that does not
                        // move; a Retain here as well counted it twice, a leak)
                        if (lastUse && !moved) { n.Moves |= 1u << j; moved = true; }
                    }
                    if (lastUse && !moved) after.Add(Count(Opc.Release, v, b, body));
                    live.Add(v);
                }
                // (an address's last use is its objects' last use too)
                for (int k = 0; k < n.A.Length; k++)
                {
                    if (IsRef(n.A[k]) || !info.Bases.TryGetValue(n.A[k], out var bases)) continue;
                    foreach (var v in bases)
                    {
                        if (!seen.Add(v)) continue;
                        // (a terminator's: before it, as nothing runs after it here; the address it returns or
                        // branches with points into an object its caller or successor keeps)
                        // (but an object only this function holds, one it made or a call gave it, is not let go under
                        // a span or byref it returns: the caller would get freed memory; it stays, as a GC keeps it.
                        // A raw address returned, a native int, keeps nothing in .NET either)
                        bool keep = n.IsTerminator && !HeldElsewhere(v)
                            && !(n.A[k].Kind == VK.Ptr && n.A[k].Type is not Internal.TypeSystem.ByRefType);
                        if (!live.Contains(v) && !keep)
                            (n.IsTerminator ? before : after).Add(Count(Opc.Release, v, b, body));
                        live.Add(v);
                    }
                }
            }
            after.Reverse();
            output.AddRange(after);
            output.Add(n);
            before.Reverse();
            output.AddRange(before);
        }
        // at the block's entry: what a predecessor (or, for a handler, its try) may hold that is dead here
        var dead = new HashSet<Node>();
        foreach (var p in b.Preds) foreach (var v in info.LiveOut[p]) dead.Add(v);
        if (b.Preds.Count == 0 && b != body.Blocks[0])
            foreach (var r in body.Regions)
                if (r.Entry == b && (r.IsHandler || r.Kind == RegionKind.Filter))
                    foreach (var tb in BlocksWithin(r.Try!).Concat(r.Filter != null && r.Filter != r ? BlocksWithin(r.Filter) : []))
                    {
                        foreach (var v in info.LiveIn[tb]) dead.Add(v);
                        foreach (var n in tb.Phis.Concat(tb.Code)) if (IsRef(n)) dead.Add(n);
                    }
        dead.ExceptWith(info.LiveIn[b]);
        foreach (var phi in b.Phis) dead.Remove(phi);
        foreach (var v in dead.OrderBy(v => v.Id)) output.Add(Count(Opc.Release, v, b, body));
        output.Reverse();
        // (a handler's caught value comes first, before what its entry releases)
        int cv = output.FindIndex(n => n.Op == Opc.CatchVal);
        if (cv > 0) { var c = output[cv]; output.RemoveAt(cv); output.Insert(0, c); }
        code.Clear();
        code.AddRange(output);
    }
}

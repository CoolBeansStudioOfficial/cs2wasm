#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Reach;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Memory;

// The memory model's fact lattice (docs/MEMORY-IR.md section 2), as side tables beside the IR: the IR (Ir/Ir.cs) keeps
// its shape, and every fact of a value or a site lives in a table indexed by Node.Id, every fact of a method, field,
// element type, static, type or abstract object in a table indexed by a dense id.
//
// Orientation (section 3.1): every fact is a may-set or a may-state that starts at its bottom (empty, Local, Lend,
// Trivial...) and only grows by its Join. A must-fact (a borrow holds, a proof holds, a method returns fresh, a site
// is thread-local) is never stored as such: it is the emptiness test of the may-set of its counter-evidence, read off
// the final solution. So every join here is monotone, commutative, associative and idempotent, and the least fixed
// point is unique: nothing depends on the order in which methods, nodes or sites are processed (section 3.5).
//
// This file is the frozen interface of step 1 (MEMORY-IR.md 11.4): changes go through the coordinator.

// ======================================================================================================== dense ids

/// <summary>Dense ids for the objects of one kind (methods, keys, types, sites). Interning gives ids in arrival order;
/// <see cref="StableOrder"/> is the order output is made in, decided by a key of the object alone (its metadata
/// identity), never by arrival (section 3.5). Tables over the ids are plain arrays.</summary>
public sealed class IdTable<T> where T : notnull
{
    readonly Dictionary<T, int> _ids;
    readonly List<T> _items = [];

    public IdTable(IEqualityComparer<T>? comparer = null) { _ids = new Dictionary<T, int>(comparer); }

    public int Count => _items.Count;
    public T this[int id] => _items[id];
    public IReadOnlyList<T> Items => _items;

    public int Intern(T x)
    {
        if (_ids.TryGetValue(x, out int id)) return id;
        id = _items.Count;
        _ids[x] = id;
        _items.Add(x);
        return id;
    }

    /// <summary>The id, or -1 when the object was never interned.</summary>
    public int IdOf(T x) => _ids.TryGetValue(x, out int id) ? id : -1;

    /// <summary>The ids sorted by the object's stable key (ordinal), ties by id: the order every output over these ids
    /// is made in.</summary>
    public int[] StableOrder(Func<T, string> key)
    {
        var keys = _items.Select(key).ToArray();
        var ids = Enumerable.Range(0, _items.Count).ToArray();
        Array.Sort(ids, (a, b) => { int c = string.CompareOrdinal(keys[a], keys[b]); return c != 0 ? c : a.CompareTo(b); });
        return ids;
    }
}

/// <summary>The stable key of a type system object: its printed identity (assembly, namespace, name, instantiation,
/// signature), which does not depend on the order things were reached in.</summary>
public static class StableKey
{
    public static string Of(object o) => o switch
    {
        MethodDesc m => "M " + m,
        FieldDesc f => "F " + f.OwningType + "::" + f.GetName(),
        TypeDesc t => "T " + t,
        _ => o.ToString() ?? "",
    };
}

/// <summary>The kinds of memory location a store or a borrow path names (section 2.2, RefStores' rules): an instance
/// field by its typical definition (FieldLoads' key), the elements of arrays of an element type, a static field,
/// any slot of a type written through a <c>ref T</c> (an address of unknown target), a frame variable, and a runtime
/// type's private memory (its own tables in memory no object holds: no program load reads it, so no borrow path names
/// it; Memory/IntrinsicSignatures.cs).</summary>
public enum KeyKind : byte { Field, Elem, Static, Slot, Var, Private }

/// <summary>A memory location key before it has a dense id: the summary of one method names keys by their type
/// system objects (no shared state while summaries are built), the solver interns them (<see cref="KeyTable"/>).</summary>
public readonly record struct MemKey(KeyKind Kind, object? Target, int Index = 0)
{
    public static MemKey Field(FieldDesc f) => new(KeyKind.Field, f.GetTypicalFieldDefinition());
    public static MemKey Elem(TypeDesc elementType) => new(KeyKind.Elem, elementType);
    public static MemKey Static(FieldDesc f) => new(KeyKind.Static, f.GetTypicalFieldDefinition());
    public static MemKey Slot(TypeDesc t) => new(KeyKind.Slot, t);
    /// <summary>A frame variable of the method itself (Body.Vars index): never part of a callee's Mods.</summary>
    public static MemKey Var(int slot) => new(KeyKind.Var, null, slot);
    /// <summary>A runtime type's private memory (the heap's records, the object marks' tables): written by the runtime
    /// only, read by no program load.</summary>
    public static MemKey Private(TypeDesc owner) => new(KeyKind.Private, owner);
    public override string ToString() => Kind + " " + (Target != null ? StableKey.Of(Target) : Index.ToString());
}

/// <summary>Dense ids of memory keys (FieldKey, ElemKey, StaticKey and slot keys share one id space).</summary>
public sealed class KeyTable
{
    readonly IdTable<MemKey> _t = new();
    public int Count => _t.Count;
    public MemKey this[int id] => _t[id];
    public int Intern(MemKey k) => _t.Intern(k);
    public int IdOf(MemKey k) => _t.IdOf(k);
    public int[] StableOrder() => _t.StableOrder(k => k.ToString());
}

// ======================================================================================================== sets

/// <summary>A set of dense ids: a sorted int array, hash-consed by a <see cref="SetPool"/> so that equal sets share
/// storage and compare by reference. Joins only grow it.</summary>
public readonly struct IdSet : IEquatable<IdSet>
{
    readonly int[]? _a;
    internal IdSet(int[] a) { _a = a.Length == 0 ? null : a; }
    public static readonly IdSet Empty = default;
    public bool IsEmpty => _a == null;
    public int Count => _a?.Length ?? 0;
    public ReadOnlySpan<int> Items => _a ?? [];
    public bool Contains(int id) => _a != null && Array.BinarySearch(_a, id) >= 0;
    public bool IsSubsetOf(IdSet o)
    {
        if (_a == null) return true;
        if (o._a == null || o._a.Length < _a.Length) return false;
        foreach (int x in _a) if (!o.Contains(x)) return false;
        return true;
    }
    public bool Overlaps(IdSet o)
    {
        if (_a == null || o._a == null) return false;
        int i = 0, j = 0;
        while (i < _a.Length && j < o._a.Length) { if (_a[i] == o._a[j]) return true; if (_a[i] < o._a[j]) i++; else j++; }
        return false;
    }
    /// <summary>Equal sets from one pool are the same array; sets from different pools compare by content.</summary>
    public bool Equals(IdSet o) => ReferenceEquals(_a, o._a) || _a != null && o._a != null && _a.AsSpan().SequenceEqual(o._a);
    public override bool Equals(object? o) => o is IdSet s && Equals(s);
    public override int GetHashCode() { var h = new HashCode(); foreach (int x in Items) h.Add(x); return h.ToHashCode(); }
    public override string ToString() => "{" + string.Join(",", Items.ToArray()) + "}";
}

/// <summary>Interns id sets (hash-consing): one per solve, not shared between threads.</summary>
public sealed class SetPool
{
    readonly Dictionary<int[], int[]> _sets = new(new ArrayComparer());

    sealed class ArrayComparer : IEqualityComparer<int[]>
    {
        public bool Equals(int[]? a, int[]? b) => a!.AsSpan().SequenceEqual(b);
        public int GetHashCode(int[] a) { var h = new HashCode(); foreach (int x in a) h.Add(x); return h.ToHashCode(); }
    }

    IdSet Intern(int[] sorted)
    {
        if (sorted.Length == 0) return IdSet.Empty;
        if (_sets.TryGetValue(sorted, out var s)) return new IdSet(s);
        _sets[sorted] = sorted;
        return new IdSet(sorted);
    }

    public IdSet Of(IEnumerable<int> ids) => Intern(ids.Distinct().OrderBy(x => x).ToArray());
    public IdSet Of(int id) => Intern([id]);
    /// <summary>A set from ids already sorted and distinct (no copy).</summary>
    public IdSet OfSorted(int[] sorted) => Intern(sorted);

    public IdSet Add(IdSet s, int id) => s.Contains(id) ? s : Join(s, Of(id));

    /// <summary>The union (the join of every may-set).</summary>
    public IdSet Join(IdSet a, IdSet b)
    {
        if (b.IsEmpty || a.Equals(b)) return a;
        if (a.IsEmpty) return b;
        var x = a.Items; var y = b.Items;
        // (the union's size first: when one side holds the other, that side is the union, with nothing made)
        int n = 0;
        for (int i = 0, j = 0; i < x.Length || j < y.Length; n++)
        {
            if (j >= y.Length || i < x.Length && x[i] < y[j]) i++;
            else if (i >= x.Length || y[j] < x[i]) j++;
            else { i++; j++; }
        }
        if (n == x.Length) return a;
        if (n == y.Length) return b;
        var r = new int[n];
        for (int i = 0, j = 0, k = 0; k < n; k++)
        {
            if (j >= y.Length || i < x.Length && x[i] < y[j]) r[k] = x[i++];
            else if (i >= x.Length || y[j] < x[i]) r[k] = y[j++];
            else { r[k] = x[i]; i++; j++; }
        }
        return Intern(r);
    }
}

/// <summary>A may-set of memory keys a method or a release may write (Mods, ReleaseEffects, FilterMods, a borrow's
/// path writes), with the Wild bit for a store through a raw pointer or an effect not known (section 2.8).</summary>
public readonly record struct KeySet(IdSet Keys, bool Wild)
{
    public static readonly KeySet Empty = default;
    public static readonly KeySet All = new(IdSet.Empty, true);
    public bool IsEmpty => !Wild && Keys.IsEmpty;
    /// <summary>The key may be written.</summary>
    public bool Hits(int key) => Wild || Keys.Contains(key);
    public bool HitsAny(IdSet keys) => !keys.IsEmpty && (Wild || Keys.Overlaps(keys));
    public KeySet Join(KeySet o, SetPool pool) => new(pool.Join(Keys, o.Keys), Wild | o.Wild);
    public override string ToString() => Wild ? "*" : Keys.ToString();
}

// ======================================================================================================== 2.1 ownership

/// <summary>How a counted value holds its object (section 2.1, OSSA): Trivial holds no count (null, a constant, a
/// literal string, a Persistent object); Guaranteed is a borrow whose base outlives it; Owned holds +1, consumed
/// exactly once on every path. The order Trivial &lt; Guaranteed &lt; Owned is the join's: a borrow that cannot be
/// kept flips to Owned (4.6).</summary>
public enum OwnKind : byte { Trivial, Guaranteed, Owned }

/// <summary>A value's ownership fact: its kind, and for a Guaranteed value its borrow path (2.2).</summary>
public readonly record struct OwnFact(OwnKind Kind, BorrowPath? Path)
{
    public static readonly OwnFact Trivial = new(OwnKind.Trivial, null);
    public static readonly OwnFact Owned = new(OwnKind.Owned, null);
    public static OwnFact Borrowed(BorrowPath p) => new(OwnKind.Guaranteed, p);

    /// <summary>Trivial joins to the other; two borrows of the same path stay one; anything else is Owned.</summary>
    public static OwnFact Join(OwnFact a, OwnFact b)
    {
        if (a.Kind == OwnKind.Trivial) return b;
        if (b.Kind == OwnKind.Trivial) return a;
        if (a.Kind == OwnKind.Guaranteed && b.Kind == OwnKind.Guaranteed && BorrowPath.Same(a.Path, b.Path)) return a;
        return Owned;
    }
}

// ======================================================================================================== 2.2 borrow paths

/// <summary>Where a borrow path starts (2.2): a parameter (the caller's convention guarantees it), an Owned SSA value
/// of this body, a static (key id), or a frame variable (Body.Vars index).</summary>
public enum RootKind : byte { Arg, Val, Static, Var }

/// <summary>One step of a path: a field key or an element key (dense key ids).</summary>
public readonly record struct PathStep(int Key, bool Elem);

/// <summary>A borrow source path (2.2): a root and at most <see cref="MaxSteps"/> field or element steps (Lobster's
/// prefix rule). A longer path, or a value that comes from no path, is generic: null here, always Owned.</summary>
public sealed class BorrowPath : IEquatable<BorrowPath>
{
    public const int MaxSteps = 3;
    public readonly RootKind Root;
    /// <summary>Arg: the parameter index; Val: the Node.Id; Static: the key id; Var: the variable's index.</summary>
    public readonly int Index;
    public readonly PathStep[] Steps;

    public BorrowPath(RootKind root, int index, PathStep[]? steps = null)
    {
        Root = root; Index = index; Steps = steps ?? [];
        if (Steps.Length > MaxSteps) throw new ArgumentException("a borrow path has at most " + MaxSteps + " steps");
    }

    /// <summary>The path one step further, or null (generic) past the cap.</summary>
    public BorrowPath? Then(PathStep s) => Steps.Length >= MaxSteps ? null : new BorrowPath(Root, Index, [.. Steps, s]);

    /// <summary>The keys a write to which invalidates the path (2.2); the root's own key for a static root.</summary>
    public IEnumerable<int> Keys() { if (Root == RootKind.Static) yield return Index; foreach (var s in Steps) yield return s.Key; }

    public static bool Same(BorrowPath? a, BorrowPath? b) => a == null ? b == null : a.Equals(b);
    public bool Equals(BorrowPath? o) => o != null && Root == o.Root && Index == o.Index && Steps.AsSpan().SequenceEqual(o.Steps);
    public override bool Equals(object? o) => Equals(o as BorrowPath);
    public override int GetHashCode() { var h = new HashCode(); h.Add(Root); h.Add(Index); foreach (var s in Steps) h.Add(s); return h.ToHashCode(); }
    public override string ToString() => Root + "(" + Index + ")" + string.Concat(Steps.Select(s => (s.Elem ? "[" : ".") + s.Key + (s.Elem ? "]" : "")));
}

/// <summary>The counter-evidence of a speculative borrow (4.6): the keys written during its interval (proof 1), and
/// whether its root may die or be written, or another thread may write the path (proof 2). The borrow holds when all
/// of it is empty; otherwise it flips to Owned.</summary>
public readonly record struct BorrowEvidence(KeySet Writes, bool RootMayDie, bool OtherThread)
{
    public static readonly BorrowEvidence None = default;
    public bool Holds => Writes.IsEmpty && !RootMayDie && !OtherThread;
    public BorrowEvidence Join(BorrowEvidence o, SetPool pool) => new(Writes.Join(o.Writes, pool), RootMayDie | o.RootMayDie, OtherThread | o.OtherThread);
}

// ======================================================================================================== 2.3 allocation

/// <summary>Where an allocation site's objects live (2.3, Graal's PEA): scalar-replaced, in the frame, or on the
/// heap. The join is the maximum: a site that escapes anywhere is Heap.</summary>
public enum AllocKind : byte { Virtual, FrameOwned, Heap }

/// <summary>How a value or a parameter escapes (2.8's ParamEscape, EscapeAnalysis.Esc plus the thread case): a may-set
/// of ways, joined by union. No escape is the empty set.</summary>
[Flags]
public enum Escape : byte { None = 0, Returned = 1, Stored = 2, Thrown = 4, ToThread = 8, Unknown = 16 }

/// <summary>A parameter's escape: the ways, and for Stored the keys it may be stored under.</summary>
public readonly record struct ParamEscape(Escape Ways, IdSet StoredKeys)
{
    public static readonly ParamEscape None = default;
    public bool Escapes => Ways != Escape.None;
    public ParamEscape Join(ParamEscape o, SetPool pool) => new(Ways | o.Ways, pool.Join(StoredKeys, o.StoredKeys));
}

public static class Lattice
{
    public static AllocKind Join(AllocKind a, AllocKind b) => a > b ? a : b;
    public static ThreadEsc Join(ThreadEsc a, ThreadEsc b) => a > b ? a : b;
    public static CountKind Join(CountKind a, CountKind b) => a > b ? a : b;
    public static AccessKind Join(AccessKind a, AccessKind b) => a > b ? a : b;
    public static LoadKind Join(LoadKind a, LoadKind b) => a > b ? a : b;
    public static ParamConv Join(ParamConv a, ParamConv b) => a > b ? a : b;
    /// <summary>SCCP's lattice (Reach/Sccp.cs) in this file's orientation: Top is the empty start, Meet grows it.</summary>
    public static Val Join(Val a, Val b) => Val.Meet(a, b);
}

// ======================================================================================================== 2.4 thread escape

/// <summary>An abstract object's (or a static's, or a field key's) thread fact (2.4): reachable by its maker's thread
/// only, handed to exactly one other thread by a moving hand-off, or shared. Join is the maximum.</summary>
public enum ThreadEsc : byte { Local, Published, Shared }

/// <summary>An abstract object (2.4, 6.3): an allocation site (method id, IL offset), cloned by a calling context
/// (the call site's method id and IL offset, or -1 for none) where the context policy says so (depth at most 1).</summary>
public readonly record struct AbstractObject(int Method, int ILOffset, int ContextMethod = -1, int ContextOffset = -1);

/// <summary>Dense SiteIds of abstract objects.</summary>
public sealed class SiteTable
{
    readonly IdTable<AbstractObject> _t = new();
    public int Count => _t.Count;
    public AbstractObject this[int id] => _t[id];
    public int Intern(AbstractObject o) => _t.Intern(o);
    public int IdOf(AbstractObject o) => _t.IdOf(o);
}

// ======================================================================================================== 2.5 groups and owner chains

/// <summary>A field key's edge kind in the type graph (2.5, 5.1, 5.2): an owning reference down a structure, a proven
/// back reference k owners up (Up, with its level beside it), an exclusive part, an unproven edge that may close a
/// cycle, or one that cannot. It is a decision read off the proofs' evidence, not a lattice of its own.</summary>
public enum EdgeKind : byte { Outside, Down, Up, Part, Inside }

/// <summary>The owner-chain proofs of 5.2 and the other edge removals of 5.1, each with its own violation set.</summary>
public enum Proof : byte { Part, Up, DownStore, Fresh, ConstructionOnly, OwnedArray, Persistent }

/// <summary>A proof's counter-evidence: the store sites (dense ids of (method id, node id) pairs) that violate it. The
/// proof holds when the set is empty.</summary>
public readonly record struct ProofEvidence(IdSet Violations)
{
    public static readonly ProofEvidence None = default;
    public bool Holds => Violations.IsEmpty;
    public ProofEvidence Join(ProofEvidence o, SetPool pool) => new(pool.Join(Violations, o.Violations));
}

/// <summary>A value's chain position (2.5, ChainStores' fact on SSA): read out of the value Base (a Node.Id) by Depth
/// owning field reads. None: not read out of anything known; Unknown: two different positions met (the top).</summary>
public readonly record struct ChainPos(int Base, int Depth)
{
    public static readonly ChainPos None = new(-1, 0);
    public static readonly ChainPos Unknown = new(-2, 0);
    public bool IsKnown => Base >= 0;
    public ChainPos Below() => IsKnown ? new ChainPos(Base, Depth + 1) : this;
    public static ChainPos Join(ChainPos a, ChainPos b) => a == b || b == None ? a : a == None ? b : Unknown;
}

/// <summary>A type's cycle capability (5.1): the id of its strongly connected component in the type graph after the
/// proofs removed edges, or -1 when the type is on no cycle.</summary>
public readonly record struct CycleFact(int Scc)
{
    public static readonly CycleFact Acyclic = new(-1);
    public bool CycleCapable => Scc >= 0;
}

// ======================================================================================================== 2.6 types and constants

/// <summary>The exact types a value can have (2.6), as dense type ids, capped at <see cref="Cap"/>; past the cap it
/// saturates to Cone(declared type): every constructed subtype. Empty is Top (nothing seen yet). Saturating on
/// arrival equals saturating on the final size, because sets only grow (3.1).</summary>
public readonly record struct TypeSet(IdSet Types, int Cone)
{
    public const int Cap = 16;
    public static readonly TypeSet Empty = new(IdSet.Empty, -1);
    public static TypeSet ConeOf(int declaredType) => new(IdSet.Empty, declaredType);
    public bool IsEmpty => Cone < 0 && Types.IsEmpty;
    public bool Saturated => Cone >= 0;
    /// <summary>The one exact type, or -1.</summary>
    public int Single => Cone < 0 && Types.Count == 1 ? Types.Items[0] : -1;
    public TypeSet Join(TypeSet o, int declaredType, SetPool pool)
    {
        if (Saturated) return this;
        if (o.Saturated) return o;
        var u = pool.Join(Types, o.Types);
        return u.Count > Cap ? ConeOf(declaredType) : new TypeSet(u, -1);
    }
}

// ======================================================================================================== 2.7 per site

/// <summary>A count operation's kind (2.7, 6.5): elided, a plain count, a seq_cst atomic count.</summary>
public enum CountKind : byte { Elided, Plain, Atomic }

/// <summary>A store's lowering (5.3).</summary>
public enum StoreKind : byte { Plain, Init, Down, Fresh, Iso, PartAttach, PartDetach, UpStore, Link, LinkAt, Move, LastContainer }

/// <summary>A counted load's kind (2.7, 6.5): a plain load, or rt.load_retain at a shared slot.</summary>
public enum LoadKind : byte { Plain, LoadRetain }

/// <summary>An Interlocked/Volatile/volatile. access's kind (6.6).</summary>
public enum AccessKind : byte { Plain, Atomic }

/// <summary>How a call site passes a counted argument (4.3): the caller lends it (and releases after the call if it
/// was the last use), hands its count over (a move), or retains before the call because the value lives on and the
/// parameter is HandOver, or a borrowed argument's path is written by the callee (KeepAlive, 4.6).</summary>
public enum ArgConv : byte { Lend, HandOver, KeepAlive }

/// <summary>A parameter's convention in a signature (4.1): the callee borrows it or wants it owned. Join: HandOver
/// wins (an override that needs it adds the demand).</summary>
public enum ParamConv : byte { Lend, HandOver }

/// <summary>Why a parameter is HandOver (4.3, 4.4): it is stored, the callee keeps running after its last use, a
/// dispatch group's join, or the method is reached indirectly. A may-set.</summary>
[Flags]
public enum HandOverReason : byte { None = 0, Stores = 1, KeepsRunning = 2, Group = 4, Indirect = 8, Large = 16 }

/// <summary>A method entry (4.4, 3.5): the method's own function, an owned entry for a mask of HandOver parameters
/// (<c>|own&lt;mask&gt;</c>), a lend entry (<c>|lend</c>), or a borrowed-return entry (<c>|retborrow</c>). The
/// name suffix is the stable key of a variant function.</summary>
public enum VariantKind : byte { Entry, Owned, Lend, BorrowReturn }

public readonly record struct Variant(VariantKind Kind, uint Mask = 0)
{
    public static readonly Variant Entry = default;
    public string Suffix => Kind switch
    {
        VariantKind.Owned => "|own" + Mask.ToString(System.Globalization.CultureInfo.InvariantCulture),
        VariantKind.Lend => "|lend",
        VariantKind.BorrowReturn => "|retborrow",
        _ => "",
    };
}

/// <summary>The category of a planned count operation (section 9.1), for the executed-count statistics.</summary>
public enum RcCategory : byte
{
    None, ArgKeepAlive, ArgAfterCall, HandOverRelease, OwnedConvention, FlipField, FlipElem, FlipStatic, FlipVar, Generic,
    StoreOld, StoreRetain, Phi, Unwind, Result, Destroy, Group, Part, Runtime, Naive,
}

// ======================================================================================================== 2.8 per method

/// <summary>A method's ownership signature (section 4.1): each parameter's convention (this is parameter 0) with
/// its reasons, and whether its result is owned or borrowed from a parameter's path (the borrowed-return entry, 4.4).
/// The bottom is every parameter Lend and the result Owned.</summary>
public sealed class OwnershipSignature
{
    public readonly ParamConv[] Params;
    public readonly HandOverReason[] Reasons;
    /// <summary>The borrowed-return entry's path (Root is Arg), or null: the result is owned.</summary>
    public BorrowPath? ReturnBorrow;

    public OwnershipSignature(int parameters) { Params = new ParamConv[parameters]; Reasons = new HandOverReason[parameters]; }

    public static OwnershipSignature Bottom(MethodDesc m) => new(m.Signature.Length + (m.Signature.IsStatic ? 0 : 1));

    /// <summary>Joins the other's demand into this one; true when anything grew.</summary>
    public bool JoinFrom(OwnershipSignature o)
    {
        bool grew = false;
        for (int i = 0; i < Params.Length && i < o.Params.Length; i++)
        {
            var p = Lattice.Join(Params[i], o.Params[i]);
            var r = Reasons[i] | o.Reasons[i];
            if (p != Params[i] || r != Reasons[i]) { Params[i] = p; Reasons[i] = r; grew = true; }
        }
        return grew;
    }

    public override string ToString() => "(" + string.Join(",", Params) + ")" + (ReturnBorrow != null ? " -> " + ReturnBorrow : "");
}

/// <summary>The solved per-method facts (section 2.8's MethodSummary, the solved part; the local part is
/// Memory/Summary.cs's <see cref="MethodSummary"/>). Every field is a may-fact that starts empty; the must-facts are
/// the tests below.</summary>
public sealed class MethodFacts
{
    /// <summary>The keys the method may write, transitively (Gel's mutation summary, FieldBorrows.StoreSummary).</summary>
    public KeySet Mods;
    /// <summary>The Mods of every destroy and finalizer this method's releases can run (3.6).</summary>
    public KeySet ReleaseEffects;
    public ParamEscape[] ParamEscapes = [];
    public OwnershipSignature? Signature;
    /// <summary>Counter-evidence of a fresh result (FreshStores.ReturnsFresh): the ways a returned value may be held
    /// elsewhere. Fresh when None.</summary>
    public Escape NotFresh;
    /// <summary>The path the result is borrowed from (getters, return this), or null.</summary>
    public BorrowPath? BorrowReturn;
    /// <summary>The method may return normally (NeverReturns is its negation), may throw, may run a filter, may observe
    /// an object's lifetime (a weak handle read, GC.KeepAlive: Swift's deinit barriers inverted).</summary>
    public bool MayReturn, MayThrow, RunsFilters, ObservesLifetime;

    public bool NeverReturns => !MayReturn;
    public bool FreshReturn => NotFresh == Escape.None;
}

// ======================================================================================================== side tables

/// <summary>A fact per node of one body, indexed by Node.Id; unset entries read as the default (the fact's bottom).
/// Nodes added after the table was made (the planner's Retain and Release) grow it.</summary>
public sealed class NodeTable<T>
{
    T[]? _a;
    readonly T _default;
    public NodeTable(T bottom = default!) { _default = bottom; }
    public bool IsEmpty => _a == null;
    public T this[Node n]
    {
        get => _a != null && n.Id < _a.Length ? _a[n.Id] : _default;
        set
        {
            if (_a == null) { _a = new T[Math.Max(16, n.Id + 1)]; if (!EqualityComparer<T>.Default.Equals(_default, default!)) Array.Fill(_a, _default); }
            else if (n.Id >= _a.Length)
            {
                int old = _a.Length;
                Array.Resize(ref _a, Math.Max(n.Id + 1, old * 2));
                if (!EqualityComparer<T>.Default.Equals(_default, default!)) Array.Fill(_a, _default, old, _a.Length - old);
            }
            _a[n.Id] = value;
        }
    }
}

/// <summary>A method's plan (section 1.2's Memory/Plan.Build result): what lowering reads instead of the naive
/// Ownership.Info. The Retain and Release nodes and Node.Moves bits are in the body itself; the tables say how each
/// site is lowered. A table left unset reads as today's naive lowering (every count Plain, every store Plain, every
/// argument lent as today, every allocation Heap), so <see cref="Naive"/> lowers exactly as Ownership.Run did.</summary>
public sealed class MethodPlan
{
    public readonly Body Body;
    /// <summary>The values that hold a count at some point (each in a local of its own, zero when it holds none, and
    /// released when an exception leaves the function): Ownership.Info.RefValues.</summary>
    public readonly List<Node> CountedValues;
    /// <summary>Phi operands (phi, predecessor index) whose count moves into the phi; the others are counted again.</summary>
    public readonly HashSet<(Node, int)> PhiMoves;
    /// <summary>Each derived address's bases (Ownership.FindBases): they live to its last use.</summary>
    public readonly Dictionary<Node, List<Node>> Bases;
    public readonly NodeTable<OwnFact> Own = new(OwnFact.Owned);
    /// <summary>Per Retain/Release node, and per load or store whose count lowering emits.</summary>
    public readonly NodeTable<CountKind> Counts = new(CountKind.Plain);
    public readonly NodeTable<StoreKind> Stores = new(StoreKind.Plain);
    public readonly NodeTable<LoadKind> Loads = new(LoadKind.Plain);
    public readonly NodeTable<AccessKind> Accesses = new(AccessKind.Plain);
    public readonly NodeTable<AllocKind> Allocs = new(AllocKind.Heap);
    /// <summary>Per call node: each argument's convention (null: as today, every argument lent with the callee's
    /// naive entry retain).</summary>
    public readonly NodeTable<ArgConv[]?> Args = new(null);
    /// <summary>Per call node: the callee entry chosen (4.4).</summary>
    public readonly NodeTable<Variant> Callees = new(Variant.Entry);
    /// <summary>Per count node: its category (9.1) for Lower/RcStats.cs.</summary>
    public readonly NodeTable<RcCategory> Categories = new(RcCategory.None);
    /// <summary>The method's own entry this plan is for.</summary>
    public Variant Entry = Variant.Entry;

    public MethodPlan(Body body, List<Node> counted, HashSet<(Node, int)> phiMoves, Dictionary<Node, List<Node>> bases)
    {
        Body = body; CountedValues = counted; PhiMoves = phiMoves; Bases = bases;
    }

    /// <summary>The naive plan: Ownership.Run's result as it is (IL2WASM_MEM_NAIVE, and the default until Plan.cs is
    /// built).</summary>
    public static MethodPlan Naive(Body body, Ownership.Info info) => new(body, info.RefValues, info.PhiMoves, info.Bases);

    public bool PhiMove(Node phi, int pred) => PhiMoves.Contains((phi, pred));
}

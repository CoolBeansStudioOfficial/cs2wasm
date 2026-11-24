#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

// How lowering reads the method's plan (docs/MEMORY-IR.md 1.2, 2.1, 2.3, 2.7, 4.3, 4.4; the contract agreed for S1
// row f). Every table left unset reads as the naive lowering, so MethodPlan.Naive lowers byte for byte as before:
// - Own[v] Guaranteed or Trivial: v's local holds no count. Its load (and a cast, a catch value, a phi copy into it)
//   takes none, and the unwind cleanup does not release it. Counts[load] Elided also means the load takes none.
// - Counts[Retain/Release] Elided: nothing. Atomic (and Loads' LoadRetain): plain, and only in a program the solution
//   says is single-threaded (S3 brings the atomic forms); in a threaded one it is an internal error.
// - Stores[st] Init, or Counts[st] Elided: the old value is neither read nor released. Every other store kind is Plain
//   (the group kinds are S2's).
// - Args[call][i], aligned with call.A (a constructor's new object is always lent): Lend as before; HandOver (or a
//   Moves bit on a call operand) moves the caller's count in (its local is cleared at the call; a struct copy is
//   moved, not counted, and not released after the call); KeepAlive is one retain at the push, which the callee keeps.
// - Callees[call]: the entry called. A variant only at a direct call or a constructor (IlCompiler.VariantFor).
// - Entry BorrowReturn: a return takes no count.
// - Allocs[NewObj/Box/NewArr] Virtual or FrameOwned: a frame slot made with Rt.InitFrameObject (an immortal count).
//   The plan's Release of the value releases its children and empties the slot (so does making the slot again, and
//   an exception leaving the function, for what it still holds). Virtual is lowered as a frame slot for now.
// - Categories: Lower/RcStats.cs's counter at every count operation, the implied ones included.
sealed partial class FunctionLowering
{
    /// <summary>The frame objects: their slot offset, size and the type whose children they hold.</summary>
    readonly Dictionary<Node, (int Off, int Size, TypeDesc Type)> _frameObj = new();
    readonly List<Node> _frameObjOrder = [];

    /// <summary>The value holds a count of its own (Owned: the default).</summary>
    bool Holds(Node v) => _own.Own.IsEmpty || _own.Own[v].Kind == OwnKind.Owned;

    /// <summary>A load (or a cast, a catch value) of a counted value takes a count.</summary>
    internal bool LoadRetains(Node n)
    {
        if (_own.Own.IsEmpty && _own.Counts.IsEmpty && _own.Loads.IsEmpty) return true;
        if (!Holds(n)) return false;
        if (_own.Loads[n] == LoadKind.LoadRetain) SingleThreadOnly(n, "rt.load_retain");
        return Kind(n) != CountKind.Elided;
    }

    /// <summary>A store reads and releases the old value.</summary>
    bool OldReleased(Node store) => (_own.Stores.IsEmpty || _own.Stores[store] != StoreKind.Init) && Kind(store) != CountKind.Elided;

    /// <summary>A site's count kind; an atomic one is lowered plain only in a single-threaded program.</summary>
    CountKind Kind(Node site)
    {
        if (_own.Counts.IsEmpty) return CountKind.Plain;
        var k = _own.Counts[site];
        if (k == CountKind.Atomic) SingleThreadOnly(site, "an atomic count");
        return k;
    }

    void SingleThreadOnly(Node site, string what)
    {
        if (C.Solution == null || C.Solution.Threaded)
            throw new PlanVerifyException($"{_body.Method}: {what} at {site.Op} #{site.Id} in a program that can start threads (S3)");
    }

    ArgConv Conv(Node call, int i) => _own.Args.IsEmpty || _own.Args[call] is not { } a || i >= a.Length ? ArgConv.Lend : a[i];

    /// <summary>The caller's count of operand i moves into the call.</summary>
    bool ArgMoves(Node call, int i) => (call.Moves & (1u << i)) != 0 || Conv(call, i) == ArgConv.HandOver;

    /// <summary>An executed-count site (IL2WASM_COUNT_RC=1): the plan's category, else the site's own.</summary>
    void Rc(Node site, RcCategory fallback)
    {
        if (C.RcStats is not { } s) return;
        var c = _own.Categories[site];
        s.Count(E, _body.Method, site, c != RcCategory.None ? c : fallback);
    }

    /// <summary>The category of a load's count when it is a flip (4.6) of a borrow from that root.</summary>
    static RcCategory LoadCategory(Node n) => n.Op switch
    {
        Opc.LdFld => RcCategory.FlipField, Opc.LdElem => RcCategory.FlipElem, Opc.LdSFld => RcCategory.FlipStatic,
        Opc.VarLd => RcCategory.FlipVar, _ => RcCategory.Generic,
    };

    /// <summary>The function a direct call or a constructor calls: the entry the plan chose (4.4).</summary>
    WasmFunction Direct(Node n, MethodDesc m)
    {
        if (_own.Callees.IsEmpty) return C.FunctionFor(m);
        var v = _own.Callees[n];
        return v.Kind == VariantKind.Entry ? C.FunctionFor(m) : C.VariantFor(m, v);
    }

    /// <summary>A call that cannot take a variant (dispatch, a delegate's invoke, an intrinsic) must be planned for the
    /// method's own entry.</summary>
    void EntryOnly(Node n)
    {
        if (!_own.Callees.IsEmpty && _own.Callees[n].Kind != VariantKind.Entry)
            throw new PlanVerifyException($"{_body.Method}: {n.Op} #{n.Id} names the variant {_own.Callees[n].Suffix}, which only a direct call can use");
    }

    /// <summary>A call the compiler expands (IlCompiler.TryIntrinsic), with the plan's argument conventions: what the
    /// intrinsic keeps (KeepAlive) is counted once more after it, what is handed over is no longer the caller's.</summary>
    bool TryIntrinsicCall(MethodDesc m, Node n)
    {
        if (!C.TryIntrinsic(E, m, n, this)) return false;
        EntryOnly(n);
        if (n.Moves == 0 && (_own.Args.IsEmpty || _own.Args[n] == null)) return true;
        for (int i = 0; i < n.A.Length; i++)
        {
            var a = n.A[i];
            if (a.Kind != VK.Ref) { if (a.Kind == VK.Struct && Conv(n, i) != ArgConv.Lend) throw new PlanVerifyException($"{_body.Method}: a struct argument of the intrinsic {m} is not lent"); continue; }
            if (ArgMoves(n, i)) Clear(a);
            else if (Conv(n, i) == ArgConv.KeepAlive) { Rc(n, RcCategory.OwnedConvention); Get(a); E.Call(C.Rt.Retain); E.Op(WOp.Drop); }
        }
        return true;
    }

    /// <summary>IL2WASM_LOWER_STATS=1: at exit, on stderr, the frame object sites and the variant functions lowered,
    /// and the variants' WASM body sizes (instructions).</summary>
    internal static readonly bool Stats = Environment.GetEnvironmentVariable("IL2WASM_LOWER_STATS") == "1";
    static int s_frameSites, s_frameBytes, s_functions, s_instructions;
    static readonly Dictionary<VariantKind, (int Count, int Instructions)> s_variants = new();
    static bool s_statsHooked;
    static IlCompiler? s_statsOf;

    void CountStats()
    {
        lock (s_variants)
        {
            if (!s_statsHooked)
            {
                s_statsHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    Console.Error.WriteLine($"lower: {s_functions} functions ({s_instructions} instructions), {s_frameSites} frame object sites ({s_frameBytes} bytes of frame)");
                    foreach (var (k, (c, ins)) in s_variants.OrderBy(p => p.Key)) Console.Error.WriteLine($"lower: {c} {k} variants ({ins} instructions)");
                };
            }
            // (each round lowers the program again: the last round's are the ones reported)
            if (s_statsOf != C) { s_statsOf = C; s_functions = s_instructions = s_frameSites = s_frameBytes = 0; s_variants.Clear(); }
            s_functions++; s_instructions += _fn.Body.Count;
            s_frameSites += _frameObj.Count;
            foreach (var (_, (_, size, _)) in _frameObj) s_frameBytes += size;
            if (_own.Entry.Kind != VariantKind.Entry)
            {
                var (c, ins) = s_variants.GetValueOrDefault(_own.Entry.Kind);
                s_variants[_own.Entry.Kind] = (c + 1, ins + _fn.Body.Count);
            }
        }
    }

    // ------------------------------------------------------------------ frame objects (2.3)

    const int MaxFrameObjectSize = 256;

    void ReserveFrameObject(Node n)
    {
        TypeDesc type;
        int size;
        switch (n.Op)
        {
            case Opc.NewObj when ((MethodDesc)n.X!).OwningType is DefType { IsValueType: false, IsString: false, IsDelegate: false } t:
                type = t; size = IlContext.InstanceSize(t); break;
            case Opc.Box when !((TypeDesc)n.X!).IsNullable:
                type = (TypeDesc)n.X!; size = IlContext.ObjectHeader + System.Math.Max(4, Kinds.SizeOf(type)); break;
            case Opc.NewArr when n.A[0].Op == Opc.Const && n.A[0].I >= 0 && n.Type is ArrayType at:
                type = at; size = IlCompiler.ArrayDataOffset(at) + (int)n.A[0].I * Kinds.SizeOf(at.ElementType); break;
            default:
                throw new PlanVerifyException($"{_body.Method}: {n.Op} #{n.Id} is planned {_own.Allocs[n]}, which lowering makes only for an object, a box or an array of a constant length");
        }
        if (size > MaxFrameObjectSize) throw new PlanVerifyException($"{_body.Method}: {n.Op} #{n.Id} ({type}, {size} bytes) is above the frame object cap of {MaxFrameObjectSize}");
        if (IlCompiler.FinalizerOf(type) != null) throw new PlanVerifyException($"{_body.Method}: {n.Op} #{n.Id} ({type}) is finalizable: a frame object's finalizer is not lowered yet");
        size = (size + 7) & ~7;
        _frameSize = (_frameSize + 7) & ~7;
        _frameObj[n] = (_frameSize, size, type);
        _frameSlots.Add((_frameSize, size));
        _frameObjOrder.Add(n);
        _frameSize += size;
    }

    /// <summary>A frame object made: what its slot still holds released, then the object's header (immortal).</summary>
    void AllocFrame(Node n)
    {
        var (off, size, type) = _frameObj[n];
        C.Constructed(type);
        FrameDrop(n);
        FrameAddr(off); E.I32(size); E.Tid(C.TypeId(type));
        E.Call(C.FunctionFor(C.CoreMethod("Il2Wasm.Runtime", "Rt", "InitFrameObject")));
        Set(n);
        if (type is ArrayType) { E.Get(L(n)); E.I32((int)n.A[0].I); E.Store(WOp.I32Store, 12); }
    }

    /// <summary>A frame object's children released and its slot emptied (zero: its count reads as none, so a retain or
    /// a release of a stale address does nothing).</summary>
    void FrameDrop(Node n)
    {
        var (off, size, type) = _frameObj[n];
        if (type is ArrayType at)
        {
            if (Kinds.Counted(at.ElementType))
            {
                FrameAddr(off + IlCompiler.ArrayDataOffset(at)); FrameAddr(off); E.Load(WOp.I32Load, 12);
                E.Call(C.Rt.Range(at.ElementType, false));
            }
        }
        else if (type.IsValueType) { if (Kinds.Counted(type)) { FrameAddr(off + IlContext.ObjectHeader); E.Call(C.Rt.ReleaseStruct(type)); } }
        else
            for (TypeDesc? c = type; c != null; c = c.BaseType)
                foreach (var fd in c.GetFields())
                {
                    if (fd.IsStatic) continue;
                    if (Kinds.Of(fd.FieldType) == VK.Ref) { FrameAddr(off); E.Load(WOp.I32Load, IlContext.FieldOffset(fd)); E.Call(C.Rt.Release); }
                    else if (Kinds.Of(fd.FieldType) == VK.Struct && Kinds.Counted(fd.FieldType)) { FrameAddr(off + IlContext.FieldOffset(fd)); E.Call(C.Rt.ReleaseStruct(fd.FieldType)); }
                }
        FrameAddr(off); E.I32(0); E.I32(size); E.Emit(WOp.MemoryFill);
    }
}

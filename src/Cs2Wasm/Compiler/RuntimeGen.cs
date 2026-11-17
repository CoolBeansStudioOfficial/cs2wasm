using System.Text;
using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>WASM functions the generated code calls: counting, type tests, checked arithmetic, throw helpers.</summary>
internal sealed partial class RuntimeFns
{
    readonly Driver D;
    readonly WasmModule M;
    public WasmFunction Retain = null!, Release = null!, Destroy = null!, TypeNameFn = null!, TypeNameById = null!, TypeShortNameById = null!, TypeFlagsById = null!;
    readonly Dictionary<string, WasmFunction> _cache = [];
    public readonly Dictionary<string, (ITypeSymbol type, WasmFunction fn)> IsInstFns = [];
    public readonly Dictionary<string, (ITypeSymbol type, WasmFunction fn, bool exact)> BoxTests = [];

    public RuntimeFns(Driver d) { D = d; M = d.Module; }

    static FuncType FT(ValType[] p, ValType[] r) => new(p, r);
    static readonly ValType I = ValType.I32, L = ValType.I64, F = ValType.F32, Dd = ValType.F64;

    public void Create()
    {
        // retain(p) -> p : count +1 unless null or immortal (negative)
        Retain = M.AddFunction("rt.retain", FT([I], [I]));
        {
            var e = new Emitter(Retain);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store); e.End();
            e.End();
            e.Get(0);
        }
        Destroy = M.AddFunction("rt.destroy", FT([I], []));
        Release = M.AddFunction("rt.release", FT([I], []));
        {
            var e = new Emitter(Release);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(1); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.Else();
            e.Get(r); e.I32(1); e.Op(Op.I32Eq); e.If(); e.Get(0); e.I32(0); e.Store(Op.I32Store); e.Get(0); e.Call(Destroy); e.End();
            e.End();
            e.End();
        }
        TypeNameFn = M.AddFunction("rt.type_name", FT([I], [I]));
        TypeNameById = M.AddFunction("rt.type_name_by_id", FT([I], [I]));
        TypeShortNameById = M.AddFunction("rt.type_short_name_by_id", FT([I], [I]));
        TypeFlagsById = M.AddFunction("rt.type_flags_by_id", FT([I], [I]));
    }

    WasmFunction Lib(string type, string name, int arity = -1) => D.LibMethod(type, name, arity).Fn;

    public WasmFunction AllocObject => Lib("Cs2Wasm.Runtime.Rt", "AllocObject", 2);
    public WasmFunction AllocArray => Lib("Cs2Wasm.Runtime.Rt", "AllocArray", 3);
    /// <summary>Frees an object's memory (Rt.Free frees what is no object).</summary>
    public WasmFunction Free => Lib("Cs2Wasm.Runtime.Rt", "FreeObject", 2);
    public WasmFunction ThrowNullRef => Lib("Cs2Wasm.Runtime.ThrowHelpers", "NullReference");
    public WasmFunction ThrowIndex => Lib("Cs2Wasm.Runtime.ThrowHelpers", "IndexOutOfRange");
    public WasmFunction ThrowOverflow => Lib("Cs2Wasm.Runtime.ThrowHelpers", "Overflow");
    public WasmFunction ThrowDivideByZero => Lib("Cs2Wasm.Runtime.ThrowHelpers", "DivideByZero");
    public WasmFunction ThrowSwitch => Lib("Cs2Wasm.Runtime.ThrowHelpers", "SwitchExpression");
    public WasmFunction CaptureStack => Retain;

    WasmFunction Cached(string key, FuncType t, Action<Emitter> body)
    {
        if (_cache.TryGetValue(key, out var f)) return f;
        f = M.AddFunction(key, t);
        _cache[key] = f;
        body(new Emitter(f));
        return f;
    }

    /// <summary>(p) → p, throwing NullReferenceException when p is null: one call at each check (the frame is hidden,
    /// so traces show the caller, at the check).</summary>
    public WasmFunction NonNull => Cached("rt.nonnull", FT([I], [I]), e =>
    {
        e.Get(0); e.Op(Op.I32Eqz); e.If(); e.Call(ThrowNullRef); e.End(); e.Get(0);
    });

    /// <summary>(obj) → throws InvalidCastException naming both types.</summary>
    public WasmFunction ThrowInvalidCast(ITypeSymbol target)
    {
        var name = Driver.ClrName(D.Types.Canonical(target));
        return Cached("rt.throw_cast " + name, FT([I], []), e =>
        {
            e.Get(0); e.I32Addr(D.StringLiteral(name));
            e.Call(Lib("Cs2Wasm.Runtime.ThrowHelpers", "InvalidCast", 2));
            e.Op(Op.Unreachable);
        });
    }

    public WasmFunction IsInst(ITypeSymbol target)
    {
        target = D.Types.Canonical(target);
        var key = Types.Key(target);
        if (IsInstFns.TryGetValue(key, out var x)) return x.fn;
        var f = M.AddFunction("rt.isinst " + key, FT([I], [I]));
        IsInstFns[key] = (target, f);
        return f;
    }

    public WasmFunction IsExactBox(ITypeSymbol vt)
    {
        vt = D.Types.Canonical(vt);
        var key = "exact " + Types.Key(vt);
        if (BoxTests.TryGetValue(key, out var x)) return x.fn;
        var f = M.AddFunction("rt.isbox " + Types.Key(vt), FT([I], [I]));
        BoxTests[key] = (vt, f, true);
        return f;
    }

    /// <summary>(obj) → throws unless obj is a box of vt (or of an enum/underlying type with the same representation).</summary>
    public WasmFunction UnboxCheck(ITypeSymbol vt)
    {
        vt = D.Types.Canonical(vt);
        var key = "unbox " + Types.Key(vt);
        if (BoxTests.TryGetValue(key, out var x)) return x.fn;
        var f = M.AddFunction("rt.unbox_check " + Types.Key(vt), FT([I], []));
        BoxTests[key] = (vt, f, false);
        return f;
    }

    public WasmFunction ReleaseStruct(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        return Cached("rt.release_struct " + Types.Key(t), FT([I], []), e => EmitReleaseFields(e, 0, 0, t));
    }

    void EmitReleaseFields(Emitter e, int addrLocal, int baseOff, ITypeSymbol t)
    {
        var l = D.Types.LayoutOf(t);
        foreach (var f in l.Fields)
        {
            if (Types.IsRef(f.Type)) { e.Get(addrLocal); e.Load(Op.I32Load, baseOff + f.Offset); e.Call(Release); }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs)
            {
                e.Get(addrLocal); if (baseOff + f.Offset != 0) { e.I32(baseOff + f.Offset); e.Op(Op.I32Add); }
                e.Call(ReleaseStruct(f.Type));
            }
        }
    }

    /// <summary>Each reference of src (at baseOff) exchanged into dst's place, the old one released.</summary>
    void EmitExchangeFields(Emitter e, int dst, int src, int baseOff, ITypeSymbol t)
    {
        foreach (var f in D.Types.LayoutOf(t).Fields)
        {
            int o = baseOff + f.Offset;
            if (Types.IsRef(f.Type))
            {
                e.Get(dst); if (o != 0) { e.I32(o); e.Op(Op.I32Add); }
                e.Get(src); e.Load(Op.I32Load, o);
                e.Call(StoreRef); e.Call(Release);
            }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs) EmitExchangeFields(e, dst, src, o, f.Type);
        }
    }

    void EmitRetainFields(Emitter e, int addrLocal, ITypeSymbol t)
    {
        var l = D.Types.LayoutOf(t);
        foreach (var f in l.Fields)
        {
            if (Types.IsRef(f.Type)) { e.Get(addrLocal); e.Load(Op.I32Load, f.Offset); e.Call(Retain); e.Op(Op.Drop); }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs)
            {
                e.Get(addrLocal); if (f.Offset != 0) { e.I32(f.Offset); e.Op(Op.I32Add); }
                e.Call(RetainStruct(f.Type));
            }
        }
    }

    /// <summary>Threads (Driver.ThreadMode): a struct copied from memory another thread may write, field by field. A
    /// reference is read with its count in one step (rt.load_retain) and put in place as the destination needs: plainly
    /// into memory nobody else has yet (init), exchanged under the place's lock into memory others may see (shared),
    /// or over a value of this thread's own (neither); the old reference is released. Everything else is copied as
    /// bytes. `move`: the source is this thread's own and gives its counts up (it is cleared by the caller).</summary>
    void EmitCopyFieldsThreads(Emitter e, int dst, int src, int baseOff, ITypeSymbol t, bool init, bool shared, bool move, int tmp)
    {
        foreach (var f in D.Types.LayoutOf(t).Fields)
        {
            int o = baseOff + f.Offset;
            void Addr(int l) { e.Get(l); if (o != 0) { e.I32(o); e.Op(Op.I32Add); } }
            if (Types.IsRef(f.Type))
            {
                void Value() { if (move) { e.Get(src); e.Load(Op.I32Load, o); } else { Addr(src); e.Call(LoadRetain); } }
                if (init) { e.Get(dst); Value(); e.Store(Op.I32Store, o); }
                else if (shared) { Addr(dst); Value(); e.Call(StoreRef); e.Call(Release); }
                else { Value(); e.Set(tmp); e.Get(dst); e.Load(Op.I32Load, o); e.Get(dst); e.Get(tmp); e.Store(Op.I32Store, o); e.Call(Release); }
            }
            else if (Types.IsStruct(f.Type) && D.Types.LayoutOf(f.Type).HasRefs) EmitCopyFieldsThreads(e, dst, src, o, f.Type, init, shared, move, tmp);
            else
            {
                int size = D.Types.StorageOf(f.Type).size;
                if (size > 0) { Addr(dst); Addr(src); e.I32(size); e.Op(Op.MemoryCopy); }
            }
        }
    }

    public WasmFunction RetainStruct(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        return Cached("rt.retain_struct " + Types.Key(t), FT([I], []), e => EmitRetainFields(e, 0, t));
    }

    /// <summary>(dst, src): copy into uninitialized memory, taking counts.</summary>
    public WasmFunction CopyInit(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        return Cached("rt.copy_init " + Types.Key(t), FT([I, I], []), e =>
        {
            if (D.ThreadMode && D.Types.LayoutOf(t).HasRefs) { EmitCopyFieldsThreads(e, 0, 1, 0, t, init: true, shared: false, move: false, e.AddLocal(I)); return; }
            e.Get(1); e.Call(RetainStruct(t));
            e.Get(0); e.Get(1); e.I32(D.Types.StorageOf(t).size); e.Op(Op.MemoryCopy);
            if (D.Types.LayoutOf(t).HasRefs) NoteStructCopy(e.Fn, t);
        });
    }

    /// <summary>(dst, src): a move into uninitialized memory (StructMoves.cs): the bytes copied with their counts, the
    /// source cleared; what the value refers to is published as a copy's is (section 4.5).</summary>
    public WasmFunction MoveInit(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        return Cached("rt.move_init " + Types.Key(t), FT([I, I], []), e =>
        {
            int size = D.Types.StorageOf(t).size;
            e.Get(0); e.Get(1); e.I32(size); e.Op(Op.MemoryCopy);
            e.Get(1); e.I32(0); e.I32(size); e.Op(Op.MemoryFill);
            if (D.Types.LayoutOf(t).HasRefs) NoteStructCopy(e.Fn, t);
        });
    }

    /// <summary>(dst, src): a move over a value: the old contents released, then as MoveInit. Into memory that is not
    /// the frame (shared), each reference is exchanged into place and the old one released (rt.store_ref: another
    /// thread's store or compare-and-swap of the same slot cannot make it released twice).</summary>
    public WasmFunction MoveAssign(ITypeSymbol t, bool shared = true)
    {
        t = D.Types.Canonical(t);
        return Cached((shared ? "rt.move_assign " : "rt.move_assign_frame ") + Types.Key(t), FT([I, I], []), e =>
        {
            int size = D.Types.StorageOf(t).size;
            if (D.ThreadMode && shared && D.Types.LayoutOf(t).HasRefs)
            {
                EmitCopyFieldsThreads(e, 0, 1, 0, t, init: false, shared: true, move: true, e.AddLocal(I));
                e.Get(1); e.I32(0); e.I32(size); e.Op(Op.MemoryFill);
                return;
            }
            if (shared) EmitExchangeFields(e, 0, 1, 0, t);
            else { e.Get(0); e.Call(ReleaseStruct(t)); }
            e.Get(0); e.Get(1); e.I32(size); e.Op(Op.MemoryCopy);
            e.Get(1); e.I32(0); e.I32(size); e.Op(Op.MemoryFill);
            if (D.Types.LayoutOf(t).HasRefs) NoteStructCopy(e.Fn, t);
        });
    }

    /// <summary>(dst, src): assignment: take new counts, release old contents, copy (into memory that is not the
    /// frame, the references exchanged: MoveAssign).</summary>
    public WasmFunction CopyAssign(ITypeSymbol t, bool shared = true)
    {
        t = D.Types.Canonical(t);
        return Cached((shared ? "rt.copy_assign " : "rt.copy_assign_frame ") + Types.Key(t), FT([I, I], []), e =>
        {
            if (D.ThreadMode && D.Types.LayoutOf(t).HasRefs) { EmitCopyFieldsThreads(e, 0, 1, 0, t, init: false, shared, move: false, e.AddLocal(I)); return; }
            e.Get(1); e.Call(RetainStruct(t));
            if (shared) EmitExchangeFields(e, 0, 1, 0, t);
            else { e.Get(0); e.Call(ReleaseStruct(t)); }
            e.Get(0); e.Get(1); e.I32(D.Types.StorageOf(t).size); e.Op(Op.MemoryCopy);
            if (D.Types.LayoutOf(t).HasRefs) NoteStructCopy(e.Fn, t);
        });
    }

    /// <summary>(addr, count): release counted contents of consecutive elements.</summary>
    public WasmFunction ReleaseRange(ITypeSymbol et) => Range(et, release: true);
    public WasmFunction RetainRange(ITypeSymbol et) => Range(et, release: false);

    WasmFunction Range(ITypeSymbol et, bool release)
    {
        et = D.Types.Canonical(et);
        return Cached((release ? "rt.release_range " : "rt.retain_range ") + Types.Key(et), FT([I, I], []), e =>
        {
            if (!D.Types.NeedsRc(et)) return;
            int size = D.Types.StorageOf(et).size;
            int end = e.AddLocal(I);
            e.Get(0); e.Get(1); e.I32(size); e.Op(Op.I32Mul); e.Op(Op.I32Add); e.Set(end);
            var brk = e.Block(); var top = e.Loop();
            e.Get(0); e.Get(end); e.Op(Op.I32GeU); e.BrIf(brk);
            if (Types.IsRef(et)) { e.Get(0); e.Load(Op.I32Load); if (release) e.Call(Release); else { e.Call(Retain); e.Op(Op.Drop); } }
            else { e.Get(0); e.Call(release ? ReleaseStruct(et) : RetainStruct(et)); }
            e.Get(0); e.I32(size); e.Op(Op.I32Add); e.Set(0);
            e.Br(top); e.End(); e.End();
        });
    }

    // ---------------- arithmetic helpers ----------------
    public WasmFunction IntDiv(bool div, ValType w, bool signed)
    {
        var name = $"rt.{(div ? "div" : "rem")}_{(signed ? "s" : "u")}{(w == L ? 64 : 32)}";
        return Cached(name, FT([w, w], [w]), e =>
        {
            bool l = w == L;
            e.Get(1); e.Op(l ? Op.I64Eqz : Op.I32Eqz); e.If(); e.Call(ThrowDivideByZero); e.End();
            if (signed)
            {
                // MinValue / -1 overflows (.NET throws for both / and %)
                e.Get(1); if (l) e.I64(-1); else e.I32(-1); e.Op(l ? Op.I64Eq : Op.I32Eq);
                e.If();
                e.Get(0); if (l) e.I64(long.MinValue); else e.I32(int.MinValue); e.Op(l ? Op.I64Eq : Op.I32Eq);
                e.If(); e.Call(ThrowOverflow); e.End();
                e.End();
            }
            e.Get(0); e.Get(1);
            e.Op((div, l, signed) switch
            {
                (true, false, true) => Op.I32DivS, (true, false, false) => Op.I32DivU,
                (true, true, true) => Op.I64DivS, (true, true, false) => Op.I64DivU,
                (false, false, true) => Op.I32RemS, (false, false, false) => Op.I32RemU,
                (false, true, true) => Op.I64RemS, _ => Op.I64RemU,
            });
        });
    }

    public WasmFunction FloatRem(ValType w)
    {
        if (w == Dd) return Lib("Cs2Wasm.Runtime.Rt", "FMod", 2);
        return Cached("rt.fmodf", FT([F, F], [F]), e =>
        {
            e.Get(0); e.Op(Op.F64PromoteF32); e.Get(1); e.Op(Op.F64PromoteF32);
            e.Call(Lib("Cs2Wasm.Runtime.Rt", "FMod", 2));
            e.Op(Op.F32DemoteF64);
        });
    }

    public WasmFunction CheckedArith(BinaryOperatorKind kind, Prim p)
    {
        var w = Types.WasmPrim(p);
        bool signed = Types.IsSigned(p);
        var name = $"rt.checked_{kind}_{p}";
        return Cached(name, FT([w, w], [w]), e =>
        {
            if (w == I)
            {
                // compute in 64 bits, check the range of p
                var ext = signed ? Op.I64ExtendI32S : Op.I64ExtendI32U;
                int r = e.AddLocal(L);
                e.Get(0); e.Op(ext); e.Get(1); e.Op(ext);
                e.Op(kind switch { BinaryOperatorKind.Add => Op.I64Add, BinaryOperatorKind.Subtract => Op.I64Sub, _ => Op.I64Mul });
                e.Set(r);
                long min = p switch { Prim.I32 or Prim.IntPtr => int.MinValue, Prim.I16 => short.MinValue, Prim.I8 => sbyte.MinValue, _ => 0 };
                long max = p switch { Prim.I32 or Prim.IntPtr => int.MaxValue, Prim.I16 => short.MaxValue, Prim.I8 => sbyte.MaxValue, Prim.U8 => byte.MaxValue, Prim.U16 or Prim.Char => ushort.MaxValue, _ => uint.MaxValue };
                e.Get(r); e.I64(min); e.Op(Op.I64LtS); e.Get(r); e.I64(max); e.Op(Op.I64GtS); e.Op(Op.I32Or);
                e.If(); e.Call(ThrowOverflow); e.End();
                e.Get(r); e.Op(Op.I32WrapI64);
                return;
            }
            int res = e.AddLocal(L);
            switch (kind)
            {
                case BinaryOperatorKind.Add:
                    e.Get(0); e.Get(1); e.Op(Op.I64Add); e.Set(res);
                    if (signed) { e.Get(0); e.Get(res); e.Op(Op.I64Xor); e.Get(1); e.Get(res); e.Op(Op.I64Xor); e.Op(Op.I64And); e.I64(0); e.Op(Op.I64LtS); }
                    else { e.Get(res); e.Get(0); e.Op(Op.I64LtU); }
                    break;
                case BinaryOperatorKind.Subtract:
                    e.Get(0); e.Get(1); e.Op(Op.I64Sub); e.Set(res);
                    if (signed) { e.Get(0); e.Get(1); e.Op(Op.I64Xor); e.Get(0); e.Get(res); e.Op(Op.I64Xor); e.Op(Op.I64And); e.I64(0); e.Op(Op.I64LtS); }
                    else { e.Get(0); e.Get(1); e.Op(Op.I64LtU); }
                    break;
                default:
                    e.Get(0); e.Get(1);
                    e.Call(Lib("Cs2Wasm.Runtime.Rt", signed ? "CheckedMulI64" : "CheckedMulU64", 2));
                    return;
            }
            e.If(); e.Call(ThrowOverflow); e.End();
            e.Get(res);
        });
    }

    public WasmFunction CheckedFloatToInt(Prim from, Prim to)
    {
        return Cached($"rt.checked_conv_{from}_{to}", FT([Types.WasmPrim(from)], [Types.WasmPrim(to)]), e =>
        {
            int x = e.AddLocal(Dd);
            e.Get(0); if (from == Prim.F32) e.Op(Op.F64PromoteF32); e.Set(x);
            double lo, hi; // valid iff lo < x < hi
            switch (to)
            {
                case Prim.I64: lo = -9223372036854777856.0; hi = 9223372036854775808.0; break;
                case Prim.U64: lo = -1.0; hi = 18446744073709551616.0; break;
                default:
                    var (mn, mx) = to switch
                    {
                        Prim.I8 => (sbyte.MinValue, (double)sbyte.MaxValue), Prim.U8 => (0, byte.MaxValue), Prim.I16 => (short.MinValue, short.MaxValue),
                        Prim.U16 or Prim.Char => (0, ushort.MaxValue), Prim.U32 or Prim.UIntPtr => (0, uint.MaxValue), _ => ((double)int.MinValue, (double)int.MaxValue),
                    };
                    lo = mn - 1.0; hi = mx + 1.0; break;
            }
            e.Get(x); e.F64(lo); e.Op(Op.F64Gt); e.Get(x); e.F64(hi); e.Op(Op.F64Lt); e.Op(Op.I32And); e.Op(Op.I32Eqz);
            e.If(); e.Call(ThrowOverflow); e.End();
            e.Get(x);
            e.Op(Types.Is64(to) ? (to == Prim.U64 ? Op.I64TruncSatF64U : Op.I64TruncSatF64S) : (to is Prim.U32 or Prim.UIntPtr ? Op.I32TruncSatF64U : Op.I32TruncSatF64S));
            if (to is Prim.U8) { e.I32(0xFF); e.Op(Op.I32And); }
            else if (to is Prim.U16 or Prim.Char) { e.I32(0xFFFF); e.Op(Op.I32And); }
            else if (to is Prim.I8) e.Op(Op.I32Extend8S);
            else if (to is Prim.I16) e.Op(Op.I32Extend16S);
        });
    }
}

/// <summary>Generates whole-program runtime pieces and finishes the module.</summary>
sealed partial class RuntimeGen
{
    readonly Driver D;
    readonly Dictionary<int, WasmFunction> _destroyFns = [];
    // virtual dispatch: the constructed types in the order they appeared, indexed by each of their supertypes; each
    // stub walks the lists it needs from where it last stopped (every stub and type pair is looked at once)
    SubtypeIndex? _index;
    readonly Dictionary<string, (List<int> list, int own, int special)> _stubPos = [];

    bool IsSpecial(int id) => D.SyntheticById.ContainsKey(id) || D.DisplayById.ContainsKey(id) || D.TypeById[id] is IArrayTypeSymbol;
    readonly List<(string name, MethodInst mi)> _exports = [];
    readonly WasmFunction _init;

    public RuntimeGen(Driver d)
    {
        D = d;
        D.Rt = new RuntimeFns(d);
        D.Rt.Create();
        _init = d.Module.AddFunction("init", new FuncType([], []));
        _init.Exports.Add("init");
    }

    public void Prepare()
    {
        // library pieces every module needs
        D.Rt.AllocObject.ToString();
        D.LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1);
        D.LibMethod("Cs2Wasm.Runtime.ThrowHelpers", "InvalidCast", 2);
        D.LibMethod("Cs2Wasm.Runtime.Rt", "FreeObject", 2);
        D.LibMethod("Cs2Wasm.Runtime.Rt", "AllocFirst", 1);
        if (D.ThreadMode) { D.LibMethod("Cs2Wasm.Runtime.Rt", "PendDestroy", 1); D.LibMethod("Cs2Wasm.Runtime.Rt", "NextPending", 0); }
    }

    public void Export(string name, MethodInst mi) => _exports.Add((name, mi));
    public IEnumerable<MethodInst> UserExports => _exports.Where(e => !e.name.StartsWith("cs2wasm_")).Select(e => e.mi);

    /// <summary>Generates destroy functions for newly constructed types and resolves virtual calls; true if new work appeared.</summary>
    public bool Expand()
    {
        bool any = D.ExpandObjectArrayThunks() | D.ExpandAttributeArrays() | D.ExpandDynamicInvokeThunks() | D.ExpandInterpreterTypes();
        _index ??= new SubtypeIndex(D.Types);
        foreach (var id in D.Constructed) _index.Add(id, IsSpecial(id) ? null : D.TypeById[id]);
        foreach (var id in D.Constructed.ToList())
        {
            if (_destroyFns.ContainsKey(id)) continue;
            _destroyFns[id] = MakeDestroy(id);
            any = true;
        }
        foreach (var (key, stub) in D.Stubs.ToList())
        {
            // the types this stub can dispatch to (those that may be subtypes of the slot's type, and the special
            // ones), from where it stopped last time
            if (!_stubPos.TryGetValue(key, out var pos)) pos = (_index.Candidates(stub.Slot.ContainingType), 0, 0);
            var fresh = new List<int>();
            for (int i = pos.own; i < pos.list.Count; i++) fresh.Add(pos.list[i]);
            pos.own = pos.list.Count;
            if (pos.list != _index.All) { for (int i = pos.special; i < _index.Special.Count; i++) fresh.Add(_index.Special[i]); pos.special = _index.Special.Count; }
            _stubPos[key] = pos;
            // (by id, as each round's new cases always came: the stubs' code does not depend on the index)
            fresh.Sort();
            foreach (var id in fresh)
            {
                if (D.SyntheticById.TryGetValue(id, out var syn))
                {
                    var slotKey = Types.MethodKey(D.CanonicalMethod(stub.Slot));
                    if (syn.Impls.TryGetValue(slotKey, out var sfn))
                        stub.Cases["synthetic#" + id] = (D.ObjectType, new MethodInst { Sym = stub.Slot, Fn = sfn, Key = "synthetic " + id + " " + slotKey }, false);
                    continue;
                }
                if (D.DisplayById.ContainsKey(id))
                {
                    // a closure object has object's members (its ToString in an expression tree's text)
                    if (Types.IsObject(stub.Slot.ContainingType) && D.Implementation((INamedTypeSymbol)D.ObjectType, stub.Slot) is { } oimpl)
                    {
                        stub.Cases["synthetic#" + id] = (D.ObjectType, D.Method(oimpl, stub.Consts), false);
                        any = true;
                    }
                    continue;
                }
                var t = D.TypeById[id];
                var slotOwner = stub.Slot.ContainingType;
                if (!D.Types.IsSubtype(t, slotOwner)) continue;
                var impl = D.Implementation(t, stub.Slot);
                if (impl == null) continue;
                bool boxed = t.IsValueType && Types.Key(D.Types.Canonical(impl.ContainingType)) == Types.Key(D.Types.Canonical(t));
                var mi = t.IsValueType && impl.Name == "Equals" && impl.Parameters.Length == 1 && impl.ContainingType.SpecialType == SpecialType.System_ValueType
                    && !FunctionCompiler.CanCompareBits(D.Types, t) && Types.PrimOf(t) == Prim.None && t.TypeKind != TypeKind.Enum
                    ? FunctionCompiler.ValueEquals(D, t, stub.Slot)
                    : D.Method(impl, stub.Consts);
                stub.Cases[Types.Key(t) + "#" + id] = (t, mi, boxed);
                any = true;
            }
        }
        return any;
    }

    /// <summary>The keys of a type, its base types and its interfaces (what IsSubtype finds without variance).</summary>

    /// <summary>The type's destruction frees the object and does nothing else that can destroy another: it calls
    /// only the free (and the marks' notice).</summary>
    bool IsLeafDestroy(int id) =>
        _destroyFns.TryGetValue(id, out var f) && f.Body.All(i => i.Op != Op.Call && i.Op != Op.CallIndirect || ReferenceEquals(i.Ref, D.Rt.Free) || ReferenceEquals(i.Ref, D.Rt.OnDestroy));

    bool HasDestroy(int id) => _destroyFns.ContainsKey(id);

    WasmFunction MakeDestroy(int id)
    {
        var f = D.Module.AddFunction("destroy " + id, new FuncType([ValType.I32], []));
        EmitDestroy(new Emitter(f), id, clear: true, free: true);
        return f;
    }

    /// <summary>An object's destruction: clear (finalizer, release of counted contents) and/or free (its memory).</summary>
    void EmitDestroy(Emitter e, int id, bool clear, bool free, int finalizer = 0)
    {
        // finalizer: 0 with the clear, 1 the finalizer alone, -1 the clear without it (an arena's members: the
        // finalizers of all that die together run before any of them lets go of what it refers to)
        var rt = D.Rt;
        if (D.DisplayById.TryGetValue(id, out var disp))
        {
            if (clear)
            {
                e.Get(0); e.Load(Op.I32Load, 12); e.Call(rt.Release);
                foreach (var (off, t) in disp.Vars.Select(v => disp.Slots[v]).Concat(disp.ExtraSlots.Values))
                {
                    if (Types.IsRef(t)) { e.Get(0); e.Load(Op.I32Load, off); e.Call(rt.Release); }
                    else if (Types.IsStruct(t) && D.Types.LayoutOf(t).HasRefs) { e.Get(0); e.I32(off); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(t)); }
                }
            }
            if (free) { e.Get(0); e.I32(disp.Size); e.Call(rt.Free); }
            return;
        }
        var type = D.TypeById[id];
        if (id == 1)
        {
            // string: header + length + chars + terminator
            if (clear) { e.Get(0); e.Call(rt.OnDestroy); e.Op(Op.Drop); }
            if (free) { e.Get(0); e.Get(0); e.Load(Op.I32Load, 12); e.I32(1); e.Op(Op.I32Shl); e.I32(Types.ArrayData + 2); e.Op(Op.I32Add); e.Call(rt.Free); }
            return;
        }
        if (type is IArrayTypeSymbol at)
        {
            var et = D.Types.Canonical(at.ElementType);
            int size = D.Types.StorageOf(et).size;
            int data = Types.ArrayDataOffset(at.Rank);
            if (clear) { e.Get(0); e.Call(rt.OnDestroy); e.Op(Op.Drop); }
            if (clear && D.Types.NeedsRc(et)) { e.Get(0); e.I32(data); e.Op(Op.I32Add); e.Get(0); e.Load(Op.I32Load, 12); e.Call(rt.ReleaseRange(et)); }
            if (free) { e.Get(0); e.Get(0); e.Load(Op.I32Load, 12); e.I32(size); e.Op(Op.I32Mul); e.I32(data); e.Op(Op.I32Add); e.Call(rt.Free); }
            return;
        }
        if (type.IsValueType)
        {
            int off = FunctionCompilerHelpers.BoxDataOffset(D.Types, type);
            if (clear) { e.Get(0); e.Call(rt.OnDestroy); e.Op(Op.Drop); }
            if (clear && Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) { e.Get(0); e.I32(off); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(type)); }
            if (free) { e.Get(0); e.I32(off + D.Types.StorageOf(type).size); e.Call(rt.Free); }
            return;
        }
        // class: finalizer, then fields, then the memory
        var l = D.Types.LayoutOf(type);
        // A sealed class with a field of its own type (a list's node and its next): the object that field holds is
        // of this very class, so when this object's reference to it is the last, its destruction is this function
        // again: a loop, not a call. A list let go at its head is freed node by node in a few instructions each,
        // with no nesting to count and no stack to run out of. (The field is let go of last, after the object's
        // memory is freed: any order C# allows. A tree's node loops on its last such field and nests on the others.)
        if (clear && free && finalizer == 0 && !D.ThreadMode && FindFinalizer(type) == null
            && type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true }
            && l.Fields.LastOrDefault(f => f.Type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true } ft && D.ExistingTypeId(D.Types.Canonical(ft)) == id) is { } tail)
        {
            int next = e.AddLocal(ValType.I32), c = e.AddLocal(ValType.I32);
            var top = e.Loop();
            e.Get(0); e.Call(rt.OnDestroy); e.Op(Op.Drop);
            foreach (var fs in l.Fields)
            {
                if (ReferenceEquals(fs, tail)) continue;
                if (Types.IsRef(fs.Type)) { e.Get(0); e.Load(Op.I32Load, fs.Offset); e.Call(ReleaseOfField(fs.Type)); }
                else if (Types.IsStruct(fs.Type) && D.Types.LayoutOf(fs.Type).HasRefs) { e.Get(0); e.I32(fs.Offset); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(fs.Type)); }
            }
            e.Get(0); e.Load(Op.I32Load, tail.Offset); e.Set(next);
            e.Get(0); e.I32(Math.Max(l.Size, Types.ObjectHeader)); e.Call(rt.Free);
            e.Get(next);
            e.If();
            e.Get(next); e.Load(Op.I32Load); e.Tee(c); e.I32(1); e.Op(Op.I32GtS);
            e.If();
            e.Get(next); e.Get(c); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.Else();
            e.Get(c); e.I32(1); e.Op(Op.I32Eq);
            e.If();
            e.Get(next); e.I32(0); e.Store(Op.I32Store);
            e.Get(next); e.Set(0);
            e.Br(top);
            e.End();
            // (a member of a cycle's group, or an object that is never freed: as any release)
            e.Get(next); e.Call(rt.Release);
            e.End();
            e.End();
            e.End();
            return;
        }
        if (clear || finalizer == 1)
        {
            var fin = finalizer == -1 ? null : FindFinalizer(type);
            if (finalizer == -1 && FindFinalizer(type) != null) { }
            else
            {
            // weak references to it are cleared first; GC.SuppressFinalize can skip the finalizer
            e.Get(0); e.Call(rt.OnDestroy);
            if (fin != null)
            {
                e.If();
                // the finalizer may resurrect nothing observable here; it runs with a temporary count
                // (an arena member can refer to itself: its finalizer clearing that field must not destroy it)
                e.Get(0); e.I32(finalizer == 1 ? 0x40000000 : 1); e.Store(Op.I32Store);
                e.Call(rt.FinHold); e.Get(0); e.Call(D.Method(fin).Fn); e.Call(rt.FinUnhold);
                e.Get(0); e.I32(0); e.Store(Op.I32Store);
                e.End();
            }
            else e.Op(Op.Drop);
            }
            if (finalizer == 1) return;
            foreach (var fs in l.Fields)
            {
                if (Types.IsRef(fs.Type)) { e.Get(0); e.Load(Op.I32Load, fs.Offset); e.Call(ReleaseOfField(fs.Type)); }
                else if (Types.IsStruct(fs.Type) && D.Types.LayoutOf(fs.Type).HasRefs) { e.Get(0); e.I32(fs.Offset); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(fs.Type)); }
            }
        }
        if (free) { e.Get(0); e.I32(Math.Max(l.Size, Types.ObjectHeader)); e.Call(rt.Free); }
    }

    // ---------------- arenas (section 4.3) ----------------
    void EmitArenaSupport()
    {
        var rt = D.Rt;
        var ar = D.Arenas;
        var retainSlow = ar.RuntimeFn("ArenaRetain", 1);
        var releaseSlow = ar.RuntimeFn("ArenaRelease", 1);
        // retain(p) -> p: count +1; a member (count word below -1) counts on its arena
        {
            var f = rt.Retain; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(ValType.I32);
            e.Get(0); e.If();
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store);
            e.Else();
            // (a member that has outside references takes one more in its own count word: its arena's record counts
            // the members that have any, Rt.ArenaRetain)
            e.Get(r); e.I32(-2); e.Op(Op.I32LtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.Else(); e.Get(r); e.I32(-2); e.Op(Op.I32Eq); e.If(); e.Get(0); e.Call(retainSlow); e.End();
            e.End();
            e.End();
            e.End();
            e.Get(0);
        }
        {
            var f = rt.Release; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(ValType.I32);
            e.Get(0); e.If();
            e.Get(0); e.Load(Op.I32Load); e.Tee(r); e.I32(1); e.Op(Op.I32GtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.Else();
            e.Get(r); e.I32(1); e.Op(Op.I32Eq); e.If(); e.Get(0); e.I32(0); e.Store(Op.I32Store); e.Get(0); e.Call(rt.Destroy);
            e.Else();
            e.Get(r); e.I32(-3); e.Op(Op.I32LtS);
            e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store);
            e.Else(); e.Get(r); e.I32(-1); e.Op(Op.I32LtS); e.If(); e.Get(0); e.Call(releaseSlow); e.End();
            e.End();
            e.End();
            e.End();
            e.End();
        }
        // Array.Copy / Array.Clear into arrays whose elements can close a cycle go element by element
        if (ar.AnyArrayCycle)
        {
            if (rt.ArrayCopyIfMade is { } copy)
            {
                var pre = new Emitter(new WasmFunction("tmp", copy.Type));
                pre.Get(5); pre.I32(4); pre.Op(Op.I32Eq);
                pre.Get(2); pre.Load(Op.I32Load, 4); pre.Call(rt.SccOfFn); pre.I32(0); pre.Op(Op.I32Ne); pre.Op(Op.I32And);
                pre.If();
                // (the indices count from where a vector's elements start)
                int lead = pre.AddLocal(ValType.I32);
                for (int i = 0; i < 5; i++)
                {
                    pre.Get(i);
                    if (i is 1 or 3) { rt.EmitArrayLead(pre, i - 1, lead); pre.I32(2); pre.Op(Op.I32ShrU); pre.Op(Op.I32Add); }
                }
                pre.Call(ar.RuntimeFn("ArenaArrayCopy", 5)); pre.Op(Op.Return);
                pre.End();
                copy.Body.InsertRange(0, pre.Body);
            }
            if (rt.ArrayClearIfMade is { } clr)
            {
                var pre = new Emitter(new WasmFunction("tmp", clr.Type));
                pre.Get(3); pre.I32(4); pre.Op(Op.I32Eq);
                pre.Get(0); pre.Load(Op.I32Load, 4); pre.Call(rt.SccOfFn); pre.I32(0); pre.Op(Op.I32Ne); pre.Op(Op.I32And);
                pre.If();
                int lead = pre.AddLocal(ValType.I32);
                for (int i = 0; i < 3; i++)
                {
                    pre.Get(i);
                    if (i == 1) { rt.EmitArrayLead(pre, 0, lead); pre.I32(2); pre.Op(Op.I32ShrU); pre.Op(Op.I32Add); }
                }
                pre.Call(ar.RuntimeFn("ArenaArrayClear", 3)); pre.Op(Op.Return);
                pre.End();
                clr.Body.InsertRange(0, pre.Body);
            }
        }
        // an object of a cycle-capable type has its arena's words before its header: allocated so (AllocObject goes to
        // AllocObjectArena for such a type), and freed so by its destroy function
        var allocArena = ar.RuntimeFn("AllocObjectArena", 2);
        var freeArena = ar.RuntimeFn("FreeObjectArena", 2);
        {
            var alloc = rt.AllocObject;
            var pre = new Emitter(new WasmFunction("tmp", alloc.Type));
            pre.Get(1); pre.Call(rt.SccOfFn);
            pre.If();
            pre.Get(0); pre.Get(1); pre.Call(allocArena); pre.Op(Op.Return);
            pre.End();
            alloc.Body.InsertRange(0, pre.Body);
        }
        void FreeWithPrefix(WasmFunction fn)
        {
            var b = fn.Body;
            for (int i = 0; i < b.Count; i++)
                if (b[i].Op == Op.Call && ReferenceEquals(b[i].Ref, rt.Free)) b[i] = b[i] with { Ref = freeArena };
        }
        foreach (var id in ar.CycleTypes) if (_destroyFns.TryGetValue(id, out var df)) FreeWithPrefix(df);
        // per cycle-capable type: clear, free, visit (member-to-member slots); dispatch on the type id
        var clearFns = new Dictionary<int, WasmFunction>();
        var freeFns = new Dictionary<int, WasmFunction>();
        var visitFns = new Dictionary<int, WasmFunction>();
        var finFns = new Dictionary<int, WasmFunction>();
        var visit = ar.RuntimeFn("ArenaVisit", 1);
        foreach (var id in ar.CycleTypes)
        {
            bool hasFin = !D.DisplayById.ContainsKey(id) && D.TypeById[id] is INamedTypeSymbol { TypeKind: TypeKind.Class } ft && FindFinalizer(ft) != null;
            if (hasFin)
            {
                var fn = D.Module.AddFunction("arena.finalize " + id, new FuncType([ValType.I32], []));
                EmitDestroy(new Emitter(fn), id, clear: false, free: false, finalizer: 1);
                finFns[id] = fn;
            }
            var c = D.Module.AddFunction("arena.clear " + id, new FuncType([ValType.I32], []));
            EmitDestroy(new Emitter(c), id, clear: true, free: false, finalizer: hasFin ? -1 : 0);
            clearFns[id] = c;
            var fr = D.Module.AddFunction("arena.free " + id, new FuncType([ValType.I32], []));
            EmitDestroy(new Emitter(fr), id, clear: false, free: true);
            FreeWithPrefix(fr);
            freeFns[id] = fr;
            var t = D.TypeById[id];
            var v = D.Module.AddFunction("arena.visit " + id, new FuncType([ValType.I32], []));
            var ve = new Emitter(v);
            if (t is IArrayTypeSymbol)
            {
                if (ar.HasCyclicElements(t))
                {
                    int i = ve.AddLocal(ValType.I32), end = ve.AddLocal(ValType.I32);
                    ve.Get(0); ve.I32(Types.ArrayDataOffset(((IArrayTypeSymbol)t).Rank)); ve.Op(Op.I32Add); ve.Set(i);
                    ve.Get(i); ve.Get(0); ve.Load(Op.I32Load, 12); ve.I32(2); ve.Op(Op.I32Shl); ve.Op(Op.I32Add); ve.Set(end);
                    var exit = ve.Block(); var top = ve.Loop();
                    ve.Get(i); ve.Get(end); ve.Op(Op.I32GeU); ve.BrIf(exit);
                    ve.Get(i); ve.Call(ar.RuntimeFn("ArenaVisitElement", 1));
                    ve.Get(i); ve.I32(4); ve.Op(Op.I32Add); ve.Set(i);
                    ve.Br(top);
                    ve.End(); ve.End();
                }
            }
            else
                foreach (var off in ar.CyclicFieldOffsets(t)) { ve.Get(0); ve.I32(off); ve.Op(Op.I32Add); ve.Call(visit); }
            visitFns[id] = v;
        }
        void Dispatch(WasmFunction? fn, Dictionary<int, WasmFunction> per)
        {
            if (fn == null) return;
            var e = new Emitter(fn);
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            // (a br_table on the type id: the ids are numbered by then; types whose functions are the same code share
            // a case)
            rt.IdSwitch(e, tid, per.Select(x => { var f = x.Value; return (x.Key, (object?)WasmModule.BodyKey(f), (Action)(() => { e.Get(0); e.Call(f); e.Op(Op.Return); })); }).ToList());
        }
        _emitScc = () =>
        {
            Dispatch(rt.ArenaClearFnIfMade, clearFns);
            Dispatch(rt.ArenaFreeFnIfMade, freeFns);
            Dispatch(rt.ArenaVisitFnIfMade, visitFns);
            Dispatch(rt.ArenaFinFnIfMade, finFns);
            if (rt.ArenaFinTypeIfMade is { } finType) EmitIdTable(finType, D.TypeById.Count, i => (null, finFns.ContainsKey(i) ? 1 : 0));
            if (rt.SccOfIfMade is { } scc) EmitIdTable(scc, D.TypeById.Count, i => (null, ar.SccOf(i)));
        };
    }

    IMethodSymbol? FindFinalizer(ITypeSymbol t)
    {
        for (var b = t as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)D.Types.Canonical(bt) : null)
        {
            if (Types.IsObject(b)) break;
            var fin = b.GetMembers("Finalize").OfType<IMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Destructor && Driver.HasBody(m));
            if (fin != null) return fin;
            if (b.BaseType == null) break;
        }
        return null;
    }

    public void FinishExports()
    {
        // reflection metadata (section 8): thunks and attribute factories compile in the next round
        D.AnalyzeReflection();
        D.RequestAttributeFactories();
        // AsyncLocal in the program: async kickoffs keep the caller's context
        // (upstream: the boundary's AsyncSupport, as AsyncMethodBuilderCore.Start does it)
        if (D.Rt.EcSaveIfMade != null && D.AllMethods.Any(m => m.Sym.MethodKind == MethodKind.Constructor && m.Sym.ContainingType?.Name == "AsyncLocal" && m.Compiled))
        {
            D.LibMethod("Cs2Wasm.Runtime.AsyncSupport", "SaveContext", 0);
            D.LibMethod("Cs2Wasm.Runtime.AsyncSupport", "RestoreContext", 1);
        }
        // marks exist (weak references, SuppressFinalize): the destroy path asks ObjectMarks
        if (D.AllMethods.Any(m => m.Sym.Name == "Set" && m.Sym.ContainingType?.Name == "ObjectMarks" && m.Compiled))
            D.LibMethod("Cs2Wasm.Runtime.ObjectMarks", "OnDestroy", 1);
        // host operations (the host's fetch: section 8): the host finishes them through this export
        // a worker instance starts here: its own stack, thread-local block and id, then the thread's delegate
        if (D.UsesThreads)
        {
            // parking for snapshots (section 7.2)
            Export("cs2wasm_park", D.LibMethod("Cs2Wasm.Runtime.Threads", "ParkAll", 0));
            Export("cs2wasm_resume", D.LibMethod("Cs2Wasm.Runtime.Threads", "ResumeAll", 0));
            Export("cs2wasm_parked_count", D.LibMethod("Cs2Wasm.Runtime.Threads", "ParkedCount", 0));
            Export("cs2wasm_live_workers", D.LibMethod("Cs2Wasm.Runtime.Threads", "LiveCount", 0));
            var run = D.LibMethod("Cs2Wasm.Runtime.Threads", "Run", 1);
            var w = D.Module.AddFunction("__thread_entry", new FuncType([ValType.I32, ValType.I32, ValType.I32, ValType.I32], []));
            w.Exports.Add("__thread_entry");
            var e = new Emitter(w);
            e.Get(0); e.GSet(D.StackPointer);
            e.Get(0); e.GSet(D.StackHi!); e.Get(0); e.I32(1 << 20); e.Op(Op.I32Sub); e.GSet(D.StackLo!);
            e.Get(1); e.GSet(D.TlsBase);
            e.Get(2); e.GSet(D.ThreadId);
            var blk = e.Block(ValType.I32);
            e.TryTable(null, (0, D.ExceptionTag, blk));
            e.Get(3); e.Call(run.Fn);
            // (the thread's stack and thread-local block are not used any more: Threads can reuse them)
            e.Get(1); e.I32Addr(new LateAddr(() => D.TlsSize)); e.Op(Op.I32Add); e.I32(1); e.Emit(Op.I32AtomicStore, 0, 2);
            e.Op(Op.Return);
            e.End();
            e.Op(Op.Unreachable);
            e.End();
            e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Fn);
            e.Get(1); e.I32Addr(new LateAddr(() => D.TlsSize)); e.Op(Op.I32Add); e.I32(1); e.Emit(Op.I32AtomicStore, 0, 2);
        }
        // export wrappers
        foreach (var (name, mi) in _exports)
        {
            var w = D.Module.AddFunction("export " + name, mi.Fn.Type);
            w.Exports.Add(name);
            var e = new Emitter(w);
            var res = mi.Fn.Type.Results;
            // threads (upstream): the engine thread holds nothing uncounted outside exports (Limbo)
            const bool limbo = false;
            var blk = e.Block(ValType.I32);
            var tti = e.TryTable(null, (0, D.ExceptionTag, blk));
            for (int i = 0; i < mi.Fn.Type.Params.Length; i++) e.Get(i);
            e.Call(mi.Fn);
            if (limbo) e.Call(D.LibMethod("Cs2Wasm.Runtime.Limbo", "ExportExit", 0).Fn);
            e.Op(Op.Return);
            e.End();
            e.Op(Op.Unreachable);
            e.End();
            // exception pointer on the stack
            e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Fn);
            if (limbo) e.Call(D.LibMethod("Cs2Wasm.Runtime.Limbo", "ExportExit", 0).Fn);
            foreach (var r in res)
                switch (r) { case ValType.I64: e.I64(0); break; case ValType.F32: e.F32(0); break; case ValType.F64: e.F64(0); break; default: e.I32(0); break; }
        }
    }

    /// <summary>
    /// Types are constructed for the module only where live code constructs them. Compilation marks a type
    /// constructed wherever an allocation is compiled, also in code that later turned out dead (an initializer left
    /// out, a branch the whole program never takes); its virtual methods then stay in the dispatch stubs and its
    /// destructor in the destroy dispatch. Fixpoint: liveness over calls, the stub cases and destructors of the types
    /// still constructed; then the types constructed by live functions (or outside any function); until nothing changes.
    /// </summary>
    HashSet<WasmFunction>? _prunedLive;
    void PruneConstructed()
    {
        var types = D.Constructed.ToHashSet();
        var unboxThrows = D.Rt.BoxTests.Values.Where(x => !x.exact).ToDictionary(x => x.fn, x => D.Rt.ThrowInvalidCast(x.type));
        while (true)
        {
            var destroyOf = new Dictionary<WasmFunction, int>();
            var live = D.Module.LiveFunctions(f =>
            {
                var r = new List<WasmFunction>();
                foreach (var stub in D.Stubs.Values)
                    if (stub.Fn == f)
                        foreach (var (k, c) in stub.Cases)
                            if (c.impl != null && (k.StartsWith("synthetic#") || types.Contains(int.Parse(k[(k.LastIndexOf('#') + 1)..])))) r.Add(c.impl.Fn);
                if (f == D.Rt.Destroy) foreach (var (id, df) in _destroyFns) if (types.Contains(id)) r.Add(df);
                // (the unbox tests are written after pruning: a failed unbox throws InvalidCastException)
                if (unboxThrows.TryGetValue(f, out var th)) r.Add(th);
                return r;
            });
            var next = types.Where(id => !D.ConstructedBy.TryGetValue(id, out var by) || by.Contains(null) || by.Any(f => f != null && live.Contains(f))).ToHashSet();
            _prunedLive = live;
            if (next.Count == types.Count) break;
            types = next;
        }
        if (types.Count == D.Constructed.Count) return;
        D.Constructed.IntersectWith(types);
        foreach (var stub in D.Stubs.Values)
            foreach (var k in stub.Cases.Keys.ToList())
                if (!k.StartsWith("synthetic#") && !types.Contains(int.Parse(k[(k.LastIndexOf('#') + 1)..]))) stub.Cases.Remove(k);
        foreach (var id in _destroyFns.Keys.ToList()) if (!types.Contains(id)) _destroyFns.Remove(id);
    }

    Action? _emitScc;

    /// <summary>CS2WASM_COUNT_RC=1 (measurement, section 4.1): retain and release count the operations that do
    /// something (a live, mortal object) into two 64-bit counters, which the export cs2wasm_rc_counts gives the address
    /// of (the host prints them: CS2WASM_COUNT_RC).</summary>
    void EmitCountRc()
    {
        var counters = D.StaticAddress("rt.rc_counts", 24, 8);
        foreach (var (fn, off) in new[] { (D.Rt.Retain, 0), (D.Rt.Release, 8) })
        {
            var pre = new Emitter(new WasmFunction("tmp", fn.Type));
            pre.Get(0); pre.If();
            pre.Get(0); pre.Load(Op.I32Load); pre.I32(0); pre.Op(Op.I32GtS);
            pre.If(); pre.I32Addr(counters); pre.I64(1); pre.Emit(Op.I64AtomicRmwAdd, off, 3); pre.Op(Op.Drop); pre.End();
            pre.End();
            fn.Body.InsertRange(0, pre.Fn.Body);
        }
        var get = D.Module.AddFunction("cs2wasm_rc_counts", new FuncType([], [ValType.I32]));
        new Emitter(get).I32Addr(counters);
        get.Exports.Add("cs2wasm_rc_counts");
        // a counter per call site of retain and release, after optimization; the sites' functions are written to the
        // file CS2WASM_COUNT_RC_SITES names (the host prints the busiest: [count, then a counter per site])
        const int MaxSites = 1 << 20;
        var sites = D.StaticAddress("rt.rc_sites", 8 * MaxSites + 8, 8);
        var getSites = D.Module.AddFunction("cs2wasm_rc_sites", new FuncType([], [ValType.I32]));
        new Emitter(getSites).I32Addr(sites);
        getSites.Exports.Add("cs2wasm_rc_sites");
        D.Module.AfterOptimize = fns =>
        {
            var names = new List<string>();
            foreach (var f in fns)
            {
                if (f == D.Rt.Retain || f == D.Rt.Release) continue;
                var b = f.Body;
                // the baseline: the reference values pushed (Instr.Probes), into the third counter
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Probes == 0) continue;
                    var tmp = new Emitter(new WasmFunction("tmp", f.Type));
                    tmp.I32Addr(counters); tmp.I64(b[i].Probes); tmp.Emit(Op.I64AtomicRmwAdd, 16, 3); tmp.Op(Op.Drop);
                    b[i] = b[i] with { Probes = 0 };
                    b.InsertRange(i, tmp.Fn.Body);
                    i += tmp.Fn.Body.Count;
                }
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Op != Op.Call || b[i].Ref is not WasmFunction g || names.Count >= MaxSites) continue;
                    // (and calls of the struct helpers, which count the references a struct holds)
                    bool helper = g.Name.StartsWith("rt.retain_struct") || g.Name.StartsWith("rt.release_struct") || g.Name.StartsWith("rt.copy_");
                    if (g != D.Rt.Retain && g != D.Rt.Release && !helper) continue;
                    if (helper && (f.Name.StartsWith("rt.retain_struct") || f.Name.StartsWith("rt.release_struct") || f.Name.StartsWith("rt.copy_"))) continue;
                    names.Add((g == D.Rt.Retain ? "retain " : g == D.Rt.Release ? "release " : "[" + g.Name + "] ") + (f.FrameName ?? f.Name) + " @" + i
                        + (b[i].Imm2 > 0 && b[i].Imm2 <= Emitter.RcCauses.Count ? " {" + Emitter.RcCauses[(int)b[i].Imm2 - 1] + "}" : ""));
                    var tmp = new Emitter(new WasmFunction("tmp", f.Type));
                    tmp.I32Addr(sites); tmp.I64(1); tmp.Emit(Op.I64AtomicRmwAdd, 8 + 8 * (names.Count - 1), 3); tmp.Op(Op.Drop);
                    b.InsertRange(i, tmp.Fn.Body);
                    i += tmp.Fn.Body.Count;
                }
            }
            if (Environment.GetEnvironmentVariable("CS2WASM_COUNT_RC_SITES") is { Length: > 0 } file) File.WriteAllLines(file, names);
        };
    }

    public void Finish()
    {
        CloseTypeInfo();
        if (D.Arenas.Enabled) EmitArenaSupport();
        EmitFrameObjects();
        EmitOnDestroy();
        D.Rt.EmitCctorSupport(D.UsesThreads);
        var ecOwner = "AsyncSupport";
        D.Rt.EmitEcSupport(D.AllMethods.FirstOrDefault(m => m.Sym.Name == "SaveContext" && m.Sym.ContainingType?.Name == ecOwner && m.Compiled),
                           D.AllMethods.FirstOrDefault(m => m.Sym.Name == "RestoreContext" && m.Sym.ContainingType?.Name == ecOwner && m.Compiled));
        D.Rt.EmitFinalizerHold(D.UsesThreads);
        D.Rt.EmitThreadSupport(D.UsesThreads, () => D.Arenas.RuntimeFn("ArenaRetain", 1), () => D.Arenas.RuntimeFn("ArenaRelease", 1), D.Arenas.Enabled);
        if (Environment.GetEnvironmentVariable("CS2WASM_COUNT_RC") == "1") EmitCountRc();
        D.Module.UsesThreads = D.UsesThreads;
        D.EmitObjectArrayDelegate();
        D.EmitDynamicInvoke();
        D.Mark("  finish: D.EmitDynamicInvoke");
        if (D.Rt.ReleaseThreadStaticsIfMade is { } rts)
        {
            var e = new Emitter(rts);
            foreach (var (off, type) in D._tlsTypes)
            {
                if (Types.IsRef(type))
                {
                    e.GGet(D.TlsBase); e.Load(Op.I32Load, off); e.Call(D.Rt.Release);
                    e.GGet(D.TlsBase); e.I32(0); e.Store(Op.I32Store, off);
                }
                else if (Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs)
                {
                    e.GGet(D.TlsBase); e.I32(off); e.Op(Op.I32Add); e.Call(D.Rt.ReleaseStruct(type));
                }
            }
        }
        // single-threaded: what only another thread could make run goes (before pruning, so what it reached does)
        if (D.Rt.SingleThreadedIfMade is { } st)
        {
            bool single = !D.UsesThreads;
            st.Body.Clear(); new Emitter(st).I32(single ? 1 : 0);
            foreach (var fn in D.Module.Functions)
            {
                bool hit = false;
                for (int i = 0; i < fn.Body.Count; i++)
                    if (fn.Body[i].Op == Op.Call && fn.Body[i].Ref == st) { fn.Body[i] = new Instr { Op = Op.I32Const, Imm = single ? 1 : 0, Pos = fn.Body[i].Pos }; hit = true; }
                if (hit) ConstBranches.Run(fn);
            }
        }
        // (every function but the dispatch tables below exists now)
        PruneConstructed();
        D.Mark("  finish: PruneConstructed");
        AssignTypeIds();
        D.Mark("  finish: AssignTypeIds");
        _emitScc?.Invoke();
        D.Rt.EmitArrayDispatch();
        D.EmitReflectionTables();
        D.Mark("  finish: D.EmitReflectionTables");
        D.EmitDelegateMethod();
        D.EmitSharing(D.UsesThreads, Enumerable.Range(0, D.TypeById.Count).Where(id => D.Constructed.Contains(id)).Select(id => (id, D.TypeById[id])));
        D.Mark("  finish: D.EmitSharing");
        EmitDestroyDispatch();
        D.Mark("  finish: EmitDestroyDispatch");
        EmitRetainContents();
        EmitStubs();
        D.Mark("  finish: EmitStubs");
        EmitTypeTests();
        D.Mark("  finish: EmitTypeTests");
        D.Rt.EmitEnumInfo();
        EmitTypeNames();
        D.Mark("  finish: EmitTypeNames");
        // (every function exists: what each can store is known)
        D.ResolveFieldBorrows();
        D.ResolveInitStores();
        D.Mark("  finish: D.ResolveFieldBorrows");
        // (the floats stored to memory get the NaN of .NET on the final bodies: FloatStores.cs)
        RestoreStackAtCatches();
        D.Module.Canon = d => d ? D.Rt.CanonF64 : D.Rt.CanonF32;
        D.Module.StackPointer = D.StackPointer;
        D.LayoutMemory();
        D.Mark("  finish: D.LayoutMemory");
        EmitInit();
    }

    /// <summary>An exception leaves the frames between the throw and the catch without running their ends, so the
    /// shadow stack's pointer is still the innermost frame's when the catch gets it. Each try that catches (not one
    /// that only cleans up and throws on) notes the pointer on its way in and puts it back where the catch lands:
    /// one global read per try entered, and no function needs a handler only to give its frame back.</summary>
    void RestoreStackAtCatches()
    {
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            if (!b.Any(x => x.Ref is TryTableInfo t && t.Catches.Any(c => c.Kind != 3))) continue;
            int n = b.Count;
            var match = new int[n];
            var st = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (b[i].Op is Op.Block or Op.Loop or Op.If or Op.TryTable) st.Push(i);
                else if (b[i].Op == Op.End && st.Count > 0) match[st.Pop()] = i;
            }
            // (index, instructions to put in before it), in the order found
            var ins = new List<(int at, Instr a, Instr b2)>();
            var ctl = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var x = b[i];
                if (x.Ref is TryTableInfo t && t.Catches.Any(c => c.Kind != 3))
                {
                    f.Locals.Add(ValType.I32);
                    int tmp = f.Type.Params.Length + f.Locals.Count - 1;
                    ins.Add((i, new Instr { Op = Op.GlobalGet, Ref = D.StackPointer, Pos = -1 }, new Instr { Op = Op.LocalSet, Imm = tmp, Pos = -1 }));
                    foreach (var c in t.Catches)
                    {
                        if (c.Kind == 3) continue;
                        int k = ctl.Count - 1 - c.Label;
                        if (k < 0 || b[ctl[k]].Op == Op.Loop) continue;
                        ins.Add((match[ctl[k]] + 1, new Instr { Op = Op.LocalGet, Imm = tmp, Pos = -1 }, new Instr { Op = Op.GlobalSet, Ref = D.StackPointer, Pos = -1 }));
                    }
                }
                if (x.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) ctl.Add(i);
                else if (x.Op == Op.End && ctl.Count > 0) ctl.RemoveAt(ctl.Count - 1);
            }
            foreach (var (at, a, b2) in ins.OrderByDescending(p => p.at)) { b.Insert(at, b2); b.Insert(at, a); }
        }
    }

    /// <summary>rt.on_destroy(p): 1 if p's finalizer runs. Without marks in the program it is `1`, and the calls whose
    /// result is dropped are removed.</summary>
    void EmitOnDestroy()
    {
        var f = D.Rt.OnDestroyIfMade;
        if (f == null) return;
        var marks = D.AllMethods.FirstOrDefault(m => m.Sym.Name == "OnDestroy" && m.Sym.ContainingType?.Name == "ObjectMarks" && m.Compiled);
        var e = new Emitter(f);
        if (marks != null)
        {
            // (an object without a mark, which is nearly every object: nothing to ask; ObjectMarks.MarkedBit)
            e.Get(0); e.Load(Op.I32Load, 8); e.I32(0x40000000); e.Op(Op.I32And); e.Op(Op.I32Eqz);
            e.If(ValType.I32); e.I32(1); e.Else(); e.Get(0); e.Call(marks.Fn); e.End();
            return;
        }
        e.I32(1);
        foreach (var fn in D.Module.Functions)
        {
            var b = fn.Body;
            for (int i = 0; i + 2 < b.Count; i++)
                if (b[i].Op == Op.LocalGet && b[i + 1].Op == Op.Call && ReferenceEquals(b[i + 1].Ref, f) && b[i + 2].Op == Op.Drop)
                {
                    b[i] = b[i] with { Op = Op.Nop, Ref = null };
                    b[i + 1] = b[i + 1] with { Op = Op.Nop, Ref = null };
                    b[i + 2] = b[i + 2] with { Op = Op.Nop, Ref = null };
                }
        }
    }

    // A field of a sealed class's type holds null or an object of exactly that class: when the field's object dies
    // with the one that held it, its destruction is known where the field is released, and need not go through the
    // dispatch on the type (rt.destroy, rt.destroy_now). The release is written as a call of a function that stands
    // for "release an object of type T"; when the types are final (EmitDestroyDispatch) each such call becomes the
    // count's test and a direct call of T's destruction.
    readonly Dictionary<int, WasmFunction> _releaseOf = [];

    WasmFunction ReleaseOfField(ITypeSymbol t)
    {
        var rt = D.Rt;
        if (D.ThreadMode || t is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true } || Types.IsImmortalType(t)) return rt.Release;
        int id = D.TypeId(D.Types.Canonical(t));
        if (_releaseOf.TryGetValue(id, out var f)) return f;
        // (as it stands it is a release: what it stays when T's destruction is not in the module)
        f = D.Module.AddFunction("rt.release exact " + id, new FuncType([ValType.I32], []));
        var e = new Emitter(f);
        e.Get(0); e.Call(rt.Release);
        return _releaseOf[id] = f;
    }

    void ExpandExactReleases(int maxNesting)
    {
        if (_releaseOf.Count == 0) return;
        var byFn = _releaseOf.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var f in D.Module.Functions)
        {
            if (f.IsImport || byFn.ContainsKey(f) || !f.Body.Any(x => x.Op == Op.Call && x.Ref is WasmFunction w && byFn.TryGetValue(w, out int wid) && _destroyFns.ContainsKey(wid) && D.Arenas.SccOf(wid) != 0)) continue;
            int np = f.Type.Params.Length;
            f.Locals.Add(ValType.I32); int t = np + f.Locals.Count - 1;
            f.Locals.Add(ValType.I32); int c = np + f.Locals.Count - 1;
            var o = new List<Instr>(f.Body.Count + 64);
            foreach (var ins in f.Body)
            {
                // (only where structures are freed link by link: a type that can refer to itself, a tree's or a list's node)
                if (ins.Op != Op.Call || ins.Ref is not WasmFunction w || !byFn.TryGetValue(w, out int id) || !_destroyFns.TryGetValue(id, out var destroy) || D.Arenas.SccOf(id) == 0) { o.Add(ins); continue; }
                void Add(Op op, long imm = 0, object? r = null) => o.Add(new Instr { Op = op, Imm = imm, Imm2 = op is Op.I32Load or Op.I32Store ? 2 : 0, Ref = r, Pos = ins.Pos });
                Add(Op.LocalTee, t);
                Add(Op.If);
                Add(Op.LocalGet, t); Add(Op.I32Load); Add(Op.LocalTee, c); Add(Op.I32Const, 1); Add(Op.I32GtS);
                Add(Op.If);
                Add(Op.LocalGet, t); Add(Op.LocalGet, c); Add(Op.I32Const, 1); Add(Op.I32Sub); Add(Op.I32Store);
                Add(Op.Else);
                // (the last reference, and not too deep in destructions: straight to the class's own; anything else,
                // a member of a cycle's group among them, as any release)
                Add(Op.LocalGet, c); Add(Op.I32Const, 1); Add(Op.I32Eq);
                Add(Op.GlobalGet, 0, D.DestroyDepth); Add(Op.I32Const, maxNesting); Add(Op.I32LtU);
                Add(Op.GlobalGet, 0, D.DestroyDepth); Add(Op.I32Const, 0); Add(Op.I32Ne);
                Add(Op.I32And); Add(Op.I32And);
                Add(Op.If);
                Add(Op.LocalGet, t); Add(Op.I32Const, 0); Add(Op.I32Store);
                Add(Op.GlobalGet, 0, D.DestroyDepth); Add(Op.I32Const, 1); Add(Op.I32Add); Add(Op.GlobalSet, 0, D.DestroyDepth);
                Add(Op.LocalGet, t); Add(Op.Call, 0, destroy);
                Add(Op.GlobalGet, 0, D.DestroyDepth); Add(Op.I32Const, 1); Add(Op.I32Sub); Add(Op.GlobalSet, 0, D.DestroyDepth);
                Add(Op.Else);
                Add(Op.LocalGet, t); Add(Op.Call, 0, D.Rt.Release);
                Add(Op.End);
                Add(Op.End);
                Add(Op.End);
            }
            f.Body = o;
        }
    }

    void EmitDestroyDispatch()
    {
        // An object's destruction releases what it refers to, which may destroy that, and so on: a long chain (a
        // linked list let go at its head) would nest as deep as it is long. Past a depth, an object is put on a list
        // instead (by its count word: it is dead), and the outermost destruction, when it is done, takes them from the
        // list one by one: the same order of events as far as a program can tell (finalizers run when the count is
        // gone, in some order), and no stack to run out of.
        const int MaxNesting = 48;
        ExpandExactReleases(MaxNesting);
        if (!D.ThreadMode)
        {
            D.Module.ReleaseFn = D.Rt.Release;
            D.Module.LeafDestroy = id => IsLeafDestroy(id) && D.ExtId(id) >= 0 ? _destroyFns[id] : null;
        }
        var now = D.Module.AddFunction("rt.destroy_now", new FuncType([ValType.I32], []));
        {
            var w = new Emitter(D.Rt.Destroy);
            int q = w.AddLocal(ValType.I32);
            // (an object that holds no reference: freed, with no nesting to count. The depth is a global that every
            // destruction would write twice, each write waiting for the one before)
            if (D.LeafTypeCount > 0)
            {
                // (a string, the most usual of them: straight to its own)
                if (_destroyFns.TryGetValue(1, out var ds))
                {
                    w.Get(0); w.Load(Op.I32Load, 4); w.I32(1); w.Op(Op.I32Eq);
                    w.If(); w.Get(0); w.Call(ds); w.Op(Op.Return); w.End();
                }
                w.Get(0); w.Load(Op.I32Load, 4); w.I32(D.LeafTypeCount); w.Op(Op.I32LtU);
                w.If(); w.Get(0); w.Call(now); w.Op(Op.Return); w.End();
            }
            // (threads: the objects that wait are kept in a list of the thread's own, Rt.PendDestroy; a dead object's
            // count word stays zero, which is what a read that counts takes for "not this one", rt.load_retain)
            var pend = D.ThreadMode ? D.LibMethod("Cs2Wasm.Runtime.Rt", "PendDestroy", 1).Fn : null;
            var nextPending = D.ThreadMode ? D.LibMethod("Cs2Wasm.Runtime.Rt", "NextPending", 0).Fn : null;
            w.GGet(D.DestroyDepth); w.I32(MaxNesting); w.Op(Op.I32GeU);
            w.If();
            if (pend != null) { w.Get(0); w.Call(pend); }
            else
            {
                w.Get(0); w.GGet(D.DestroyPending); w.Store(Op.I32Store);
                w.Get(0); w.GSet(D.DestroyPending);
            }
            w.Op(Op.Return);
            w.End();
            w.GGet(D.DestroyDepth); w.I32(1); w.Op(Op.I32Add); w.GSet(D.DestroyDepth);
            w.Get(0); w.Call(now);
            w.GGet(D.DestroyDepth); w.I32(1); w.Op(Op.I32Sub); w.GSet(D.DestroyDepth);
            w.GGet(D.DestroyDepth); w.Op(Op.I32Eqz);
            w.If();
            var exit = w.Block(); var top = w.Loop();
            if (nextPending != null) { w.Call(nextPending); w.Tee(q); w.Op(Op.I32Eqz); w.BrIf(exit); }
            else
            {
                w.GGet(D.DestroyPending); w.Tee(q); w.Op(Op.I32Eqz); w.BrIf(exit);
                w.Get(q); w.Load(Op.I32Load); w.GSet(D.DestroyPending);
                w.Get(q); w.I32(0); w.Store(Op.I32Store);
            }
            w.I32(1); w.GSet(D.DestroyDepth);
            w.Get(q); w.Call(now);
            w.I32(0); w.GSet(D.DestroyDepth);
            w.Br(top);
            w.End(); w.End();
            w.End();
        }
        // (types whose destructions are the same code share a case: in a large program most do)
        var e = new Emitter(now);
        int n = D.TypeCount;
        int nowTid = e.AddLocal(ValType.I32);
        e.Get(0); e.Load(Op.I32Load, 4); e.Set(nowTid);
        var destroyCases = new List<(int, object?, Action)>();
        for (int i = 0; i < n; i++)
            if (_destroyFns.TryGetValue(D.IntId(i), out var fn)) { var f = fn; destroyCases.Add((D.IntId(i), WasmModule.BodyKey(f), () => { e.Get(0); e.Call(f); e.Op(Op.Return); })); }
        D.Rt.IdSwitch(e, nowTid, destroyCases);
        e.Op(Op.Unreachable);
    }

    /// <summary>rt.retain_contents(obj): per constructed type with counted contents, a count for each reference.</summary>
    void EmitRetainContents()
    {
        if (D.Rt.RetainContentsIfMade is not { } f) return;
        var e = new Emitter(f);
        var rt = D.Rt;
        int tid = e.AddLocal(ValType.I32);
        e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
        // (a branch on the type id; types whose references are in the same places share a case)
        var retainCases = new List<(int, object?, Action)>();
        foreach (var id in D.Constructed)
        {
            if (D.DisplayById.ContainsKey(id) || D.SyntheticById.ContainsKey(id) || id <= 1 || id >= D.TypeById.Count) continue;
            var type = D.TypeById[id];
            var body = new Emitter(new WasmFunction("tmp", f.Type));
            if (type is IArrayTypeSymbol at)
            {
                var et = D.Types.Canonical(at.ElementType);
                if (!D.Types.NeedsRc(et)) continue;
                body.Get(0); body.I32(Types.ArrayDataOffset(at.Rank)); body.Op(Op.I32Add); body.Get(0); body.Load(Op.I32Load, 12); body.Call(rt.RetainRange(et));
            }
            else if (type.IsValueType)
            {
                if (!Types.IsStruct(type) || !D.Types.LayoutOf(type).HasRefs) continue;
                body.Get(0); body.I32(FunctionCompilerHelpers.BoxDataOffset(D.Types, type)); body.Op(Op.I32Add); body.Call(rt.RetainStruct(type));
            }
            else
            {
                bool any = false;
                foreach (var fs in D.Types.LayoutOf(type).Fields)
                {
                    if (Types.IsRef(fs.Type)) { body.Get(0); body.Load(Op.I32Load, fs.Offset); body.Call(rt.Retain); body.Op(Op.Drop); any = true; }
                    else if (Types.IsStruct(fs.Type) && D.Types.LayoutOf(fs.Type).HasRefs) { body.Get(0); body.I32(fs.Offset); body.Op(Op.I32Add); body.Call(rt.RetainStruct(fs.Type)); any = true; }
                }
                if (!any) continue;
            }
            // (a cycle-capable type's copy: its references in slots that can close a cycle are linked, section 4.3)
            if (D.Arenas.Enabled && D.Arenas.CycleTypes.Contains(id)) { body.Get(0); body.Call(D.Arenas.RuntimeFn("ArenaLinkContents", 1)); }
            var instrs = body.Fn.Body;
            retainCases.Add((id, WasmModule.BodyKey(body.Fn), () => { e.Fn.Body.AddRange(instrs); e.Op(Op.Return); }));
        }
        rt.IdSwitch(e, tid, retainCases);
    }

    /// <summary>A virtual call's stub: from the object's type id to the implementation of its class. Only an object
    /// of a class that has a case can come here (the receiver is of the slot's type, and its class is one the
    /// program makes), so the cases, in the order of their ids, are cut where the implementation changes, and the
    /// stub finds the stretch the id is in by halving: `id &lt; first id of the upper half`. A slot most classes
    /// take from one base (Exception.Message, object.ToString) is a comparison or two; one with a thousand
    /// implementations (IEnumerator.MoveNext in a large program) is ten. A class whose id is not numbered has no
    /// object, and no case. One stretch: every call of the stub goes straight to the implementation.</summary>
    void EmitStubs()
    {
        foreach (var stub in D.Stubs.Values)
        {
            var e = new Emitter(stub.Fn);
            var sig = stub.Fn.Type;
            int thisIdx = Driver.Signature(stub.Slot, D.Types).Params.Length > stub.Slot.Parameters.Length + (stub.Slot.IsStatic ? 0 : 1) ? 1 : 0;
            int nparams = sig.Params.Length;
            var cases = stub.Cases.Values.ToList();
            if (Environment.GetEnvironmentVariable("CS2WASM_STUBSTATS") == "1") Console.Error.WriteLine("stub " + cases.Count + " " + stub.Fn.Name);
            int tid = e.AddLocal(ValType.I32);
            var sorted = new List<(int ext, int id, ITypeSymbol type, MethodInst impl, bool boxed)>();
            foreach (var (ckey, (type, impl, boxed)) in stub.Cases)
            {
                int id = ckey.StartsWith("synthetic#") ? int.Parse(ckey[10..]) : D.TypeId(type);
                int ext = D.ExtId(id);
                if (ext >= 0) sorted.Add((ext, id, type, impl!, boxed));
            }
            sorted.Sort((a, b) => a.ext.CompareTo(b.ext));
            // the stretches: (the first case's id, the implementation, the offset of a boxed value in its box or 0)
            var runs = new List<(int id, WasmFunction fn, int box)>();
            foreach (var c in sorted)
            {
                int box = c.boxed ? FunctionCompilerHelpers.BoxDataOffset(D.Types, c.type) : 0;
                if (runs.Count == 0 || !ReferenceEquals(runs[^1].fn, c.impl.Fn) || runs[^1].box != box) runs.Add((c.id, c.impl.Fn, box));
            }
            if (runs.Count == 0) { e.Op(Op.Unreachable); continue; }
            if (runs.Count == 1 && runs[0].box == 0) stub.Fn.ForwardTo = runs[0].fn;
            if (runs.Count > 1) { e.Get(thisIdx); e.Load(Op.I32Load, 4); e.Set(tid); }
            void Tree(int lo, int hi)
            {
                if (lo == hi)
                {
                    for (int i = 0; i < nparams; i++)
                    {
                        e.Get(i);
                        if (i == thisIdx && runs[lo].box != 0) { e.I32(runs[lo].box); e.Op(Op.I32Add); }
                    }
                    e.Call(runs[lo].fn);
                    e.Op(Op.Return);
                    return;
                }
                int mid = (lo + hi + 1) / 2;
                e.Get(tid); e.Tid(runs[mid].id); e.Op(Op.I32LtU);
                e.If(); Tree(lo, mid - 1); e.End();
                Tree(mid, hi);
            }
            Tree(0, runs.Count - 1);
        }
    }

    void EmitTypeTests()
    {
        // the candidates of each test (the constructed types now: some were pruned; IsSubtype still decides)
        var index = new SubtypeIndex(D.Types);
        foreach (var id in D.Constructed) index.Add(id, IsSpecial(id) ? null : D.TypeById[id]);
        foreach (var (key, (target, fn)) in D.Rt.IsInstFns)
        {
            var e = new Emitter(fn);
            // (every object is an object: the test is that there is one)
            if (Types.IsObject(target)) { e.Get(0); e.I32(0); e.Op(Op.I32Ne); continue; }
            var cl = index.Candidates(target);
            IEnumerable<int> candidates = cl == index.All ? cl : cl.Concat(index.Special);
            var ids = candidates.Where(id => D.SyntheticById.TryGetValue(id, out var syn)
                    ? syn.Interfaces.Any(i => Types.Key(i) == Types.Key(target)) || Types.IsObject(target)
                      || id < D.TypeById.Count && D.TypeById[id] is { } st && Types.Key(st) == Types.Key(target)
                    : !D.DisplayById.ContainsKey(id) && D.Types.IsSubtype(D.TypeById[id], target)).ToList();
            e.Get(0); e.Op(Op.I32Eqz); e.If(); e.I32(0); e.Op(Op.Return); e.End();
            if (ids.Count == 0) { e.I32(0); continue; }
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            EmitIdSetTest(e, tid, ids);
        }
        foreach (var (key, (vt, fn, exact)) in D.Rt.BoxTests)
        {
            var e = new Emitter(fn);
            var ids = new List<int> { D.TypeId(vt) };
            if (!exact)
            {
                // enums and their underlying type unbox interchangeably
                var p = Types.PrimOf(vt);
                foreach (var id in D.Constructed)
                    if (!D.DisplayById.ContainsKey(id) && D.TypeById[id] is var t && t.IsValueType && Types.PrimOf(t) == p && p != Prim.None && !ids.Contains(id)
                        && (t.TypeKind == TypeKind.Enum || vt.TypeKind == TypeKind.Enum))
                        ids.Add(id);
            }
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            EmitIdSetTest(e, tid, ids);
            if (!exact)
            {
                e.Op(Op.I32Eqz);
                e.If(); e.Get(0); e.Call(D.Rt.ThrowInvalidCast(vt)); e.End();
            }
        }
    }

    /// <summary>Whether the id in the local is one of the ids: runs of consecutive ids are range checks (the ids are
    /// numbered so that a class and what derives from it are one or two runs: AssignTypeIds). A few runs are tested
    /// one after the other; many (an interface's classes, from all over) by halving, a dozen comparisons for
    /// thousands of runs where testing each in turn took thousands.</summary>
    void EmitIdSetTest(Emitter e, int tid, List<int> internalIds)
    {
        var ids = internalIds.Select(D.ExtId).Where(x => x >= 0).Distinct().ToList();
        ids.Sort();
        var runs = new List<(int lo, int hi)>();
        for (int i = 0; i < ids.Count;)
        {
            int j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            runs.Add((ids[i], ids[j]));
            i = j + 1;
        }
        void Run(int k)
        {
            var (lo, hi) = runs[k];
            if (lo == hi) { e.Get(tid); e.I32(lo); e.Op(Op.I32Eq); }
            else { e.Get(tid); e.I32(lo); e.Op(Op.I32Sub); e.I32(hi - lo); e.Op(Op.I32LeU); }
        }
        void Tree(int a, int b)
        {
            if (b - a < 3)
            {
                Run(a);
                for (int k = a + 1; k <= b; k++) { Run(k); e.Op(Op.I32Or); }
                return;
            }
            int mid = (a + b + 1) / 2;
            e.Get(tid); e.I32(runs[mid].lo); e.Op(Op.I32LtU);
            e.If(ValType.I32); Tree(a, mid - 1); e.Else(); Tree(mid, b); e.End();
        }
        if (runs.Count == 0) { e.I32(0); return; }
        Tree(0, runs.Count - 1);
    }

    void EmitTypeNames()
    {
        { var e = new Emitter(D.Rt.TypeNameFn); e.Get(0); e.Load(Op.I32Load, 4); e.Call(D.Rt.TypeNameById); }
        int n = D.TypeCount;
        string Full(int i) => i == 0 ? "?" : D.SyntheticById.TryGetValue(i, out var syn) ? syn.Name : D.DisplayById.TryGetValue(i, out var dd) ? D.RoslynDisplayName(dd, true) ?? "<>c__DisplayClass" : Driver.ClrName(D.TypeById[i]);
        string Short(int i)
        {
            if (i == 0) return "?";
            if (D.SyntheticById.TryGetValue(i, out var syn)) return syn.Name;
            if (D.DisplayById.TryGetValue(i, out var sd)) return D.RoslynDisplayName(sd, false) ?? "<>c__DisplayClass";
            return ShortName(D.TypeById[i]);
        }
        EmitIdTable(D.Rt.TypeNameById, n, i => (D.StringLiteral(Full(i)), 0));
        EmitIdTable(D.Rt.TypeShortNameById, n, i => (D.StringLiteral(Short(i)), 0));
        EmitIdTable(D.Rt.TypeFlagsById, n, i => (null, i == 0 || D.SyntheticById.ContainsKey(i) || D.DisplayById.ContainsKey(i) ? 0 : Flags(D.TypeById[i])));
        if (D.Rt.ProgramTypesIfMade is { } ptf)
        {
            var ids = D.ProgramTypes().Select(t => D.ExtId(D.TypeId(t))).Where(x => x >= 0).ToList();
            var bytes = new byte[4 + 4 * ids.Count];
            BitConverter.GetBytes(ids.Count).CopyTo(bytes, 0);
            for (int k = 0; k < ids.Count; k++) BitConverter.GetBytes(ids[k]).CopyTo(bytes, 4 + 4 * k);
            new Emitter(ptf).I32Addr(D.DataBlob(bytes, 4, []));
        }
        if (D.Rt.TypeNameTableIfMade is { } tnt)
        {
            // Type.GetType(name) looks in the calling assembly and CoreLib: their named types the module numbers
            var corlib = D.LibCompilation.Assembly;
            var programs = D.Inputs.Select(c => c.Assembly).ToHashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var blob = new List<byte>();
            var relocs = new List<(int, IAddress)>();
            int count = 0;
            blob.AddRange(new byte[4]);
            for (int ext = 0; ext < n; ext++)
            {
                int i = D.IntId(ext);
                if (i <= 0 || D.SyntheticById.ContainsKey(i) || D.DisplayById.ContainsKey(i)) continue;
                if (D.TypeById[i] is not INamedTypeSymbol nt || nt.IsUnboundGenericType || nt.TypeArguments.Length > 0 && nt.TypeArguments.Any(a => a is ITypeParameterSymbol)) continue;
                var asm = nt.ContainingAssembly;
                if (asm == null || !SymbolEqualityComparer.Default.Equals(asm, corlib) && !programs.Contains(asm)) continue;
                relocs.Add((blob.Count, D.StringLiteral(Driver.ClrName(nt))));
                blob.AddRange(new byte[4]);
                blob.AddRange(BitConverter.GetBytes(ext));
                count++;
            }
            BitConverter.GetBytes(count).CopyTo(blob.ToArray(), 0);
            var bytes = blob.ToArray();
            BitConverter.GetBytes(count).CopyTo(bytes, 0);
            var addr = D.DataBlob(bytes, 4, relocs);
            new Emitter(tnt).I32Addr(addr);
        }
        if (D.Rt.GenericInstancesIfMade is { } gif)
        {
            var blob = new List<int> { 0 };
            int count = 0;
            for (int ext = 0; ext < n; ext++)
            {
                int i = D.IntId(ext);
                if (i <= 0 || D.SyntheticById.ContainsKey(i) || D.DisplayById.ContainsKey(i)) continue;
                if (D.TypeById[i] is not INamedTypeSymbol { IsGenericType: true } gi || Types.IsGenericDefinition(gi)) continue;
                if (D.ExistingTypeId(D.Types.Canonical(gi.OriginalDefinition)) is not int di || D.ExtId(di) < 0) continue;
                var args = gi.TypeArguments.Select(a => D.ExistingTypeId(D.Types.Canonical(a)) is int ai ? D.ExtId(ai) : -1).ToList();
                if (args.Any(a => a < 0)) continue;
                blob.Add(D.ExtId(di)); blob.Add(ext); blob.Add(args.Count); blob.AddRange(args);
                count++;
            }
            blob[0] = count;
            var bytes = blob.SelectMany(BitConverter.GetBytes).ToArray();
            new Emitter(gif).I32Addr(D.DataBlob(bytes, 4));
        }
        EmitTypeInfo();
    }

    static string ShortName(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => ShortName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => ShortName(p.PointedAtType) + "*",
        _ => t.MetadataName,
    };

    static int Flags(ITypeSymbol t)
    {
        int f = 0;
        if (t.IsValueType) f |= 1;
        if (t.TypeKind == TypeKind.Enum) f |= 2;
        if (t is IArrayTypeSymbol) f |= 4;
        if (t.TypeKind == TypeKind.Interface) f |= 8;
        if (t is INamedTypeSymbol { IsGenericType: true }) f |= 16;
        if (Types.PrimOf(t) != Prim.None && t.TypeKind != TypeKind.Enum) f |= 32;
        return f;
    }

    void EmitIdTable(WasmFunction fn, int nInternal, Func<int, (IAddress? addr, int value)> entryOfInternal, Action<Emitter>? byRef = null)
    {
        int n = D.TypeCount;
        (IAddress?, int) entry(int ext) => entryOfInternal(D.IntId(ext));
        // a table in the data (narrowest element that holds every value; addresses are relocated), read by index;
        // an id out of range reads as type 0's entry
        var e = new Emitter(fn);
        var vals = Enumerable.Range(0, n).Select(entry).ToList();
        bool addrs = vals.Any(x => x.Item1 != null);
        int lo = addrs ? 0 : vals.Min(x => x.Item2), hi = addrs ? 0 : vals.Max(x => x.Item2);
        var (width, load) = addrs ? (4, Op.I32Load)
            : lo >= 0 && hi <= 255 ? (1, Op.I32Load8U) : lo >= -128 && hi <= 127 ? (1, Op.I32Load8S)
            : lo >= 0 && hi <= 65535 ? (2, Op.I32Load16U) : lo >= -32768 && hi <= 32767 ? (2, Op.I32Load16S) : (4, Op.I32Load);
        var blob = new byte[n * width];
        var relocs = new List<(int, IAddress)>();
        for (int i = 0; i < n; i++)
        {
            var (a, v) = vals[i];
            if (a != null) relocs.Add((i * 4, a));
            else for (int b = 0; b < width; b++) blob[i * width + b] = (byte)(v >> (8 * b));
        }
        var table = relocs.Count > 0 ? D.DataBlob(blob, width, relocs) : D.DataBlob(blob, width);
        e.Get(0); e.I32(n); e.Op(Op.I32LtU);
        e.If(ValType.I32);
        e.Get(0);
        if (width > 1) { e.I32(width == 2 ? 1 : 2); e.Op(Op.I32Shl); }
        e.I32Addr(table); e.Op(Op.I32Add);
        e.Load(load);
        e.Else();
        var (a0, v0) = vals[0];
        // a by-ref type (Meta.ByRef: its element's id with bit 30)
        if (byRef != null) { e.Get(0); e.I32(0x40000000); e.Op(Op.I32And); e.If(ValType.I32); byRef(e); e.Else(); }
        if (a0 != null) e.I32Addr(a0); else e.I32(v0);
        if (byRef != null) e.End();
        e.End();
    }

    void EmitInit()
    {
        var e = new Emitter(_init);
        if (D.Data.Bytes.Count > 0)
        {
            D.Module.Data.Add(D.Data);
            var raw = D.Data.Bytes.ToArray();
            var packed = Environment.GetEnvironmentVariable("CS2WASM_NO_PACK") == "1" ? raw : DataCompression.Compress(raw);
            // (the packed bytes land at the heap's start, are expanded into place, and the heap is zero again)
            if (packed.Length + 160 < raw.Length && (long)D.HeapBase + packed.Length <= (long)D.Module.MemoryMinPages * 65536)
            {
                D.Data.Bytes.Clear(); D.Data.Bytes.AddRange(packed);
                var dec = D.Module.AddFunction("rt.unpack_data", new FuncType([ValType.I32, ValType.I32, ValType.I32], []));
                DataCompression.EmitDecoder(new Emitter(dec));
                e.I32(D.HeapBase); e.I32(0); e.I32(packed.Length); e.Emit(Op.MemoryInit, 0, 0, D.Data);
                e.I32(D.HeapBase); e.I32(D.DataBase); e.I32(D.DataBase + raw.Length); e.Call(dec);
                e.I32(D.HeapBase); e.I32(0); e.I32(packed.Length); e.Emit(Op.MemoryFill);
            }
            else { e.I32(D.DataBase); e.I32(0); e.I32(D.Data.Bytes.Count); e.Emit(Op.MemoryInit, 0, 0, D.Data); }
        }
        e.I32(D.StackTop); e.GSet(D.StackPointer);
        e.I32(D.StackTop); e.GSet(D.StackHi!); e.I32(D.StackTop - D.Options.StackSize); e.GSet(D.StackLo!);
        if (D.TlsSize > 0) { e.I32(D.TlsSize); e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "AllocFirst", 1).Fn); e.GSet(D.TlsBase); }
    }
}

static class FunctionCompilerHelpers
{
    /// <summary>Where a box keeps its value. The upstream library reads it right after the header (RuntimeHelpers.GetRawData).</summary>
    public static int BoxDataOffset(Types t, ITypeSymbol vt) => Types.ObjectHeader;
}

internal sealed partial class Driver
{
    public RuntimeFns Rt = null!;
    public readonly Dictionary<int, Display> DisplayById = [];

    public int DisplayTypeId(Display d)
    {
        if (d.TypeId > 0) return d.TypeId;
        int id = TypeById.Count;
        TypeById.Add(ObjectType);
        DisplayById[id] = d;
        Constructed.Add(id);
        d.TypeId = id;
        return id;
    }

    /// <summary>The CLR's name for a type, as Type.FullName / Object.ToString give it.</summary>
    public static string ClrName(ITypeSymbol t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a: return ClrName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]";
            case IPointerTypeSymbol p: return ClrName(p.PointedAtType) + "*";
            case INamedTypeSymbol n:
                {
                    var sb = new StringBuilder();
                    if (n.ContainingType != null) sb.Append(ClrName(n.ContainingType.OriginalDefinition)).Append('+');
                    else if (n.ContainingNamespace is { IsGlobalNamespace: false } ns) sb.Append(ns.ToDisplayString()).Append('.');
                    sb.Append(n.MetadataName);
                    var args = AllTypeArgs(n);
                    if (args.Count > 0 && !SymbolEqualityComparer.Default.Equals(n, n.OriginalDefinition))
                        sb.Append('[').Append(string.Join(",", args.Select(a => "[" + ClrName(a) + "]"))).Append(']');
                    var s = sb.ToString();
                    // reference assembly names for type arguments use assembly-qualified names in .NET; keep the readable form
                    return s.Replace("],[", ",").Replace("[[", "[").Replace("]]", "]");
                }
            default: return t.ToDisplayString();
        }
    }

    internal static List<ITypeSymbol> AllTypeArgs(INamedTypeSymbol n)
    {
        var l = new List<ITypeSymbol>();
        if (n.ContainingType != null) l.AddRange(AllTypeArgs(n.ContainingType));
        l.AddRange(n.TypeArguments);
        return l;
    }

    // ---------------- thunks ----------------
    readonly Dictionary<string, WasmFunction> _thunks = [];

    /// <summary>(ignored target, args) → static method, for delegates bound to static methods.</summary>
    public WasmFunction StaticThunk(IMethodSymbol m)
    {
        var key = "static " + Types.MethodKey(m);
        if (_thunks.TryGetValue(key, out var f)) return f;
        var target = Method(m);
        var sig = Signature(m, Types, forceInstanceThis: true);
        f = Module.AddFunction("thunk " + Display(m), sig);
        _thunks[key] = f;
        var e = new Emitter(f);
        bool sret = sig.Params.Length > m.Parameters.Length + 1;
        for (int i = 0; i < sig.Params.Length; i++)
        {
            if (i == (sret ? 1 : 0)) continue; // skip target
            e.Get(i);
        }
        e.Call(target.Fn);
        return f;
    }

    /// <summary>(box, args) → struct instance method on the boxed value.</summary>
    public WasmFunction BoxThunk(IMethodSymbol m, ITypeSymbol vt)
    {
        var key = "box " + Types.MethodKey(m);
        if (_thunks.TryGetValue(key, out var f)) return f;
        var target = Method(m);
        f = Module.AddFunction("box thunk " + Display(m), target.Fn.Type);
        _thunks[key] = f;
        var e = new Emitter(f);
        bool sret = Types.IsStruct(Types.Canonical(m.ReturnType));
        int thisIdx = sret ? 1 : 0;
        for (int i = 0; i < target.Fn.Type.Params.Length; i++)
        {
            e.Get(i);
            if (i == thisIdx) { e.I32(FunctionCompilerHelpers.BoxDataOffset(Types, vt)); e.Op(Op.I32Add); }
        }
        e.Call(target.Fn);
        return f;
    }

    // ---------------- lambdas and local functions ----------------
    internal MethodInst Lambda(FunctionCompiler owner, IAnonymousFunctionOperation af, TypeMap map, Display? env)
    {
        var key = owner.InstanceKey + " λ" + af.Syntax.SpanStart;
        if (_methods.TryGetValue(key, out var mi)) return mi;
        var sym = af.Symbol;
        var ret = Types.Resolve(sym.ReturnType, map);
        // (named by the outer method and position: a chain of the owners' names grows with every nesting)
        var fn = Module.AddFunction("lambda in " + Display(owner.OuterMethod) + "@" + af.Syntax.SpanStart, NestedSignature(sym, map));
        fn.FrameName = RoslynNestedFrameName(owner, af.Syntax, sym, localFunction: false);
        fn.ProgramCode = owner.FnProgramCode;
        mi = new MethodInst { Sym = sym, Fn = fn, Key = key, Impl = MethodImpl.Body };
        var ci = owner.MakeClosureInfo(map, env, ret);
        // (a local function called from the lambda is the outer method's: keyed by it, not by the lambda, or local
        // functions that call each other through lambdas would make new instances without end)
        ci.OuterKey = owner.OuterKey ?? owner.InstanceKey;
        mi.Compile = () => FunctionCompiler.CompileNested(this, mi, sym, af.Body, ci);
        _methods[key] = mi;
        _work.Enqueue(mi);
        return mi;
    }

    internal MethodInst LocalFunction(FunctionCompiler owner, IMethodSymbol m, TypeMap map, Display? env)
    {
        var def = m.OriginalDefinition;
        var key = (owner.OuterKey ?? owner.InstanceKey) + " local " + def.Name + "@" + def.Locations.FirstOrDefault()?.SourceSpan.Start +
                  (m.TypeArguments.Length > 0 ? "<" + string.Join(",", m.TypeArguments.Select(a => Types.Key(Types.Resolve(a, map)))) + ">" : "");
        if (_methods.TryGetValue(key, out var mi)) return mi;
        var fn = Module.AddFunction("local " + def.Name + " in " + Display(owner.OuterMethod), NestedSignature(def, map));
        fn.FrameName = RoslynNestedFrameName(owner, def.DeclaringSyntaxReferences.First().GetSyntax(), def, localFunction: true);
        fn.ProgramCode = owner.FnProgramCode;
        mi = new MethodInst { Sym = def, Fn = fn, Key = key, Impl = MethodImpl.Body };
        var syntax = def.DeclaringSyntaxReferences.First().GetSyntax();
        var op = (ILocalFunctionOperation)ModelFor(syntax.SyntaxTree).GetOperation(syntax)!;
        var ci = owner.MakeClosureInfo(map, env, Types.Resolve(def.ReturnType, map));
        ci.OuterKey = owner.OuterKey ?? owner.InstanceKey;
        mi.Compile = () => FunctionCompiler.CompileNested(this, mi, def, op.Body ?? op.IgnoredBody, ci);
        _methods[key] = mi;
        _work.Enqueue(mi);
        return mi;
    }

    FuncType NestedSignature(IMethodSymbol m, TypeMap map)
    {
        var ps = new List<ValType>();
        var rt = Types.Resolve(m.ReturnType, map);
        bool sret = !m.ReturnsByRef && Types.IsStruct(rt);
        if (sret) ps.Add(ValType.I32);
        ps.Add(ValType.I32); // closure
        foreach (var p in m.Parameters)
            ps.Add(p.RefKind != RefKind.None ? ValType.I32 : Types.WasmType(Types.Resolve(p.Type, map)) ?? ValType.I32);
        var r = Types.WasmType(rt);
        return new FuncType([.. ps], sret || r == null || m.ReturnsByRef ? (m.ReturnsByRef ? [ValType.I32] : []) : [r.Value]);
    }

    // ---------------- emission ----------------
    public (byte[] bytes, string map) Emit()
    {
        // version section: compiler version and ABI version
        var ver = new Writer();
        ver.Name(Cs2WasmCompiler.CompilerVersion); ver.U32(Options.AbiVersion);
        Module.CustomSections.Add(new CustomSection("cs2wasm.version", ver.ToArray()));
        foreach (var (n, data) in Options.CustomSections) Module.CustomSections.Add(new CustomSection(n, data));
        Mark("emit start");
        var enc = Module.Encode(Options.Optimize);
        Mark("encode (final pass)");
        if (Environment.GetEnvironmentVariable("CS2WASM_STATS") is "1" or "2") PrintCountStats(enc);
        // frames: which functions are .NET frames (a bit per function index), and the frame names of the library's
        // (the program's are in the name section), each as type, method and parameters over one string table
        var fr = new Writer();
        // (by function index: imports come first)
        int nfn = enc.FunctionOrder.Count == 0 ? 0 : enc.FunctionOrder.Max(f => f.Index) + 1;
        fr.U32(nfn);
        var bits = new byte[(nfn + 7) / 8];
        var onStack = FramesSeenByTraces(enc.FunctionOrder);
        foreach (var f in enc.FunctionOrder) if (f.FrameName != null && onStack.Contains(f)) bits[f.Index >> 3] |= (byte)(1 << (f.Index & 7));
        fr.Bytes(bits);
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>();
        int Str(string s) { if (!stringIndex.TryGetValue(s, out int i)) { i = strings.Count; strings.Add(s); stringIndex[s] = i; } return i; }
        var framesW = new Writer();
        var libFrames = enc.FunctionOrder.Select(f => (f, i: f.Index)).Where(x => x.f.FrameName != null && !x.f.ProgramCode && onStack.Contains(x.f)).ToList();
        framesW.U32(libFrames.Count);
        foreach (var (f, i) in libFrames)
        {
            framesW.U32(i);
            var name = f.FrameName!;
            int paren = name.IndexOf('(');
            int gen = name.IndexOf('[');
            int head = paren < 0 ? -1 : name.LastIndexOf('.', gen >= 0 && gen < paren ? gen : paren - 1);
            if (paren < 0 || head < 0 || !name.EndsWith(')')) { framesW.U32(0); framesW.U32(Str(name)); continue; }
            var ps = name[(paren + 1)..^1];
            var parts = ps.Length == 0 ? [] : ps.Split(", ");
            if (parts.Any(p => p.LastIndexOf(' ') <= 0)) { framesW.U32(0); framesW.U32(Str(name)); continue; }
            framesW.U32(parts.Length + 1);
            framesW.U32(Str(name[..head])); framesW.U32(Str(name[(head + 1)..paren]));
            foreach (var p in parts) { int sp = p.LastIndexOf(' '); framesW.U32(Str(p[..sp])); framesW.U32(Str(p[(sp + 1)..])); }
        }
        fr.U32(strings.Count); foreach (var s in strings) fr.Name(s);
        fr.Bytes(framesW.ToArray());
        // lines: code offset -> file and line, for the program's own files (.NET shows no file and line for the
        // library's frames). Files first, then entries in offset order: offset delta, (line delta << 1 | file
        // changes), and the new file's index when it changes.
        var inputFiles = Inputs.SelectMany(c => c.SyntaxTrees).Select(t => t.FilePath).ToHashSet();
        var own = new Dictionary<int, int>();
        var ownFiles = new List<string>();
        var entries = new List<(int off, int file, int line)>();
        foreach (var l in enc.Lines.OrderBy(l => l.Offset))
        {
            var p = PositionAt(l.Pos);
            if (!inputFiles.Contains(Files[p.File]))
            {
                // the library's code: no location from here on (file index -1, encoded as the file count)
                if (entries.Count > 0 && entries[^1].file != -1) entries.Add((l.Offset, -1, 0));
                continue;
            }
            if (!own.TryGetValue(p.File, out int fi)) { fi = ownFiles.Count; ownFiles.Add(Files[p.File]); own[p.File] = fi; }
            if (entries.Count > 0 && entries[^1].file == fi && entries[^1].line == p.Line) continue;
            entries.Add((l.Offset, fi, p.Line));
        }
        var lt = new Writer();
        lt.U32(ownFiles.Count); foreach (var f in ownFiles) lt.Name(f);
        lt.U32(entries.Count);
        int lastOff = 0, lastFile = -1, lastLine = 0;
        foreach (var (off, file, line) in entries)
        {
            lt.U32(off - lastOff);
            bool fileChanges = file != lastFile;
            lt.S32(((line - lastLine) << 1) | (fileChanges ? 1 : 0));
            if (fileChanges) lt.U32(file < 0 ? ownFiles.Count : file);
            lastOff = off; lastFile = file; lastLine = line;
        }
        var bytes = enc.Bytes;
        var withLines = new Writer();
        withLines.Bytes(bytes);
        var sec = new Writer(); sec.Name("cs2wasm.lines"); sec.Bytes(lt.ToArray());
        withLines.Byte(0); withLines.U32(sec.Length); withLines.Bytes(sec.ToArray());
        // (packed as the data segment is: a format byte, 0 plain, 1 LZ with the plain length first)
        var frRaw = fr.ToArray();
        var frPacked = DataCompression.Compress(frRaw);
        var fsec = new Writer(); fsec.Name("cs2wasm.frames");
        if (frPacked.Length + 8 < frRaw.Length) { fsec.Byte(1); fsec.U32(frRaw.Length); fsec.Bytes(frPacked); }
        else { fsec.Byte(0); fsec.Bytes(frRaw); }
        withLines.Byte(0); withLines.U32(fsec.Length); withLines.Bytes(fsec.ToArray());
        return (withLines.ToArray(), SourceMap(enc));
    }

    /// <summary>
    /// The reference-count operations removed at compile time (section 4.1, static counts): a counting scheme without
    /// ownership analysis pays an increment and a decrement for every reference transfer (an assignment, an
    /// initializer, an argument, a return of a reference) in the code compiled; the module has the retains and releases
    /// (and arena links) left after ownership and the final pass.
    /// </summary>
    void PrintCountStats(WasmModule.Encoded enc)
    {
        long transfers = 0, pushes = 0;
        foreach (var mi in AllMethods.Where(m => m.Compiled && m.Impl == MethodImpl.Body))
            foreach (var r in mi.Sym.OriginalDefinition.DeclaringSyntaxReferences)
            {
                if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
                foreach (var op in body.DescendantsAndSelf())
                {
                    bool Ref(ITypeSymbol? t) => t != null && (t.IsReferenceType || t.TypeKind == TypeKind.TypeParameter && !t.IsValueType);
                    switch (op)
                    {
                        case ISimpleAssignmentOperation sa when Ref(sa.Target.Type): transfers++; break;
                        case IVariableInitializerOperation vi when Ref(vi.Value.Type): transfers++; break;
                        case IArgumentOperation ar when ar.Parameter != null && Ref(ar.Parameter.Type): transfers++; break;
                        case IReturnOperation { ReturnedValue: { } rv } when Ref(rv.Type): transfers++; break;
                    }
                    // Lobster's baseline: every reference a value expression produces is counted up when it is pushed
                    // and down when it is consumed (a variable's or field's read, a call's or allocation's result)
                    if (op is ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation or IArrayElementReferenceOperation
                            or IInvocationOperation or IObjectCreationOperation or IArrayCreationOperation or IPropertyReferenceOperation or IInstanceReferenceOperation
                        && Ref(op.Type) && !(op.Parent is IAssignmentOperation asg && ReferenceEquals(asg.Target, op)))
                        pushes++;
                }
            }
        long emitted = 0;
        foreach (var f in enc.FunctionOrder)
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && (g.Name is "rt.retain" or "rt.release" || g.Name.StartsWith("Cs2Wasm.Runtime.Rt.Arena"))) emitted++;
        long naive = 2 * transfers;
        if (Emitter.RcSites != null)
            foreach (var (k, v) in Emitter.RcSites.OrderByDescending(x => x.Value).Take(30)) Console.Error.WriteLine($"    {v,6} {k}");
        Console.Error.WriteLine($"  reference counting: {emitted} count operations in the module, against {naive} for counting every transfer ({transfers} transfers): {(naive == 0 ? 0 : 100.0 * (naive - emitted) / naive):F1}% removed (static)");
        // (Lobster's measure: against counting every reference value up and down, the destroy functions' releases on both sides)
        long destroys = 0;
        foreach (var f in enc.FunctionOrder)
            if (f.Name.StartsWith("destroy ", StringComparison.Ordinal))
                foreach (var ins in f.Body) if (ins.Op == Op.Call && ins.Ref is WasmFunction g2 && g2.Name == "rt.release") destroys++;
        long lob = 2 * pushes + destroys;
        if (Environment.GetEnvironmentVariable("CS2WASM_RC_BYFN") == "1")
            foreach (var (n, c) in enc.FunctionOrder.Select(f => (f.Name, f.Body.Count(i => i.Op == Op.Call && i.Ref is WasmFunction g3 && g3.Name is "rt.retain" or "rt.release"))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(60))
                Console.Error.WriteLine($"    {c,5} {n}");
        Console.Error.WriteLine($"  reference counting: {emitted} against {lob} for counting every reference value pushed and popped ({pushes} values, {destroys} destroy releases): {(lob == 0 ? 0 : 100.0 * (lob - emitted) / lob):F1}% removed (static, Lobster's measure)");
    }
    /// <summary>A JSON string literal, as System.Text.Json writes one by default (quote, backslash and control
    /// characters escaped; the others outside printable ASCII as \uXXXX), so the source map is the same however the compiler is built.</summary>
    static string JsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\u0022"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E || c is '<' or '>' or '&' or '\'' or '+' or '`') sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }


    static string NormalizePath(string p) => p.Replace('\\', '/');

    string SourceMap(WasmModule.Encoded enc)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":3,\"sources\":[");
        sb.Append(string.Join(",", Files.Select(f => JsonString(NormalizePath(f)))));
        sb.Append("],\"names\":[],\"mappings\":\"");
        int prevCol = 0, prevFile = 0, prevLine = 0;
        bool first = true;
        foreach (var l in enc.Lines.OrderBy(x => x.Offset))
        {
            var p = PositionAt(l.Pos);
            if (!first) sb.Append(',');
            first = false;
            Vlq(sb, l.Offset - prevCol); Vlq(sb, p.File - prevFile); Vlq(sb, p.Line - 1 - prevLine); Vlq(sb, 0);
            prevCol = l.Offset; prevFile = p.File; prevLine = p.Line - 1;
        }
        sb.Append("\"}");
        return sb.ToString();
    }

    /// <summary>
    /// The functions that can be on the stack when a trace is taken: the stack is read only when an exception is
    /// thrown (Rt.OnThrow) or caught (Rt.OnCatch), so only functions that can reach one of those (by calls; an
    /// indirect call reaches whatever a table function reaches) ever appear in a trace or a frame count.
    /// </summary>
    HashSet<WasmFunction> FramesSeenByTraces(List<WasmFunction> fns)
    {
        var targets = new HashSet<WasmFunction>();
        foreach (var n in new[] { "OnThrow", "OnCatch" })
            if (_methods.Values.FirstOrDefault(m => m.Sym.Name == n && m.Sym.ContainingType?.ToDisplayString() == "Cs2Wasm.Runtime.Rt") is { } mi) targets.Add(mi.Fn);
        var callers = new Dictionary<WasmFunction, List<WasmFunction>>();
        var indirect = new List<WasmFunction>();
        var inTable = new HashSet<WasmFunction>();
        foreach (var f in fns)
            foreach (var ins in f.Body)
            {
                if (ins.Op == Op.CallIndirect) indirect.Add(f);
                else if (ins.Ref is WasmFunction g && ins.Op == Op.Call)
                {
                    if (!callers.TryGetValue(g, out var l)) callers[g] = l = [];
                    l.Add(f);
                }
                else if (ins.Ref is WasmFunction t && ins.Op == Op.I32Const) inTable.Add(t);
            }
        foreach (var f in fns) if (f.TableRoot) inTable.Add(f);
        var seen = new HashSet<WasmFunction>();
        var work = new Stack<WasmFunction>();
        void Mark(WasmFunction f)
        {
            if (!seen.Add(f)) return;
            work.Push(f);
            // (the callers through the table, once: taken out first, as marking them can reach this again)
            if (inTable.Contains(f) && indirect.Count > 0) { var cs = indirect.ToList(); indirect.Clear(); foreach (var c in cs) Mark(c); }
        }
        foreach (var t in targets) Mark(t);
        if (targets.Count == 0) foreach (var f in fns) seen.Add(f);
        while (work.Count > 0)
            if (callers.TryGetValue(work.Pop(), out var cs)) foreach (var c in cs) Mark(c);
        return seen;
    }

    static void Vlq(StringBuilder sb, int v)
    {
        const string b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        uint u = v < 0 ? ((uint)(-v) << 1) | 1 : (uint)v << 1;
        do { uint digit = u & 31; u >>= 5; if (u > 0) digit |= 32; sb.Append(b64[(int)digit]); } while (u > 0);
    }
}

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
    public WasmFunction Free => Lib("Cs2Wasm.Runtime.Rt", "Free", 2);
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
            e.Get(1); e.Call(RetainStruct(t));
            e.Get(0); e.Get(1); e.I32(D.Types.StorageOf(t).size); e.Op(Op.MemoryCopy);
        });
    }

    /// <summary>(dst, src): assignment: take new counts, release old contents, copy.</summary>
    public WasmFunction CopyAssign(ITypeSymbol t)
    {
        t = D.Types.Canonical(t);
        return Cached("rt.copy_assign " + Types.Key(t), FT([I, I], []), e =>
        {
            e.Get(1); e.Call(RetainStruct(t));
            e.Get(0); e.Call(ReleaseStruct(t));
            e.Get(0); e.Get(1); e.I32(D.Types.StorageOf(t).size); e.Op(Op.MemoryCopy);
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
sealed class RuntimeGen
{
    readonly Driver D;
    readonly Dictionary<int, WasmFunction> _destroyFns = [];
    readonly HashSet<(string stub, int type)> _stubDone = [];
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
        D.LibMethod("Cs2Wasm.Runtime.Rt", "Free", 2);
    }

    public void Export(string name, MethodInst mi) => _exports.Add((name, mi));

    /// <summary>Generates destroy functions for newly constructed types and resolves virtual calls; true if new work appeared.</summary>
    public bool Expand()
    {
        bool any = false;
        foreach (var id in D.Constructed.ToList())
        {
            if (_destroyFns.ContainsKey(id)) continue;
            _destroyFns[id] = MakeDestroy(id);
            any = true;
        }
        foreach (var (key, stub) in D.Stubs.ToList())
        {
            foreach (var id in D.Constructed.ToList())
            {
                if (!_stubDone.Add((key, id))) continue;
                if (D.SyntheticById.TryGetValue(id, out var syn))
                {
                    var slotKey = Types.MethodKey(D.CanonicalMethod(stub.Slot));
                    if (syn.Impls.TryGetValue(slotKey, out var sfn))
                        stub.Cases["synthetic#" + id] = (D.ObjectType, new MethodInst { Sym = stub.Slot, Fn = sfn, Key = "synthetic " + id + " " + slotKey }, false);
                    continue;
                }
                if (D.DisplayById.ContainsKey(id)) continue;
                var t = D.TypeById[id];
                var slotOwner = stub.Slot.ContainingType;
                if (!D.Types.IsSubtype(t, slotOwner)) continue;
                var impl = D.Implementation(t, stub.Slot);
                if (impl == null) continue;
                bool boxed = t.IsValueType && Types.Key(D.Types.Canonical(impl.ContainingType)) == Types.Key(D.Types.Canonical(t));
                var mi = D.Method(impl);
                stub.Cases[Types.Key(t) + "#" + id] = (t, mi, boxed);
                any = true;
            }
        }
        return any;
    }

    WasmFunction MakeDestroy(int id)
    {
        var f = D.Module.AddFunction("destroy " + id, new FuncType([ValType.I32], []));
        EmitDestroy(new Emitter(f), id, clear: true, free: true);
        return f;
    }

    /// <summary>An object's destruction: clear (finalizer, release of counted contents) and/or free (its memory).</summary>
    void EmitDestroy(Emitter e, int id, bool clear, bool free)
    {
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
            if (free) { e.Get(0); e.Get(0); e.Load(Op.I32Load, 12); e.I32(1); e.Op(Op.I32Shl); e.I32(Types.ArrayData + 2); e.Op(Op.I32Add); e.Call(rt.Free); }
            return;
        }
        if (type is IArrayTypeSymbol at)
        {
            var et = D.Types.Canonical(at.ElementType);
            int size = D.Types.StorageOf(et).size;
            int data = Types.ArrayDataOffset(at.Rank);
            if (clear && D.Types.NeedsRc(et)) { e.Get(0); e.I32(data); e.Op(Op.I32Add); e.Get(0); e.Load(Op.I32Load, 12); e.Call(rt.ReleaseRange(et)); }
            if (free) { e.Get(0); e.Get(0); e.Load(Op.I32Load, 12); e.I32(size); e.Op(Op.I32Mul); e.I32(data); e.Op(Op.I32Add); e.Call(rt.Free); }
            return;
        }
        if (type.IsValueType)
        {
            int off = FunctionCompilerHelpers.BoxDataOffset(D.Types, type);
            if (clear && Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) { e.Get(0); e.I32(off); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(type)); }
            if (free) { e.Get(0); e.I32(off + D.Types.StorageOf(type).size); e.Call(rt.Free); }
            return;
        }
        // class: finalizer, then fields, then the memory
        var l = D.Types.LayoutOf(type);
        if (clear)
        {
            var fin = FindFinalizer(type);
            if (fin != null)
            {
                // the finalizer may resurrect nothing observable here; it runs with a temporary count
                e.Get(0); e.I32(1); e.Store(Op.I32Store);
                e.Get(0); e.Call(D.Method(fin).Fn);
                e.Get(0); e.I32(0); e.Store(Op.I32Store);
            }
            foreach (var fs in l.Fields)
            {
                if (Types.IsRef(fs.Type)) { e.Get(0); e.Load(Op.I32Load, fs.Offset); e.Call(rt.Release); }
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
            e.Else(); e.Get(r); e.I32(-1); e.Op(Op.I32LtS); e.If(); e.Get(0); e.Call(retainSlow); e.End();
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
            e.Else(); e.Get(r); e.I32(-1); e.Op(Op.I32LtS); e.If(); e.Get(0); e.Call(releaseSlow); e.End();
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
                for (int i = 0; i < 5; i++) pre.Get(i);
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
                for (int i = 0; i < 3; i++) pre.Get(i);
                pre.Call(ar.RuntimeFn("ArenaArrayClear", 3)); pre.Op(Op.Return);
                pre.End();
                clr.Body.InsertRange(0, pre.Body);
            }
        }
        // per cycle-capable type: clear, free, visit (member-to-member slots); dispatch on the type id
        var clearFns = new Dictionary<int, WasmFunction>();
        var freeFns = new Dictionary<int, WasmFunction>();
        var visitFns = new Dictionary<int, WasmFunction>();
        var visit = ar.RuntimeFn("ArenaVisit", 1);
        foreach (var id in ar.CycleTypes)
        {
            var c = D.Module.AddFunction("arena.clear " + id, new FuncType([ValType.I32], []));
            EmitDestroy(new Emitter(c), id, clear: true, free: false);
            clearFns[id] = c;
            var fr = D.Module.AddFunction("arena.free " + id, new FuncType([ValType.I32], []));
            EmitDestroy(new Emitter(fr), id, clear: false, free: true);
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
                    ve.Get(i); ve.Call(visit);
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
            foreach (var (id, f) in per)
            {
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If(); e.Get(0); e.Call(f); e.Op(Op.Return); e.End();
            }
        }
        Dispatch(rt.ArenaClearFnIfMade, clearFns);
        Dispatch(rt.ArenaFreeFnIfMade, freeFns);
        Dispatch(rt.ArenaVisitFnIfMade, visitFns);
        if (rt.SccOfIfMade is { } scc) EmitIdTable(scc, D.TypeById.Count, i => (null, ar.SccOf(i)));
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
        // export wrappers
        foreach (var (name, mi) in _exports)
        {
            var w = D.Module.AddFunction("export " + name, mi.Fn.Type);
            w.Exports.Add(name);
            var e = new Emitter(w);
            var res = mi.Fn.Type.Results;
            var blk = e.Block(ValType.I32);
            var tti = e.TryTable(null, (0, D.ExceptionTag, blk));
            for (int i = 0; i < mi.Fn.Type.Params.Length; i++) e.Get(i);
            e.Call(mi.Fn);
            e.Op(Op.Return);
            e.End();
            e.Op(Op.Unreachable);
            e.End();
            // exception pointer on the stack
            e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "ReportUnhandled", 1).Fn);
            foreach (var r in res)
                switch (r) { case ValType.I64: e.I64(0); break; case ValType.F32: e.F32(0); break; case ValType.F64: e.F64(0); break; default: e.I32(0); break; }
        }
    }

    public void Finish()
    {
        if (D.Arenas.Enabled) EmitArenaSupport();
        EmitDestroyDispatch();
        EmitStubs();
        EmitTypeTests();
        D.Rt.EmitArrayDispatch();
        D.Rt.EmitEnumInfo();
        EmitTypeNames();
        D.LayoutMemory();
        EmitInit();
    }

    void EmitDestroyDispatch()
    {
        var e = new Emitter(D.Rt.Destroy);
        int n = D.TypeById.Count;
        var targets = new Label[n];
        var dflt = e.Block();
        var blocks = new Label[n];
        for (int i = n - 1; i >= 0; i--) blocks[i] = e.Block();
        e.Get(0); e.Load(Op.I32Load, 4);
        e.BrTable(blocks, dflt);
        for (int i = 0; i < n; i++)
        {
            e.End();
            if (_destroyFns.TryGetValue(i, out var fn)) { e.Get(0); e.Call(fn); e.Op(Op.Return); }
            else e.Op(Op.Unreachable);
        }
        e.End();
        e.Op(Op.Unreachable);
    }

    void EmitStubs()
    {
        foreach (var stub in D.Stubs.Values)
        {
            var e = new Emitter(stub.Fn);
            var sig = stub.Fn.Type;
            int thisIdx = Driver.Signature(stub.Slot, D.Types).Params.Length > stub.Slot.Parameters.Length + (stub.Slot.IsStatic ? 0 : 1) ? 1 : 0;
            int nparams = sig.Params.Length;
            var cases = stub.Cases.Values.ToList();
            if (cases.Count == 1 && !cases[0].boxed) stub.Fn.ForwardTo = cases[0].impl!.Fn;
            int tid = e.AddLocal(ValType.I32);
            e.Get(thisIdx); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (ckey, (type, impl, boxed)) in stub.Cases)
            {
                int id = ckey.StartsWith("synthetic#") ? int.Parse(ckey[10..]) : D.TypeId(type);
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If();
                for (int i = 0; i < nparams; i++)
                {
                    e.Get(i);
                    if (i == thisIdx && boxed) { e.I32(FunctionCompilerHelpers.BoxDataOffset(D.Types, type)); e.Op(Op.I32Add); }
                }
                e.Call(impl!.Fn);
                e.Op(Op.Return);
                e.End();
            }
            e.Op(Op.Unreachable);
        }
    }

    void EmitTypeTests()
    {
        foreach (var (key, (target, fn)) in D.Rt.IsInstFns)
        {
            var e = new Emitter(fn);
            var ids = D.Constructed.Where(id => D.SyntheticById.TryGetValue(id, out var syn)
                    ? syn.Interfaces.Any(i => Types.Key(i) == Types.Key(target)) || Types.IsObject(target)
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

    static void EmitIdSetTest(Emitter e, int tid, List<int> ids)
    {
        ids.Sort();
        e.I32(0);
        // runs of consecutive ids become range checks
        int i = 0;
        while (i < ids.Count)
        {
            int j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            if (i == j) { e.Get(tid); e.I32(ids[i]); e.Op(Op.I32Eq); }
            else { e.Get(tid); e.I32(ids[i]); e.Op(Op.I32Sub); e.I32(ids[j] - ids[i]); e.Op(Op.I32LeU); }
            e.Op(Op.I32Or);
            i = j + 1;
        }
    }

    void EmitTypeNames()
    {
        { var e = new Emitter(D.Rt.TypeNameFn); e.Get(0); e.Load(Op.I32Load, 4); e.Call(D.Rt.TypeNameById); }
        int n = D.TypeById.Count;
        string Full(int i) => i == 0 ? "?" : D.SyntheticById.TryGetValue(i, out var syn) ? syn.Name : D.DisplayById.ContainsKey(i) ? "<>c__DisplayClass" : Driver.ClrName(D.TypeById[i]);
        string Short(int i)
        {
            if (i == 0) return "?";
            if (D.SyntheticById.TryGetValue(i, out var syn)) return syn.Name;
            if (D.DisplayById.ContainsKey(i)) return "<>c__DisplayClass";
            return ShortName(D.TypeById[i]);
        }
        EmitIdTable(D.Rt.TypeNameById, n, i => (D.StringLiteral(Full(i)), 0));
        EmitIdTable(D.Rt.TypeShortNameById, n, i => (D.StringLiteral(Short(i)), 0));
        EmitIdTable(D.Rt.TypeFlagsById, n, i => (null, i == 0 || D.SyntheticById.ContainsKey(i) || D.DisplayById.ContainsKey(i) ? 0 : Flags(D.TypeById[i])));
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

    void EmitIdTable(WasmFunction fn, int n, Func<int, (IAddress? addr, int value)> entry)
    {
        var e = new Emitter(fn);
        var dflt = e.Block();
        var blocks = new Label[n];
        for (int i = n - 1; i >= 0; i--) blocks[i] = e.Block();
        e.Get(0);
        e.BrTable(blocks, dflt);
        for (int i = 0; i < n; i++)
        {
            e.End();
            var (a, v) = entry(i);
            if (a != null) e.I32Addr(a); else e.I32(v);
            e.Op(Op.Return);
        }
        e.End();
        var (a0, v0) = entry(0);
        if (a0 != null) e.I32Addr(a0); else e.I32(v0);
    }

    void EmitInit()
    {
        var e = new Emitter(_init);
        if (D.Data.Bytes.Count > 0)
        {
            D.Module.Data.Add(D.Data);
            e.I32(D.DataBase); e.I32(0); e.I32(D.Data.Bytes.Count); e.Emit(Op.MemoryInit, 0, 0, D.Data);
        }
        e.I32(D.StackTop); e.GSet(D.StackPointer);
    }
}

static class FunctionCompilerHelpers
{
    public static int BoxDataOffset(Types t, ITypeSymbol vt) => t.StorageOf(vt).align >= 8 ? 16 : Types.ObjectHeader;
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

    static List<ITypeSymbol> AllTypeArgs(INamedTypeSymbol n)
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
        var fn = Module.AddFunction("lambda in " + owner.FnName, NestedSignature(sym, map));
        fn.FrameName = NestedFrameName(owner, sym, "b__0");
        mi = new MethodInst { Sym = sym, Fn = fn, Key = key, Impl = MethodImpl.Body };
        var ci = owner.MakeClosureInfo(map, env, ret);
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
        var fn = Module.AddFunction("local " + def.Name + " in " + owner.FnName, NestedSignature(def, map));
        fn.FrameName = NestedFrameName(owner, def, "g__" + def.Name + "|0_0");
        mi = new MethodInst { Sym = def, Fn = fn, Key = key, Impl = MethodImpl.Body };
        var syntax = def.DeclaringSyntaxReferences.First().GetSyntax();
        var op = (ILocalFunctionOperation)ModelFor(syntax.SyntaxTree).GetOperation(syntax)!;
        var ci = owner.MakeClosureInfo(map, env, Types.Resolve(def.ReturnType, map));
        ci.OuterKey = owner.OuterKey ?? owner.InstanceKey;
        mi.Compile = () => FunctionCompiler.CompileNested(this, mi, def, op.Body ?? op.IgnoredBody!, ci);
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
        var enc = Module.Encode(Options.Optimize);
        // line table custom section: (offset, file, line) triples, files first
        // frame names: for each function index, a flag (1 = visible) and the .NET frame name
        var fr = new Writer();
        fr.U32(enc.FunctionOrder.Count);
        foreach (var f in enc.FunctionOrder) { fr.Byte((byte)(f.FrameName != null ? 1 : 0)); fr.Name(f.FrameName ?? ""); }
        var lt = new Writer();
        lt.U32(Files.Count); foreach (var f in Files) lt.Name(f);
        lt.U32(enc.Lines.Count);
        foreach (var l in enc.Lines) { var p = PositionAt(l.Pos); lt.U32(l.Offset); lt.U32(p.File); lt.U32(p.Line); }
        var bytes = enc.Bytes;
        var withLines = new Writer();
        withLines.Bytes(bytes);
        var sec = new Writer(); sec.Name("cs2wasm.lines"); sec.Bytes(lt.ToArray());
        withLines.Byte(0); withLines.U32(sec.Length); withLines.Bytes(sec.ToArray());
        var fsec = new Writer(); fsec.Name("cs2wasm.frames"); fsec.Bytes(fr.ToArray());
        withLines.Byte(0); withLines.U32(fsec.Length); withLines.Bytes(fsec.ToArray());
        return (withLines.ToArray(), SourceMap(enc));
    }

    static string NormalizePath(string p) => p.Replace('\\', '/');

    string SourceMap(WasmModule.Encoded enc)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\":3,\"sources\":[");
        sb.Append(string.Join(",", Files.Select(f => System.Text.Json.JsonSerializer.Serialize(NormalizePath(f)))));
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

    static void Vlq(StringBuilder sb, int v)
    {
        const string b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        uint u = v < 0 ? ((uint)(-v) << 1) | 1 : (uint)v << 1;
        do { uint digit = u & 31; u >>= 5; if (u > 0) digit |= 32; sb.Append(b64[(int)digit]); } while (u > 0);
    }
}

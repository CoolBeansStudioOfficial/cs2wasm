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
            if (D.Types.LayoutOf(t).HasRefs) NoteStructCopy(e.Fn, t);
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
    public IEnumerable<MethodInst> UserExports => _exports.Where(e => !e.name.StartsWith("cs2wasm_")).Select(e => e.mi);

    /// <summary>Generates destroy functions for newly constructed types and resolves virtual calls; true if new work appeared.</summary>
    public bool Expand()
    {
        bool any = D.ExpandObjectArrayThunks() | D.ExpandAttributeArrays() | D.ExpandDynamicInvokeThunks() | D.ExpandInterpreterTypes();
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
            if (clear) { e.Get(0); e.Call(rt.OnDestroy); e.Op(Op.Drop); }
            if (clear && Types.IsStruct(type) && D.Types.LayoutOf(type).HasRefs) { e.Get(0); e.I32(off); e.Op(Op.I32Add); e.Call(rt.ReleaseStruct(type)); }
            if (free) { e.Get(0); e.I32(off + D.Types.StorageOf(type).size); e.Call(rt.Free); }
            return;
        }
        // class: finalizer, then fields, then the memory
        var l = D.Types.LayoutOf(type);
        if (clear)
        {
            var fin = FindFinalizer(type);
            // weak references to it are cleared first; GC.SuppressFinalize can skip the finalizer
            e.Get(0); e.Call(rt.OnDestroy);
            if (fin != null)
            {
                e.If();
                // the finalizer may resurrect nothing observable here; it runs with a temporary count
                e.Get(0); e.I32(1); e.Store(Op.I32Store);
                e.Get(0); e.Call(D.Method(fin).Fn);
                e.Get(0); e.I32(0); e.Store(Op.I32Store);
                e.End();
            }
            else e.Op(Op.Drop);
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
                e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
                e.If(); e.Get(0); e.Call(f); e.Op(Op.Return); e.End();
            }
        }
        Dispatch(rt.ArenaClearFnIfMade, clearFns);
        Dispatch(rt.ArenaFreeFnIfMade, freeFns);
        Dispatch(rt.ArenaVisitFnIfMade, visitFns);
        _emitScc = () => { if (rt.SccOfIfMade is { } scc) EmitIdTable(scc, D.TypeById.Count, i => (null, ar.SccOf(i))); };
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
        // a worker instance starts here: its own stack, thread-local block and id, then the thread's delegate
        if (D.UsesThreads)
        {
            // shared counts (section 4.5): the marking runtime, filled in at Finish
            D.LibMethod("Cs2Wasm.Runtime.Sharing", "Share", 1);
            D.LibMethod("Cs2Wasm.Runtime.Sharing", "MarkPush", 1);
            D.LibMethod("Cs2Wasm.Runtime.Sharing", "RevisitIfShared", 1);
            // deferred destruction of shared objects
            {
                D.LibMethod("Cs2Wasm.Runtime.Limbo", "Add", 1);
                D.LibMethod("Cs2Wasm.Runtime.Limbo", "ExportEnter", 0);
                D.LibMethod("Cs2Wasm.Runtime.Limbo", "ExportExit", 0);
            }
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
            var limbo = D.UsesThreads;
            if (limbo) e.Call(D.LibMethod("Cs2Wasm.Runtime.Limbo", "ExportEnter", 0).Fn);
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
        D.Rt.EmitThreadSupport(D.UsesThreads, () => D.Arenas.RuntimeFn("ArenaRetain", 1), () => D.Arenas.RuntimeFn("ArenaRelease", 1), D.Arenas.Enabled);
        D.Rt.EmitArrayDispatch();
        D.Module.UsesThreads = D.UsesThreads;
        D.EmitObjectArrayDelegate();
        D.EmitDynamicInvoke();
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
        AssignTypeIds();
        _emitScc?.Invoke();
        D.EmitReflectionTables();
        D.EmitDelegateMethod();
        D.EmitSharing(D.UsesThreads, Enumerable.Range(0, D.TypeById.Count).Where(id => D.Constructed.Contains(id)).Select(id => (id, D.TypeById[id])));
        EmitDestroyDispatch();
        EmitRetainContents();
        EmitStubs();
        EmitTypeTests();
        D.Rt.EmitEnumInfo();
        EmitTypeNames();
        // (every function exists: what each can store is known)
        D.ResolveFieldBorrows();
        D.LayoutMemory();
        EmitInit();
    }

    /// <summary>rt.on_destroy(p): 1 if p's finalizer runs. Without marks in the program it is `1`, and the calls whose
    /// result is dropped are removed.</summary>
    void EmitOnDestroy()
    {
        var f = D.Rt.OnDestroyIfMade;
        if (f == null) return;
        var marks = D.AllMethods.FirstOrDefault(m => m.Sym.Name == "OnDestroy" && m.Sym.ContainingType?.Name == "ObjectMarks" && m.Compiled);
        var e = new Emitter(f);
        if (marks != null) { e.Get(0); e.Call(marks.Fn); return; }
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

    void EmitDestroyDispatch()
    {
        var e = new Emitter(D.Rt.Destroy);
        int n = D.TypeCount;
        var targets = new Label[n];
        var dflt = e.Block();
        var blocks = new Label[n];
        for (int i = n - 1; i >= 0; i--) blocks[i] = e.Block();
        e.Get(0); e.Load(Op.I32Load, 4);
        e.BrTable(blocks, dflt);
        for (int i = 0; i < n; i++)
        {
            e.End();
            if (_destroyFns.TryGetValue(D.IntId(i), out var fn)) { e.Get(0); e.Call(fn); e.Op(Op.Return); }
            else e.Op(Op.Unreachable);
        }
        e.End();
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
            e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
            e.If();
            e.Fn.Body.AddRange(body.Fn.Body);
            e.Op(Op.Return);
            e.End();
        }
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
                e.Get(tid); e.Tid(id); e.Op(Op.I32Eq);
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

    void EmitIdSetTest(Emitter e, int tid, List<int> internalIds)
    {
        var ids = internalIds.Select(D.ExtId).Where(x => x >= 0).Distinct().ToList();
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
        int n = D.TypeCount;
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
        if (D.TlsSize > 0) { e.I32(D.TlsSize); e.Call(D.LibMethod("Cs2Wasm.Runtime.Rt", "Alloc", 1).Fn); e.GSet(D.TlsBase); }
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
        var fn = Module.AddFunction("lambda in " + owner.FnName, NestedSignature(sym, map));
        fn.FrameName = NestedFrameName(owner, sym, "b__0");
        fn.ProgramCode = owner.FnProgramCode;
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
        long transfers = 0;
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
                }
            }
        long emitted = 0;
        foreach (var f in enc.FunctionOrder)
            foreach (var ins in f.Body)
                if (ins.Op == Op.Call && ins.Ref is WasmFunction g && (g.Name is "rt.retain" or "rt.release" || g.Name.StartsWith("Cs2Wasm.Runtime.Rt.Arena"))) emitted++;
        long naive = 2 * transfers;
        Console.Error.WriteLine($"  reference counting: {emitted} count operations in the module, against {naive} for counting every transfer ({transfers} transfers): {(naive == 0 ? 0 : 100.0 * (naive - emitted) / naive):F1}% removed (static)");
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
            if (inTable.Contains(f)) { foreach (var c in indirect) Mark(c); indirect.Clear(); }
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

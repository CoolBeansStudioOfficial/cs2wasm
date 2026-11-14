using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>Type-specialized helpers: generic equality, hashing and comparison; array element operations; enum names.</summary>
internal sealed partial class RuntimeFns
{
    WasmFunction? _lockEnter, _lockExit;
    /// <summary>The runtime lock (allocator, arenas, task continuations): reentrant, owned by a thread id. Empty without threads.</summary>
    public WasmFunction LockEnter => _lockEnter ??= M.AddFunction("rt.lock_enter", FT([], []));
    public WasmFunction LockExit => _lockExit ??= M.AddFunction("rt.lock_exit", FT([], []));

    /// <summary>Fills in the lock and, when threads exist, makes retain/release atomic.</summary>
    public void EmitThreadSupport(bool threads, Func<WasmFunction> retainSlow, Func<WasmFunction> releaseSlow, bool arenas)
    {
        if (threads) { _ = LockEnter; _ = LockExit; }
        var owner = D.StaticAddress("rt.lock.owner", 4, 4);
        var depth = D.StaticAddress("rt.lock.depth", 4, 4);
        if (_lockEnter != null && threads)
        {
            var e = new Emitter(_lockEnter);
            // reentrant: if we own it, depth++; else CAS 0 -> id, spinning (waiting where allowed)
            e.I32Addr(owner); e.Emit(Op.I32AtomicLoad, 0, 2); e.GGet(D.ThreadId); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(depth); e.I32Addr(depth); e.Load(Op.I32Load); e.I32(1); e.Op(Op.I32Add); e.Store(Op.I32Store); e.Op(Op.Return); e.End();
            var brk = e.Block(); var top = e.Loop();
            e.I32Addr(owner); e.I32(0); e.GGet(D.ThreadId); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Op(Op.I32Eqz); e.BrIf(brk);
            e.Br(top); e.End(); e.End();
            e.I32Addr(depth); e.I32(1); e.Store(Op.I32Store);
        }
        if (_lockExit != null && threads)
        {
            var e = new Emitter(_lockExit);
            e.I32Addr(depth); e.I32Addr(depth); e.Load(Op.I32Load); e.I32(1); e.Op(Op.I32Sub); e.Store(Op.I32Store);
            e.I32Addr(depth); e.Load(Op.I32Load); e.Op(Op.I32Eqz);
            e.If(); e.I32Addr(owner); e.I32(0); e.Emit(Op.I32AtomicStore, 0, 2); e.End();
        }
        if (!threads) return;
        // retain(p): CAS the count up; immortal (-1) and dead (0) stay; arena members go through the runtime lock
        {
            var f = Retain; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            var brk = e.Block(); var top = e.Loop();
            e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(0); e.Op(Op.I32GtS);
            e.If();
            e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Add); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(brk);
            e.Br(top);
            e.End();
            if (arenas) { e.Get(r); e.I32(-1); e.Op(Op.I32LtS); e.If(); e.Call(LockEnter); e.Get(0); e.Call(retainSlow()); e.Call(LockExit); e.End(); }
            e.End(); e.End();
            e.End();
            e.Get(0);
        }
        {
            var f = Release; f.Body.Clear(); f.Locals.Clear();
            var e = new Emitter(f);
            int r = e.AddLocal(I);
            e.Get(0); e.If();
            var brk = e.Block(); var top = e.Loop();
            e.Get(0); e.Emit(Op.I32AtomicLoad, 0, 2); e.Tee(r); e.I32(1); e.Op(Op.I32GtS);
            e.If();
            e.Get(0); e.Get(r); e.Get(r); e.I32(1); e.Op(Op.I32Sub); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.Get(r); e.Op(Op.I32Eq); e.BrIf(brk);
            e.Br(top);
            e.End();
            e.Get(r); e.I32(1); e.Op(Op.I32Eq);
            e.If();
            e.Get(0); e.I32(1); e.I32(0); e.Emit(Op.I32AtomicRmwCmpxchg, 0, 2); e.I32(1); e.Op(Op.I32Eq);
            e.If(); e.Get(0); e.Call(Destroy); e.Br(brk); e.End();
            e.Br(top);
            e.End();
            if (arenas) { e.Get(r); e.I32(-1); e.Op(Op.I32LtS); e.If(); e.Call(LockEnter); e.Get(0); e.Call(releaseSlow()); e.Call(LockExit); e.End(); }
            e.End(); e.End();
            e.End();
        }
    }

    WasmFunction? _onDestroy;
    public WasmFunction OnDestroy => _onDestroy ??= M.AddFunction("rt.on_destroy", FT([I], [I]));
    public WasmFunction? OnDestroyIfMade => _onDestroy;

    WasmFunction? _clamp;
    /// <summary>(v, lo, hi) → v clamped to [lo, hi].</summary>
    public WasmFunction ClampI32 => _clamp ??= MakeClamp();
    WasmFunction MakeClamp()
    {
        var f = M.AddFunction("rt.clamp_i32", FT([I, I, I], [I]));
        var e = new Emitter(f);
        e.Get(0); e.Get(1); e.Op(Op.I32LtS); e.If(); e.Get(1); e.Op(Op.Return); e.End();
        e.Get(0); e.Get(2); e.Op(Op.I32GtS); e.If(); e.Get(2); e.Op(Op.Return); e.End();
        e.Get(0);
        return f;
    }

    WasmFunction? _arrayElemSize, _arrayCounted, _arrayCopy, _arrayClear, _arrayRetain, _arrayRelease, _enumInfo;

    public WasmFunction ArrayElemSize => _arrayElemSize ??= M.AddFunction("rt.array_elem_size", FT([I], [I]));
    public WasmFunction ArrayCounted => _arrayCounted ??= M.AddFunction("rt.array_counted", FT([I], [I]));
    WasmFunction? _arrayRank;
    public WasmFunction ArrayRank => _arrayRank ??= M.AddFunction("rt.array_rank", FT([I], [I]));
    WasmFunction ArrayRetainRange => _arrayRetain ??= M.AddFunction("rt.array_retain_range", FT([I, I, I], []));
    WasmFunction ArrayReleaseRange => _arrayRelease ??= M.AddFunction("rt.array_release_range", FT([I, I, I], []));
    public WasmFunction EnumInfo => _enumInfo ??= M.AddFunction("rt.enum_info", FT([I], [I]));

    /// <summary>(src, si, dst, di, length, size): copy elements with counts; overlapping ranges are fine.</summary>
    public WasmFunction ArrayCopy => _arrayCopy ??= MakeArrayCopy();
    public WasmFunction? ArrayCopyIfMade => _arrayCopy;
    public WasmFunction? ArrayClearIfMade => _arrayClear;

    // arenas (section 4.3): dispatch on the type id of a member; bodies are emitted with the type graph
    WasmFunction? _arenaClear, _arenaFree, _arenaVisit, _sccOf;
    public WasmFunction ArenaClearFn => _arenaClear ??= M.AddFunction("rt.arena_clear", FT([I], []));
    public WasmFunction ArenaFreeFn => _arenaFree ??= M.AddFunction("rt.arena_free", FT([I], []));
    public WasmFunction ArenaVisitFn => _arenaVisit ??= M.AddFunction("rt.arena_visit", FT([I], []));
    public WasmFunction SccOfFn => _sccOf ??= M.AddFunction("rt.scc_of", FT([I], [I]));
    public WasmFunction? ArenaClearFnIfMade => _arenaClear;
    public WasmFunction? ArenaFreeFnIfMade => _arenaFree;
    public WasmFunction? ArenaVisitFnIfMade => _arenaVisit;
    public WasmFunction? SccOfIfMade => _sccOf;
    /// <summary>(array, index, length, size): release and zero elements.</summary>
    public WasmFunction ArrayClear => _arrayClear ??= MakeArrayClear();

    WasmFunction MakeArrayCopy()
    {
        var f = M.AddFunction("rt.array_copy", FT([I, I, I, I, I, I], []));
        var e = new Emitter(f);
        int s = e.AddLocal(I), d = e.AddLocal(I);
        e.Get(0); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(1); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.I32Add); e.Set(s);
        e.Get(2); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(3); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.I32Add); e.Set(d);
        e.Get(0); e.Get(s); e.Get(4); e.Call(ArrayRetainRange);
        e.Get(2); e.Get(d); e.Get(4); e.Call(ArrayReleaseRange);
        e.Get(d); e.Get(s); e.Get(4); e.Get(5); e.Op(Op.I32Mul); e.Op(Op.MemoryCopy);
        return f;
    }

    WasmFunction MakeArrayClear()
    {
        var f = M.AddFunction("rt.array_clear", FT([I, I, I, I], []));
        var e = new Emitter(f);
        int a = e.AddLocal(I);
        e.Get(0); e.I32(Types.ArrayData); e.Op(Op.I32Add); e.Get(1); e.Get(3); e.Op(Op.I32Mul); e.Op(Op.I32Add); e.Set(a);
        e.Get(0); e.Get(a); e.Get(2); e.Call(ArrayReleaseRange);
        e.Get(a); e.I32(0); e.Get(2); e.Get(3); e.Op(Op.I32Mul); e.Op(Op.MemoryFill);
        return f;
    }

    IEnumerable<(int id, IArrayTypeSymbol at)> ConstructedArrays() =>
        D.Constructed.Where(id => !D.DisplayById.ContainsKey(id) && D.TypeById[id] is IArrayTypeSymbol).Select(id => (id, (IArrayTypeSymbol)D.TypeById[id]));

    public void EmitArrayDispatch()
    {
        if (_arrayElemSize != null)
        {
            var e = new Emitter(_arrayElemSize);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, at) in ConstructedArrays())
            {
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If(); e.I32(D.Types.StorageOf(D.Types.Canonical(at.ElementType)).size); e.Op(Op.Return); e.End();
            }
            e.I32(1);
        }
        if (_arrayRank != null)
        {
            var e = new Emitter(_arrayRank);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, at) in ConstructedArrays())
            {
                if (at.Rank == 1) continue;
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If(); e.I32(at.Rank); e.Op(Op.Return); e.End();
            }
            e.I32(1);
        }
        if (_arrayCounted != null)
        {
            var e = new Emitter(_arrayCounted);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, at) in ConstructedArrays())
            {
                if (!D.Types.NeedsRc(D.Types.Canonical(at.ElementType))) continue;
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If(); e.I32(1); e.Op(Op.Return); e.End();
            }
            e.I32(0);
        }
        foreach (var (fn, release) in new[] { (_arrayRetain, false), (_arrayRelease, true) })
        {
            if (fn == null) continue;
            var e = new Emitter(fn);
            int tid = e.AddLocal(I);
            e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
            foreach (var (id, at) in ConstructedArrays())
            {
                var et = D.Types.Canonical(at.ElementType);
                if (!D.Types.NeedsRc(et)) continue;
                e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
                e.If(); e.Get(1); e.Get(2); e.Call(release ? ReleaseRange(et) : RetainRange(et)); e.Op(Op.Return); e.End();
            }
        }
    }

    /// <summary>Per boxed enum type: [count][flags][size][unsigned] then 16-byte entries (value i64, name).</summary>
    public void EmitEnumInfo()
    {
        if (_enumInfo == null) return;
        var e = new Emitter(_enumInfo);
        int tid = e.AddLocal(I);
        e.Get(0); e.Load(Op.I32Load, 4); e.Set(tid);
        foreach (var id in D.Constructed)
        {
            if (D.DisplayById.ContainsKey(id) || D.TypeById[id] is not INamedTypeSymbol { TypeKind: TypeKind.Enum } et) continue;
            var p = Types.PrimOf(et);
            bool unsigned = !Types.IsSigned(p);
            // .NET orders by the unsigned value; duplicates: first declared wins in GetName
            var dedup = new List<(long value, string name)>();
            foreach (var g in et.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
            {
                long v = System.Convert.ToInt64(g.ConstantValue is ulong u ? unchecked((long)u) : g.ConstantValue);
                if (!dedup.Any(x => x.value == v)) dedup.Add((v, g.Name));
            }
            dedup.Sort((a, b) => ((ulong)a.value).CompareTo((ulong)b.value));
            bool flags = et.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute");
            var blob = new List<byte>();
            void I32b(int v) => blob.AddRange(BitConverter.GetBytes(v));
            I32b(dedup.Count); I32b(flags ? 1 : 0); I32b(Types.PrimSize(p)); I32b(unsigned ? 1 : 0);
            foreach (var (value, name) in dedup)
            {
                blob.AddRange(BitConverter.GetBytes(value));
                I32b(D.StringLiteral(name).Address); I32b(0);
            }
            var addr = D.DataBlob([.. blob], 8);
            e.Get(tid); e.I32(id); e.Op(Op.I32Eq);
            e.If(); e.I32Addr(addr); e.Op(Op.Return); e.End();
        }
        e.Op(Op.Unreachable);
    }

    // ---------------- generic equality / hash / compare ----------------
    readonly Dictionary<string, WasmFunction> _generic = [];

    WasmFunction GenericFn(string kind, ITypeSymbol t, FuncType sig, Action<Emitter> body)
    {
        var key = kind + " " + Types.Key(t);
        if (_generic.TryGetValue(key, out var f)) return f;
        f = M.AddFunction("rt." + key, sig);
        _generic[key] = f;
        body(new Emitter(f));
        return f;
    }

    static ValType W(ITypeSymbol t) => Types.WasmType(t) ?? ValType.I32;

    IMethodSymbol ObjMethod(string name, int arity) =>
        ((INamedTypeSymbol)D.ObjectType).GetMembers(name).OfType<IMethodSymbol>().First(m => m.Parameters.Length == arity && !m.IsStatic);

    IMethodSymbol? FindMethod(ITypeSymbol t, string name, ITypeSymbol? paramType)
    {
        for (var b = t as INamedTypeSymbol; b != null; b = b.BaseType is { } bt ? (INamedTypeSymbol)D.Types.Canonical(bt) : null)
        {
            var m = b.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(x => !x.IsStatic && x.Parameters.Length == (paramType == null ? 0 : 1)
                && (paramType == null || Types.Key(D.Types.Canonical(x.Parameters[0].Type)) == Types.Key(paramType)));
            if (m != null) return m;
            if (t.IsValueType) break;
        }
        return null;
    }

    public WasmFunction GenericEquals(ITypeSymbol t)
    {
        var w = W(t);
        return GenericFn("equals", t, FT([w, w], [I]), e =>
        {
            var p = Types.PrimOf(t);
            if (p != Prim.None)
            {
                if (Types.IsFloat(p))
                {
                    var eq = p == Prim.F32 ? Op.F32Eq : Op.F64Eq; var ne = p == Prim.F32 ? Op.F32Ne : Op.F64Ne;
                    e.Get(0); e.Get(1); e.Op(eq);
                    e.Get(0); e.Get(0); e.Op(ne); e.Get(1); e.Get(1); e.Op(ne); e.Op(Op.I32And);
                    e.Op(Op.I32Or);
                    return;
                }
                e.Get(0); e.Get(1); e.Op(w == L ? Op.I64Eq : Op.I32Eq);
                return;
            }
            if (Types.IsRef(t))
            {
                e.Get(0); e.Get(1); e.Op(Op.I32Eq); e.If(); e.I32(1); e.Op(Op.Return); e.End();
                e.Get(0); e.Op(Op.I32Eqz); e.Get(1); e.Op(Op.I32Eqz); e.Op(Op.I32Or); e.If(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(0); e.Get(1); e.Call(D.Stub(ObjMethod("Equals", 1)));
                return;
            }
            // struct: Equals(T) if declared, else Equals(object) through a box
            var eqT = FindMethod(t, "Equals", t);
            if (eqT != null) { e.Get(0); e.Get(1); e.Call(D.Method(eqT).Fn); return; }
            var slow = D.LibMethod("System.Collections.Generic.Generic", "SlowEquals").Sym;
            e.Get(0); e.Get(1); e.Call(D.Method(slow.Construct(t)).Fn);
        });
    }

    public WasmFunction GenericHash(ITypeSymbol t)
    {
        var w = W(t);
        return GenericFn("hash", t, FT([w], [I]), e =>
        {
            var p = Types.PrimOf(t);
            switch (p)
            {
                case Prim.None: break;
                case Prim.I64: case Prim.U64:
                    e.Get(0); e.Op(Op.I32WrapI64); e.Get(0); e.I64(32); e.Op(Op.I64ShrS); e.Op(Op.I32WrapI64); e.Op(Op.I32Xor); return;
                case Prim.Char:
                    e.Get(0); e.Get(0); e.I32(16); e.Op(Op.I32Shl); e.Op(Op.I32Or); return;
                case Prim.F64:
                    {
                        int b = e.AddLocal(L);
                        e.Get(0); e.Op(Op.I64ReinterpretF64); e.Set(b);
                        // NaN and ±0 hash alike
                        e.Get(b); e.I64(1); e.Op(Op.I64Sub); e.I64(0x7FFFFFFFFFFFFFFF); e.Op(Op.I64And); e.I64(0x7FF0000000000000); e.Op(Op.I64GeU);
                        e.If(); e.Get(b); e.I64(0x7FF0000000000000); e.Op(Op.I64And); e.Set(b); e.End();
                        e.Get(b); e.Op(Op.I32WrapI64); e.Get(b); e.I64(32); e.Op(Op.I64ShrS); e.Op(Op.I32WrapI64); e.Op(Op.I32Xor);
                        return;
                    }
                case Prim.F32:
                    {
                        int b = e.AddLocal(I);
                        e.Get(0); e.Op(Op.I32ReinterpretF32); e.Set(b);
                        e.Get(b); e.I32(1); e.Op(Op.I32Sub); e.I32(0x7FFFFFFF); e.Op(Op.I32And); e.I32(0x7F800000); e.Op(Op.I32GeU);
                        e.If(); e.Get(b); e.I32(0x7F800000); e.Op(Op.I32And); e.Set(b); e.End();
                        e.Get(b);
                        return;
                    }
                default: e.Get(0); return;
            }
            if (Types.IsRef(t))
            {
                e.Get(0); e.Op(Op.I32Eqz); e.If(); e.I32(0); e.Op(Op.Return); e.End();
                e.Get(0); e.Call(D.Stub(ObjMethod("GetHashCode", 0)));
                return;
            }
            var h = FindMethod(t, "GetHashCode", null);
            if (h != null && Types.Key(D.Types.Canonical(h.ContainingType)) == Types.Key(t)) { e.Get(0); e.Call(D.Method(h).Fn); return; }
            e.I32(0);
        });
    }

    public WasmFunction GenericCompare(ITypeSymbol t)
    {
        var w = W(t);
        return GenericFn("compare", t, FT([w, w], [I]), e =>
        {
            var p = Types.PrimOf(t);
            if (p != Prim.None)
            {
                bool f = Types.IsFloat(p), signed = Types.IsSigned(p);
                Op lt = (w, signed) switch { (ValType.I64, true) => Op.I64LtS, (ValType.I64, false) => Op.I64LtU, (ValType.F32, _) => Op.F32Lt, (ValType.F64, _) => Op.F64Lt, (_, true) => Op.I32LtS, _ => Op.I32LtU };
                Op gt = (w, signed) switch { (ValType.I64, true) => Op.I64GtS, (ValType.I64, false) => Op.I64GtU, (ValType.F32, _) => Op.F32Gt, (ValType.F64, _) => Op.F64Gt, (_, true) => Op.I32GtS, _ => Op.I32GtU };
                e.Get(0); e.Get(1); e.Op(lt); e.If(); e.I32(-1); e.Op(Op.Return); e.End();
                e.Get(0); e.Get(1); e.Op(gt); e.If(); e.I32(1); e.Op(Op.Return); e.End();
                if (f)
                {
                    var ne = p == Prim.F32 ? Op.F32Ne : Op.F64Ne;
                    // equal, or at least one NaN: NaN sorts first
                    e.Get(0); e.Get(0); e.Op(ne);
                    e.If(ValType.I32); e.Get(1); e.Get(1); e.Op(ne); e.If(ValType.I32); e.I32(0); e.Else(); e.I32(-1); e.End();
                    e.Else(); e.Get(1); e.Get(1); e.Op(ne); e.End();
                    return;
                }
                e.I32(0);
                return;
            }
            var icT = D.Main.GetTypeByMetadataName("System.IComparable`1")!.Construct(t);
            var ic = D.Main.GetTypeByMetadataName("System.IComparable")!;
            bool hasGeneric = D.Types.IsSubtype(t, icT);
            bool hasPlain = D.Types.IsSubtype(t, ic);
            if (Types.IsRef(t))
            {
                e.Get(0); e.Op(Op.I32Eqz); e.If(); e.Get(1); e.Op(Op.I32Eqz); e.If(ValType.I32); e.I32(0); e.Else(); e.I32(-1); e.End(); e.Op(Op.Return); e.End();
                e.Get(1); e.Op(Op.I32Eqz); e.If(); e.I32(1); e.Op(Op.Return); e.End();
                if (hasGeneric) { e.Get(0); e.Get(1); e.Call(D.Stub(icT.GetMembers("CompareTo").OfType<IMethodSymbol>().First())); return; }
                if (hasPlain) { e.Get(0); e.Get(1); e.Call(D.Stub(ic.GetMembers("CompareTo").OfType<IMethodSymbol>().First())); return; }
                e.Call(D.LibMethod("System.Collections.Generic.Generic", "CompareFailed").Fn); e.I32(0);
                return;
            }
            var cmp = FindMethod(t, "CompareTo", t);
            if (cmp != null) { e.Get(0); e.Get(1); e.Call(D.Method(cmp).Fn); return; }
            var slow = D.LibMethod("System.Collections.Generic.Generic", "SlowCompare").Sym;
            e.Get(0); e.Get(1); e.Call(D.Method(slow.Construct(t)).Fn);
        });
    }
}


using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>Nullable value types: the library's Nullable&lt;T&gt; struct {hasValue, value}, lifted operators, boxing.</summary>
sealed partial class FunctionCompiler
{
    ITypeSymbol Underlying(ITypeSymbol nullable) => T.Canonical(((INamedTypeSymbol)nullable).TypeArguments[0]);

    (int hasOff, int valOff) NullableLayout(ITypeSymbol nullable)
    {
        var l = T.LayoutOf(nullable);
        var has = l.Fields.First(f => f.Symbol.Name == "hasValue");
        var val = l.Fields.First(f => f.Symbol.Name == "value");
        return (has.Offset, val.Offset);
    }

    /// <summary>Stack: address of T? → i32 hasValue.</summary>
    void NullableHas(ITypeSymbol nt) => E.Load(Op.I32Load8U, NullableLayout(nt).hasOff);

    /// <summary>Stack: address of T? → the value (or its address, for struct T).</summary>
    void NullableVal(ITypeSymbol nt)
    {
        var u = Underlying(nt);
        int off = NullableLayout(nt).valOff;
        if (Types.IsStruct(u)) { if (off != 0) { E.I32(off); E.Op(Op.I32Add); } }
        else E.Load(Types.LoadOp(u), off);
    }

    /// <summary>Stack: value of T → address of a T? temporary holding it.</summary>
    void WrapNullable(ITypeSymbol nt, Own o)
    {
        var u = Underlying(nt);
        var (hasOff, valOff) = NullableLayout(nt);
        int tmp = TempStruct(nt);
        var vt = Types.WasmType(u) ?? ValType.I32;
        int v = Scratch(vt);
        if (Types.IsRef(u)) ToOwned(o);
        E.Set(v);
        E.Get(_fb); E.I32(1); E.Store(Op.I32Store8, tmp + hasOff);
        if (Types.IsStruct(u)) { E.Get(_fb); E.I32(tmp + valOff); E.Op(Op.I32Add); E.Get(v); CopyStructInit(u); }
        else { E.Get(_fb); E.Get(v); E.Store(Types.StoreOp(u), tmp + valOff); }
        FreeScratch(vt, v);
        E.Get(_fb); E.I32(tmp); E.Op(Op.I32Add);
    }

    /// <summary>Conversions involving Nullable&lt;T&gt;. Returns false when neither side is nullable.</summary>
    bool ConvertNullable(ITypeSymbol from, ITypeSymbol to, bool isChecked, ref Own o)
    {
        bool fn = IsNullable(from), tn = IsNullable(to);
        if (!fn && !tn) return false;
        if (fn && tn)
        {
            if (Types.Key(from) == Types.Key(to)) return true;
            // T? -> U? : has ? (U?)(U)value : null
            var uf = Underlying(from); var ut = Underlying(to);
            int a = Scratch(ValType.I32); E.Set(a);
            int res = TempStruct(to);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(to).size); E.Op(Op.MemoryFill);
            E.Get(a); NullableHas(from);
            E.If();
            E.Get(a); NullableVal(from);
            var ov = Own.None; ConvertValue(uf, ut, isChecked, ref ov, true, null);
            WrapNullable(to, ov);
            int w = Scratch(ValType.I32); E.Set(w);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(to);
            FreeScratch(ValType.I32, w);
            E.End();
            FreeScratch(ValType.I32, a);
            E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
            o = Own.None;
            return true;
        }
        if (tn && !from.IsValueType)
        {
            UnboxNullable(to);
            o = Own.None;
            return true;
        }
        if (tn)
        {
            // T -> T? (after converting to the underlying type)
            var ut = Underlying(to);
            ConvertValue(from, ut, isChecked, ref o, true, null);
            WrapNullable(to, o);
            o = Own.None;
            return true;
        }
        // from nullable to non-nullable
        var u = Underlying(from);
        int addr = Scratch(ValType.I32); E.Set(addr);
        if (Types.IsRef(to))
        {
            // boxing: null when empty, else a box of the value
            E.Get(addr); NullableHas(from);
            E.If(ValType.I32);
            E.Get(addr); NullableVal(from);
            var bo = Own.None;
            ConvertValue(u, to, false, ref bo, false, null);
            ToOwned(bo);
            E.Else(); E.I32(0); E.End();
            FreeScratch(ValType.I32, addr);
            o = Own.Owned;
            return true;
        }
        // explicit (T)nullable: throws when empty
        E.Get(addr); NullableHas(from); E.Op(Op.I32Eqz);
        E.If(); E.I32Addr(D.StringLiteral("Cannot read the Value property of a Nullable object that has no value. Check HasValue before reading Value.")); E.Call(D.LibMethod("Il2Wasm.Runtime.ThrowHelpers", "InvalidOperation", 1).Fn); E.End();
        E.Get(addr); NullableVal(from);
        FreeScratch(ValType.I32, addr);
        o = Types.IsRef(u) ? Own.Unstable : Own.None;
        ConvertValue(u, to, isChecked, ref o, true, null);
        return true;
    }

    /// <summary>Unboxing to T?: null gives an empty T?.</summary>
    void UnboxNullable(ITypeSymbol nt)
    {
        var u = Underlying(nt);
        int obj = Scratch(ValType.I32); E.Set(obj);
        int res = TempStruct(nt);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(nt).size); E.Op(Op.MemoryFill);
        E.Get(obj);
        E.If();
        E.Get(obj); EmitUnbox(u);
        WrapNullable(nt, Own.None);
        int w = Scratch(ValType.I32); E.Set(w);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(nt);
        FreeScratch(ValType.I32, w);
        E.End();
        FreeScratch(ValType.I32, obj);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
    }

    /// <summary>Evaluates an operand of a lifted operator into a local holding the address of a T?.</summary>
    int LiftedOperand(IOperation op, ITypeSymbol nt)
    {
        if (op.Type == null) { EmitDefault(nt); int z = E.AddLocal(ValType.I32); E.Set(z); return z; }
        var t = TypeOf(op);
        var o = EmitValue(op);
        if (!IsNullable(t)) ConvertValue(t, nt, false, ref o, false, null);
        int l = E.AddLocal(ValType.I32);
        E.Set(l);
        return l;
    }

    Own EmitLiftedBinary(IBinaryOperation b)
    {
        // x == null, x != null: whether it has a value (no operator runs, user-defined or not)
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && b.OperatorMethod == null)
        {
            static bool NullLit(IOperation o)
            {
                while (o is IConversionOperation c) o = c.Operand;
                return o.ConstantValue is { HasValue: true, Value: null } || o is IDefaultValueOperation;
            }
            var side = NullLit(b.RightOperand) ? b.LeftOperand : NullLit(b.LeftOperand) ? b.RightOperand : null;
            if (side != null && side.Type != null && IsNullable(TypeOf(side)))
            {
                var st = TypeOf(side);
                int sa = LiftedOperand(side, st);
                E.Get(sa); NullableHas(st);
                if (b.OperatorKind == BinaryOperatorKind.Equals) E.Op(Op.I32Eqz);
                return Own.None;
            }
        }
        var lt = b.LeftOperand.Type != null ? TypeOf(b.LeftOperand) : null;
        var rt = b.RightOperand.Type != null ? TypeOf(b.RightOperand) : null;
        var nt = lt != null && IsNullable(lt) ? lt : rt!;
        var ntR = rt != null && IsNullable(rt) ? rt : nt;
        if (!IsNullable(nt)) nt = ntR;
        var resT = TypeOf(b);
        int la = LiftedOperand(b.LeftOperand, nt);
        int ra = LiftedOperand(b.RightOperand, ntR);
        var u = Underlying(nt);
        var kind = b.OperatorKind;
        void Vals()
        {
            E.Get(la); NullableVal(nt);
            E.Get(ra); NullableVal(ntR);
        }
        if (kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
        {
            // equal when both empty, or both have equal values
            E.Get(la); NullableHas(nt); E.Get(ra); NullableHas(ntR); E.Op(Op.I32Eq);
            E.If(ValType.I32);
            E.Get(la); NullableHas(nt);
            E.If(ValType.I32);
            EmitUnderlyingOp(b, u, BinaryOperatorKind.Equals, Vals);
            E.Else(); E.I32(1); E.End();
            E.Else(); E.I32(0); E.End();
            if (kind == BinaryOperatorKind.NotEquals) E.Op(Op.I32Eqz);
            return Own.None;
        }
        if (kind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)
        {
            E.Get(la); NullableHas(nt); E.Get(ra); NullableHas(ntR); E.Op(Op.I32And);
            E.If(ValType.I32);
            EmitUnderlyingOp(b, u, kind, Vals);
            E.Else(); E.I32(0); E.End();
            return Own.None;
        }
        if (Types.PrimOf(u) == Prim.Bool && kind is BinaryOperatorKind.And or BinaryOperatorKind.Or)
        {
            // Three values (C# 12.13.5): false & anything is false, true | anything is true, whatever the other
            // is; otherwise null when either is null.
            int dom = kind == BinaryOperatorKind.And ? 0 : 1;
            int r3 = TempStruct(resT);
            E.Get(_fb); E.I32(r3); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(resT).size); E.Op(Op.MemoryFill);
            void Wrap(int v)
            {
                E.I32(v);
                WrapNullable(resT, Own.None);
                int ws = Scratch(ValType.I32); E.Set(ws);
                E.Get(_fb); E.I32(r3); E.Op(Op.I32Add); E.Get(ws); CopyStructInit(resT);
                FreeScratch(ValType.I32, ws);
            }
            void Decides(int addr, ITypeSymbol t)
            {
                E.Get(addr); NullableHas(t);
                E.If(ValType.I32); E.Get(addr); NullableVal(t); E.I32(dom); E.Op(Op.I32Eq); E.Else(); E.I32(0); E.End();
            }
            Decides(la, nt); Decides(ra, ntR); E.Op(Op.I32Or);
            E.If();
            Wrap(dom);
            E.Else();
            E.Get(la); NullableHas(nt); E.Get(ra); NullableHas(ntR); E.Op(Op.I32And);
            E.If(); Wrap(1 - dom); E.End();
            E.End();
            E.Get(_fb); E.I32(r3); E.Op(Op.I32Add);
            return Own.None;
        }
        // arithmetic: has && has ? value : null (the slot may hold the value of an earlier turn of a loop)
        int res = TempStruct(resT);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(resT).size); E.Op(Op.MemoryFill);
        E.Get(la); NullableHas(nt); E.Get(ra); NullableHas(ntR); E.Op(Op.I32And);
        E.If();
        EmitUnderlyingOp(b, u, kind, Vals);
        var ru = Underlying(resT);
        WrapNullable(resT, Types.IsRef(ru) ? Own.Owned : Own.None);
        int w = Scratch(ValType.I32); E.Set(w);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(w); CopyStructInit(resT);
        FreeScratch(ValType.I32, w);
        E.End();
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
        return Own.None;
    }

    void EmitUnderlyingOp(IBinaryOperation b, ITypeSymbol u, BinaryOperatorKind kind, Action pushVals)
    {
        if (b.OperatorMethod != null)
        {
            var m = SubstMethod(b.OperatorMethod);
            CallWith(m, false, null, pushVals, null, b.Syntax);
            return;
        }
        pushVals();
        var p = Types.PrimOf(u);
        if (p == Prim.None) throw new CompileException("lifted operator on " + u, b.Syntax.GetLocation());
        EmitArith(kind, p, b.IsChecked, p);
    }

    Own EmitLiftedUnary(IUnaryOperation un)
    {
        var nt = TypeOf(un.Operand);
        var resT = TypeOf(un);
        int a = LiftedOperand(un.Operand, nt);
        var u = Underlying(nt);
        int res = TempStruct(resT);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.I32(0); E.I32(T.StorageOf(resT).size); E.Op(Op.MemoryFill);
        E.Get(a); NullableHas(nt);
        E.If();
        if (un.OperatorMethod != null) CallWith(SubstMethod(un.OperatorMethod), false, null, () => { E.Get(a); NullableVal(nt); }, null, un.Syntax);
        else
        {
            var p = Types.PrimOf(u); var w = Types.WasmPrim(p);
            switch (un.OperatorKind)
            {
                case UnaryOperatorKind.Minus:
                    if (Types.IsFloat(p)) { E.Get(a); NullableVal(nt); E.Op(w == ValType.F32 ? Op.F32Neg : Op.F64Neg); }
                    else { if (w == ValType.I64) E.I64(0); else E.I32(0); E.Get(a); NullableVal(nt); EmitArith(BinaryOperatorKind.Subtract, p, un.IsChecked, p); }
                    break;
                case UnaryOperatorKind.Plus: E.Get(a); NullableVal(nt); break;
                case UnaryOperatorKind.Not: E.Get(a); NullableVal(nt); E.Op(Op.I32Eqz); break;
                case UnaryOperatorKind.BitwiseNegation:
                    E.Get(a); NullableVal(nt);
                    if (w == ValType.I64) { E.I64(-1); E.Op(Op.I64Xor); } else { E.I32(-1); E.Op(Op.I32Xor); }
                    break;
                default: throw new CompileException("lifted unary " + un.OperatorKind, un.Syntax.GetLocation());
            }
        }
        WrapNullable(resT, Own.None);
        int wv = Scratch(ValType.I32); E.Set(wv);
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add); E.Get(wv); CopyStructInit(resT);
        FreeScratch(ValType.I32, wv);
        E.End();
        E.Get(_fb); E.I32(res); E.Op(Op.I32Add);
        return Own.None;
    }

    /// <summary>x ?? y with a nullable value type on the left.</summary>
    Own EmitNullableCoalesce(ICoalesceOperation c)
    {
        var nt = TypeOf(c.Value);
        var rt = TypeOf(c);
        int a = LiftedOperand(c.Value, nt);
        var wt = Types.WasmType(rt) ?? ValType.I32;
        E.Get(a); NullableHas(nt);
        E.If(wt);
        if (IsNullable(rt)) { E.Get(a); }
        else
        {
            E.Get(a); NullableVal(nt);
            var o = Types.IsRef(Underlying(nt)) ? Own.Unstable : Own.None;
            ConvertTop(Underlying(nt), rt, ref o);
            if (Types.IsRef(rt)) ToOwned(o);
        }
        E.Else();
        var o2 = EmitValueAs(c.WhenNull, rt);
        if (Types.IsRef(rt)) ToOwned(o2);
        E.End();
        return Types.IsRef(rt) ? Own.Owned : Own.None;
    }
}

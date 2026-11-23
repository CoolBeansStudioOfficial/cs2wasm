using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Il2Wasm.Compiler;

// ValueType.Equals for a struct that does not override it, as .NET defines it: when the struct can be compared bit by
// bit (no references, no floating point, no padding, no field whose type overrides Equals) its bytes are compared
// (the library's InternalEquals); otherwise each field is compared with its own Equals (null equal only to null), as
// CoreCLR's reflection over the fields does. The compiler knows the fields, so the field-wise case is a function per
// struct, called from the dispatch stub where ValueType.Equals would be.
sealed partial class FunctionCompiler
{
    ITypeSymbol? _valueEqualsType;

    internal static MethodInst ValueEquals(Driver d, ITypeSymbol t, IMethodSymbol slot)
    {
        var key = "valuetype equals " + Types.Key(t);
        if (d.ValueEqualsFns.TryGetValue(key, out var mi)) return mi;
        var fn = d.Module.AddFunction(key, Driver.Signature(slot, d.Types));
        mi = new MethodInst { Sym = slot, Fn = fn, Key = key, Impl = MethodImpl.Synthesized };
        var m = mi;
        mi.Compile = () => { var fc = new FunctionCompiler(d, fn, slot, m) { _valueEqualsType = t }; fc.CompileMethod(); };
        d.ValueEqualsFns[key] = mi;
        d.EnqueueExtra(mi);
        return mi;
    }

    /// <summary>CoreCLR's CanCompareBits: the struct's bytes decide its equality.</summary>
    internal static bool CanCompareBits(Types types, ITypeSymbol t)
    {
        var layout = types.LayoutOf(t);
        int sum = 0;
        foreach (var f in layout.Fields)
        {
            var ft = f.Type;
            if (Types.IsRef(ft)) return false;
            var p = Types.PrimOf(ft);
            if (p != Prim.None && Types.IsFloat(p)) return false;
            if (p == Prim.None && Types.IsStruct(ft))
            {
                if (ft.GetMembers("Equals").OfType<IMethodSymbol>().Any(m => m.IsOverride && m.Parameters.Length == 1)) return false;
                if (!CanCompareBits(types, ft)) return false;
            }
            sum += types.StorageOf(ft).size;
        }
        return sum == types.StorageOf(t).size;
    }

    void EmitValueEquals()
    {
        var t = _valueEqualsType!;
        var fields = T.LayoutOf(t).Fields;
        void Load(int obj, FieldSlot f)
        {
            E.Get(obj);
            int off = Types.ObjectHeader + f.Offset;
            if (Types.IsStruct(f.Type)) { E.I32(off); E.Op(Op.I32Add); } else E.Load(Types.LoadOp(f.Type), off);
        }
        var other = _vars[M.Parameters[0]];
        var done = E.Block(ValType.I32);
        int o2 = E.AddLocal(ValType.I32);
        LoadVar(other); E.Set(o2);
        E.Get(o2); E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
        // (the same type: a boxed value of another type is not equal)
        E.Get(o2); E.Load(Op.I32Load, 4); E.Get(_thisLocal); E.Load(Op.I32Load, 4); E.Op(Op.I32Ne); E.If(); E.I32(0); E.Br(done); E.End();
        foreach (var f in fields)
        {
            Load(_thisLocal, f);
            Load(o2, f);
            E.Call(Rt.GenericEquals(f.Type));
            E.Op(Op.I32Eqz); E.If(); E.I32(0); E.Br(done); E.End();
        }
        E.I32(1);
        E.End();
        _retLocal = E.AddLocal(ValType.I32); E.Set(_retLocal);
        EmitReturnSequence(true);
    }
}

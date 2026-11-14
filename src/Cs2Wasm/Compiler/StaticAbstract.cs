using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Static abstract and static virtual interface members (C# 11), after monomorphization: `T.Zero`, `a + b` on a
// `T : IMonoid<T>`, generic math (`INumber<T>`). The member is the interface's; its implementation is the type
// argument's. For the primitive types, the generic math interfaces of System.Numerics are lowered to the primitive
// operations directly (the library's primitives do not declare those interfaces).
sealed partial class FunctionCompiler
{
    static bool IsStaticInterfaceMember(IMethodSymbol m) =>
        m.IsStatic && m.ContainingType?.TypeKind == TypeKind.Interface && (m.IsAbstract || m.IsVirtual);

    /// <summary>The type a static interface member is called on: the interface's type argument that implements it
    /// (TSelf in INumber&lt;TSelf&gt;, T in IMonoid&lt;T&gt;).</summary>
    ITypeSymbol? ConstrainedTarget(IMethodSymbol m)
    {
        var iface = m.ContainingType;
        foreach (var a in iface.TypeArguments)
        {
            var t = T.Canonical(a);
            if (t is ITypeParameterSymbol) continue;
            if (Types.PrimOf(t) != Prim.None) return t;
            if (t.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iface.OriginalDefinition))) return t;
        }
        return null;
    }

    /// <summary>Resolves a static interface member to its implementation; true when it was emitted as primitive code.</summary>
    bool TryStaticInterfaceMember(ref IMethodSymbol m, out Own result)
    {
        result = Own.None;
        var target = ConstrainedTarget(m);
        if (target == null) return false;
        var p = Types.PrimOf(target);
        if (p != Prim.None && m.ContainingType.ContainingNamespace?.ToDisplayString() == "System.Numerics") return EmitGenericMathPrim(m, target, p, out result);
        var impl = D.Implementation(target, m);
        if (impl != null) m = impl;
        return false;
    }

    bool EmitGenericMathPrim(IMethodSymbol m, ITypeSymbol t, Prim p, out Own result)
    {
        result = Own.None;
        bool f = Types.IsFloat(p);
        void Const(double v)
        {
            switch (Types.WasmPrim(p))
            {
                case ValType.I64: E.I64((long)v); break;
                case ValType.F32: E.F32((float)v); break;
                case ValType.F64: E.F64(v); break;
                default: E.I32((int)v); break;
            }
        }
        switch (m.Name)
        {
            case "get_Zero": case "get_AdditiveIdentity": Const(0); return true;
            case "get_One": case "get_MultiplicativeIdentity": Const(1); return true;
            case "get_NegativeOne": Const(-1); return true;
            case "get_MinValue" or "get_MaxValue":
                {
                    var field = T.Canonical(t).GetMembers(m.Name[4..]).FirstOrDefault();
                    if (field is IFieldSymbol { HasConstantValue: true } cf) { result = EmitConstant(cf.ConstantValue, t); return true; }
                    break;
                }
            case "op_Addition": EmitArith(BinaryOperatorKind.Add, p, false, p); return true;
            case "op_CheckedAddition": EmitArith(BinaryOperatorKind.Add, p, true, p); return true;
            case "op_Subtraction": EmitArith(BinaryOperatorKind.Subtract, p, false, p); return true;
            case "op_CheckedSubtraction": EmitArith(BinaryOperatorKind.Subtract, p, true, p); return true;
            case "op_Multiply": EmitArith(BinaryOperatorKind.Multiply, p, false, p); return true;
            case "op_CheckedMultiply": EmitArith(BinaryOperatorKind.Multiply, p, true, p); return true;
            case "op_Division": EmitArith(BinaryOperatorKind.Divide, p, false, p); return true;
            case "op_Modulus": EmitArith(BinaryOperatorKind.Remainder, p, false, p); return true;
            case "op_BitwiseAnd": EmitArith(BinaryOperatorKind.And, p, false, p); return true;
            case "op_BitwiseOr": EmitArith(BinaryOperatorKind.Or, p, false, p); return true;
            case "op_ExclusiveOr": EmitArith(BinaryOperatorKind.ExclusiveOr, p, false, p); return true;
            case "op_LeftShift": EmitArith(BinaryOperatorKind.LeftShift, p, false, p); return true;
            case "op_RightShift": EmitArith(BinaryOperatorKind.RightShift, p, false, p); return true;
            case "op_Equality": EmitCompare(BinaryOperatorKind.Equals, p); return true;
            case "op_Inequality": EmitCompare(BinaryOperatorKind.NotEquals, p); return true;
            case "op_LessThan": EmitCompare(BinaryOperatorKind.LessThan, p); return true;
            case "op_LessThanOrEqual": EmitCompare(BinaryOperatorKind.LessThanOrEqual, p); return true;
            case "op_GreaterThan": EmitCompare(BinaryOperatorKind.GreaterThan, p); return true;
            case "op_GreaterThanOrEqual": EmitCompare(BinaryOperatorKind.GreaterThanOrEqual, p); return true;
            case "op_UnaryPlus": return true;
            case "op_UnaryNegation":
                {
                    int v = Scratch(Types.WasmPrim(p)); E.Set(v); Const(0); E.Get(v); FreeScratch(Types.WasmPrim(p), v);
                    if (f) { E.Get(v); E.Op(Types.WasmPrim(p) == ValType.F32 ? Op.F32Neg : Op.F64Neg); int r = Scratch(Types.WasmPrim(p)); E.Set(r); E.Op(Op.Drop); E.Op(Op.Drop); E.Get(r); FreeScratch(Types.WasmPrim(p), r); return true; }
                    EmitArith(BinaryOperatorKind.Subtract, p, false, p); return true;
                }
            case "op_Increment": Const(1); EmitArith(BinaryOperatorKind.Add, p, false, p); return true;
            case "op_Decrement": Const(1); EmitArith(BinaryOperatorKind.Subtract, p, false, p); return true;
        }
        // the rest by name on the primitive itself, then on Math (Abs, Max, Min, Sign, Clamp, IsNegative, ...)
        var prim = (INamedTypeSymbol)T.Canonical(t);
        var direct = prim.GetMembers(m.Name).OfType<IMethodSymbol>().FirstOrDefault(x => x.IsStatic && x.Parameters.Length == m.Parameters.Length && !x.IsGenericMethod);
        direct ??= D.LibType("System.Math").GetMembers(m.Name).OfType<IMethodSymbol>().FirstOrDefault(x => x.Parameters.Length == m.Parameters.Length && x.Parameters.All(pp => Types.Key(T.Canonical(pp.Type)) == Types.Key(prim)));
        if (direct == null) throw new CompileException($"generic math member {m.Name} on {t} is not supported yet");
        result = EmitCallInstr(D.CanonicalMethod(direct), false, null, -1);
        return true;
    }
}

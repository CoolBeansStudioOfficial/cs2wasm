using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>
/// C# function pointers (`delegate*&lt;int, void&gt;`): a pointer is the method's index in the module's function table
/// (`&amp;M`), and an invocation is a call_indirect with the pointer type's signature. The WASM engine checks the
/// signature at the call, as the CLR does not.
/// </summary>
sealed partial class FunctionCompiler
{
    /// <summary>`&amp;M` for a static method: its table index.</summary>
    Own? TryEmitMethodAddress(IOperation op)
    {
        if (op is not IAddressOfOperation { Reference: IMethodReferenceOperation mr }) return null;
        var m = SubstMethod(mr.Method);
        if (!m.IsStatic) throw new CompileException("a function pointer to an instance method", op.Syntax.GetLocation());
        E.I32(D.Module.TableSlot(D.Method(m).Fn));
        return Own.None;
    }

    Own EmitFunctionPointerInvocation(IFunctionPointerInvocationOperation fpi)
    {
        var fpt = (IFunctionPointerTypeSymbol)TypeOf(fpi.Target);
        var sig = fpt.Signature;
        var rt = T.Resolve(sig.ReturnType, Map);
        bool sret = !sig.ReturnsByRef && !sig.ReturnsByRefReadonly && Types.IsStruct(rt);
        int sretOff = -1;
        if (sret) { sretOff = TempStruct(rt); E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); }
        var args = fpi.Arguments.Cast<IOperation>().ToList();
        for (int i = 0; i < sig.Parameters.Length; i++)
        {
            var a = args.OfType<IArgumentOperation>().FirstOrDefault(x => x.Parameter?.Ordinal == i)?.Value ?? (i < args.Count ? (args[i] as IArgumentOperation)?.Value ?? args[i] : null);
            PushArg(sig.Parameters[i], a);
        }
        EmitValue(fpi.Target);
        var ps = new List<ValType>();
        if (sret) ps.Add(ValType.I32);
        foreach (var p in sig.Parameters) ps.Add(p.RefKind != RefKind.None ? ValType.I32 : Types.WasmType(T.Resolve(p.Type, Map)) ?? ValType.I32);
        var ret = sig.ReturnsByRef || sig.ReturnsByRefReadonly ? ValType.I32 : Types.WasmType(rt);
        E.Emit(Op.CallIndirect, 0, 0, new FuncType([.. ps], sret || ret == null ? [] : [ret.Value]));
        if (sig.ReturnsByRef || sig.ReturnsByRefReadonly)
        {
            if (_wantRefReturnAddress) { _wantRefReturnAddress = false; return Own.None; }
            if (Types.IsStruct(rt)) return Own.None;
            E.Load(Types.LoadOp(rt));
            return Types.IsRef(rt) ? Own.Unstable : Own.None;
        }
        if (sret) { E.Get(_fb); E.I32(sretOff); E.Op(Op.I32Add); return Own.None; }
        if (Types.IsVoid(rt)) return Own.None;
        return Types.IsRef(rt) ? Own.Owned : Own.None;
    }
}

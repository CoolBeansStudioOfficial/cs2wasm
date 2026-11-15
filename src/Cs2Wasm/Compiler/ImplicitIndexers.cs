using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>`x[^i]` and `x[a..b]` on a type with a Length (or Count) and an int indexer (or Slice/Substring), as C#
/// lowers them: the receiver once, its length, the offset(s) from the Index or Range, then the member.</summary>
sealed partial class FunctionCompiler
{
    Own EmitImplicitIndexer(IImplicitIndexerReferenceOperation ii, bool wantAddress)
    {
        var rt = TypeOf(ii.Instance);
        // the receiver, held for both calls: a struct's address, or a reference kept for the statement
        int recv = E.AddLocal(ValType.I32);
        if (rt.IsValueType) PushValueTypeReceiver(ii.Instance, rt);
        else
        {
            var o = EmitValue(ii.Instance);
            ToCallSafe(o, rt);
            if (!IsKnownNonNull(ii.Instance)) NullCheckKeep();
        }
        E.Set(recv);
        var lengthGetter = ii.LengthSymbol switch
        {
            IPropertySymbol p => p.GetMethod!,
            IMethodSymbol m => m,
            _ => throw new CompileException("implicit indexer without a length", ii.Syntax.GetLocation()),
        };
        int len = E.AddLocal(ValType.I32);
        CallWith(SubstMethod(lengthGetter), !rt.IsValueType, () => E.Get(recv), () => { }, null, ii.Syntax);
        E.Set(len);
        var member = ii.IndexerSymbol switch
        {
            IPropertySymbol p => p.GetMethod!,
            IMethodSymbol m => m,
            _ => throw new CompileException("implicit indexer without a member", ii.Syntax.GetLocation()),
        };
        member = SubstMethod(member);
        if (TypeOf(ii.Argument) is INamedTypeSymbol { Name: "Range" })
        {
            // Slice(start, end - start)
            int start = E.AddLocal(ValType.I32), end = E.AddLocal(ValType.I32);
            if (ii.Argument is IRangeOperation ro)
            {
                EmitIndexOffset(ro.LeftOperand, len, fromStartDefault: true); E.Set(start);
                EmitIndexOffset(ro.RightOperand, len, fromStartDefault: false); E.Set(end);
            }
            else
            {
                var range = (INamedTypeSymbol)TypeOf(ii.Argument);
                int ra = E.AddLocal(ValType.I32);
                EmitAddress(ii.Argument); E.Set(ra);
                var indexT = (INamedTypeSymbol)range.GetMembers("Start").OfType<IPropertySymbol>().First().Type;
                var getOffset = indexT.GetMembers("GetOffset").OfType<IMethodSymbol>().First();
                foreach (var (name, dst) in new[] { ("Start", start), ("End", end) })
                {
                    var prop = range.GetMembers(name).OfType<IPropertySymbol>().First();
                    int ia = TempStruct(indexT);
                    CallWith(prop.GetMethod!, false, () => E.Get(ra), () => { }, () => { E.Get(_fb); E.I32(ia); E.Op(Op.I32Add); }, ii.Syntax);
                    E.Op(Op.Drop);
                    CallWith(getOffset, false, () => { E.Get(_fb); E.I32(ia); E.Op(Op.I32Add); }, () => E.Get(len), null, ii.Syntax);
                    E.Set(dst);
                }
            }
            var save = _wantRefReturnAddress; _wantRefReturnAddress = wantAddress;
            try
            {
                return CallWith(member, !rt.IsValueType, () => E.Get(recv), () => { E.Get(start); E.Get(end); E.Get(start); E.Op(Op.I32Sub); }, null, ii.Syntax);
            }
            finally { _wantRefReturnAddress = save; }
        }
        int idx = E.AddLocal(ValType.I32);
        EmitIndexOffset(ii.Argument, len, fromStartDefault: true); E.Set(idx);
        {
            var save = _wantRefReturnAddress; _wantRefReturnAddress = wantAddress;
            try { return CallWith(member, !rt.IsValueType, () => E.Get(recv), () => E.Get(idx), null, ii.Syntax); }
            finally { _wantRefReturnAddress = save; }
        }
    }

    /// <summary>The int offset an Index denotes in a sequence of length `len` (`^n` is len - n); a missing range end
    /// is 0 (start) or len (end).</summary>
    void EmitIndexOffset(IOperation? index, int len, bool fromStartDefault)
    {
        if (index == null) { if (fromStartDefault) E.I32(0); else E.Get(len); return; }
        if (index is IConversionOperation { OperatorMethod: { Name: "op_Implicit" } } c && Types.PrimOf(TypeOf(c.Operand)) != Prim.None) index = c.Operand;
        if (index is IUnaryOperation { OperatorKind: UnaryOperatorKind.Hat } hat)
        {
            E.Get(len);
            EmitValueAs(hat.Operand, D.LibType("System.Int32"));
            E.Op(Op.I32Sub);
            return;
        }
        var t = TypeOf(index);
        if (Types.PrimOf(t) != Prim.None) { EmitValueAs(index, D.LibType("System.Int32")); return; }
        // an Index value: Index.GetOffset(len)
        var getOffset = ((INamedTypeSymbol)t).GetMembers("GetOffset").OfType<IMethodSymbol>().First();
        CallWith(getOffset, false, () => EmitAddress(index), () => E.Get(len), null, index.Syntax);
    }
}

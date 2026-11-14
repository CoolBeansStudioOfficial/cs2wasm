using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>Tuples (ValueTuple structs), tuple conversions and equality, deconstruction.</summary>
sealed partial class FunctionCompiler
{
    static bool IsValueTuple(ITypeSymbol t) => t is INamedTypeSymbol n && (n.IsTupleType || (Types.FullName(n).StartsWith("System.ValueTuple`") && n.ContainingType == null));

    List<IFieldSymbol> TupleFields(ITypeSymbol tupleType)
    {
        var lt = (INamedTypeSymbol)T.Canonical(tupleType);
        var l = new List<IFieldSymbol>();
        for (int i = 1; i <= lt.Arity; i++) l.Add(lt.GetMembers("Item" + i).OfType<IFieldSymbol>().First());
        if (lt.Arity > 7) throw new CompileException("tuples with more than 7 elements are not supported yet");
        return l;
    }

    /// <summary>A tuple literal: a temporary ValueTuple with each element stored.</summary>
    Own EmitTuple(ITupleOperation tup, ITypeSymbol? target = null)
    {
        var tt = T.Canonical(target ?? TypeOf(tup));
        var fields = TupleFields(tt);
        int off = TempStruct(tt);
        var layout = T.LayoutOf(tt);
        for (int i = 0; i < tup.Elements.Length; i++)
        {
            var slot = T.FieldIn(tt, fields[i]);
            var ft = slot.Type;
            var el = tup.Elements[i];
            if (el is ITupleOperation inner && IsValueTuple(ft)) { EmitTuple(inner, ft); }
            else if (el.Type == null) EmitValue(el); // null literal / default
            else { var o = EmitValueAs(el, ft); StoreToAddressFromStack(() => E.Get(_fb), off + slot.Offset, ft, o); continue; }
            StoreToAddressFromStack(() => E.Get(_fb), off + slot.Offset, ft, Types.IsRef(ft) ? Own.Stable : Own.None);
        }
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        return Own.None;
    }

    /// <summary>Stack: address of a tuple of type `from` → address of a new tuple of type `to` (element-wise conversion).</summary>
    void ConvertTuple(ITypeSymbol from, ITypeSymbol to)
    {
        int src = Scratch(ValType.I32); E.Set(src);
        var ff = TupleFields(from); var tf = TupleFields(to);
        int off = TempStruct(to);
        for (int i = 0; i < tf.Count; i++)
        {
            var fs = T.FieldIn(from, ff[i]); var ts = T.FieldIn(to, tf[i]);
            E.Get(src);
            Own o;
            if (Types.IsStruct(fs.Type)) { if (fs.Offset != 0) { E.I32(fs.Offset); E.Op(Op.I32Add); } o = Own.None; }
            else { E.Load(Types.LoadOp(fs.Type), fs.Offset); o = Types.IsRef(fs.Type) ? Own.Unstable : Own.None; }
            ConvertTop(fs.Type, ts.Type, ref o);
            StoreToAddressFromStack(() => E.Get(_fb), off + ts.Offset, ts.Type, o);
        }
        FreeScratch(ValType.I32, src);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
    }

    Own EmitTupleBinary(ITupleBinaryOperation tb)
    {
        var lt = TypeOf(tb.LeftOperand); var rt = TypeOf(tb.RightOperand);
        EmitValue(tb.LeftOperand); int la = E.AddLocal(ValType.I32); E.Set(la);
        EmitValue(tb.RightOperand); int ra = E.AddLocal(ValType.I32); E.Set(ra);
        var lf = TupleFields(lt); var rf = TupleFields(rt);
        // all elements equal (evaluated left to right, short-circuit like C#)
        var done = E.Block(ValType.I32);
        for (int i = 0; i < lf.Count; i++)
        {
            var ls = T.FieldIn(lt, lf[i]); var rs = T.FieldIn(rt, rf[i]);
            var et = ls.Type;
            void Push(int a, FieldSlot s)
            {
                E.Get(a);
                if (Types.IsStruct(s.Type)) { if (s.Offset != 0) { E.I32(s.Offset); E.Op(Op.I32Add); } }
                else E.Load(Types.LoadOp(s.Type), s.Offset);
            }
            Push(la, ls);
            Push(ra, rs);
            if (Types.Key(rs.Type) != Types.Key(et)) { var o = Own.None; ConvertTop(rs.Type, et, ref o); }
            if (Types.IsString(et)) E.Call(D.LibMethod("System.String", "Equals", 2).Fn);
            else if (Types.PrimOf(et) != Prim.None) EmitCompare(Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Equals, et);
            else E.Call(Rt.GenericEquals(et));
            E.Op(Op.I32Eqz);
            E.If(); E.I32(tb.OperatorKind == Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Equals ? 0 : 1); E.Br(done); E.End();
        }
        E.I32(tb.OperatorKind == Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Equals ? 1 : 0);
        E.End();
        return Own.None;
    }

    // ---------------- deconstruction ----------------
    void EmitDeconstructionCore(IDeconstructionAssignmentOperation da)
    {
        var target = da.Target is IDeclarationExpressionOperation de ? de.Expression : da.Target;
        if (target is not ITupleOperation tt) throw new CompileException("unsupported deconstruction target", da.Syntax.GetLocation());
        var info = D.ModelFor(da.Syntax.SyntaxTree).GetDeconstructionInfo((AssignmentExpressionSyntax)da.Syntax);
        var vt = TypeOf(da.Value);
        BeginTemps();
        // evaluate the right side completely first
        int src;
        if (da.Value is ITupleOperation lit && info.Method == null) { EmitTuple(lit); }
        else EmitValue(da.Value);
        src = E.AddLocal(ValType.I32);
        if (Types.IsRef(vt)) { /* object with Deconstruct */ }
        E.Set(src);
        DeconstructInto(tt, vt, src, info);
        EndTemps();
    }

    /// <summary>Assigns the parts of the value at local `src` (a tuple address, or a reference/struct with Deconstruct) to the targets.</summary>
    void DeconstructInto(ITupleOperation targets, ITypeSymbol vt, int src, DeconstructionInfo info)
    {
        var parts = new List<(int local, ITypeSymbol type)>();
        if (info.Method != null)
        {
            // call Deconstruct(out a, out b, ...) into frame temporaries
            var m = SubstMethod(info.Method);
            var temps = new List<(int off, ITypeSymbol type)>();
            foreach (var p in m.Parameters)
            {
                var pt = T.Canonical(p.Type);
                int off = Types.IsStruct(pt) ? TempStruct(pt) : TempValue(pt);
                temps.Add((off, pt));
            }
            Action? recv = m.IsStatic ? null : () => E.Get(src);
            CallWith(m, !vt.IsValueType && IsVirtualDispatch(m), recv, () =>
            {
                if (m.IsStatic) E.Get(src); // extension Deconstruct(this T x, out ...)
                foreach (var (off, _) in temps) { E.Get(_fb); E.I32(off); E.Op(Op.I32Add); }
            }, null, null);
            int skip = m.IsStatic ? 1 : 0;
            temps = temps.Skip(skip).ToList();
            for (int i = 0; i < temps.Count; i++)
            {
                var (off, pt) = temps[i];
                int l = E.AddLocal(ValType.I32);
                E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(l);
                parts.Add((l, pt));
            }
            AssignParts(targets, parts, info.Nested, byAddress: true);
            return;
        }
        var fields = TupleFields(vt);
        for (int i = 0; i < fields.Count; i++)
        {
            var slot = T.FieldIn(vt, fields[i]);
            int l = E.AddLocal(ValType.I32);
            E.Get(src); if (slot.Offset != 0) { E.I32(slot.Offset); E.Op(Op.I32Add); } E.Set(l);
            parts.Add((l, slot.Type));
        }
        AssignParts(targets, parts, info.Nested, byAddress: true);
    }

    void AssignParts(ITupleOperation targets, List<(int local, ITypeSymbol type)> parts, System.Collections.Immutable.ImmutableArray<DeconstructionInfo> nested, bool byAddress)
    {
        for (int i = 0; i < targets.Elements.Length && i < parts.Count; i++)
        {
            var el = targets.Elements[i];
            if (el is IDeclarationExpressionOperation d) el = d.Expression;
            var (addr, pt) = parts[i];
            if (el is IDiscardOperation) continue;
            if (el is ITupleOperation inner)
            {
                // nested: the part is itself a tuple or deconstructible value
                int v = addr;
                if (!Types.IsStruct(pt)) { v = E.AddLocal(ValType.I32); E.Get(addr); E.Load(Op.I32Load); E.Set(v); }
                var ninfo = i < nested.Length ? nested[i] : default;
                DeconstructInto(inner, pt, v, ninfo);
                continue;
            }
            var lv = LValueOf(el);
            lv.Prepare();
            E.Get(addr);
            Own o;
            if (Types.IsStruct(pt)) o = Own.None;
            else { E.Load(Types.LoadOp(pt)); o = Types.IsRef(pt) ? Own.Unstable : Own.None; }
            ConvertTop(pt, lv.Type, ref o);
            lv.Store(o, false);
        }
    }
}

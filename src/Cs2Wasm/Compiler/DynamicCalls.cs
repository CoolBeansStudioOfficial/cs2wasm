using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>A dynamic operation compiles to a call of its call site's method (DynamicLowering.cs), with the
/// operation's operands as the arguments, in the order the site's method takes them.</summary>
sealed partial class FunctionCompiler
{
    Own? TryEmitDynamic(IOperation op)
    {
        switch (op)
        {
            case IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation or IDynamicObjectCreationOperation
                or ISimpleAssignmentOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                or ICompoundAssignmentOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                or IIncrementOrDecrementOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation }
                or IBinaryOperation or IUnaryOperation:
                if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } lb && D.Dynamic.Find(op, "test") is { } ts)
                    return EmitDynamicLogical(lb, ts, D.Dynamic.Find(op, "op")!);
                if (op is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } sb && D.Dynamic.Find(op, "box") is { } bs)
                    return EmitStaticLogical(sb, bs, D.Dynamic.Find(op, "op")!);
                return D.Dynamic.Find(op, "op") is { } s ? EmitDynamicSite(op, s) : null;
            case IConversionOperation c:
                return D.Dynamic.Find(c, "conv") is { } cs ? EmitDynamicSite(c, cs) : null;
        }
        return null;
    }

    /// <summary>The site method of a compound assignment or ++/-- on a variable (the operator's site; the
    /// variable is read and written as for a user-defined operator).</summary>
    IMethodSymbol? DynamicOperatorSite(IOperation op) =>
        D.Dynamic.Any && D.Dynamic.Find(op, "op") is { } s ? SiteMethod(s) : null;

    IMethodSymbol SiteMethod(DynamicLowering.Site s) =>
        D.Main.GetTypeByMetadataName(DynamicLowering.ClassName)!.GetMembers(s.Method).OfType<IMethodSymbol>().First();

    /// <summary>x && y on a dynamic x: the test site on x; when it holds, x, else the operator's site on x and y.</summary>
    Own EmitDynamicLogical(IBinaryOperation b, DynamicLowering.Site test, DynamicLowering.Site op)
    {
        var tm = SiteMethod(test); var om = SiteMethod(op);
        var obj = D.ObjectType;
        int x = E.AddLocal(ValType.I32);
        var xo = EmitValueAs(b.LeftOperand, obj); ToOwned(xo); E.Set(x);
        CallWith(tm, false, null, () => E.Get(x), null, b.Syntax);
        E.If(ValType.I32);
        E.Get(x); // the result owns x
        E.Else();
        var pt = T.Canonical(om.Parameters[1].Type);
        var r = CallWith(om, false, null, () => { E.Get(x); var ro = EmitValueAs(b.RightOperand, pt); if (Types.IsRef(pt)) ToCallSafe(ro, pt); }, null, b.Syntax);
        ToOwned(r);
        E.Get(x); E.Call(Rt.Release);
        E.End();
        return Own.Owned;
    }

    /// <summary>b && y on a static bool b: when b decides (false for &&, true for ||), b as dynamic; else the
    /// operator's site on b and y.</summary>
    Own EmitStaticLogical(IBinaryOperation b, DynamicLowering.Site box, DynamicLowering.Site op)
    {
        var bm = SiteMethod(box); var om = SiteMethod(op);
        bool and = b.OperatorKind == BinaryOperatorKind.ConditionalAnd;
        int x = E.AddLocal(ValType.I32);
        EmitValueAs(b.LeftOperand, bm.Parameters[0].Type);
        E.Set(x);
        E.Get(x); if (and) E.Op(Op.I32Eqz);
        E.If(ValType.I32);
        var r1 = CallWith(bm, false, null, () => E.Get(x), null, b.Syntax);
        ToOwned(r1);
        E.Else();
        var pt = T.Canonical(om.Parameters[1].Type);
        var r2 = CallWith(om, false, null, () => { E.Get(x); var ro = EmitValueAs(b.RightOperand, pt); if (Types.IsRef(pt)) ToCallSafe(ro, pt); }, null, b.Syntax);
        ToOwned(r2);
        E.End();
        return Own.Owned;
    }

    Own EmitDynamicSite(IOperation op, DynamicLowering.Site site)
    {
        var m = SiteMethod(site);
        var args = new List<IOperation>();
        // a receiver is an argument unless the site names its type (a static call)
        void Receiver(IOperation? instance)
        {
            if (instance == null) return;
            if (instance is IInstanceReferenceOperation { IsImplicit: true } && M.IsStatic) return;
            args.Add(instance);
        }
        void Target(IOperation t)
        {
            if (t is IDynamicMemberReferenceOperation mr) Receiver(mr.Instance);
            else if (t is IDynamicIndexerAccessOperation ia) { args.Add(ia.Operation); args.AddRange(ia.Arguments); }
        }
        switch (op)
        {
            case IDynamicInvocationOperation di:
                if (di.Operation is IDynamicMemberReferenceOperation imr) Receiver(imr.Instance); else args.Add(di.Operation);
                args.AddRange(di.Arguments);
                break;
            case IDynamicMemberReferenceOperation mr: Receiver(mr.Instance); break;
            case IDynamicIndexerAccessOperation ia: args.Add(ia.Operation); args.AddRange(ia.Arguments); break;
            case IDynamicObjectCreationOperation oc: args.AddRange(oc.Arguments); break;
            case ISimpleAssignmentOperation sa: Target(sa.Target); args.Add(sa.Value); break;
            case ICompoundAssignmentOperation ca: Target(ca.Target); args.Add(ca.Value); break;
            case IIncrementOrDecrementOperation inc: Target(inc.Target); break;
            case IBinaryOperation b: args.Add(b.LeftOperand); args.Add(b.RightOperand); break;
            case IUnaryOperation u: args.Add(u.Operand); break;
            case IConversionOperation c: args.Add(c.Operand); break;
        }
        if (args.Count != m.Parameters.Length)
            throw new CompileException($"dynamic: internal error: {args.Count} operands for a site of {m.Parameters.Length} parameters", op.Syntax.GetLocation());
        return EmitCall(m, null, args, false, op.Syntax);
    }
}

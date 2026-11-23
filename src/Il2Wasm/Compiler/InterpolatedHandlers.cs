using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>
/// Interpolated string handlers (C# 10): `$"..."` converted to a handler type (DefaultInterpolatedStringHandler for
/// string.Create, StringBuilder's AppendInterpolatedStringHandler, ...). As C# lowers it: the handler is constructed
/// (with the literal length, the hole count, the call's arguments it names, and an `out bool` saying whether to
/// go on), then each part is an Append call on it, stopping at the first that returns false. A call whose handler
/// argument names the call's receiver or other arguments evaluates them first, into locals (a spilled call).
/// </summary>
sealed partial class FunctionCompiler
{
    sealed class HandlerSite
    {
        public int Receiver = -1;
        public ITypeSymbol? ReceiverType;
        public required IMethodSymbol Method;
        public required int[] Args;
    }

    readonly List<HandlerSite> _handlerSites = [];
    readonly List<(int addrLocal, ITypeSymbol type, int successOffset)> _handlers = [];

    static bool HasHandlerPlaceholders(IOperation arg) =>
        arg.DescendantsAndSelf().OfType<IInterpolatedStringHandlerArgumentPlaceholderOperation>()
            .Any(p => p.PlaceholderKind != InterpolatedStringArgumentPlaceholderKind.TrailingValidityArgument);

    /// <summary>A call whose handler argument reads the receiver or other arguments: all of them into locals, in order,
    /// then the call.</summary>
    Own EmitSpilledCall(IMethodSymbol m, bool virt, Action? pushThis, IReadOnlyList<IOperation> args, SyntaxNode syntax)
    {
        var site = new HandlerSite { Method = m, Args = new int[m.Parameters.Length], ReceiverType = m.IsStatic ? null : T.Canonical(m.ContainingType) };
        if (pushThis != null)
        {
            pushThis();
            site.Receiver = E.AddLocal(ValType.I32);
            E.Set(site.Receiver);
        }
        _handlerSites.Add(site);
        try
        {
            PushArgs(m, args, i =>
            {
                var p = m.Parameters[i];
                var pt = T.Canonical(p.Type);
                var wt = p.RefKind != RefKind.None || Types.IsStruct(pt) ? ValType.I32 : Types.WasmType(pt) ?? ValType.I32;
                site.Args[i] = E.AddLocal(wt);
                E.Set(site.Args[i]);
            });
        }
        finally { _handlerSites.RemoveAt(_handlerSites.Count - 1); }
        return CallWith(m, virt, site.Receiver >= 0 ? () => E.Get(site.Receiver) : null, () => { foreach (var l in site.Args) E.Get(l); }, null, syntax);
    }

    Own EmitHandlerPlaceholder(IInterpolatedStringHandlerArgumentPlaceholderOperation ph)
    {
        if (ph.PlaceholderKind == InterpolatedStringArgumentPlaceholderKind.TrailingValidityArgument)
            throw new CompileException("the handler's validity argument as a value", ph.Syntax.GetLocation());
        var site = _handlerSites.Count > 0 ? _handlerSites[^1] : throw new CompileException("interpolated string handler argument outside a call", ph.Syntax.GetLocation());
        // (a placeholder has no type: it is the receiver's, or the parameter's)
        if (ph.PlaceholderKind == InterpolatedStringArgumentPlaceholderKind.CallsiteReceiver)
        {
            E.Get(site.Receiver);
            return site.ReceiverType != null && Types.IsRef(site.ReceiverType) ? Own.Stable : Own.None;
        }
        var p = site.Method.Parameters[ph.ArgumentIndex];
        var t = T.Canonical(p.Type);
        E.Get(site.Args[ph.ArgumentIndex]);
        if (p.RefKind != RefKind.None && !Types.IsStruct(t)) E.Load(Types.LoadOp(t)); // a ref argument: its value
        return Types.IsRef(t) ? Own.Stable : Own.None;
    }

    Own EmitHandlerCreation(IInterpolatedStringHandlerCreationOperation hc)
    {
        var ht = TypeOf(hc);
        // the handler: a struct in the frame (its address), or an object (kept for the statement)
        int addr = E.AddLocal(ValType.I32);
        int success = hc.HandlerCreationHasSuccessParameter ? AllocFrame(D.LibType("System.Boolean")) : -1;
        if (success >= 0) { _usesFrame = true; E.Get(_fb); E.I32(0); E.Store(Op.I32Store8, success); }
        _handlers.Add((addr, ht, success));
        try
        {
            if (Types.IsStruct(ht))
            {
                int off = TempStruct(ht);
                E.Get(_fb); E.I32(off); E.Op(Op.I32Add); E.Set(addr);
                EmitStructInto(hc.HandlerCreation, () => E.Get(addr));
            }
            else
            {
                var o = EmitValue(hc.HandlerCreation);
                ToCallSafe(o, ht);
                E.Set(addr);
            }
            var done = E.Block();
            if (success >= 0) { E.Get(_fb); E.Load(Op.I32Load8U, success); E.Op(Op.I32Eqz); E.BrIf(done); }
            foreach (var part in HandlerParts(hc.Content))
            {
                if (part is not IInterpolatedStringAppendOperation ap) throw new CompileException("interpolated string handler part", part.Syntax.GetLocation());
                var r = EmitValue(ap.AppendCall);
                var rt = ap.AppendCall.Type;
                if (hc.HandlerAppendCallsReturnBool && rt is { SpecialType: SpecialType.System_Boolean }) { E.Op(Op.I32Eqz); E.BrIf(done); }
                else if (rt != null && !Types.IsVoid(rt)) Drop(r, rt);
            }
            E.End();
        }
        finally { _handlers.RemoveAt(_handlers.Count - 1); }
        E.Get(addr);
        return Types.IsRef(ht) ? Own.Stable : Own.None;
    }

    static IEnumerable<IOperation> HandlerParts(IOperation content) => content switch
    {
        IInterpolatedStringOperation s => s.Parts,
        IInterpolatedStringAdditionOperation a => HandlerParts(a.Left).Concat(HandlerParts(a.Right)),
        _ => [content],
    };

    /// <summary>The handler an Append call is made on (an instance reference of kind InterpolatedStringHandler).</summary>
    Own EmitHandlerReference()
    {
        var (addr, type, _) = _handlers[^1];
        E.Get(addr);
        return Types.IsRef(type) ? Own.Stable : Own.None;
    }
}

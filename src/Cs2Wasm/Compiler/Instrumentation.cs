using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Plugin instrumentation (section 7.1): calls to plugin hooks at member writes, member reads and statements. The
// code around a hook is ordinary code, so the optimizer sees its cost.
sealed partial class FunctionCompiler
{
    CloneSpec? _clone;
    IOperation? _captureOp;
    int _captureLocal = -1;

    static IOperation StripImplicit(IOperation o) { while (o is IConversionOperation { IsImplicit: true } c) o = c.Operand; return o; }

    static ISymbol? HookedMember(IOperation? t) => t == null ? null : StripImplicit(t) switch
    {
        IFieldReferenceOperation f => f.Field.OriginalDefinition,
        IPropertyReferenceOperation p => p.Property.OriginalDefinition,
        _ => null,
    };

    static IOperation? WriteTarget(IOperation op) => op switch
    {
        ISimpleAssignmentOperation a => a.Target,
        ICompoundAssignmentOperation c => c.Target,
        IIncrementOrDecrementOperation i => i.Target,
        ICoalesceAssignmentOperation k => k.Target,
        _ => null,
    };

    /// <summary>The receiver evaluated by the next emission of `inst` is also kept in a local for the hooks.</summary>
    bool TryCaptured(IOperation op, out Own own)
    {
        own = default;
        if (_captureOp == null || !ReferenceEquals(op, _captureOp)) return false;
        _captureOp = null;
        own = EmitValueCore(op);
        E.Tee(_captureLocal);
        return true;
    }

    /// <summary>Hooks on a member write: `emit` produces the write (and its value, if wanted).</summary>
    bool TryInstrumentedWrite(IOperation op, Func<Own> emit, out Own result)
    {
        result = Own.None;
        if (!D.HasInstrumentation) return false;
        var target = WriteTarget(op);
        var member = HookedMember(target);
        if (member == null) return false;
        D.WriteHooks.TryGetValue(member, out var hooks);
        // (the hooks at this write alone: PluginContext.InstrumentWrite)
        if (D.SiteWriteHooks.Count > 0 && op.Syntax != null && D.SiteWriteHooks.TryGetValue(Driver.SiteKey(op.Syntax), out var here))
            hooks = hooks == null ? here : [.. hooks, .. here];
        if (hooks == null) return false;
        var inst = (StripImplicit(target!) as IMemberReferenceOperation)?.Instance;
        bool refRecv = inst != null && !TypeOf(inst).IsValueType;
        bool simple = inst is null or IInstanceReferenceOperation or ILocalReferenceOperation or IParameterReferenceOperation;
        foreach (var h in hooks)
            if (h.Point == InstrumentPoint.Before)
                EmitHookCall(h.Hook, h.Tag, () => { if (refRecv && simple) PushReceiver(inst!); else E.I32(0); });
        int recv = -1;
        if (refRecv && !simple && hooks.Any(h => h.Point == InstrumentPoint.After))
        {
            recv = E.AddLocal(ValType.I32);
            E.I32(0); E.Set(recv);
            _captureOp = inst; _captureLocal = recv;
        }
        result = emit();
        _captureOp = null;
        foreach (var h in hooks)
            if (h.Point == InstrumentPoint.After)
                EmitHookCall(h.Hook, h.Tag, () => { if (!refRecv) E.I32(0); else if (simple) PushReceiver(inst!); else E.Get(recv); });
        return true;
    }

    /// <summary>Hooks after a member read: the value stays on the stack under the hook's arguments.</summary>
    bool TryInstrumentedRead(IOperation op, Func<Own> emit, out Own result)
    {
        result = Own.None;
        if (!D.HasInstrumentation) return false;
        var member = HookedMember(op);
        if (member == null || !D.ReadHooks.TryGetValue(member, out var hooks)) return false;
        var inst = ((IMemberReferenceOperation)op).Instance;
        bool refRecv = inst != null && !TypeOf(inst).IsValueType;
        bool simple = inst is null or IInstanceReferenceOperation or ILocalReferenceOperation or IParameterReferenceOperation;
        int recv = -1;
        if (refRecv && !simple) { recv = E.AddLocal(ValType.I32); E.I32(0); E.Set(recv); _captureOp = inst; _captureLocal = recv; }
        result = emit();
        _captureOp = null;
        foreach (var h in hooks) EmitHookCall(h.Hook, h.Tag, () => { if (!refRecv) E.I32(0); else if (simple) PushReceiver(inst!); else E.Get(recv); });
        return true;
    }

    IOperation? _hookedValue;

    /// <summary>The expression's value through its hooks (PluginContext.InterceptValue): each is called with the value
    /// (the first with the expression's own, the next with what the one before returned) and returns the value the
    /// program goes on with. An ordinary call: its cost is in the code for the optimizer to see.</summary>
    Own EmitValueHooked(IOperation op, List<ValueHook> hooks)
    {
        var t = TypeOf(op);
        Own Level(int i)
        {
            if (i < 0)
            {
                var saved = _hookedValue;
                _hookedValue = op;
                try { return EmitValue(op); }
                finally { _hookedValue = saved; }
            }
            var h = hooks[i];
            h.Applied = true;
            var m = D.CanonicalMethod(h.Hook);
            return CallWith(m, false, null, () =>
            {
                var o = Level(i - 1);
                // (a struct: the hook gets a copy of its own, as any callee does)
                if (Types.IsStruct(t)) CopyToTemp(t);
                else if (Types.IsRef(t)) ToCallSafe(o, t);
                if (m.Parameters.Length > 1) E.I32(h.Tag);
            }, null, op.Syntax);
        }
        return Level(hooks.Count - 1);
    }

    void PushReceiver(IOperation inst)
    {
        var o = EmitValue(inst);
        if (o == Own.Owned) { int t = TempRef(TypeOf(inst)); E.Tee(t); }
    }

    void EmitStatementHooks(IOperation op)
    {
        if (D.StatementHooks.Count == 0 || op.Syntax == null) return;
        if (!D.StatementHooks.TryGetValue(Driver.SiteKey(op.Syntax), out var hooks)
            && !(_machine != null && op.Syntax.Parent != null && D.StatementHooks.TryGetValue(Driver.SiteKey(op.Syntax.Parent), out hooks)))
            return;
        foreach (var (hook, tag) in hooks) EmitHookCall(hook, tag, null);
    }

    /// <summary>Calls a hook taking (), (int tag), (object receiver) or (object receiver, int tag).</summary>
    void EmitHookCall(IMethodSymbol hook, int tag, Action? pushReceiver)
    {
        var mi = D.Method(D.CanonicalMethod(hook));
        Inst?.Callees.Add(mi);
        foreach (var p in hook.Parameters)
        {
            if (p.Type.SpecialType is SpecialType.System_Int32) E.I32(tag);
            else if (pushReceiver != null) pushReceiver();
            else E.I32(0);
        }
        E.Call(mi.Fn);
        foreach (var _ in mi.Fn.Type.Results) E.Op(Op.Drop);
    }
}

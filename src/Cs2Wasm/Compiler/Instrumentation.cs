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
        if (_entryCaptures.Count > 0 && _entryCaptures.Remove(op, out var cap)) { own = CaptureEntryValue(op, cap); return true; }
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
        if (D.HasEntryHooks && !ReferenceEquals(op, _entryOp) && TryInstrumentedEntry(op, () => TryInstrumentedWrite(op, emit, out var inner) ? inner : emit(), out result)) return true;
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

    // ---------------- collection entries (PluginContext.InstrumentEntryWrites) ----------------
    /// <summary>A value an entry hook is given (the key, a receiver that is not a local), kept as the program computes it.</summary>
    sealed class EntryCapture { public int Local = -1; public bool Done; }
    readonly Dictionary<IOperation, EntryCapture> _entryCaptures = new(ReferenceEqualityComparer.Instance);
    /// <summary>The entry operation being compiled with its hooks (it is compiled once more, without).</summary>
    IOperation? _entryOp;

    static bool SimpleReceiver(IOperation inst) => StripImplicit(inst) is IInstanceReferenceOperation or ILocalReferenceOperation or IParameterReferenceOperation;

    /// <summary>The value is computed where it stands and kept for the hook: a reference counted until the statement
    /// ends (the hook runs after the operation, which may have let go of its own), a number in a local.</summary>
    Own CaptureEntryValue(IOperation op, EntryCapture cap)
    {
        var t = TypeOf(op);
        var own = EmitValueCore(op);
        if (Types.IsRef(t))
        {
            if (D.ThreadMode) own = CountedLoad(own);
            if (own is Own.Stable or Own.Unstable && !Types.IsImmortalType(t)) { E.Call(Rt.RetainOf(t)); own = Own.Owned; }
            if (own == Own.Owned) { cap.Local = TempRef(t); E.Tee(cap.Local); own = Own.Stable; }
            else { cap.Local = E.AddLocal(ValType.I32); E.Tee(cap.Local); }
        }
        else { cap.Local = E.AddLocal(Types.WasmType(t) ?? ValType.I32); E.Tee(cap.Local); }
        cap.Done = true;
        return own;
    }

    ISymbol? EntryMember(IOperation coll) => coll switch
    {
        IMemberReferenceOperation mr => mr.Member.OriginalDefinition,
        ILocalReferenceOperation { Syntax: { } s } when D.EntryLocalSites.TryGetValue(Driver.SiteKey(s), out var m) => m,
        _ => null,
    };

    List<(EntrySite site, EntryHook hook)>? EntryHooksAt(IOperation op)
    {
        List<(EntrySite, EntryHook)>? at = null;
        // (not in the plugins' own code: a hook that reads the collection would call itself)
        if (op.Syntax != null && D.PluginTrees.Contains(op.Syntax.SyntaxTree)) return null;
        foreach (var s in Entries.Of(op))
        {
            if (!s.Hookable || EntryMember(s.Collection) is not { } member) continue;
            var table = s.Kind == AccessKind.Read ? D.EntryReadHooks : D.EntryWriteHooks;
            if (!table.TryGetValue(member, out var hooks)) continue;
            foreach (var h in hooks) (at ??= []).Add((s, h));
        }
        return at;
    }

    /// <summary>Hooks after an entry access (Entries.Of): `emit` produces the operation; its key and a receiver that
    /// is not a local are kept as it computes them, and the hooks are called after it with its value left under them.</summary>
    bool TryInstrumentedEntry(IOperation op, Func<Own> emit, out Own result)
    {
        result = Own.None;
        if (ReferenceEquals(op, _entryOp) || EntryHooksAt(op) is not { } at) return false;
        var mine = new List<IOperation>();
        EntryCapture? Capture(IOperation o)
        {
            if (_entryCaptures.TryGetValue(o, out var c)) return c;
            c = new EntryCapture();
            _entryCaptures[o] = c; mine.Add(o);
            return c;
        }
        var keys = new EntryCapture?[at.Count];
        var recvs = new EntryCapture?[at.Count];
        for (int i = 0; i < at.Count; i++)
        {
            var (s, h) = at[i];
            if (h.Keyed && s.Key is { ConstantValue.HasValue: false } k) keys[i] = Capture(k);
            if ((s.Collection as IMemberReferenceOperation)?.Instance is { } inst && !TypeOf(inst).IsValueType && !SimpleReceiver(inst)
                && h.Hook.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_Object)) recvs[i] = Capture(inst);
        }
        var saved = _entryOp;
        _entryOp = op;
        try { result = emit(); }
        finally
        {
            _entryOp = saved;
            foreach (var o in mine) _entryCaptures.Remove(o);
        }
        for (int i = 0; i < at.Count; i++)
        {
            if (keys[i] is { Done: false } || recvs[i] is { Done: false })
                throw new CompileException("an entry hook's key or receiver is not computed as a value where it stands: " + op.Syntax, op.Syntax.GetLocation());
            EmitEntryHookCall(at[i].hook, at[i].site, keys[i], recvs[i]);
        }
        return true;
    }

    void EmitEntryHookCall(EntryHook h, EntrySite s, EntryCapture? key, EntryCapture? recv)
    {
        var mi = D.Method(D.CanonicalMethod(h.Hook));
        Inst?.Callees.Add(mi);
        var inst = (s.Collection as IMemberReferenceOperation)?.Instance;
        void Receiver()
        {
            if (inst == null || TypeOf(inst).IsValueType) E.I32(0);
            else if (recv != null) E.Get(recv.Local);
            else if (SimpleReceiver(inst)) PushReceiver(StripImplicit(inst));
            else E.I32(0);
        }
        var ps = h.Hook.Parameters;
        if (h.Keyed)
        {
            Receiver();
            if (s.Key == null)
                switch (Types.WasmType(ps[1].Type) ?? ValType.I32) { case ValType.I64: E.I64(0); break; case ValType.F32: E.F32(0); break; case ValType.F64: E.F64(0); break; default: E.I32(0); break; }
            else if (key != null) E.Get(key.Local);
            else
            {
                var kt = TypeOf(s.Key);
                if (EmitConstant(s.Key.ConstantValue.Value, kt) == Own.Owned) E.Tee(TempRef(kt));
            }
            E.I32(s.Key != null ? 1 : 0);
            E.I32(h.Tag);
        }
        else
            foreach (var p in ps)
                if (p.Type.SpecialType == SpecialType.System_Int32) E.I32(h.Tag);
                else Receiver();
        E.Call(mi.Fn);
        foreach (var _ in mi.Fn.Type.Results) E.Op(Op.Drop);
    }

    void EmitStatementHooks(IOperation op)
    {
        // (a foreach over an entry-hooked collection: its read hooks, as the loop begins)
        if (op is IForEachLoopOperation && D.EntryReadHooks.Count > 0 && EntryHooksAt(op) is { } reads)
            foreach (var (s, h) in reads) EmitEntryHookCall(h, s, null, null);
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
